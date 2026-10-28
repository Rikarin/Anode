using Anode.Engine.Recording;
using Anode.Ipc;
using Anode.Ipc.Segments;
using Anode.Ipc.Telemetry;
using System.Runtime.InteropServices;

namespace Anode.Engine.Tests;

/// <summary>
///     The audio callback must never allocate (a collection would stop it; docs/adr/0001-process-model.md), and neither
///     may the threads that run at a steady rate beside it: the disk thread's passes and the telemetry writes. Measured
///     per thread, so everything runs synchronously on the test's thread. Each figure is the least of three
///     measurements, as in Cathode's HostAllocationTests: the runtime now and then does one-off work on the measuring
///     thread (a tier-up, a type load) that shows in one measurement, while a real allocation shows in all three.
/// </summary>
public sealed unsafe class AllocationTests : IDisposable {
    const int Rate = 48_000;
    readonly string dir = TestAudio.TempDirectory();

    public void Dispose() => Directory.Delete(dir, true);

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
    public void Callbacks_disk_passes_and_telemetry_allocate_nothing() {
        var mono = TestAudio.Write24(dir, "mono.wav", 1, 20 * Rate, 1);
        var stereo = TestAudio.Write24(dir, "stereo.wav", 2, 20 * Rate, 2);
        EngineFile[] files = [new(0, mono, 1, Rate, 20 * Rate), new(1, stereo, 2, Rate, 20 * Rate)];
        var models = new List<TrackModel>();
        var tracks = new List<EngineTrack>();
        for (var t = 0; t < 8; t++) {
            var regions = Enumerable.Range(0, 40)
                .Select(r => TestAudio.Region(r * 12_000L, 11_000, t & 1, r * 3_000L, 0.7f, 200, 300))
                .ToArray();
            models.Add(new(t, [], regions));
            tracks.Add(
                TestAudio.Track(t, (t & 1) == 1, new(0.8f, t / 8f - 0.5f, t == 5, t == 3), t & 1, t == 2, t < 4)
            );
        }

        models.Add(
            new(
                TrackEntry.MetronomeTrack,
                [..Enumerable.Range(0, 400).Select(static i => TestAudio.Click(i * 6_000L, i % 4 == 0))],
                []
            )
        );
        using var segment = TestAudio.Segment(models, 1);
        using var controller = new EngineController(new(Rate, 64));
        controller.LoadModel(TestAudio.Load(segment, 1, true, tracks, files, dir));
        controller.Engine.SetMetronome(true, 0.5f);
        using var driver = new OfflineDriver(controller, 64);
        using var take = new RecordTake(2, Path.Combine(dir, "2-take.wav"), 1, Rate);
        controller.Engine.Root.Tracks[2].Take = take;
        var region = NativeMemory.AlignedAlloc((nuint)TelemetryLayout.Size, 64);
        using var telemetry = new TelemetryWriter(controller.Engine, (byte*)region, static () => 0);
        var scratch = new float[8192];
        try {
            driver.Send(new(EngineCommandKind.SetCycle, 30_000, 150_000, true));
            driver.PlayFrom(20_000);
            driver.Send(new(EngineCommandKind.Record));
            var callbacks = Least(() => {
                    for (var i = 0; i < 100; i++) {
                        driver.Process(false);
                    }
                }
            );
            var pumps = 0L;
            var callbackBytes = callbacks;
            var telemetryBytes = 0L;
            for (var round = 0; round < 20; round++) {
                // Cross the cycle end a few times, and let the playlists' regions come and go.
                pumps = Math.Max(pumps, Least(driver.Pump));
                take.Drain(scratch);
                callbackBytes = Math.Max(
                    callbackBytes,
                    Least(() => {
                            for (var i = 0; i < 100; i++) {
                                driver.Process(false);
                            }
                        }
                    )
                );
                telemetryBytes = Math.Max(telemetryBytes, Least(telemetry.Write));
            }

            Assert.Equal(0, callbackBytes);
            Assert.Equal(0, pumps);
            Assert.Equal(0, telemetryBytes);
            Assert.True(take.Pushed > 0);
            Assert.Equal(0, take.Dropped);
            Assert.Equal(0, controller.Engine.Snapshot.Underruns);
        } finally {
            controller.Engine.Root.Tracks[2].Take = null;
            NativeMemory.AlignedFree(region);
        }
    }

    /// <summary>
    ///     Phase 5's warped playback (docs/06-phase-5.md): tracks whose warped regions (several algorithms, mono and
    ///     stereo) crossfade with unwarped ones, played across a cycle wrap, then located into the middle of a warped
    ///     region (renderers priming) and edited (a new fill). The disk thread's passes allocate nothing: the renderers
    ///     were made when the model loaded.
    /// </summary>
    [Fact]
    public void Warped_playback_cycles_locates_and_edits_allocate_nothing_on_the_disk_thread() {
        var bursts = FlexPlaybackTests.WriteBursts(dir, "bursts.wav", 2);
        var mono = TestAudio.Write24(dir, "mono.wav", 1, 20 * Rate, 1);
        EngineFile[] files = [new(0, bursts, 2, Rate, 80_000), new(1, mono, 1, Rate, 20 * Rate)];
        FlexAlgorithm[] algorithms = [
            FlexAlgorithm.Polyphonic, FlexAlgorithm.Slicing, FlexAlgorithm.Speed, FlexAlgorithm.Monophonic,
            FlexAlgorithm.Rhythmic, FlexAlgorithm.Tempophone
        ];

        TrackModel[] Models(long shift) => [
            ..algorithms.Select((algorithm, t) => {
                    var length = FlexPlaybackTests.MapLength;
                    AudioPlaylistEntry[] playlist = [
                        TestAudio.Region(0, 12_000, 1, t * 1_000L, 0.8f, 0, 2_000),
                        FlexPlaybackTests.Warped(10_000 + shift, t & 1, algorithm, 0, 0.9f, 2_000, 3_000),
                        TestAudio.Region(10_000 + shift + length - 3_000, 40_000, 1, 5_000, 0.7f, 3_000)
                    ];
                    return new TrackModel(t, [], playlist, FlexPlaybackTests.Map());
                }
            )
        ];

        var tracks = algorithms.Select(static (_, t) => TestAudio.Track(t, (t & 1) == 0)).ToArray();
        using var segment = TestAudio.Segment(Models(0), 1);
        using var controller = new EngineController(new(Rate, 64));
        controller.LoadModel(TestAudio.Load(segment, 1, true, tracks, files, dir));
        using var driver = new OfflineDriver(controller, 64);

        void Callbacks() {
            for (var i = 0; i < 100; i++) {
                driver.Process(false);
            }
        }

        driver.Send(new(EngineCommandKind.SetCycle, 30_000, 75_000, true));
        driver.PlayFrom(20_000);
        var callbackBytes = Least(Callbacks);
        var pumps = 0L;
        for (var round = 0; round < 30; round++) {
            if (round == 12) {
                // Each locate into the warped regions starts new fills: every renderer primes.
                driver.Send(new(EngineCommandKind.Stop));
                pumps = Math.Max(
                    pumps,
                    Least(() => {
                            driver.Send(new(EngineCommandKind.Locate, 51_234));
                            driver.Process(false);
                            driver.Pump();
                        }
                    )
                );
                driver.Send(new(EngineCommandKind.Play));
            }

            if (round == 20) {
                using var edit = TestAudio.Segment(Models(500), 2);
                controller.LoadModel(TestAudio.Load(edit, 2, false, [], [], dir));
            }

            pumps = Math.Max(pumps, Least(driver.Pump));
            callbackBytes = Math.Max(callbackBytes, Least(Callbacks));
        }

        Assert.Equal(0, callbackBytes);
        Assert.Equal(0, pumps);
        Assert.Equal(0, controller.Engine.Snapshot.Underruns);
        Assert.All(controller.Engine.Root.Tracks, static t => Assert.NotNull(t.Crossfade));
    }

    /// <summary>
    ///     Phase 3's paths: tracks crossfading continuously through curved fades (two streams each), the preview channel,
    ///     Flashback Capture's buffers and MIDI history while playing, then cycle recording with autopunch (passes
    ///     wrapping), a stop and a locate. Neither the callback nor the disk thread's passes allocate.
    /// </summary>
    [Fact]
    public void Crossfades_preview_flashback_cycle_recording_and_punch_allocate_nothing() {
        var mono = TestAudio.Write24(dir, "mono.wav", 1, 20 * Rate, 1);
        var stereo = TestAudio.Write24(dir, "stereo.wav", 2, 20 * Rate, 2);
        var preview = TestAudio.Write24(dir, "preview.wav", 2, 20 * Rate, 3);
        EngineFile[] files = [new(0, mono, 1, Rate, 20 * Rate), new(1, stereo, 2, Rate, 20 * Rate)];
        var models = new List<TrackModel>();
        var tracks = new List<EngineTrack>();
        for (var t = 0; t < 6; t++) {
            // Regions of 12,000 frames every 10,000: each crossfades into the next over 2,000 frames.
            var regions = Enumerable.Range(0, 30)
                .Select(r => TestAudio.Region(r * 10_000L, 12_000, t & 1, r * 3_000L, 0.7f, 2_000, 2_000) with {
                        FadeInCurve = (r % 5 - 2) * 0.4f, FadeOutCurve = (2 - r % 5) * 0.3f
                    }
                )
                .ToArray();
            models.Add(new(t, [], regions));
            tracks.Add(TestAudio.Track(t, (t & 1) == 1, input: t & 1, armed: t is 2 or 3, monitoring: t == 3));
        }

        var (registry, _) = MidiFixtures.Registry();
        models.Add(new(6, [], []));
        tracks.Add(MidiFixtures.InstrumentTrack(6));
        using var segment = TestAudio.Segment(models, 1);
        using var controller = new EngineController(new(Rate, 64, 100, FlashbackSeconds: 30), registry);
        controller.LoadModel(TestAudio.Load(segment, 1, true, tracks, files, dir));
        controller.SetMidiThru(6);
        Assert.All(controller.Engine.Root.Tracks[..6], static t => Assert.NotNull(t.Crossfade));
        Assert.NotNull(controller.Engine.Root.Tracks[2].Flashback);
        using var driver = new OfflineDriver(controller, 64);
        using var take = new RecordTake(3, Path.Combine(dir, "3-take.wav"), 2, Rate);
        controller.Engine.Root.Tracks[3].Take = take;
        controller.Preview(preview, 0.5f);
        var scratch = new float[8192];

        void Callbacks() {
            for (var i = 0; i < 100; i++) {
                driver.Process(false);
            }
        }

        driver.Send(new(EngineCommandKind.SetCycle, 30_000, 75_000, true));
        driver.PlayFrom(20_000);
        var callbackBytes = Least(Callbacks);
        var pumps = 0L;
        for (var round = 0; round < 30; round++) {
            if (round == 10) {
                driver.Send(new(EngineCommandKind.SetPunch, 40_000, 70_000, true));
                driver.Send(new(EngineCommandKind.Record)); // cycle recording, punched
            }

            if (round == 25) {
                driver.Send(new(EngineCommandKind.Stop));
                driver.PlayFrom(50_000); // a new Flashback run
            }

            controller.LiveMidi(6, [new(0x90, 60 + round % 12, 100), new(0x80, 59 + round % 12, 0)]);
            controller.Midi.Pump();
            pumps = Math.Max(pumps, Least(driver.Pump));
            take.Drain(scratch);
            callbackBytes = Math.Max(callbackBytes, Least(Callbacks));
        }

        Assert.Equal(0, callbackBytes);
        Assert.Equal(0, pumps);
        Assert.True(take.Pushed > 0);
        Assert.Equal(0, take.Dropped);
        Assert.Equal(0, controller.Engine.Snapshot.Underruns);
        controller.Engine.Root.Tracks[3].Take = null;
        var result = take.Complete(scratch);
        Assert.True(result.Passes is { Count: > 1 }, "the punched cycle recording made passes");
        Assert.All(result.Passes!, static p => Assert.Equal(40_000, p.StartSample));
        Assert.NotEmpty(controller.FlashbackCapture().Takes);
    }

    /// <summary>
    ///     The callback with instruments active: the built-in synth and sampler, a test instrument and a CLAP plugin
    ///     playing dense regions across a cycle wrap, live MIDI arriving, parameters changing, an external MIDI track
    ///     sending and an armed track capturing while recording, then a stop and a locate (note bookkeeping). Neither
    ///     the callback nor anything it calls allocates.
    /// </summary>
    [Fact]
    public void Callbacks_with_instruments_live_midi_and_parameters_allocate_nothing() {
        var (registry, _) = MidiFixtures.Registry();
        registry.ScanClap([MidiFixtures.ClapFolder]);
        var notes = Enumerable.Range(0, 400)
            .Select(static i => ((long)i * 300, 450L, 36 + i % 48))
            .ToArray();
        var models = new List<TrackModel>();
        var tracks = new List<EngineTrack>();
        for (var t = 0; t < 6; t++) {
            models.Add(new(t, MidiFixtures.Notes(notes), []));
            var instrument = t switch {
                5 => new EngineInstrument(
                    MidiFixtures.ClapId,
                    new Dictionary<string, double> { ["1"] = 0.5, ["7"] = 0 }
                ),
                2 or 3 => new EngineInstrument("anode.synth", new Dictionary<string, double>()), // its defaults
                4 => new EngineInstrument("anode.sampler", new Dictionary<string, double>()),
                _ => MidiFixtures.Instrument()
            };
            tracks.Add(MidiFixtures.InstrumentTrack(t, instrument, t == 0));
        }

        models.Add(new(6, MidiFixtures.Notes(notes), []));
        tracks.Add(MidiFixtures.ExternalTrack(6, "out-a", 2));
        using var segment = TestAudio.Segment(models, 1);
        using var controller = new EngineController(new(Rate, 64), registry, new FakeMidi());
        controller.LoadModel(TestAudio.Load(segment, 1, true, tracks, [], dir));
        using var driver = new OfflineDriver(controller, 64);
        Assert.IsType<Plugins.Clap.ClapInstrument>(controller.Engine.Root.Tracks[5].Instrument!.Instrument);
        Assert.Equal("anode.synth", controller.Engine.Root.Tracks[2].Instrument!.Instrument.Descriptor.Id);

        void Callbacks() {
            for (var i = 0; i < 100; i++) {
                driver.Process(false);
            }
        }

        // Between measurements, on the control and MIDI threads: live MIDI, parameters, draining the queues.
        void Feed(int round) {
            controller.LiveMidi(0, [new(0x90, 60 + round % 12, 100), new(0x80, 59 + round % 12, 0)]);
            controller.LiveMidi(5, [new(0x90, 72, 100), new(0xB0, 1, round % 128)]);
            controller.LiveMidi(2, [new(0x90, 50 + round % 7, 90), new(0xE0, 0, 64 + round % 32), new(0xB0, 64, 127)]);
            controller.SetParameter(1, "gain", 0.2 + round % 5 * 0.1);
            controller.SetParameter(5, "1", 0.2 + round % 5 * 0.1);
            controller.Midi.Pump();
            driver.Pump();
        }

        driver.Send(new(EngineCommandKind.SetCycle, 20_000, 90_000, true));
        driver.PlayFrom(10_000);
        var callbackBytes = Least(Callbacks);
        for (var round = 0; round < 20; round++) {
            Feed(round);
            callbackBytes = Math.Max(callbackBytes, Least(Callbacks));
            if (round == 10) {
                driver.Send(new(EngineCommandKind.Record)); // punch in: the armed track captures live MIDI
            }
        }

        // Stop and locate release and reset what sounds.
        driver.Send(new(EngineCommandKind.Stop));
        callbackBytes = Math.Max(callbackBytes, Least(Callbacks));
        driver.Send(new(EngineCommandKind.Locate, 40_000));
        Feed(99);
        callbackBytes = Math.Max(callbackBytes, Least(Callbacks));

        Assert.Equal(0, callbackBytes);
        Assert.True(controller.Engine.Snapshot.Callbacks > 4_000);
        var captured = controller.Midi.EndCapture();
        Assert.True(captured.TryGetValue(0, out var take) && take.Count > 0);
        Assert.Equal(
            0,
            ((Plugins.Clap.ClapInstrument)controller.Engine.Root.Tracks[5].Instrument!.Instrument).AudioThreadLogs
        );
    }
}
