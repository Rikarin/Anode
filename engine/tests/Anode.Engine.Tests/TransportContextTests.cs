using Anode.Engine.Timing;
using Anode.Ipc;
using Anode.Ipc.Segments;

namespace Anode.Engine.Tests;

/// <summary>
///     The transport context (docs/07-phase-6.md, "Tempo in the engine"): computed every block from the segment's tempo
///     points, exact over constant and ramped pieces, never spanning a cycle jump; every effect and instrument gets it
///     through <c>SetTransport</c> right before each <c>Process</c>, describing exactly the frames processed (an automated
///     insert or instrument processes 32-frame pieces, each with its own context).
/// </summary>
public sealed unsafe class TransportContextTests : IDisposable {
    const int Rate = 48_000;
    const int Block = 64;
    readonly string dir = TestAudio.TempDirectory();

    public void Dispose() => Directory.Delete(dir, true);

    /// <summary>120 bpm to quarter 4, a ramp of 10 bpm per quarter for 8 quarters, then 200 bpm.</summary>
    static TempoMapPoint[] Ramp() {
        var end = 96_000 + (long)Math.Round(60 / 10.0 * Math.Log(1 + 10 * 8 / 120.0) * Rate);
        var growth = Math.Exp(10 * ((end - 96_000) / (double)Rate) / 60);
        return [
            TestAudio.Tempo(0, 0, 120), TestAudio.Tempo(96_000, 4, 120, 10),
            TestAudio.Tempo(end, 4 + 120 / 10.0 * (growth - 1), 120 * growth)
        ];
    }

    /// <summary>The position and tempo at <paramref name="sample" />, worked out independently of the engine.</summary>
    static (double Quarter, double Bpm) At(TempoMapPoint[] map, long sample) {
        var p = map.Last(m => m.Sample <= sample);
        var seconds = (sample - p.Sample) / (double)Rate;
        if (p.Slope == 0) {
            return (p.Quarter + p.Bpm * seconds / 60, p.Bpm);
        }

        var growth = Math.Exp(p.Slope * seconds / 60);
        return (p.Quarter + p.Bpm / p.Slope * (growth - 1), p.Bpm * growth);
    }

    [Fact]
    public void The_tempo_map_is_exact_over_ramps_and_inverts() {
        var points = Ramp();
        fixed (TempoMapPoint* p = points) {
            var map = new TempoMap(p, points.Length, Rate);
            for (long s = 0; s < 400_000; s += 1_234) {
                var (quarter, bpm) = map.At(s);
                var expected = At(points, s);
                Assert.Equal(expected.Quarter, quarter, 9);
                Assert.Equal(expected.Bpm, bpm, 9);
                Assert.Equal(s, map.SampleAt(quarter), 6);
            }

            // Integrating the tempo numerically over the ramp gives the same position.
            double q = 4;
            for (var s = 96_000; s < 140_000; s++) {
                var b0 = map.At(s).Bpm;
                var b1 = map.At(s + 1).Bpm;
                q += (b0 + b1) / 2 / 60 / Rate;
            }

            Assert.Equal(q, map.QuarterAt(140_000), 6);
        }
    }

    [Fact]
    public void Boundaries_follow_signature_changes() {
        // 4/4 for two bars, then 3/4 from quarter note 8 (sample 192,000), all at 120 bpm.
        TempoMapPoint[] points = [
            TestAudio.Tempo(0, 0, 120), TestAudio.Tempo(192_000, 8, 120, barQuarter: 8, numerator: 3)
        ];
        fixed (TempoMapPoint* p = points) {
            var map = new TempoMap(p, points.Length, Rate);
            static long At(double quarter) => (long)(quarter * 24_000);
            Assert.Equal(At(4), map.NextBoundary(At(1), EngineQuantize.Bar));
            Assert.Equal(At(8), map.NextBoundary(At(5), EngineQuantize.TwoBars));
            Assert.Equal(At(11), map.NextBoundary(At(9), EngineQuantize.Bar));
            Assert.Equal(At(14), map.NextBoundary(At(9), EngineQuantize.TwoBars)); // counted from the change
            Assert.Equal(At(13), map.NextBoundary(At(11.5), EngineQuantize.Half));
            Assert.Equal(At(14), map.NextBoundary(At(13.2), EngineQuantize.Half)); // the bar's end is a boundary
            Assert.Equal(At(11), map.NextBoundary(At(11), EngineQuantize.Sixteenth)); // on one: that one
            Assert.Equal(12_345, map.NextBoundary(12_345, EngineQuantize.Off));
            var context = map.Context(true, At(12.5), 64);
            Assert.Equal(
                (3, 4, 11.0, 3.0),
                (context.Numerator, context.Denominator, context.BarStartQuarter, context.BarQuarters)
            );
            Assert.Equal(12.5, context.Quarter, 12);
        }

        // No points: 120 bpm in 4/4.
        var empty = new TempoMap(Rate);
        Assert.Equal(new(true, 48_000, 2, 120, 4, 4, 0, Rate, 64), empty.Context(true, 48_000, 64));
    }

    [Fact]
    public void Every_block_gets_the_exact_context_across_a_ramp_and_never_across_a_cycle_jump() {
        var map = Ramp();
        var registry = TestMidiEffects.Registry();
        using var controller = new EngineController(new(Rate, Block), registry);
        using (var segment = TestAudio.Segment([new(1, [], [])], 1, tempo: map)) {
            controller.LoadModel(
                TestAudio.Load(
                    segment,
                    1,
                    true,
                    [
                        MidiFixtures.InstrumentTrack(1, MidiFixtures.Instrument(ProbeInstrument.Id)) with {
                            Strip = TestAudio.Unity with { Inserts = [TestEffects.Insert("p", ProbeAudioEffect.Id)] }
                        }
                    ],
                    [],
                    dir
                )
            );
        }

        using var driver = new OfflineDriver(controller, Block);
        const long start = 100_000, end = 130_030; // inside the ramp, and not a multiple of the block
        driver.Send(new(EngineCommandKind.SetCycle, start, end, true));
        driver.PlayFrom(95_000);
        for (var i = 0; i < 2_000; i++) {
            driver.Process();
        }

        var track = controller.Engine.Root.Tracks[0];
        var instrument = (ProbeInstrument)track.Instrument!.Instrument;
        var insert = (ProbeAudioEffect)track.Channel.Config.Inserts[0].Slot.Effect!;
        var contexts = instrument.Contexts.Take(instrument.Count).Where(static c => c.Playing).ToList();
        Assert.True(contexts.Count > 2_000);
        Assert.Equal(95_000, contexts[0].Sample);
        var wraps = 0;
        for (var i = 0; i < contexts.Count; i++) {
            var c = contexts[i];
            var (quarter, bpm) = At(map, c.Sample);
            Assert.Equal(quarter, c.Quarter, 9);
            Assert.Equal(bpm, c.Bpm, 9);
            Assert.Equal((4, 4), (c.Numerator, c.Denominator));
            Assert.Equal(Math.Floor(quarter / 4) * 4, c.BarStartQuarter, 9);
            Assert.True(c.Sample >= end || c.Sample + c.Frames <= end, $"block {c.Sample}+{c.Frames} spans the jump");
            if (i + 1 < contexts.Count) {
                var next = c.Sample + c.Frames;
                wraps += next == end ? 1 : 0;
                Assert.Equal(next == end ? start : next, contexts[i + 1].Sample);
            }
        }

        Assert.True(wraps >= 3);
        // The insert got the same contexts, each right before a Process of exactly its frames.
        var calls = insert.Calls.Take(insert.Count).Where(static c => c.Transport.Playing).ToList();
        Assert.Equal(contexts, calls.Select(static c => c.Transport));
        Assert.All(calls, static c => Assert.Equal(c.Transport.Frames, c.Processed));

        // Stopped: not playing, at the playhead.
        driver.Send(new(EngineCommandKind.Stop));
        driver.Process();
        var stopped = instrument.Contexts[instrument.Count - 1];
        Assert.False(stopped.Playing);
        Assert.Equal(Block, stopped.Frames);
    }

    [Fact]
    public void An_automated_insert_gets_a_context_for_each_32_frame_piece() {
        using var controller = new EngineController(new(Rate, Block), TestMidiEffects.Registry());
        var lane = new PlaybackEvent {
            SamplePos = 0,
            Track = 1,
            Kind = PlaybackEventKind.Automation,
            Value = 0.5f,
            Target = 0
        };
        using (var segment = TestAudio.Segment([new(1, [lane], [])], 1, tempo: [TestAudio.Tempo(0, 0, 90)])) {
            controller.LoadModel(
                TestAudio.Load(
                    segment,
                    1,
                    true,
                    [
                        TestAudio.Track(1) with {
                            Strip = TestAudio.Unity with { Inserts = [TestEffects.Insert("p", ProbeAudioEffect.Id)] }
                        }
                    ],
                    [],
                    dir
                ) with {
                    AutomationTargets = [new(0, 1, "insert", "p", "x")]
                }
            );
        }

        using var driver = new OfflineDriver(controller, Block);
        driver.PlayFrom(1_000);
        for (var i = 0; i < 10; i++) {
            driver.Process();
        }

        var insert = (ProbeAudioEffect)controller.Engine.Root.Tracks[0].Channel.Config.Inserts[0].Slot.Effect!;
        var calls = insert.Calls.Take(insert.Count).Where(static c => c.Transport.Playing).ToList();
        Assert.Equal(20, calls.Count);
        for (var i = 0; i < calls.Count; i++) {
            var c = calls[i].Transport;
            Assert.Equal((1_000L + 32 * i, 32, 32), (c.Sample, c.Frames, calls[i].Processed));
            Assert.Equal((1_000 + 32 * i) * 90 / 60.0 / Rate, c.Quarter, 12);
            Assert.Equal(90, c.Bpm);
        }
    }
}
