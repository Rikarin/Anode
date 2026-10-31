using Anode.Engine.Sync;
using Anode.Engine.Timing;
using Anode.Ipc;
using Anode.Ipc.Segments;

namespace Anode.Engine.Tests;

/// <summary>
///     MIDI Clock and MTC out (docs/09-phase-8.md, "Sync"): the encoders place every clock and quarter frame on its
///     sample at any tempo and block size and through a tempo ramp; Start, Stop, Continue and the Song Position Pointer
///     follow the transport and a locate; MTC at each rate decodes to the playhead within a frame. Through the engine,
///     the messages are stamped on the callbacks' smoothed clock, so wake-up jitter does not reach their spacing, and the
///     MIDI thread sends each to the destinations that take it, plus their delay.
/// </summary>
public sealed unsafe class SyncOutTests : IDisposable {
    const int Rate = 48_000;
    readonly string dir = TestAudio.TempDirectory();

    public void Dispose() => Directory.Delete(dir, true);

    /// <summary>The sample of every clock the encoder places over a map, played from sample 0 in blocks.</summary>
    static List<double> ClockSamples(TempoMap map, int block, long frames) {
        var encoder = new MidiClockEncoder();
        var events = new SyncMidiEvent[256];
        var samples = new List<double>();
        for (long at = 0; at < frames; at += block) {
            var start = map.QuarterAt(at) * 960;
            var end = map.QuarterAt(at + block) * 960;
            var count = encoder.Process(true, start, end, block, events);
            foreach (var e in events.AsSpan(0, count)) {
                if (e.Status == 0xF8) {
                    samples.Add(at + e.Frame);
                }
            }
        }

        return samples;
    }

    [Theory]
    [InlineData(60.0, 32)]
    [InlineData(120.0, 64)]
    [InlineData(120.0, 1024)]
    [InlineData(174.3, 256)]
    [InlineData(300.0, 4096)]
    public void Clocks_fall_on_their_samples_at_any_tempo_and_block_size(double bpm, int block) {
        TempoMapPoint[] points = [TestAudio.Tempo(0, 0, bpm)];
        fixed (TempoMapPoint* p = points) {
            var map = new TempoMap(p, 1, Rate);
            var samples = ClockSamples(map, block, 10 * Rate);
            var spacing = Rate * 60.0 / (bpm * 24);
            Assert.True(samples.Count >= (int)(10 * bpm * 24 / 60) - 1);
            for (var k = 0; k < samples.Count; k++) {
                Assert.InRange(samples[k] - k * spacing, -1.0, 1e-6); // on its sample (the one it falls in)
            }
        }
    }

    [Theory]
    [InlineData(64)]
    [InlineData(512)]
    public void Clocks_follow_a_tempo_ramp(int block) {
        // 120 bpm for a bar, then a ramp up (tempo linear in quarters) for eight bars, then steady at the top.
        var growth = Math.Exp(10 * 16.0 / 60);
        TempoMapPoint[] points = [
            TestAudio.Tempo(0, 0, 120), TestAudio.Tempo(96_000, 4, 120, 10),
            TestAudio.Tempo(96_000 + 16 * Rate, 4 + 120 / 10.0 * (growth - 1), 120 * growth)
        ];
        fixed (TempoMapPoint* p = points) {
            var map = new TempoMap(p, points.Length, Rate);
            var samples = ClockSamples(map, block, 20 * Rate);
            for (var k = 0; k < samples.Count; k++) {
                var ideal = map.SampleAt(k / 24.0);
                Assert.InRange(samples[k] - ideal, -1.5, 0.5); // within a sample and a half, across the ramp (0.03 ms)
            }

            var first = samples[1] - samples[0];
            var last = samples[^1] - samples[^2];
            Assert.True(first / last > growth * 0.99, $"spacing {first} → {last}");
        }
    }

    [Fact]
    public void Start_stop_continue_and_the_pointer_follow_the_transport() {
        var encoder = new MidiClockEncoder();
        var events = new SyncMidiEvent[64];

        string Run(bool playing, double start, double end) {
            var count = encoder.Process(playing, start, end, 64, events);
            return string.Join(" ", events.AsSpan(0, count).ToArray().Select(Hex));
        }

        // From the project start: Start, and the first clock at once.
        Assert.Equal("FA@0 F8@0", Run(true, 0, 30));
        Assert.Equal("F8@21", Run(true, 30, 60)); // tick 40 at frame 64 × 10/30
        Assert.Equal("FC@0", Run(false, 60, 90));

        // A locate while stopped: the pointer, in sixteenths, floored.
        var count = encoder.Locate(4 * 960 + 300, events);
        Assert.Equal("F21100@0", string.Join(" ", events.AsSpan(0, count).ToArray().Select(Hex))); // 17 sixteenths

        // Playing from there: the pointer to the next sixteenth and Continue; no clock until it is reached.
        Assert.Equal("F21200@0 FB@0", Run(true, 4 * 960 + 300, 4 * 960 + 320));
        Assert.Equal("F8@42 F8@53", Run(true, 4 * 960 + 320, 4 * 960 + 560)); // 4320 = 18 sixteenths, at 64 × 160/240

        // A jump while playing (a cycle wrap): Stop, the pointer, Continue.
        Assert.Equal("FC@0 F20000@0 FB@0 F8@0", Run(true, 0, 30));
        return;

        static string Hex(SyncMidiEvent e) =>
            (e.Length switch {
                1 => $"{e.Status:X2}",
                2 => $"{e.Status:X2}{e.Data1:X2}",
                _ => $"{e.Status:X2}{e.Data1:X2}{e.Data2:X2}"
            }) + $"@{e.Frame}";
    }

    public static TheoryData<EngineMtcRate> Rates => [
        EngineMtcRate.Fps24, EngineMtcRate.Fps25, EngineMtcRate.Fps2997Drop, EngineMtcRate.Fps30
    ];

    [Theory]
    [MemberData(nameof(Rates))]
    public void Mtc_decodes_to_the_playhead_within_a_frame_at_each_rate(EngineMtcRate rate) {
        // Across a minute boundary (29.97 drop skips labels there) and an hour: 00:59:58 to 01:00:04.
        var encoder = new MtcEncoder(rate);
        var decoder = new MtcDecoder(rate);
        var events = new SyncMidiEvent[64];
        var start = 3598.0;
        var block = 256;
        var positions = 0;
        var worst = 0.0;
        var frame = 1 / Timecode.FramesPerSecond(rate);
        for (long at = 0; at < 6 * Rate; at += block) {
            var a = start + (double)at / Rate;
            var count = encoder.Process(true, a, a + (double)block / Rate, block, events);
            foreach (var e in events.AsSpan(0, count)) {
                var time = (long)((at + e.Frame) * 1e9 / Rate);
                if (decoder.Receive([e.Status, e.Data1], time) is not { } position) {
                    continue;
                }

                positions++;
                var playhead = start + time / 1e9;
                worst = Math.Max(worst, Math.Abs(position.Seconds - playhead));
                Assert.Equal(rate, decoder.Rate);
            }
        }

        Assert.True(positions >= (int)(6 / frame / 2) - 2, $"{positions} positions");
        Assert.True(worst < frame / 4, $"off by {worst / frame:0.###} frames"); // well within one
        Assert.True(decoder.Running);
        Assert.True(decoder.Check(6L * 1_000_000_000 + (long)(3e9 * frame))); // stopped
    }

    [Theory]
    [MemberData(nameof(Rates))]
    public void A_full_frame_carries_the_position_and_the_rate(EngineMtcRate rate) {
        var encoder = new MtcEncoder(rate);
        var bytes = new byte[10];
        Assert.Equal(10, encoder.FullFrame(3723.5, bytes)); // 01:02:03 and a half
        Assert.Equal([0xF0, 0x7F, 0x7F, 0x01, 0x01], bytes[..5]);
        Assert.Equal(0xF7, bytes[9]);
        Assert.Equal((Timecode.RateCode(rate) << 5) | 1, bytes[5]);
        Assert.Equal((2, 3), (bytes[6], bytes[7]));
        var position = new MtcDecoder(EngineMtcRate.Fps24).Receive(bytes, 5)!.Value;
        Assert.True(position.FullFrame);
        Assert.InRange(position.Seconds, 3723.5 - 1 / Timecode.FramesPerSecond(rate), 3723.5);
    }

    [Fact]
    public void Drop_frame_labels_skip_two_frames_at_minutes_not_divisible_by_ten() {
        const EngineMtcRate drop = EngineMtcRate.Fps2997Drop;
        Assert.Equal(new(0, 0, 59, 29), Timecode.Label(1799, drop));
        Assert.Equal(new(0, 1, 0, 2), Timecode.Label(1800, drop));
        Assert.Equal(new(0, 10, 0, 0), Timecode.Label(17_982, drop));
        Assert.Equal(new(0, 9, 59, 29), Timecode.Label(17_981, drop));
        var random = new Random(8);
        for (var i = 0; i < 10_000; i++) {
            var count = random.NextInt64(24L * 107_892);
            Assert.Equal(count, Timecode.Count(Timecode.Label(count, drop), drop));
        }
    }

    // ---- Through the engine ------------------------------------------------------------------------------------------

    /// <summary>
    ///     An engine whose callbacks start on a clock the test drives: every block, plus up to the
    ///     wake-up jitter it is given.
    /// </summary>
    sealed class Rig : IDisposable {
        readonly Random random = new(3);
        long ideal = 1_000_000_000_000;
        long now;

        public Rig(int block, long jitterNs, FakeMidi? midi = null) {
            Block = block;
            Controller = new(new(Rate, block), null, midi);
            Driver = new(Controller, block);
            Controller.Engine.HostClock = () => now;
            Jitter = jitterNs;
        }

        public int Block { get; }

        public long Jitter { get; }

        public EngineController Controller { get; }

        public OfflineDriver Driver { get; }

        public List<OutgoingSync> Out { get; } = [];

        public void Run(int callbacks) {
            for (var i = 0; i < callbacks; i++) {
                now = ideal + (Jitter == 0 ? 0 : random.NextInt64(-Jitter, Jitter + 1));
                Driver.Process();
                ideal += (long)(Block * 1e9 / Rate);
                while (Controller.Engine.SyncOut.TryDequeue(out var e)) {
                    Out.Add(e);
                }
            }
        }

        public void Dispose() {
            Driver.Dispose();
            Controller.Dispose();
        }
    }

    static SetSyncMessage Sync(params EngineSyncDestination[] destinations) =>
        new(EngineSyncSource.Internal, destinations, EngineMtcRate.Fps25, 0, false, null, null);

    [Theory]
    [InlineData(64, 0)]
    [InlineData(64, 300_000)]
    [InlineData(256, 1_000_000)]
    [InlineData(1024, 2_000_000)]
    public void Clock_out_is_even_on_the_smoothed_callback_clock(int block, long jitterNs) {
        using var rig = new Rig(block, jitterNs);
        rig.Controller.SetSync(Sync(new EngineSyncDestination("out-a", true, false, 0)));
        rig.Run(50); // the smoothed clock settles
        rig.Driver.Send(new(EngineCommandKind.Play));
        rig.Run(10 * Rate / block);
        var clocks = rig.Out.Where(static e => e.Kind == SyncMessageKind.Clock).ToList();
        Assert.Equal(0xFA, clocks[0].B0);
        var ticks = clocks.Where(static e => e.B0 == 0xF8).Select(static e => e.TimeNs).ToList();
        var intervals = ticks.Zip(ticks.Skip(1), static (a, b) => (b - a) / 1e6).ToList();
        var worst = intervals.Max(static i => Math.Abs(i - 20.833333));
        Assert.True(worst < 0.5, $"clock jitter {worst:0.000} ms with {jitterNs / 1e6} ms of wake-up jitter");
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"block {block}, wake-up jitter ±{jitterNs / 1e6} ms: clock spacing within {worst * 1000:0.0} µs of 20.833 ms"
        );
        Assert.InRange(ticks.Count, 470, 481);
    }

    [Fact]
    public void A_locate_sends_the_pointer_and_a_full_frame_and_mtc_follows_the_playhead() {
        using var rig = new Rig(128, 0);
        rig.Controller.SetSync(
            new(
                EngineSyncSource.Internal,
                [new("out-a", true, true, 0)],
                EngineMtcRate.Fps25,
                3600,
                false,
                null,
                null
            )
        );
        rig.Run(4);
        rig.Driver.Send(new(EngineCommandKind.Locate, 2 * Rate + Rate / 4)); // 2.25 s: 4.5 beats at 120
        rig.Run(2);
        var clock = rig.Out.Where(static e => e.Kind == SyncMessageKind.Clock).ToList();
        var pointer = Assert.Single(clock);
        Assert.Equal((0xF2, 18, 0), (pointer.B0, pointer.B1, pointer.B2)); // 18 sixteenths
        var full = Assert.Single(rig.Out, static e => e.Length == 10);
        Assert.Equal((1, 0, 2, 6), (full.B0 & 0x1F, full.B1, full.B2, full.B3)); // 01:00:02:06 (6.25 frames)
        Assert.Equal(1, full.B0 >> 5); // 25 fps

        rig.Out.Clear();
        rig.Driver.Send(new(EngineCommandKind.Play));
        rig.Run(2 * Rate / 128);
        Assert.Equal([0xF2, 0xFB], rig.Out.Where(static e => e.Kind == SyncMessageKind.Clock).Take(2).Select(static e => (int)e.B0));
        var decoder = new MtcDecoder(EngineMtcRate.Fps25);
        var first = rig.Out.First(static e => e.Kind == SyncMessageKind.Mtc).TimeNs;
        var checkedPositions = 0;
        foreach (var e in rig.Out.Where(static e => e.Kind == SyncMessageKind.Mtc && e.Length == 2)) {
            if (decoder.Receive([e.B0, e.B1], e.TimeNs) is { } position) {
                var playhead = 3600 + 2.25 + (e.TimeNs - first) / 1e9;
                Assert.InRange(position.Seconds - playhead, -0.04, 0.04); // within a frame
                checkedPositions++;
            }
        }

        Assert.True(checkedPositions > 10);
        rig.Out.Clear();
        rig.Driver.Send(new(EngineCommandKind.Stop));
        rig.Run(2);
        Assert.Equal(0xFC, Assert.Single(rig.Out, static e => e.Kind == SyncMessageKind.Clock).B0);
        Assert.DoesNotContain(rig.Out, static e => e.Kind == SyncMessageKind.Mtc);
    }

    [Fact]
    public void The_midi_thread_sends_each_stream_to_its_destinations_with_their_delays() {
        var midi = new FakeMidi { SchedulesOutput = true };
        midi.SetPorts([new("out-a", "A", false), new("out-b", "B", false)]);
        using var rig = new Rig(64, 0, midi);
        rig.Controller.SetSync(Sync(new EngineSyncDestination("out-a", true, false, 5), new EngineSyncDestination("out-b", false, true, -3)));
        rig.Driver.Send(new(EngineCommandKind.Play));
        rig.Run(4);
        foreach (var e in rig.Out) {
            Assert.True(rig.Controller.Engine.SyncOut.TryEnqueue(e)); // hand them to the MIDI thread
        }

        rig.Controller.Midi.Pump();
        var sent = midi.SentNow();
        var clock = rig.Out.Where(static e => e.Kind == SyncMessageKind.Clock).ToList();
        var mtc = rig.Out.Where(static e => e.Kind == SyncMessageKind.Mtc).ToList();
        Assert.Equal(clock.Select(static e => e.TimeNs + 5_000_000), sent.Where(static s => s.Uid == "out-a").Select(static s => s.TimeNs));
        Assert.Equal(mtc.Select(static e => e.TimeNs - 3_000_000), sent.Where(static s => s.Uid == "out-b").Select(static s => s.TimeNs));
        Assert.Equal([0xFA], sent.First(static s => s.Uid == "out-a").Message);
        Assert.All(sent.Where(static s => s.Uid == "out-b"), static s => Assert.Equal(0xF1, s.Message[0]));
    }
}
