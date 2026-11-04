using Anode.Contracts;
using Anode.Ipc.Segments;
using Anode.Model.Compile;
using Anode.Model.Editing;

namespace Anode.Model.Tests;

/// <summary>
///     Region loops: audio, MIDI and pattern regions repeat in the compiled model, cut where the loop ends or the next
///     region starts. At 120 BPM and 48 kHz a tick is 25 samples and a bar 96 000.
/// </summary>
public sealed class LoopTests {
    const long Bar = 3840;
    readonly SequentialIds ids = new();

    ProjectState state = ProjectDefaults.NewProject("prj_1", "Song", 48_000)
        .Apply(new AudioFileUpserted(new("aud_1", "A", "Audio Files/A.wav", 48_000, 1, 480_000, 24, null, 0)));

    void Apply(params EditCommand[] commands) => state = Edits.Apply(state, commands, ids).State;

    string Audio(long start = 0, long length = 96_000) {
        if (state.Tracks.Count == 0) {
            Apply(new AddTrackCommand());
        }

        Apply(new AddRegionCommand(state.Tracks[0].Id, "aud_1", start, 0, length));
        return state.Regions.Values.Single(r => r.StartTick == start).Id;
    }

    string Midi(long start = 0, long length = Bar) {
        if (state.Tracks.Count == 0) {
            Apply(new AddTrackCommand(Kind: TrackKind.Instrument));
        }

        Apply(new AddMidiRegionCommand(state.Tracks[0].Id, start, length));
        return state.MidiRegions.Values.Single(r => r.StartTick == start).Id;
    }

    CompiledModel Model() => new ModelCompiler().Compile(state, EngineIds.Empty.With(state));

    [Fact]
    public void Audio_repeats_end_to_end_and_the_last_repeat_is_cut() {
        var region = Audio();
        Apply(new SetRegionCommand(region, FadeIn: 100, FadeOut: 200, LoopTicks: 2 * Bar + Bar / 2));
        Assert.Equal(
            [
                (0L, 0L, 96_000L, 100, 200), (96_000, 0, 96_000, 100, 200), (192_000, 0, 96_000, 100, 200),
                (288_000, 0, 48_000, 100, 0)
            ],
            Model().Tracks[0].Playlist.Select(static e => (e.Start, e.FileOffset, e.Length, e.FadeIn, e.FadeOut))
        );
    }

    [Fact]
    public void Unwarped_audio_stays_sample_accurate_under_a_changing_tempo() {
        var region = Audio(0, 100_000);
        Apply(new SetTempoEventsCommand([new(0, 120, TempoCurve.Ramp), new(Bar, 140, TempoCurve.Step)]));
        Apply(new SetRegionCommand(region, LoopTicks: Bar));
        var playlist = Model().Tracks[0].Playlist;
        Assert.True(playlist.Length >= 2);
        for (var i = 1; i < playlist.Length; i++) {
            Assert.Equal(playlist[i - 1].Start + playlist[i - 1].Length, playlist[i].Start);
        }
    }

    [Fact]
    public void A_loop_stops_at_the_next_region_on_the_track() {
        var region = Audio();
        Audio(Bar * 2 + Bar / 2, 1000);
        Apply(new SetRegionCommand(region, LoopTicks: Bar * 8));
        Assert.Equal(
            [(0L, 96_000L), (96_000, 96_000), (192_000, 48_000), (240_000, 1000)],
            Model().Tracks[0].Playlist.Select(static e => (e.Start, e.Length))
        );
    }

    [Fact]
    public void Warped_audio_repeats_on_ticks_without_a_gap() {
        var region = Audio(0, 48_000);
        var track = state.Tracks[0].Id;
        Apply(new SetTrackFlexCommand(track, true), new SetRegionCommand(region, LoopTicks: 3840));
        var playlist = Model().Tracks[0].Playlist;
        Assert.Equal([0L, 48_000, 96_000], playlist.Select(static e => e.Start));
        Assert.All(playlist, static e => Assert.Equal(48_000, e.Length));
    }

    [Fact]
    public void Midi_repeats_its_notes_and_cuts_those_past_the_loop() {
        var region = Midi();
        Apply(new AddNotesCommand(region, [new(60, 100, 1, 0, 480), new(62, 100, 1, 2880, 1920)]));
        Apply(new SetMidiRegionCommand(region, LoopTicks: Bar + 3000));
        var events = Model().Tracks[0].Events;
        Assert.Equal(
            [0L, 2880 * 25, Bar * 25, (Bar + 2880) * 25, 2 * Bar * 25, (2 * Bar + 2880) * 25],
            events.Where(static e => e.Kind == PlaybackEventKind.NoteOn).Select(static e => e.SamplePos)
        );
        // The last note starts in the repeat and is cut where the loop ends.
        Assert.Equal(
            (Bar + Bar + 3000) * 25,
            events.Where(static e => e.Kind == PlaybackEventKind.NoteOff).Max(static e => e.SamplePos)
        );
    }

    [Fact]
    public void Midi_repeats_stop_at_the_next_region_and_a_muted_region_plays_no_repeats() {
        var region = Midi();
        Apply(new AddNotesCommand(region, [new(60, 100, 1, 0, 480)]));
        Midi(Bar + Bar / 2);
        Apply(new SetMidiRegionCommand(region, LoopTicks: Bar * 8));
        Assert.Equal(2, Model().Tracks[0].Events.Count(static e => e.Kind == PlaybackEventKind.NoteOn));
        Apply(new SetMidiRegionCommand(region, Muted: true));
        Assert.Empty(Model().Tracks[0].Events);
    }

    [Fact]
    public void Pattern_regions_repeat() {
        Apply(new AddTrackCommand(Kind: TrackKind.Instrument));
        Apply(new AddPatternRegionCommand(state.Tracks[0].Id, 0, Bar));
        var region = state.PatternRegions.Values.Single();
        var row = region.Pattern.Rows[0];
        var steps = row.Steps.ToArray();
        steps[0] = steps[0] with { On = true };
        Apply(
            new SetPatternCommand(region.Id, region.Pattern with { Rows = [row with { Steps = steps }] }),
            new SetPatternRegionCommand(region.Id, LoopTicks: 2 * Bar)
        );
        Assert.Equal(
            [0L, Bar * 25, 2 * Bar * 25],
            Model()
                .Tracks[0]
                .Events.Where(static e => e.Kind == PlaybackEventKind.NoteOn)
                .Select(static e => e.SamplePos)
        );
    }

    [Fact]
    public void Repeats_extend_the_model_and_recompile_the_track() {
        var region = Audio();
        var engine = EngineIds.Empty.With(state);
        var compiler = new ModelCompiler();
        Assert.Equal(96_000, compiler.Compile(state, engine).Tracks[0].Playlist[^1].Length);
        Apply(new SetRegionCommand(region, LoopTicks: Bar));
        var model = compiler.Compile(state, engine);
        Assert.Contains(model.Tracks[0].TrackId, model.Recompiled);
        Assert.Equal(2, model.Tracks[0].Playlist.Length);
    }

    [Fact]
    public void The_played_end_counts_the_repeats() {
        var audio = Audio();
        Apply(new SetRegionCommand(audio, LoopTicks: Bar));
        Assert.Equal(192_000, Loops.EndSample(state, state.Regions[audio]));
    }

    [Fact]
    public void Converting_loops_makes_regions_that_play_the_same() {
        var audio = Audio();
        Apply(new SetRegionCommand(audio, LoopTicks: Bar + Bar / 2));
        var before = Model().Tracks[0].Playlist;
        var result = Edits.Apply(state, new ConvertLoopsCommand([audio]), ids);
        state = result.State;
        Assert.Equal(3, state.Regions.Count);
        Assert.All(state.Regions.Values, static r => Assert.Equal(0, r.LoopTicks));
        Assert.Equal(
            before.Select(static e => (e.Start, e.Length, e.FileOffset)),
            Model().Tracks[0].Playlist.Select(static e => (e.Start, e.Length, e.FileOffset))
        );
        state = state.Apply(result.Inverse);
        Assert.Single(state.Regions);
        Assert.Equal(Bar + Bar / 2, state.Regions[audio].LoopTicks);
    }

    [Fact]
    public void Converting_midi_and_pattern_loops_gives_notes_ids_of_their_own() {
        var region = Midi();
        Apply(new AddNotesCommand(region, [new(60, 100, 1, 0, 480), new(62, 100, 1, 2880, 480)]));
        Apply(new SetMidiRegionCommand(region, LoopTicks: Bar + Bar / 2));
        Apply(new ConvertLoopsCommand([region]));
        Assert.Equal(3, state.MidiRegions.Count);
        var notes = state.MidiRegions.Values.SelectMany(static r => r.Notes).Select(static n => n.Id).ToArray();
        Assert.Equal(notes.Length, notes.Distinct().Count());
        Assert.Equal(
            [Bar, Bar, Bar / 2],
            state.MidiRegions.Values.OrderBy(static r => r.StartTick).Select(static r => r.LengthTicks)
        );
    }

    [Fact]
    public void A_looped_region_is_not_split_or_joined() {
        var region = Audio();
        var other = Audio(Bar);
        Apply(new SetRegionCommand(region, LoopTicks: Bar));
        Assert.Contains(
            "loops",
            Assert.Throws<EditException>(() => Apply(new SplitRegionCommand(region, Bar / 2))).Message
        );
        Assert.Contains(
            "loops",
            Assert.Throws<EditException>(() => Apply(new JoinRegionsCommand([region, other]))).Message
        );
    }

    [Fact]
    public void A_loop_is_not_negative_and_a_piece_does_not_loop() {
        var region = Audio();
        Assert.Throws<EditException>(() => Apply(new SetRegionCommand(region, LoopTicks: -1)));
        Apply(new SetRegionCommand(region, LoopTicks: Bar));
        var kept = state.Regions[region];
        Assert.Equal(0, Edits.Piece(kept, 0, 1000, 0).LoopTicks);
    }

    [Fact]
    public void A_pattern_converted_to_midi_keeps_its_loop() {
        Apply(new AddTrackCommand(Kind: TrackKind.Instrument));
        Apply(new AddPatternRegionCommand(state.Tracks[0].Id, 0, Bar));
        var region = state.PatternRegions.Values.Single().Id;
        Apply(new SetPatternRegionCommand(region, LoopTicks: Bar), new ConvertPatternToMidiCommand(region));
        Assert.Equal(Bar, state.MidiRegions.Values.Single().LoopTicks);
    }
}
