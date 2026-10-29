using Anode.Dsp.MidiEffects;
using Anode.Ipc;
using Anode.Ipc.Segments;

namespace Anode.Engine.Tests;

/// <summary>
///     MIDI FX slots (docs/07-phase-6.md, "MIDI FX") and the Drum Machine Designer's live routing: the slots run in
///     order on the track's MIDI (regions, live input, cells) before the instrument; a bypassed slot passes MIDI
///     through; parameters change and automate (<c>midiEffect</c> targets); a structure change keeps the instances by
///     slot id; a DMD track's live notes reach its pads by note, and recording it captures them as its own.
/// </summary>
public sealed class MidiEffectTests : IDisposable {
    const int Rate = 48_000;
    const int Block = 64;
    readonly string dir = TestAudio.TempDirectory();
    readonly List<IMidiEffect> created = [];
    readonly EngineController controller;
    readonly OfflineDriver driver;

    public MidiEffectTests() {
        controller = new(new(Rate, Block), TestMidiEffects.Registry(created));
        driver = new(controller, Block);
    }

    public void Dispose() {
        driver.Dispose();
        controller.Dispose();
        Directory.Delete(dir, true);
    }

    static Dictionary<string, double> Shift(int semitones) => new() { ["semitones"] = semitones };

    static Dictionary<string, double> Limit(int note) => new() { ["limit"] = note };

    static EngineStrip Strip(params EngineInsert[] midiEffects) => TestAudio.Unity with { MidiEffects = midiEffects };

    static EngineTrack Track(int id, EngineStrip strip) => MidiFixtures.InstrumentTrack(id) with { Strip = strip };

    void Load(EngineTrack[] tracks, TrackModel[] models, EngineAutomationTarget[]? targets = null, long version = 1) {
        using var segment = TestAudio.Segment(models, version);
        controller.LoadModel(
            TestAudio.Load(segment, version, true, tracks, [], dir) with { AutomationTargets = targets }
        );
    }

    TestInstrument InstrumentOf(int trackId) =>
        (TestInstrument)controller.Engine.Root.Tracks[controller.Engine.Root.IndexOf(trackId)].Instrument!.Instrument;

    /// <summary>Plays the first <paramref name="frames" /> frames; returns the note-ons the instrument heard, by sample.</summary>
    List<(long Sample, int Note)> Play(int trackId, int frames, long from = 0) {
        var instrument = InstrumentOf(trackId);
        driver.PlayFrom(from);
        var first = instrument.Rendered;
        for (var i = 0; i < frames / Block; i++) {
            driver.Process();
        }

        return NoteOns(instrument, first, from);
    }

    static List<(long Sample, int Note)> NoteOns(TestInstrument instrument, long first, long from) => [
        ..instrument.Log.Take(instrument.Logged)
            .Where(e => e.At >= first && (e.Event.Status & 0xF0) == 0x90 && e.Event.Data2 > 0)
            .Select(e => (from + e.At - first, (int)e.Event.Data1))
    ];

    static TrackModel Notes(int trackId) => new(trackId, MidiFixtures.Notes((1_000, 500, 60), (3_000, 500, 64)), []);

    [Fact]
    public void Slots_run_in_order_before_the_instrument_and_keep_their_instances_when_moved() {
        var transpose = TestMidiEffects.Slot("t", TestMidiEffects.Transposer, Shift(12));
        var filter = TestMidiEffects.Slot("f", TestMidiEffects.Filter, Limit(71));
        Load([Track(1, Strip(transpose, filter))], [Notes(1)]);
        // Up an octave, then the filter drops everything above 71: 60 → 72 (dropped), 64 → 76 (dropped).
        Assert.Empty(Play(1, 4_096));
        Assert.Equal(2, created.Count);

        // The filter first: 60 and 64 pass, then go up an octave.
        Assert.True(controller.SetChannel(1, Strip(filter, transpose)));
        controller.Stop();
        Assert.Equal([(1_000L, 72), (3_000L, 76)], Play(1, 4_096));
        Assert.Equal(2, created.Count); // the same instances, moved
        Assert.All(created, static e => Assert.False(((TestMidiEffect)e).Disposed));

        // Removing a slot disposes its instance once nothing plays it.
        Assert.True(controller.SetChannel(1, Strip(filter)));
        Assert.True(((TestMidiEffect)created[0]).Disposed);
        controller.Stop();
        Assert.Equal([(1_000L, 60), (3_000L, 64)], Play(1, 4_096));
    }

    [Fact]
    public void A_bypassed_slot_passes_midi_through_and_an_unknown_plugin_is_reported_and_passes_too() {
        var problems = new List<PluginProblemMessage>();
        controller.PluginProblem = problems.Add;
        Load(
            [
                Track(
                    1,
                    Strip(
                        TestMidiEffects.Slot("t", TestMidiEffects.Transposer, Shift(5), true),
                        TestMidiEffects.Slot("x", "no.such.effect"),
                        TestMidiEffects.Slot("d", TestMidiEffects.Doubler)
                    )
                )
            ],
            [Notes(1)]
        );
        Assert.Equal([(1_000L, 60), (1_000L, 72), (3_000L, 64), (3_000L, 76)], Play(1, 4_096));
        Assert.Equal(("x", "no.such.effect"), (problems.Single().InsertId, problems.Single().PluginId));
    }

    [Fact]
    public void Parameters_change_by_slot_id_and_automate_through_midi_effect_targets() {
        var transpose = TestMidiEffects.Slot("t", TestMidiEffects.Transposer, Shift(0));
        // The transposer's semitones are automated: 0 until 2,000, then 7.
        var lane = new[] { Automation(0, 0, 0f), Automation(2_000, 0, 7f, true) };
        Load(
            [Track(1, Strip(transpose))],
            [new(1, [..Notes(1).Events.Concat(lane).OrderBy(static e => e.SamplePos)], [])],
            [new(0, 1, "midiEffect", "t", "semitones")]
        );
        Assert.Equal([(1_000L, 60), (3_000L, 71)], Play(1, 4_096));

        // A change by the slot's id (as an insert's) holds the parameter against its lane until the next play.
        Assert.True(controller.SetInsertParameter(1, "t", "semitones", -12));
        var instrument = InstrumentOf(1);
        instrument.ClearLog();
        Assert.True(controller.LiveMidi(1, [new(0x90, 60, 100)]));
        driver.Process();
        driver.Process();
        Assert.Equal([48], instrument.Events.Where(static e => e.Status == 0x90).Select(static e => (int)e.Data1));
        Assert.False(controller.SetInsertParameter(1, "t", "nope", 1));
        Assert.False(controller.SetInsertParameter(1, "missing", "semitones", 1));
    }

    static PlaybackEvent Automation(long position, int target, float value, bool step = false) =>
        new() {
            SamplePos = position,
            Track = 1,
            Kind = PlaybackEventKind.Automation,
            Data1 = step ? (byte)1 : (byte)0,
            Value = value,
            Target = target
        };

    [Fact]
    public void Live_midi_goes_through_the_chain_and_stop_resets_the_effects() {
        Load([Track(1, Strip(TestMidiEffects.Slot("d", TestMidiEffects.Doubler)))], [new(1, [], [])]);
        driver.Process();
        Assert.True(controller.LiveMidi(1, [new(0x90, 50, 100)]));
        driver.Process();
        driver.Process();
        Assert.Equal(
            [50, 62],
            InstrumentOf(1).Events.Where(static e => e.Status == 0x90).Select(static e => (int)e.Data1)
        );
        var doubler = (TestMidiEffect)created.Single();
        var resets = doubler.Resets;
        driver.Send(new(EngineCommandKind.Stop));
        driver.Process();
        Assert.Equal(resets + 1, doubler.Resets);
    }

    [Fact]
    public void Each_effect_gets_the_block_transport_and_its_output_is_sorted_and_kept_inside_the_block() {
        Load(
            [
                Track(
                    1,
                    Strip(
                        TestMidiEffects.Slot("p", TestMidiEffects.Probe),
                        TestMidiEffects.Slot("d", TestMidiEffects.Doubler)
                    )
                )
            ],
            [Notes(1)]
        );
        // The doubler writes its octaves after the originals; the instrument gets them sorted by frame.
        Assert.Equal([(1_000L, 60), (1_000L, 72)], Play(1, 1_024, 640));
        var probe = (ProbeMidiEffect)created[0];
        var playing = probe.Contexts.Take(probe.Count).Where(static c => c.Playing).ToList();
        Assert.Equal(640, playing[0].Sample);
        Assert.All(playing, static c => Assert.Equal(Block, c.Frames));
        Assert.All(playing.Zip(playing.Skip(1)), static p => Assert.Equal(p.First.Sample + Block, p.Second.Sample));
        Assert.Equal(640 / 24_000.0, playing[0].Quarter, 12);
    }

    [Fact]
    public void A_drum_machine_routes_live_notes_to_its_pads_and_channel_messages_to_all() {
        Load(
            [
                new(10, -1, false, false, false, TestAudio.Unity, NoteRoutes: [new(36, 11), new(38, 12)]),
                MidiFixtures.InstrumentTrack(11), MidiFixtures.InstrumentTrack(12)
            ],
            [new(10, [], []), new(11, [], []), new(12, [], [])]
        );
        driver.Process();
        Assert.True(
            controller.LiveMidi(10, [new(0x90, 36, 100), new(0x90, 38, 90), new(0x90, 40, 80), new(0xB0, 64, 127)])
        );
        driver.Process();
        driver.Process();
        Assert.Equal(
            [(0x90, 36), (0xB0, 64)],
            InstrumentOf(11).Events.Select(static e => ((int)e.Status, (int)e.Data1))
        );
        Assert.Equal(
            [(0x90, 38), (0xB0, 64)],
            InstrumentOf(12).Events.Select(static e => ((int)e.Status, (int)e.Data1))
        );
    }

    [Fact]
    public async Task Recording_a_drum_machine_captures_its_routed_notes_as_its_own() {
        Load(
            [
                new(10, -1, false, true, false, TestAudio.Unity, NoteRoutes: [new(36, 11)]),
                MidiFixtures.InstrumentTrack(11)
            ],
            [new(10, [], []), new(11, [], [])]
        );
        using var stop = new CancellationTokenSource();
        var callbacks = Task.Run(
            async () => {
                while (!stop.IsCancellationRequested) {
                    driver.Process();
                    await Task.Delay(1, CancellationToken.None);
                }
            },
            TestContext.Current.CancellationToken
        );
        try {
            await controller.RecordAsync(48_000, TestContext.Current.CancellationToken);
            await WaitFor(() => controller.Engine.Snapshot.Position > 48_000 + 1_000);
            Assert.True(controller.LiveMidi(10, [new(0x90, 36, 100)]));
            await WaitFor(() => InstrumentOf(11).Events.Any());
            Assert.True(controller.LiveMidi(10, [new(0x80, 36, 0)]));
            await WaitFor(() => InstrumentOf(11).Events.Count() == 2);
            var result = controller.Stop();
            Assert.NotNull(result);
            Assert.Empty(result.Takes); // no audio take for the drum machine
            var take = Assert.Single(result.MidiTakes);
            Assert.Equal(10, take.TrackId);
            Assert.Equal([(0x90, 36), (0x80, 36)], take.Events.Select(static e => (e.Status, e.Data1)));
        } finally {
            await stop.CancelAsync();
            await callbacks;
        }
    }

    static async Task WaitFor(Func<bool> condition) {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition()) {
            Assert.True(DateTime.UtcNow < deadline, "condition not met in time");
            await Task.Delay(2, TestContext.Current.CancellationToken);
        }
    }
}
