using Anode.Contracts;
using Anode.Model.Editing;
using Anode.Model.History;

namespace Anode.Model.Tests;

/// <summary>Instrument, MIDI I/O and MIDI region edits (docs/03-phase-2.md): what they do and what they refuse.</summary>
public sealed class MidiEditTests {
    readonly SequentialIds ids = new();

    ProjectState state = ProjectDefaults.NewProject("prj_1", "Song", 48_000)
        .Apply(new AudioFileUpserted(new("aud_1", "Kick", "Audio Files/Kick.wav", 48_000, 1, 96_000, 24, null)));

    [Fact]
    public void A_new_instrument_track_gets_the_synth_with_every_parameter_at_its_default() {
        Apply(
            new AddTrackCommand(Kind: TrackKind.Instrument),
            new AddTrackCommand(Kind: TrackKind.Instrument, PluginId: "com.example.sine"),
            new AddTrackCommand(Kind: TrackKind.Midi),
            new AddTrackCommand()
        );
        Assert.Equal(["Inst 1", "Inst 2", "MIDI 1", "Audio 1"], state.Tracks.Select(static t => t.Name));
        var synth = state.Tracks[0].Instrument!;
        Assert.Equal(("anode.synth", "Synth", null), (synth.PluginId, synth.Name, synth.File));
        Assert.Equal(
            RandomProjects.Plugins.Find("anode.synth")!.Parameters.ToDictionary(
                static p => p.Id,
                static p => p.Default
            ),
            synth.Parameters
        );
        Assert.Equal(["1", "2"], state.Tracks[1].Instrument!.Parameters.Keys);
        Assert.Null(state.Tracks[2].Instrument);
        Assert.Null(state.Tracks[2].MidiOutput);
        Assert.Equal(new(null, 0), state.Tracks[2].MidiInput);
        Assert.Null(state.Tracks[3].Instrument);
    }

    [Fact]
    public void Without_a_catalogue_the_default_synth_starts_without_parameters() {
        state = Edits.Apply(state, new AddTrackCommand(Kind: TrackKind.Instrument), ids).State;
        Assert.Equal("anode.synth", state.Tracks[0].Instrument!.PluginId);
        Assert.Empty(state.Tracks[0].Instrument!.Parameters);
        Assert.Throws<EditException>(() =>
            Edits.Apply(state, new AddTrackCommand(Kind: TrackKind.Instrument, PluginId: "anode.sampler"), ids)
        );
    }

    [Fact]
    public void Changing_the_instrument_resets_its_parameters_and_file() {
        Apply(new AddTrackCommand(Kind: TrackKind.Instrument, PluginId: "anode.sampler"));
        var track = state.Tracks[0].Id;
        Apply(new SetParameterCommand(track, "tune", 50), new LoadInstrumentFileCommand(track, "/kits/Piano.sfz"));
        Assert.Equal("/kits/Piano.sfz", state.Tracks[0].Instrument!.File);
        Apply(new SetInstrumentCommand(track, "anode.synth"));
        var slot = state.Tracks[0].Instrument!;
        Assert.Equal(("anode.synth", null), (slot.PluginId, slot.File));
        Assert.Equal(8_000, slot.Parameters["cutoff"]);
        Apply(new SetInstrumentCommand(track, "anode.sampler"));
        Assert.Equal(0, state.Tracks[0].Instrument!.Parameters["tune"]);
    }

    [Fact]
    public void Discrete_parameters_snap_to_their_steps() {
        Apply(new AddTrackCommand(Kind: TrackKind.Instrument));
        Apply(new SetParameterCommand(state.Tracks[0].Id, "wave", 1.6));
        Assert.Equal(2, state.Tracks[0].Instrument!.Parameters["wave"]);
    }

    [Fact]
    public void Parameter_drags_coalesce_into_one_undo_step_per_parameter() {
        Apply(new AddTrackCommand(Kind: TrackKind.Instrument));
        var track = state.Tracks[0].Id;
        var time = new ManualTime();
        var history = new EditHistory(time);
        var original = RandomProjects.Json(state);
        foreach (var value in new[] { 1000.0, 2000, 3000 }) {
            Push(history, new SetParameterCommand(track, "cutoff", value));
            time.Advance(TimeSpan.FromMilliseconds(300));
        }

        Push(history, new SetParameterCommand(track, "resonance", 0.9));
        Assert.Equal(["Change Parameter", "Change Parameter"], history.History().Undo.Select(static h => h.Label));
        state = state.Apply(history.PeekUndo()!.Inverse);
        history.Undone(0);
        Assert.Equal(3000, state.Tracks[0].Instrument!.Parameters["cutoff"]);
        state = state.Apply(history.PeekUndo()!.Inverse);
        history.Undone(0);
        Assert.Equal(original, RandomProjects.Json(state));
        Assert.Null(UndoLabels.CoalesceKey(new([new SetParameterCommand(track, "a", 0), new SetStripCommand()])));
    }

    [Fact]
    public void Only_an_external_MIDI_track_has_an_output() {
        Apply(new AddTrackCommand(Kind: TrackKind.Instrument), new AddTrackCommand(Kind: TrackKind.Midi));
        var (inst, ext) = (state.Tracks[0].Id, state.Tracks[1].Id);
        Apply(new SetMidiIoCommand(inst, new("port-a", 3)), new SetMidiIoCommand(ext, Output: new("port-b", 10)));
        Assert.Equal(new("port-a", 3), state.Tracks[0].MidiInput);
        Assert.Equal(new("port-b", 10), state.Tracks[1].MidiOutput);
        Assert.Throws<EditException>(() => Apply(new SetMidiIoCommand(inst, Output: new("port-b", 1))));
        Assert.Throws<EditException>(() => Apply(new SetMidiIoCommand(ext, Output: new("port-b", 0))));
    }

    [Fact]
    public void Resizing_a_MIDI_region_keeps_its_notes_where_they_are_in_time() {
        var region = AddRegion(1920, 3840);
        Apply(new AddNotesCommand(region, [new(60, 100, 1, 0, 480), new(62, 100, 1, 960, 480)]));
        Apply(new ResizeMidiRegionCommand(region, 2880, 1920));
        var resized = state.MidiRegions[region];
        Assert.Equal([-960L, 0], resized.Notes.Select(static n => n.StartTick));
        Assert.Equal(
            [1920L, 2880],
            resized.Notes.Select(n => resized.StartTick + n.StartTick)
        );
    }

    [Fact]
    public void Splitting_a_MIDI_region_gives_each_note_to_the_side_it_starts_on() {
        var region = AddRegion(960, 3840);
        Apply(
            new AddNotesCommand(region, [new(60, 100, 1, 0, 1440), new(62, 100, 1, 1920, 480)]),
            new AddControlsCommand(region, [new(ControlKind.ControlChange, 1, 1, 64, 2000)])
        );
        var before = RandomProjects.Json(state);
        var result = Edits.Apply(state, new SplitRegionCommand(region, 2880), ids);
        state = result.State;
        var parts = state.MidiRegions.Values.OrderBy(static r => r.StartTick).ToArray();
        Assert.Equal(
            (960L, 1920L, 2880L, 1920L),
            (parts[0].StartTick, parts[0].LengthTicks, parts[1].StartTick,
                parts[1].LengthTicks)
        );
        Assert.Equal([0L], parts[0].Notes.Select(static n => n.StartTick));
        Assert.Equal([0L], parts[1].Notes.Select(static n => n.StartTick));
        Assert.Equal([80L], parts[1].Controls.Select(static e => e.Tick));
        Assert.StartsWith("mid_", parts[1].Id, StringComparison.Ordinal);
        Assert.Equal(before, RandomProjects.Json(state.Apply(result.Inverse)));
        Assert.Throws<EditException>(() => Apply(new SplitRegionCommand(region, 960)));
    }

    [Fact]
    public void Note_ids_are_unique_and_never_handed_out_again() {
        var a = AddRegion(0, 3840);
        var b = AddRegion(3840, 3840);
        Apply(new AddNotesCommand(a, [new(60, 100, 1, 0, 480), new(61, 100, 1, 0, 480)]));
        var result = Edits.Apply(state, new AddNotesCommand(b, [new(62, 100, 1, 0, 480)]), ids);
        Assert.Equal(3, result.State.MidiRegions[b].Notes[0].Id);
        state = result.State.Apply(result.Inverse); // undo
        Apply(new AddControlsCommand(b, [new(ControlKind.ProgramChange, 1, 0, 5, 0)]));
        Assert.Equal(4, state.MidiRegions[b].Controls[0].Id);
    }

    [Fact]
    public void Quantizing_moves_starts_towards_the_grid_by_the_strength_with_swing() {
        var region = AddRegion(960, 3840);
        Apply(
            new AddNotesCommand(
                region,
                [new(60, 100, 1, 100, 200), new(62, 100, 1, 460, 200), new(64, 100, 1, 1000, 200)]
            )
        );
        var noteIds = state.MidiRegions[region].Notes.Select(static n => n.Id).ToArray();
        // Absolute 1060 → 960 (grid 480), half way: 1010; length stays.
        Apply(new QuantizeNotesCommand(region, 480, 0.5, NoteIds: [noteIds[0]]));
        Assert.Equal((50L, 200L), (Note(region, noteIds[0]).StartTick, Note(region, noteIds[0]).LengthTicks));
        Assert.Equal(460, Note(region, noteIds[1]).StartTick);
        // Full swing on a 480 grid: the points are 0, 720, 960, 1680, 1920 … (odd steps 240 late). Absolute 1010 →
        // 960, 1420 → 1680 (the swung point), 1960 → 1920.
        Apply(new QuantizeNotesCommand(region, 480, 1, 1));
        Assert.Equal([0L, 720, 960], state.MidiRegions[region].Notes.Select(static n => n.StartTick));
    }

    [Fact]
    public void Shared_region_commands_take_either_kind_and_resize_does_not() {
        Apply(new AddTrackCommand(), new AddTrackCommand(Kind: TrackKind.Instrument));
        var (audioTrack, instTrack) = (state.Tracks[0].Id, state.Tracks[1].Id);
        Apply(new AddRegionCommand(audioTrack, "aud_1", 0), new AddMidiRegionCommand(instTrack, 0, 3840));
        var (audio, midi) = (state.Regions.Keys.Single(), state.MidiRegions.Keys.Single());
        Apply(new MoveRegionCommand(midi, 960));
        Assert.Equal(960, state.MidiRegions[midi].StartTick);
        Assert.Throws<EditException>(() => Apply(new MoveRegionCommand(midi, 0, audioTrack)));
        Assert.Throws<EditException>(() => Apply(new MoveRegionCommand(audio, 0, instTrack)));
        Assert.Throws<EditException>(() => Apply(new ResizeRegionCommand(midi, 0, 0, 100)));
        Assert.Throws<EditException>(() => Apply(new AddRegionCommand(instTrack, "aud_1", 0)));
        Assert.Throws<EditException>(() => Apply(new AddMidiRegionCommand(audioTrack, 0, 960)));
        var result = Edits.Apply(state, new RemoveTrackCommand(instTrack), ids);
        Assert.Empty(result.State.MidiRegions);
        Apply(new DeleteRegionsCommand([audio, midi]));
        Assert.Empty(state.Regions);
        Assert.Empty(state.MidiRegions);
    }

    [Fact]
    public void Merge_recording_is_a_setting() {
        Apply(new SetMidiRecordingCommand(true));
        Assert.True(state.Settings.MergeMidiRecording);
    }

    public static TheoryData<string> Refused() => [
        "instrument on audio",
        "unknown plugin",
        "effect as instrument",
        "parameter out of range",
        "unknown parameter",
        "file type",
        "file on synth",
        "input channel",
        "note pitch",
        "note velocity",
        "note channel",
        "note length",
        "note before project",
        "unknown note",
        "grid",
        "strength",
        "pitch bend range",
        "controller number",
        "transpose",
        "swing",
        "region length",
        "plugin on audio track"
    ];

    [Theory]
    [MemberData(nameof(Refused))]
    public void Edits_that_do_not_apply_are_refused(string what) {
        Apply(new AddTrackCommand(), new AddTrackCommand(Kind: TrackKind.Instrument));
        var (audio, inst) = (state.Tracks[0].Id, state.Tracks[1].Id);
        var region = AddRegion(960, 3840, inst);
        Apply(new AddNotesCommand(region, [new(60, 100, 1, 0, 480)]));
        EditCommand command = what switch {
            "instrument on audio" => new SetInstrumentCommand(audio, "anode.synth"),
            "unknown plugin" => new SetInstrumentCommand(inst, "com.example.nope"),
            "effect as instrument" => new SetInstrumentCommand(inst, "com.example.fx"),
            "parameter out of range" => new SetParameterCommand(inst, "cutoff", 5),
            "unknown parameter" => new SetParameterCommand(inst, "nope", 1),
            "file type" => new LoadInstrumentFileCommand(inst, "/a.txt"),
            "file on synth" => new LoadInstrumentFileCommand(inst, "/a.sfz"),
            "input channel" => new SetMidiIoCommand(inst, new(null, 17)),
            "note pitch" => new AddNotesCommand(region, [new(128, 100, 1, 0, 480)]),
            "note velocity" => new AddNotesCommand(region, [new(60, 0, 1, 0, 480)]),
            "note channel" => new AddNotesCommand(region, [new(60, 100, 0, 0, 480)]),
            "note length" => new AddNotesCommand(region, [new(60, 100, 1, 0, 0)]),
            "note before project" => new AddNotesCommand(region, [new(60, 100, 1, -961, 480)]),
            "unknown note" => new EditNotesCommand(region, [new(999, 61)]),
            "grid" => new QuantizeNotesCommand(region, 0),
            "strength" => new QuantizeNotesCommand(region, 240, 1.5),
            "pitch bend range" => new AddControlsCommand(region, [new(ControlKind.PitchBend, 1, 0, 8192, 0)]),
            "controller number" => new AddControlsCommand(region, [new(ControlKind.ControlChange, 1, 128, 0, 0)]),
            "transpose" => new SetMidiRegionCommand(region, Transpose: 200),
            "swing" => new SetMidiRegionCommand(region, Swing: -0.1),
            "region length" => new AddMidiRegionCommand(inst, 0, 0),
            "plugin on audio track" => new AddTrackCommand(PluginId: "anode.synth"),
            _ => throw new ArgumentOutOfRangeException(nameof(what))
        };
        var before = RandomProjects.Json(state);
        Assert.Throws<EditException>(() => Apply(command));
        Assert.Equal(before, RandomProjects.Json(state));
    }

    Note Note(string region, long id) => state.MidiRegions[region].Notes.Single(n => n.Id == id);

    string AddRegion(long start, long length, string? track = null) {
        if (track is null) {
            Apply(new AddTrackCommand(Kind: TrackKind.Instrument));
            track = state.Tracks[^1].Id;
        }

        var before = state.MidiRegions.Keys.ToHashSet();
        Apply(new AddMidiRegionCommand(track, start, length));
        return state.MidiRegions.Keys.Single(k => !before.Contains(k));
    }

    void Push(EditHistory history, EditCommand command) {
        var batch = new CommandBatch([command]);
        var result = Edits.Apply(state, batch.Commands, ids, RandomProjects.Plugins);
        state = result.State;
        history.Push(UndoLabels.For(batch), 0, new(result.Forward, result.Inverse), UndoLabels.CoalesceKey(batch));
    }

    void Apply(params EditCommand[] commands) =>
        state = Edits.Apply(state, commands, ids, RandomProjects.Plugins).State;

    sealed class ManualTime : TimeProvider {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }
}
