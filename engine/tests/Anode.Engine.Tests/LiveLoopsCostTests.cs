using Anode.Ipc;
using Anode.Ipc.Segments;
using System.Diagnostics;

namespace Anode.Engine.Tests;

/// <summary>
///     What Phase 6 costs the audio callback (docs/07-phase-6.md, workstream A's tests): 32 instrument tracks, each with
///     three test MIDI effects on dense region MIDI, and 16 playing Live Loops cells (8 MIDI, 8 audio from memory), at
///     64 frames, against the same session without MIDI FX and cells. The instruments are silent probes, so the figures
///     are the engine's own work. Reported, with a loose bound (the callback fits its budget).
/// </summary>
public sealed class LiveLoopsCostTests : IDisposable {
    const int Rate = 48_000;
    const int Block = 64;
    readonly string dir = TestAudio.TempDirectory();

    public void Dispose() => Directory.Delete(dir, true);

    (double Mean, double P99, double Max) Measure(bool phase6) {
        var mono = TestAudio.Write24(dir, $"loop-{phase6}.wav", 1, 2 * Rate, 1);
        var models = new List<TrackModel>();
        var tracks = new List<EngineTrack>();
        var cells = new List<CellModel>();
        Dictionary<string, double> shift = new() { ["semitones"] = 3 };
        Dictionary<string, double> limit = new() { ["limit"] = 120 };
        for (var t = 0; t < 32; t++) {
            // A note every 1,500 frames (a sixteenth at 120 bpm is 6,000), two sounding at once.
            var notes = MidiFixtures.Notes(
                [..Enumerable.Range(0, 400).Select(n => (n * 1_500L, 3_000L, 40 + (n * 7 + t) % 48))]
            );
            models.Add(new(t, notes, []));
            var active = phase6 && t >= 24;
            tracks.Add(
                MidiFixtures.InstrumentTrack(t, MidiFixtures.Instrument(ProbeInstrument.Id)) with {
                    LiveLoopsActive = active,
                    Strip = phase6
                        ? TestAudio.Unity with {
                            MidiEffects = [
                                TestMidiEffects.Slot("t", TestMidiEffects.Transposer, shift),
                                TestMidiEffects.Slot("d", TestMidiEffects.Doubler),
                                TestMidiEffects.Slot("f", TestMidiEffects.Filter, limit)
                            ]
                        }
                        : TestAudio.Unity
                }
            );
            if (active) {
                cells.Add(new(t, t, CellMode.Loop, 48_000, [..notes.Where(static e => e.SamplePos < 47_000)], []));
            }
        }

        for (var t = 32; t < 40; t++) {
            models.Add(new(t, [], []));
            tracks.Add(TestAudio.Track(t) with { LiveLoopsActive = phase6 });
            if (phase6) {
                cells.Add(new(t, t, CellMode.Loop, 72_000, [], [TestAudio.Region(0, 72_000, 0, t * 100L)]));
            }
        }

        using var segment = TestAudio.Segment(
            models,
            1,
            tempo: [TestAudio.Tempo(0, 0, 120), TestAudio.Tempo(96_000, 4, 120, 2)],
            cells: cells
        );
        using var controller = new EngineController(new(Rate, Block), TestMidiEffects.Registry());
        controller.LoadModel(TestAudio.Load(segment, 1, true, tracks, [new(0, mono, 1, Rate, 2 * Rate)], dir));
        using var driver = new OfflineDriver(controller, Block);
        driver.PlayFrom(0);
        if (phase6) {
            controller.LaunchCells([..cells.Select(static c => new CellLaunch(c.Index, EngineQuantize.Off))]);
        }

        var times = new List<double>();
        for (var i = 0; i < 6_000; i++) {
            if (i % 64 == 0) {
                driver.Pump();
            }

            var start = Stopwatch.GetTimestamp();
            driver.Process(false);
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
            if (i >= 1_000) {
                times.Add(elapsed);
            }
        }

        if (phase6) {
            controller.PumpCellStates();
            Assert.Equal(16, controller.Engine.Root.Tracks.Count(static t => t.Player.Playing >= 0));
        }

        times.Sort();
        return (times.Average(), times[(int)(times.Count * 0.99)], times[^1]);
    }

    [Fact]
    public void Callback_cost_of_32_tracks_of_midi_fx_and_16_playing_cells() {
        var baseline = Measure(false);
        var phase6 = Measure(true);
        var budget = Block * 1e6 / Rate;
        var message =
            $"callback at {Block} frames ({budget:0} µs): 32 instrument tracks of region MIDI {baseline.Mean:0.0} µs mean, "
            + $"{baseline.P99:0.0} µs p99, {baseline.Max:0} µs max; with 3 MIDI FX each and 16 playing cells (8 MIDI, 8 audio) "
            + $"{phase6.Mean:0.0} µs mean ({100 * phase6.Mean / budget:0.0} % of the budget), {phase6.P99:0.0} µs p99, "
            + $"{phase6.Max:0} µs max";
        TestContext.Current.SendDiagnosticMessage(message);
        Console.WriteLine(message);
        Assert.True(phase6.Mean < budget, message);
    }
}
