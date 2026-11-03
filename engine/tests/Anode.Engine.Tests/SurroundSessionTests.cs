using Anode.Ipc;
using Anode.Ipc.Segments;
using Anode.Ipc.Telemetry;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using static Anode.Engine.Tests.MultichannelTests;

namespace Anode.Engine.Tests;

/// <summary>
///     Phase 10's real-time promises (docs/11-phase-10.md, workstream A's tests): the listen-in tap's contents and
///     stamps, the surround meters in the telemetry, no allocation in the callback (or the telemetry) with a 7.1.4
///     master, 32 objects and binaural monitoring, and that session's callback cost, recorded. Workstream D's DSP is
///     doubled (<see cref="TestSpatial" />): the figures are the engine's own work plus the doubles' gain loops.
/// </summary>
public sealed unsafe class SurroundSessionTests : IDisposable {
    const int Rate = 48_000;
    const int Seconds = 20;
    readonly string dir = TestAudio.TempDirectory();
    readonly string mono;
    readonly string stereo;

    public SurroundSessionTests() {
        mono = TestAudio.Write24(dir, "mono.wav", 1, Seconds * Rate, 1);
        stereo = TestAudio.Write24(dir, "stereo.wav", 2, Seconds * Rate, 10);
    }

    public void Dispose() => Directory.Delete(dir, true);

    EngineFile[] Files => [new(0, mono, 1, Rate, Seconds * Rate), new(1, stereo, 2, Rate, Seconds * Rate)];

    static float M(long n) => TestAudio.Sample(n, 1);

    static PlaybackEvent Automation(long position, int target, float value, int track) =>
        new() {
            SamplePos = position,
            Track = track,
            Kind = PlaybackEventKind.Automation,
            Value = value,
            Target = target
        };

    static List<TapStamp> Stamps(byte* region) {
        var stamps = new List<TapStamp>();
        MasterTapWriter.Read(region, 0, [], stamps);
        return stamps;
    }

    [Fact]
    public void The_master_tap_carries_the_master_and_a_stamp_per_block_while_it_is_on() {
        var region = (byte*)NativeMemory.AlignedAlloc((nuint)MasterTapLayout.Size, 64);
        try {
            using var segment = TestAudio.Segment([new(1, [], [TestAudio.Region(0, Seconds * Rate, 1)])], 1);
            using var controller = new EngineController(new(Rate, 64));
            controller.LoadModel(
                TestAudio.Load(segment, 1, true, [TestAudio.Track(1, true, Strip(0.5f))], Files, dir, Strip(0.8f))
            );
            controller.Engine.MasterTap = new(region, Rate);
            var header = (MasterTapHeader*)region;
            Assert.Equal(
                (MasterTapHeader.MagicValue, MasterTapHeader.CurrentFormat, Rate, 2),
                (header->Magic, header->Format, header->SampleRate, header->Channels)
            );
            Assert.Equal(MasterTapLayout.Offsets().Samples, header->SamplesOffset);
            Assert.Equal(MasterTapLayout.Size, header->TotalLength);
            using var driver = new OfflineDriver(controller, 64);

            // Off: nothing is written.
            driver.PlayFrom(0);
            driver.Process();
            Assert.Equal(0, header->WriteFrame);

            controller.SetMasterTap(true);
            var played = new List<(float L, float R)>();
            for (var b = 0; b < 20; b++) {
                driver.Process();
                for (var i = 0; i < 64; i++) {
                    played.Add((driver.Output(0)[i], driver.Output(1)[i]));
                }
            }

            Assert.Equal(20 * 64, header->WriteFrame);
            var samples = new float[2 * 20 * 64];
            MasterTapWriter.Read(region, 0, samples, []);
            for (var f = 0; f < played.Count; f++) {
                Assert.Equal(played[f].L, samples[2 * f]);
                Assert.Equal(played[f].R, samples[2 * f + 1]);
            }

            Assert.Contains(samples, static v => v != 0);
            var stamps = Stamps(region);
            Assert.Equal(20, stamps.Count);
            for (var k = 0; k < stamps.Count; k++) {
                Assert.Equal(k * 64L, stamps[k].Frame);
                Assert.Equal(64 + k * 64L, stamps[k].TransportSample); // the tap came on a block into playing
                var flags = TapStamp.FlagPlaying | (k == 0 ? TapStamp.FlagDiscontinuity : 0);
                Assert.Equal(flags, stamps[k].Flags);
            }

            // A locate is a discontinuity; stopped, the transport sample is −1.
            driver.Send(new(EngineCommandKind.Locate, 96_000));
            driver.Process();
            driver.Send(new(EngineCommandKind.Stop));
            driver.Process();
            stamps = Stamps(region);
            Assert.Equal(
                (96_000L, TapStamp.FlagPlaying | TapStamp.FlagDiscontinuity),
                (stamps[^2].TransportSample, stamps[^2].Flags)
            );
            Assert.Equal((-1L, 0), (stamps[^1].TransportSample, stamps[^1].Flags));

            // Off again: the counters stand still, even through an offline render.
            controller.SetMasterTap(false);
            var written = header->WriteFrame;
            driver.Process();
            Assert.Equal(written, header->WriteFrame);
        } finally {
            NativeMemory.AlignedFree(region);
        }
    }

    [Fact]
    public void A_surround_master_taps_its_stereo_down_mix_and_meters_every_channel() {
        const EngineChannelFormat f714 = EngineChannelFormat.Surround714;
        var tap = (byte*)NativeMemory.AlignedAlloc((nuint)MasterTapLayout.Size, 64);
        var telemetryRegion = (byte*)NativeMemory.AlignedAlloc((nuint)TelemetryLayout.Size, 64);
        try {
            using var segment = TestAudio.Segment([new(1, [], [TestAudio.Region(0, Seconds * Rate, 0)])], 1);
            using var controller = new EngineController(new(Rate, 64));
            controller.SpatialFactory = new TestSpatial();
            controller.LoadModel(
                TestAudio.Load(
                    segment,
                    1,
                    true,
                    [TestAudio.Track(1, false, Strip(outputFormat: f714, panner: Surround(5)))],
                    Files,
                    dir,
                    Strip(input: f714, outputFormat: f714)
                )
            );
            controller.Engine.MasterTap = new(tap, Rate);
            controller.SetMasterTap(true);
            using var telemetry = new TelemetryWriter(controller.Engine, telemetryRegion, static () => 0);
            using var driver = new OfflineDriver(controller, 64, 2, 12);
            driver.PlayFrom(0);
            for (var b = 0; b < 10; b++) {
                driver.Process();
            }

            telemetry.Write();
            var samples = new float[2 * 640];
            MasterTapWriter.Read(tap, 0, samples, []);
            for (var f = 64; f < 640; f++) { // after the stopped block of the locate
                Assert.Equal(0f, samples[2 * f]); // the down-mix double: odd channels to the right
                Assert.Equal(0.5f * M(f - 64), samples[2 * f + 1], 6);
            }

            var meters = new MeterEntry[2];
            var channels = new ChannelMeterEntry[2];
            Assert.True(TelemetryWriter.TryRead(telemetryRegion, out var header, meters, channels));
            Assert.Equal(1, header.TrackCount);
            Assert.Equal((-1, 12), (meters[1].TrackId, meters[1].Channels));
            Assert.Equal((1, 12), (meters[0].TrackId, meters[0].Channels)); // the track's surround output
            var peaks = new float[16];
            fixed (float* p = channels[1].Peaks) {
                new ReadOnlySpan<float>(p, 16).CopyTo(peaks);
            }

            Assert.True(peaks[5] > 0.1f);
            Assert.Equal(0f, peaks[0]);
            Assert.Equal(meters[1].PeakLeft, peaks[0]);
        } finally {
            NativeMemory.AlignedFree(tap);
            NativeMemory.AlignedFree(telemetryRegion);
        }
    }

    /// <summary>
    ///     The 7.1.4 session: 32 objects (mono, half with automated positions), 8 bed tracks (Surround Panner, half
    ///     automated, two with a send to a 5.1 bus), a 7.1.4 bed aux with a per-channel insert, the 5.1 aux into it, a
    ///     linked insert and a per-channel one on the master, binaural monitoring and the master tap on.
    /// </summary>
    (EngineController Controller, OfflineDriver Driver, IDisposable Cleanup) Session(int block) {
        const EngineChannelFormat f714 = EngineChannelFormat.Surround714;
        const EngineChannelFormat f51 = EngineChannelFormat.Surround51;
        var models = new List<TrackModel>();
        var tracks = new List<EngineTrack>();
        var targets = new List<EngineAutomationTarget>();
        for (var t = 0; t < 40; t++) {
            var objectTrack = t < 32;
            var events = new List<PlaybackEvent>();
            if (t % 2 == 0) {
                var target = targets.Count;
                targets.Add(
                    new(target, t, objectTrack ? "object" : "surround", null, objectTrack ? "azimuth" : "angle")
                );
                for (var k = 0; k < 40; k++) {
                    events.Add(Automation(k * 12_000L, target, k % 12, t));
                }
            }

            models.Add(new(t, [..events], [TestAudio.Region(0, Seconds * Rate, t % 7 == 0 ? 1 : 0, t * 113L)]));
            var sends = !objectTrack && t % 4 == 0 ? new EngineSend[] { new("s", 2, 0.5f, 0f, false) } : null;
            tracks.Add(
                TestAudio.Track(
                    t,
                    t % 7 == 0,
                    Strip(
                        0.3f,
                        objectTrack ? 0 : 1,
                        outputFormat: f714,
                        panner: objectTrack ? Object(t % 12, 0.7f) : Surround(t % 12, 0.9f),
                        sends: sends
                    )
                )
            );
        }

        var gain = TestEffects.Insert("g", TestEffects.Gain, new Dictionary<string, double> { ["gain"] = 0.9 });
        var duck = TestEffects.Insert("k", DuckEffect.Id, new Dictionary<string, double> { ["frames"] = 16 });
        tracks.Add(TestAudio.Track(100, true, Strip(0.8f, 0, f714, f714, inserts: [gain])) with { InputBus = 1 });
        tracks.Add(TestAudio.Track(101, true, Strip(0.8f, 1, f51, f714)) with { InputBus = 2 });
        models.Add(new(100, [], []));
        models.Add(new(101, [], []));
        var segment = TestAudio.Segment(models, 1);
        var controller = new EngineController(new(Rate, block), SpatialFixtures.Registry()) {
            SpatialFactory = new TestSpatial { Record = false, BinauralLatency = 64 }
        };
        controller.LoadModel(
            TestAudio.Load(
                segment,
                1,
                true,
                tracks,
                Files,
                dir,
                Strip(0.9f, input: f714, outputFormat: f714, inserts: [duck, gain])
            ) with {
                AutomationTargets = targets
            }
        );
        controller.SetMixer("all", false, 0);
        controller.SetMonitoring(new(true, null, [0, 1], "set.anhrtf"));
        var tap = (byte*)NativeMemory.AlignedAlloc((nuint)MasterTapLayout.Size, 64);
        controller.Engine.MasterTap = new(tap, Rate);
        controller.SetMasterTap(true);
        var driver = new OfflineDriver(controller, block, 2, 2);
        Assert.Equal(32, controller.Engine.Root.Mix.Objects.Length);
        return (controller, driver, new Cleanup(() => {
                driver.Dispose();
                controller.Dispose();
                segment.Dispose();
                NativeMemory.AlignedFree(tap);
            }
        ));
    }

    sealed class Cleanup(Action action) : IDisposable {
        public void Dispose() => action();
    }

    static long Least(Action action) {
        action();
        var least = long.MaxValue;
        for (var i = 0; i < 3 && least > 0; i++) {
            var before = GC.GetAllocatedBytesForCurrentThread();
            action();
            least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        return least;
    }

    [Fact]
    public void A_714_session_with_32_objects_and_binaural_monitoring_allocates_nothing_in_the_callback() {
        var (controller, driver, cleanup) = Session(64);
        using var _ = cleanup;
        var region = (byte*)NativeMemory.AlignedAlloc((nuint)TelemetryLayout.Size, 64);
        try {
            using var telemetry = new TelemetryWriter(controller.Engine, region, static () => 0);
            driver.PlayFrom(0);
            long callbacks = 0, telemetryBytes = 0;
            for (var round = 0; round < 10; round++) {
                driver.Pump();
                callbacks = Math.Max(
                    callbacks,
                    Least(() => {
                            for (var i = 0; i < 100; i++) {
                                driver.Process(false);
                            }
                        }
                    )
                );
                telemetryBytes = Math.Max(telemetryBytes, Least(telemetry.Write));
            }

            Assert.Equal(0, callbacks);
            Assert.Equal(0, telemetryBytes);
            Assert.Equal(0, controller.Engine.Snapshot.Underruns);
        } finally {
            NativeMemory.AlignedFree(region);
        }
    }

    [Fact]
    public void Callback_cost_of_a_714_session_with_32_objects_and_binaural_monitoring() {
        var results = new List<string>();
        foreach (var block in new[] { 64, 256 }) {
            var best = double.MaxValue;
            var p99 = 0.0;
            for (var run = 0; run < 3; run++) {
                var (_, driver, cleanup) = Session(block);
                using var _ = cleanup;
                driver.PlayFrom(0);
                var times = new List<double>();
                var blocks = 96_000 / block;
                for (var i = 0; i < blocks; i++) {
                    if (i % (1024 / block) == 0) {
                        driver.Pump();
                    }

                    var start = Stopwatch.GetTimestamp();
                    driver.Process(false);
                    var elapsed = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
                    if (i >= blocks / 5) {
                        times.Add(elapsed);
                    }
                }

                times.Sort();
                if (times.Average() < best) {
                    best = times.Average();
                    p99 = times[(int)(times.Count * 0.99)];
                }
            }

            var budget = block * 1e6 / Rate;
            results.Add(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{block} frames ({budget:0} µs): {best:0.0} µs mean ({100 * best / budget:0.0} % of the budget), {p99:0.0} µs p99"
                )
            );
            Assert.True(best < budget * 4, results[^1]);
        }

        var message = "7.1.4 session, 32 objects, 8 beds, 2 auxes, binaural monitoring and the tap (spatial doubles): "
            + string.Join("; ", results);
        TestContext.Current.SendDiagnosticMessage(message);
        Console.WriteLine(message);
        if (Environment.GetEnvironmentVariable("ANODE_MEASURE_LOG") is { Length: > 0 } log) {
            File.AppendAllText(log, message + Environment.NewLine);
        }
    }
}
