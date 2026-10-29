using Anode.Contracts;
using Anode.Ipc.Segments;
using Anode.Model.Compile;
using Anode.Model.Editing;

namespace Anode.Model.Tests;

/// <summary>
///     Phase 6's edits and compilation (docs/07-phase-6.md): Smart Control maths and application, the Drum Machine
///     Designer and its routing, MIDI FX slots, tempo points, cells, Smart Control automation and performance recording.
/// </summary>
public sealed class Phase6Tests {
    readonly SequentialIds ids = new();
    ProjectState state = ProjectDefaults.NewProject("prj_1", "Song", 48_000);

    void Apply(params EditCommand[] commands) =>
        state = Edits.Apply(state, commands, ids, RandomProjects.Plugins, RandomProjects.Patches).State;

    Track T(string name) => state.Tracks.Single(t => t.Name == name);

    [Fact]
    public void Smart_control_maths_follow_the_graph_the_range_and_invert() {
        Assert.Equal(0.25, SmartMath.Graph(SmartMath.Line, 0.25), 9);
        IReadOnlyList<GraphPoint> bend = [new(0, 0), new(0.5, 0.8), new(1, 1)];
        Assert.Equal(0.4, SmartMath.Graph(bend, 0.25), 9);
        Assert.Equal(0.9, SmartMath.Graph(bend, 0.75), 9);
        Assert.Equal(1, SmartMath.Graph(bend, 2), 9);
        Assert.Equal(-12, SmartMath.Map(new("volume", -12, 0, false, SmartMath.Line), 0), 9);
        Assert.Equal(-3, SmartMath.Map(new("volume", -12, 0, false, SmartMath.Line), 0.75), 9);
        Assert.Equal(-9, SmartMath.Map(new("volume", -12, 0, true, SmartMath.Line), 0.75), 9);
        Assert.Equal(200 + 0.6 * 800, SmartMath.Map(new("x", 200, 1000, true, bend), 0.25), 9);
        // A vertical step takes the later point.
        IReadOnlyList<GraphPoint> step = [new(0, 0), new(0.5, 0), new(0.5, 1), new(1, 1)];
        Assert.Equal((0.0, 1.0, 1.0), (SmartMath.Graph(step, 0.49), SmartMath.Graph(step, 0.5), SmartMath.Graph(step, 0.51)));
    }

    [Fact]
    public void A_smart_control_sets_every_mapped_parameter_and_automatic_mapping_takes_the_macro_hints() {
        Apply(new AddTrackCommand("Lead", Kind: TrackKind.Instrument));
        var lead = T("Lead").Id;
        Apply(new AddInsertCommand("anode.test-compressor", lead));
        Apply(new AutoMapSmartControlsCommand(lead));
        var smart = T("Lead").SmartControls!;
        var compressor = T("Lead").Strip.Inserts[0].Id;
        Assert.Equal(
            ["instrument:cutoff", "instrument:resonance", $"insert:{compressor}:ratio", $"insert:{compressor}:threshold"],
            smart.Controls.Take(4).Select(static c => c.Mappings.Single().Target)
        );
        Assert.All(smart.Controls.Skip(4).Where(static c => c.Kind == SmartControlKind.Knob), static c => Assert.Empty(c.Mappings));
        Assert.Equal((8_000 - 20) / (20_000.0 - 20), smart.Controls[0].Value, 9);

        Apply(
            new SetSmartMappingsCommand(
                lead,
                "knob5",
                [
                    new("volume", -24, 0, false, []),
                    new("instrument:resonance", 0, 1, true, [new(0, 0), new(0.5, 0.8), new(1, 1)])
                ]
            ),
            new SetSmartControlCommand(lead, "knob5", 0.25)
        );
        var track = T("Lead");
        Assert.Equal(-18, track.Strip.VolumeDb, 9);
        Assert.Equal(0.6, track.Instrument!.Parameters["resonance"], 9);
        Assert.Equal((0.25, 0.0), (track.SmartControls!.Controls[4].Value, track.SmartControls.Controls[4].SavedValue!.Value));

        // Compare swaps saved and edited values (and the parameters follow), and swaps back.
        Apply(new CompareSmartControlsCommand(lead, true));
        Assert.Equal((-24.0, true), (T("Lead").Strip.VolumeDb, T("Lead").SmartControls!.Comparing));
        Apply(new CompareSmartControlsCommand(lead, false));
        Assert.Equal(-18, T("Lead").Strip.VolumeDb, 9);

        Assert.Throws<EditException>(() =>
            Apply(new SetSmartMappingsCommand(lead, "knob1", [new("instrument:nothing", 0, 1, false, [])]))
        );
        Assert.Throws<EditException>(() =>
            Apply(new SetSmartMappingsCommand(lead, "knob1", [new("volume", -12, 0, false, [new(0.2, 0), new(1, 1)])]))
        );
    }

    [Fact]
    public void Smart_control_automation_compiles_to_automation_of_the_mapped_parameters() {
        Apply(new AddTrackCommand("Lead", Kind: TrackKind.Instrument));
        var lead = T("Lead").Id;
        Apply(
            new SetSmartMappingsCommand(lead, "knob1", [new("volume", -20, 0, true, [])]),
            new SetAutomationCommand("smartControl:knob1", [new(0, 0, 0), new(960, 1, 0)], lead)
        );
        var engine = EngineIds.Empty.With(state);
        Assert.Equal(["volume"], AutomationEvents.Targets(state, T("Lead")));
        var target = engine.Target(lead, "volume");
        var events = AutomationEvents.Compile(state, state.TempoMap, engine, T("Lead"), engine.Track(lead));
        Assert.All(events, e => Assert.Equal(target, e.Target));
        // Inverted: the knob going 0 → 1 takes the volume from 0 dB down to −20 dB.
        Assert.Equal(1, events[0].Value, 4);
        Assert.Equal(Units.DbToGain(-20), events[^1].Value, 4);
    }

    [Fact]
    public void A_drum_machine_from_a_kit_routes_its_notes_to_its_pads_at_their_base_note() {
        Apply(new CreateDrumMachineCommand(null, "pat_kit", "Beats"));
        var dmd = T("Beats");
        Assert.Equal([36, 38, 42], dmd.DrumMachine!.Pads.Select(static p => p.Note));
        Assert.All(dmd.DrumMachine.Pads, p => Assert.Equal(dmd.Id, state.FindTrack(p.TrackId)!.ParentId));
        Assert.Single(T("Hat").Strip.Inserts);
        Apply(new AddPatternRegionCommand(dmd.Id, 0, 3840));
        var pattern = state.PatternRegions.Values.Single();
        Assert.Equal([36, 38, 42], pattern.Pattern.Rows.Select(static r => r.Note));
        var rows = pattern.Pattern.Rows.Select(r => r with {
                Steps = [..r.Steps.Select((s, i) => s with { On = r.Note == 38 ? i == 4 : i % 8 == 0 })]
            }
        );
        Apply(new SetPatternCommand(pattern.Id, pattern.Pattern with { Rows = [..rows] }));

        var engine = EngineIds.Empty.With(state);
        var model = new ModelCompiler().Compile(state, engine);
        var dmdModel = model.Tracks.Single(m => m.TrackId == engine.Track(dmd.Id));
        Assert.Empty(dmdModel.Events);
        var snare = model.Tracks.Single(m => m.TrackId == engine.Track(T("Snare").Id));
        var on = Assert.Single(snare.Events, static e => e.Kind == PlaybackEventKind.NoteOn);
        Assert.Equal((Edits.PadBaseNote, 24_000L), (on.Data1, on.SamplePos));
        Assert.Equal(2, model.Tracks.Single(m => m.TrackId == engine.Track(T("Kick").Id)).Events.Count(static e => e.Kind == PlaybackEventKind.NoteOn));

        // Removing a pad removes its track; renaming one renames its track too.
        Apply(new SetDrumPadCommand(dmd.Id, 38, "Clap", ToNote: 39), new RemoveDrumPadCommand(dmd.Id, 42));
        Assert.Equal([36, 39], T("Beats").DrumMachine!.Pads.Select(static p => p.Note));
        Assert.NotNull(T("Clap"));
        Assert.DoesNotContain(state.Tracks, static t => t.Name == "Hat");
    }

    [Fact]
    public void MIDI_FX_slots_take_MIDI_effects_only_and_reach_the_engine_strip() {
        Apply(new AddTrackCommand("Keys", Kind: TrackKind.Instrument), new AddTrackCommand("Vox"));
        var keys = T("Keys").Id;
        Apply(new AddInsertCommand("anode.arpeggiator", keys, Chain: InsertChain.MidiEffects));
        Assert.Throws<EditException>(() => Apply(new AddInsertCommand("anode.test-compressor", keys, Chain: InsertChain.MidiEffects)));
        Assert.Throws<EditException>(() => Apply(new AddInsertCommand("anode.arpeggiator", keys)));
        Assert.Throws<EditException>(() => Apply(new AddInsertCommand("anode.arpeggiator", T("Vox").Id, Chain: InsertChain.MidiEffects)));
        var slot = T("Keys").Strip.MidiEffects.Single();
        Apply(
            new SetInsertParameterCommand(slot.Id, "octaves", 2, keys, InsertChain.MidiEffects),
            new SetSmartControlsCommand(keys)
        );
        Assert.True(T("Keys").SmartControls!.Arpeggiator);
        Apply(new SetSmartControlsCommand(keys, Arpeggiator: false));
        Assert.True(T("Keys").Strip.MidiEffects[0].Bypassed);
        var engine = EngineIds.Empty.With(state);
        var strip = Strips.Engine(state, T("Keys"), engine);
        var fx = Assert.Single(strip.MidiEffects!);
        Assert.Equal(("anode.arpeggiator", true, 2.0), (fx.PluginId, fx.Bypassed, fx.Parameters["octaves"]));
        Apply(new SetAutomationCommand($"midiEffect:{slot.Id}:octaves", [new(0, 1, 0), new(960, 3, 0)], keys));
        var described = EngineIds.Empty.With(state).AutomationTargets(state).Single();
        Assert.Equal(("midiEffect", slot.Id, "octaves"), (described.Kind, described.Id, described.ParameterId));
    }

    [Fact]
    public void Tempo_points_carry_steps_ramps_and_the_signature_after_Varispeed() {
        Apply(
            new SetTempoEventsCommand([new(0, 120, TempoCurve.Ramp), new(3840, 60, TempoCurve.Step), new(7680, 100, TempoCurve.Step)]),
            new SetSignaturesCommand([new(0, 4, 4), new(7680, 3, 4)]),
            new SetVarispeedCommand(true, VarispeedMode.SpeedOnly, 1.5)
        );
        var points = TempoPoints.Of(state);
        Assert.Equal([0, 4, 8], points.Select(static p => p.Quarter));
        Assert.Equal((180.0, -15 * 1.5), (points[0].Bpm, points[0].Slope));
        Assert.Equal((90.0, 0.0), (points[1].Bpm, points[1].Slope));
        Assert.Equal((150.0, (short)3, 8.0), (points[2].Bpm, points[2].Numerator, points[2].BarQuarter));
        Assert.Equal(state.PlaybackMap.SampleAt(3840, 48_000), points[1].Sample);
        // Between points the position is exact: the ramp's end, from its start point.
        var p = points[0];
        var e = (double)points[1].Sample / 48_000;
        Assert.Equal(4, p.Quarter + p.Bpm / p.Slope * (Math.Exp(p.Slope * e / 60) - 1), 3);

        // A shaped ramp is sampled into linear pieces every 1/16 of a quarter note.
        Apply(new SetTempoEventsCommand([new(0, 100, TempoCurve.Ramp, 0.5), new(960, 140, TempoCurve.Step)]), new SetVarispeedCommand(false));
        Assert.Equal(17, TempoPoints.Of(state).Length - 1);
    }

    [Fact]
    public void Cells_compile_into_cell_models_with_stable_indices() {
        Apply(new AddTrackCommand("Keys", Kind: TrackKind.Instrument), new AddSceneCommand(), new AddSceneCommand());
        var keys = T("Keys").Id;
        var scenes = state.LiveLoops.Scenes;
        var midi = new MidiRegion("x", keys, "Riff", 0, 1920, ProjectDefaults.Playback, [new(0, 60, 100, 1, 0, 240), new(0, 64, 100, 1, 960, 240)], [], false);
        Apply(
            new SetCellCommand(keys, scenes[0].Id, new(Midi: midi), PlayMode: CellPlayMode.OneShot),
            new SetCellCommand(keys, scenes[1].Id, new(Pattern: new("x", keys, "Beat", 0, 960, false, new([new("r", PatternRowKind.Note, 48, "C2", 4, null, PatternPlayMode.Forward, false, [..Enumerable.Repeat(new PatternStep(true, 100, 1, 1, 1, 0), 4)])], 0, 1))))
        );
        var cell = state.LiveLoops.Cells[0];
        Assert.Equal(cell.Id, cell.Content.Midi!.Id);
        Assert.All(cell.Content.Midi.Notes, n => Assert.True(n.Id > 0));
        Assert.Equal(2, cell.Content.Midi.Notes.Select(static n => n.Id).Distinct().Count());

        var engine = EngineIds.Empty.With(state);
        var model = new ModelCompiler().Compile(state, engine);
        Assert.Equal(2, model.Cells.Count);
        var first = model.Cells.Single(c => c.Index == engine.Cell(cell.Id));
        Assert.Equal((CellMode.OneShot, 48_000L, engine.Track(keys)), (first.Mode, first.Length, first.TrackId));
        Assert.Equal([0L, 24_000], first.Events.Where(static e => e.Kind == PlaybackEventKind.NoteOn).Select(static e => e.SamplePos));
        var second = model.Cells.Single(c => c != first);
        Assert.Equal(4, second.Events.Count(static e => e.Kind == PlaybackEventKind.NoteOn));

        // A segment writes and reads them back.
        using var segment = SegmentWriter.Write(model.Tracks, 1, model.LengthSamples, "antest", Random.Shared.NextInt64(1, 1 << 30), true, tempo: model.Tempo, cells: model.Cells);
        using var mapped = MappedSegment.Map(1, segment.Name, segment.Length);
        mapped.ValidateLayout();
        mapped.Verify();
        Assert.Equal((2L, 1L), (mapped.CellCount, mapped.TempoCount));
    }

    [Fact]
    public void A_performance_becomes_regions_where_the_cells_played_and_automation_of_its_moves() {
        Apply(new AddTrackCommand("Keys", Kind: TrackKind.Instrument), new AddSceneCommand(), new AddInsertCommand("anode.test-compressor"));
        var keys = T("Keys").Id;
        var midi = new MidiRegion("x", keys, "Riff", 0, 3840, ProjectDefaults.Playback, [new(0, 60, 100, 1, 0, 240)], [], false);
        Apply(new SetCellCommand(keys, state.LiveLoops.Scenes[0].Id, new(Midi: midi)));
        var cell = state.LiveLoops.Cells[0];
        var insert = state.Master.Inserts[0].Id;
        // 120 bpm: a bar is 96 000 samples. Played from bar 2 for two and a half bars.
        var ops = Performance.Ops(
            state,
            [new(cell.Id, 96_000, 96_000 + 240_000)],
            [new(3840, null, $"insert:{insert}:threshold", -30), new(7680, null, $"insert:{insert}:threshold", -10)],
            ids
        );
        var after = state.Apply(ops);
        var regions = after.MidiRegionsOf(keys).ToArray();
        Assert.Equal([(3840L, 3840L), (7680, 3840), (11_520, 1920)], regions.Select(static r => (r.StartTick, r.LengthTicks)));
        Assert.All(regions, static r => Assert.Single(r.Notes));
        Assert.Equal(3, regions.SelectMany(static r => r.Notes).Select(static n => n.Id).Distinct().Count());
        var lane = after.LaneOf(null, $"insert:{insert}:threshold")!;
        Assert.Equal([(3840L, -30.0), (7680, -10)], lane.Points.Select(static p => (p.Tick, p.Value)));
    }
}
