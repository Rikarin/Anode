using Anode.Contracts;
using Anode.Ipc.Segments;
using Anode.Model.Compile;
using Anode.Model.Editing;

namespace Anode.Model.Tests;

/// <summary>
///     MIDI regions compiled to events (docs/03-phase-2.md, "Compilation"). At 120 BPM and 48 kHz a tick is 25
///     samples: a beat (960 ticks) is 24 000.
/// </summary>
public sealed class MidiCompilerTests {
    readonly SequentialIds ids = new();
    ProjectState state = ProjectDefaults.NewProject("prj_1", "Song", 48_000);

    [Fact]
    public void Notes_become_note_ons_and_offs_in_samples_on_channels_0_to_15() {
        var region = Region(960, 3840);
        Apply(new AddNotesCommand(region, [new(60, 100, 2, 0, 480), new(72, 90, 16, 960, 240)]));
        Assert.Equal(
            [
                (24_000L, PlaybackEventKind.NoteOn, 60, 100, 1), (36_000, PlaybackEventKind.NoteOff, 60, 0, 1),
                (48_000, PlaybackEventKind.NoteOn, 72, 90, 15), (54_000, PlaybackEventKind.NoteOff, 72, 0, 15)
            ],
            Events().Select(static e => (e.SamplePos, e.Kind, (int)e.Data1, (int)e.Data2, (int)e.Channel))
        );
        Assert.All(Events(), e => Assert.Equal(Engine(), e.Track));
        Assert.Empty(Model().Tracks[0].Playlist);
    }

    [Fact]
    public void Transpose_and_velocity_offset_are_clamped() {
        var region = Region(0, 3840);
        Apply(new AddNotesCommand(region, [new(60, 100, 1, 0, 480), new(10, 20, 1, 960, 480)]));
        Apply(new SetMidiRegionCommand(region, Transpose: 80, Velocity: 40));
        Assert.Equal([(127, 127), (90, 60)], NoteOns().Select(static e => ((int)e.Data1, (int)e.Data2)));
        Apply(new SetMidiRegionCommand(region, Transpose: -20, Velocity: -127));
        Assert.Equal([(40, 1), (0, 1)], NoteOns().Select(static e => ((int)e.Data1, (int)e.Data2)));
    }

    [Fact]
    public void Region_quantise_moves_starts_by_the_strength_and_swing_delays_every_second_step() {
        var region = Region(0, 3840 * 2);
        Apply(new AddNotesCommand(region, [new(60, 100, 1, 100, 480), new(62, 100, 1, 700, 480)]));
        Apply(new SetMidiRegionCommand(region, QuantizeTicks: 480, QuantizeStrength: 1));
        Assert.Equal([0L, 480 * 25], NoteOns().Select(static e => e.SamplePos));
        // The note keeps its length from where it now starts.
        Assert.Equal(480 * 25, Events().First(static e => e.Kind == PlaybackEventKind.NoteOff).SamplePos);

        Apply(new SetMidiRegionCommand(region, QuantizeStrength: 0.5));
        Assert.Equal([50L * 25, 590 * 25], NoteOns().Select(static e => e.SamplePos));

        // Full swing puts step 1 at 480 + 240: the note at 700 goes there.
        Apply(new SetMidiRegionCommand(region, QuantizeStrength: 1, Swing: 1));
        Assert.Equal([0L, 720 * 25], NoteOns().Select(static e => e.SamplePos));
        Assert.Equal(700, state.MidiRegions[region].Notes[1].StartTick); // non-destructive
    }

    [Fact]
    public void Notes_are_cut_at_the_region_end_and_nothing_outside_it_plays() {
        var region = Region(1920, 960);
        Apply(
            new AddNotesCommand(
                region,
                [new(60, 100, 1, 0, 480), new(61, 100, 1, 800, 400), new(62, 100, 1, 960, 100)]
            ),
            new AddControlsCommand(region, [new(ControlKind.ControlChange, 1, 7, 100, 1000)])
        );
        // A note before the start, after a resize that moved the start later.
        Apply(new ResizeMidiRegionCommand(region, 2400, 480));
        // Left: the note at 1920 (now before the start) and the one at 2720 (the end) do not play; the one at
        // 2720 … 3120 is cut at 2880.
        Assert.Equal(
            [(2720L * 25, PlaybackEventKind.NoteOn, 61), (2880 * 25, PlaybackEventKind.NoteOff, 61)],
            Events().Select(static e => (e.SamplePos, e.Kind, (int)e.Data1))
        );
    }

    [Fact]
    public void Where_regions_overlap_the_later_one_plays_and_the_earlier_resumes_after_it() {
        Apply(new AddTrackCommand(Kind: TrackKind.Instrument));
        var track = state.Tracks[0].Id;
        var a = Region(0, 3840, track);
        var b = Region(1440, 960, track);
        Apply(
            new AddNotesCommand(a, [new(60, 100, 1, 0, 1920), new(61, 100, 1, 1920, 100), new(62, 100, 1, 3000, 100)]),
            new AddNotesCommand(b, [new(70, 100, 1, 0, 2000)])
        );
        Assert.Equal(
            [
                (0L, PlaybackEventKind.NoteOn, 60), (1440 * 25, PlaybackEventKind.NoteOff, 60),
                (1440 * 25, PlaybackEventKind.NoteOn, 70), (2400 * 25, PlaybackEventKind.NoteOff, 70),
                (3000 * 25, PlaybackEventKind.NoteOn, 62), (3100 * 25, PlaybackEventKind.NoteOff, 62)
            ],
            Events().Select(static e => (e.SamplePos, e.Kind, (int)e.Data1))
        );
    }

    [Fact]
    public void At_equal_samples_note_offs_come_first_then_controllers_then_note_ons() {
        var region = Region(0, 3840);
        Apply(
            new AddNotesCommand(region, [new(60, 100, 1, 480, 480), new(60, 100, 1, 0, 480)]),
            new AddControlsCommand(region, [new(ControlKind.ProgramChange, 1, 0, 9, 480)])
        );
        Assert.Equal(
            [
                PlaybackEventKind.NoteOn, PlaybackEventKind.NoteOff, PlaybackEventKind.ProgramChange,
                PlaybackEventKind.NoteOn, PlaybackEventKind.NoteOff
            ],
            Events().Select(static e => e.Kind)
        );
        var events = Events();
        Assert.Equal(events[1].SamplePos, events[3].SamplePos);
        Assert.True(events.Zip(events.Skip(1)).All(static p => p.First.SamplePos <= p.Second.SamplePos));
    }

    [Fact]
    public void Controller_events_carry_their_data_bytes() {
        var region = Region(0, 3840);
        Apply(
            new AddControlsCommand(
                region,
                [
                    new(ControlKind.ControlChange, 3, 64, 127, 0), new(ControlKind.PitchBend, 1, 0, -8192, 10),
                    new(ControlKind.PitchBend, 1, 0, 0, 20), new(ControlKind.PitchBend, 1, 0, 8191, 30),
                    new(ControlKind.ChannelPressure, 1, 0, 33, 40), new(ControlKind.ProgramChange, 1, 0, 5, 50)
                ]
            )
        );
        Assert.Equal(
            [
                (PlaybackEventKind.ControlChange, 64, 127, 2), (PlaybackEventKind.PitchBend, 0, 0, 0),
                (PlaybackEventKind.PitchBend, 0, 64, 0), (PlaybackEventKind.PitchBend, 127, 127, 0),
                (PlaybackEventKind.ChannelPressure, 33, 0, 0), (PlaybackEventKind.ProgramChange, 5, 0, 0)
            ],
            Events().Select(static e => (e.Kind, (int)e.Data1, (int)e.Data2, (int)e.Channel))
        );
    }

    [Fact]
    public void A_MIDI_edit_recompiles_only_its_track_and_external_MIDI_tracks_compile_too() {
        Apply(
            new AddTrackCommand(Kind: TrackKind.Instrument),
            new AddTrackCommand(Kind: TrackKind.Midi),
            new AddTrackCommand()
        );
        var (inst, ext) = (state.Tracks[0].Id, state.Tracks[1].Id);
        var a = Region(0, 3840, inst);
        var b = Region(0, 3840, ext);
        Apply(new AddNotesCommand(a, [new(60, 100, 1, 0, 480)]), new AddNotesCommand(b, [new(48, 100, 1, 0, 960)]));
        var compiler = new ModelCompiler();
        var engine = EngineIds.Empty.With(state);
        var first = compiler.Compile(state, engine);
        Assert.Equal(2, first.Tracks[1].Events.Length);

        Apply(new EditNotesCommand(b, [new(state.MidiRegions[b].Notes[0].Id, Velocity: 20)]));
        var second = compiler.Compile(state, engine);
        Assert.Equal([engine.Track(ext)], second.Recompiled);
        Assert.Same(first.Tracks[0], second.Tracks[0]);
        Assert.Equal(20, second.Tracks[1].Events[0].Data2);

        // The parameters of an instrument are no playback data.
        Apply(new SetParameterCommand(inst, "cutoff", 100));
        Assert.Empty(compiler.Compile(state, engine).Recompiled);
        Assert.True(first.LengthSamples >= 24_000);
    }

    string Region(long start, long length, string? track = null) {
        if (track is null) {
            Apply(new AddTrackCommand(Kind: TrackKind.Instrument));
            track = state.Tracks[^1].Id;
        }

        var before = state.MidiRegions.Keys.ToHashSet();
        Apply(new AddMidiRegionCommand(track, start, length));
        return state.MidiRegions.Keys.Single(k => !before.Contains(k));
    }

    int Engine() => EngineIds.Empty.With(state).Track(state.Tracks[0].Id);

    CompiledModel Model() => new ModelCompiler().Compile(state, EngineIds.Empty.With(state));

    PlaybackEvent[] Events() => Model().Tracks[0].Events;

    PlaybackEvent[] NoteOns() => [..Events().Where(static e => e.Kind == PlaybackEventKind.NoteOn)];

    void Apply(params EditCommand[] commands) =>
        state = Edits.Apply(state, commands, ids, RandomProjects.Plugins).State;
}
