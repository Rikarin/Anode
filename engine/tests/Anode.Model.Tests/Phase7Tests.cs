using Anode.Contracts;
using Anode.Ipc.Segments;
using Anode.Model.Compile;
using Anode.Model.Editing;
using Anode.Testing;
using System.Text.Json;

namespace Anode.Model.Tests;

/// <summary>
///     Phase 7's model (docs/08-phase-7.md): the Event List's edits, Transform, grooves and the groove track,
///     articulations and their compilation, lane sets, steps, file versions and Scripter's run-time parameters. At 120
///     BPM and 48 kHz a tick is 25 samples.
/// </summary>
public sealed class Phase7Tests {
    readonly SequentialIds ids = new();
    ProjectState state = ProjectDefaults.NewProject("prj_1", "Song", 48_000);

    /// <summary>A project as a schema-6 package holds it: everything Phase 7 added at its default.</summary>
    public static ProjectState WithoutPhase7(ProjectState state) {
        return state with {
            Settings = state.Settings with { GrooveTrackId = null },
            Tracks = state.Tracks.ConvertAll(static t => t with {
                    MatchGroove = false,
                    ArticulationSetId = null,
                    Strip = t.Strip with { MidiEffects = Slots(t.Strip.MidiEffects), Inserts = Slots(t.Strip.Inserts) },
                    Alternatives = [
                        ..t.Alternatives.Select(static a => a with { MidiRegions = [..a.MidiRegions.Select(Plain)] })
                    ]
                }
            ),
            MidiRegions = state.MidiRegions.SetItems(
                state.MidiRegions.Select(static p => KeyValuePair.Create(p.Key, Plain(p.Value)))
            ),
            Files = state.Files.SetItems(
                state.Files.Select(static p => KeyValuePair.Create(p.Key, p.Value with { Version = 0 }))
            ),
            LiveLoops = state.LiveLoops with {
                Cells = [
                    ..state.LiveLoops.Cells.Select(static c => c.Content.Midi is { } m
                            ? c with { Content = c.Content with { Midi = Plain(m) } }
                            : c
                    )
                ]
            },
            Grooves = state.Grooves.Clear(),
            ArticulationSets = state.ArticulationSets.Clear(),
            LaneSets = state.LaneSets.Clear()
        };

        static MidiRegion Plain(MidiRegion m) =>
            m with {
                Playback = m.Playback with { GrooveId = null },
                Notes = [..m.Notes.Select(static n => n with { ArticulationId = 0 })]
            };

        static IReadOnlyList<Insert> Slots(IReadOnlyList<Insert> slots) =>
            [..slots.Select(static i => i with { RuntimeParameters = null, Source = null })];
    }

    [Fact]
    public void The_Event_List_edits_controller_events_of_every_kind_and_keeps_their_kind() {
        var region = Region(0, 3840);
        Apply(
            new AddControlsCommand(
                region,
                [new(ControlKind.PolyPressure, 1, 60, 40, 0), new(ControlKind.ControlChange, 1, 1, 64, 480)]
            )
        );
        var (poly, cc) = (Controls(region)[0], Controls(region)[1]);
        Apply(new SetControlsCommand(region, [new(poly.Id, 960, 2, 62, 90), new(cc.Id, Value: 10)]));
        Assert.Equal(
            [(ControlKind.ControlChange, 1, 1, 10, 480L), (ControlKind.PolyPressure, 2, 62, 90, 960)],
            Controls(region).Select(static e => (e.Kind, e.Channel, e.Number, e.Value, e.Tick))
        );
        Refused(new SetControlsCommand(region, [new(poly.Id, Value: 128)]));
        Refused(new SetControlsCommand(region, [new(poly.Id, Number: 200)]));
        Refused(new SetControlsCommand(region, [new(12345, Value: 1)]));
        Refused(new AddControlsCommand(region, [new(ControlKind.ProgramChange, 1, 5, 1, 0)]));
    }

    [Fact]
    public void Poly_pressure_compiles_with_its_key_and_value() {
        var region = Region(0, 3840);
        Apply(new AddControlsCommand(region, [new(ControlKind.PolyPressure, 3, 61, 77, 40)]));
        Assert.Equal(
            [(PlaybackEventKind.PolyPressure, 61, 77, 2, 1000L)],
            Events().Select(static e => (e.Kind, (int)e.Data1, (int)e.Data2, (int)e.Channel, e.SamplePos))
        );
    }

    [Fact]
    public void A_note_carries_an_articulation_that_edits_set_and_clear() {
        var region = Region(0, 3840);
        Apply(new AddNotesCommand(region, [new(60, 100, 1, 0, 480, 3), new(62, 100, 1, 480, 480)]));
        Assert.Equal([3, 0], Notes(region).Select(static n => n.ArticulationId));
        var first = Notes(region)[0].Id;
        Apply(new EditNotesCommand(region, [new(first, ArticulationId: 0)]));
        Assert.Equal(0, Notes(region)[0].ArticulationId);
        Refused(new EditNotesCommand(region, [new(first, ArticulationId: 255)]));
        Refused(new AddNotesCommand(region, [new(60, 100, 1, 0, 480, -1)]));
    }

    [Fact]
    public void Transform_changes_the_matching_events_in_one_step_and_refuses_select_only() {
        var a = Region(0, 3840);
        Apply(new AddNotesCommand(a, [new(48, 30, 1, 0, 480), new(72, 40, 1, 480, 480), new(76, 50, 1, 960, 480)]));
        var set = new TransformSet(
            "trf_t",
            "Above C4",
            [new(TransformField.Pitch, TransformConditionOp.GreaterEqual, 61)],
            [
                new(TransformField.Velocity, TransformOperationKind.Fix, 100),
                new(TransformField.Length, TransformOperationKind.Fix, 240)
            ],
            null,
            TransformMode.OperateOnly,
            1
        );
        var result = Edits.Apply(state, new TransformCommand([a], set), ids, RandomProjects.Plugins);
        state = result.State;
        Assert.Equal(
            [(48, 30, 480L), (72, 100, 240), (76, 100, 240)],
            Notes(a).Select(static n => (n.Pitch, n.Velocity, n.LengthTicks))
        );
        Assert.Single(result.Forward);
        Refused(new TransformCommand([a], set with { Mode = TransformMode.SelectOnly }));
        Refused(
            new TransformCommand(
                [a],
                set with {
                    Operations = [
                        new(TransformField.Pitch, TransformOperationKind.Add, 1),
                        new(TransformField.Pitch, TransformOperationKind.Add, 2)
                    ]
                }
            )
        );
        Refused(
            new TransformCommand(
                [a],
                set with { Operations = [new(TransformField.Status, TransformOperationKind.Fix, 1)] }
            )
        );
        Refused(new TransformCommand([a], set with { Map = [1, 2, 3] }));
    }

    [Fact]
    public void Transform_runs_in_project_ticks_across_regions_and_only_on_the_selection() {
        var a = Region(0, 3840);
        var b = Region(3840, 3840, state.Tracks[0].Id);
        Apply(new AddNotesCommand(a, [new(60, 30, 1, 0, 480)]), new AddNotesCommand(b, [new(60, 30, 1, 0, 480)]));
        var late = new TransformSet(
            "trf_late",
            "Late",
            [new(TransformField.Position, TransformConditionOp.GreaterEqual, 3840)],
            [new(TransformField.Velocity, TransformOperationKind.Add, 10)],
            null,
            TransformMode.SelectAndOperate,
            0
        );
        Apply(new TransformCommand([a, b], late));
        Assert.Equal((30, 40), (Notes(a)[0].Velocity, Notes(b)[0].Velocity));

        var all = late with { Conditions = [] };
        Apply(new TransformCommand([a, b], all, [Notes(a)[0].Id]));
        Assert.Equal((40, 40), (Notes(a)[0].Velocity, Notes(b)[0].Velocity));
        Refused(new TransformCommand([a], all, [Notes(b)[0].Id]));
    }

    [Fact]
    public void A_groove_made_from_notes_quantises_another_region_onto_its_positions() {
        Apply(new AddTrackCommand(Kind: TrackKind.Instrument), new AddTrackCommand(Kind: TrackKind.Instrument));
        var (drums, keys) = (state.Tracks[0].Id, state.Tracks[1].Id);
        var swung = Region(0, 3840, drums);
        // Eighths, every second one 120 ticks late (swing), the downbeats loudest.
        Apply(
            new AddNotesCommand(
                swung,
                [
                    ..Enumerable.Range(0, 8)
                        .Select(static i => new NewNote(36, i % 2 == 0 ? 127 : 64, 1, i * 480 + i % 2 * 120, 100))
                ]
            )
        );
        Apply(new MakeGrooveCommand(swung, "Swing", 1, 480));
        var groove = Assert.Single(state.Grooves.Values);
        Assert.Equal(
            ("Swing", 3840L, 480L, 8),
            (groove.Name, groove.LengthTicks, groove.StepTicks, groove.Points.Count)
        );
        Assert.Equal(600, groove.Points[1].Tick);

        var straight = Region(3840, 3840, keys);
        Apply(
            new AddNotesCommand(
                straight,
                [new(60, 100, 1, 0, 100), new(60, 100, 1, 480, 100), new(60, 100, 1, 960, 100)]
            )
        );
        Apply(new QuantizeNotesCommand(straight, 0, 1, GrooveId: groove.Id, GrooveVelocity: true));
        Assert.Equal([0L, 600, 960], Notes(straight).Select(static n => n.StartTick));
        Assert.Equal([100, 50, 100], Notes(straight).Select(static n => n.Velocity));
        Refused(new QuantizeNotesCommand(straight, 0, 1, GrooveId: "grv_none"));
        Refused(new QuantizeNotesCommand(straight, 240, 1, GrooveVelocity: true));
    }

    [Fact]
    public void A_track_that_matches_the_groove_track_quantises_to_its_first_region_at_quantise_time() {
        Apply(new AddTrackCommand(Kind: TrackKind.Instrument), new AddTrackCommand(Kind: TrackKind.Instrument));
        var (drums, keys) = (state.Tracks[0].Id, state.Tracks[1].Id);
        var swung = Region(0, 3840, drums);
        Apply(new AddNotesCommand(swung, [new(36, 100, 1, 0, 100), new(36, 100, 1, 240 + 60, 100)]));
        Apply(new SetGrooveTrackCommand(drums), new SetMatchGrooveCommand(keys, true));
        var straight = Region(0, 3840, keys);
        Apply(new AddNotesCommand(straight, [new(60, 100, 1, 250, 100)]));
        Apply(new QuantizeNotesCommand(straight, 240));
        Assert.Equal(300, Notes(straight)[0].StartTick);
        Assert.Empty(state.Grooves); // made at quantise time, not kept

        // The groove track does not match itself; removing it ends the matching.
        Refused(new SetMatchGrooveCommand(drums, true));
        Apply(new SetGrooveTrackCommand(keys));
        Assert.False(state.FindTrack(keys)!.MatchGroove);
        Apply(new RemoveTrackCommand(keys));
        Assert.Null(state.Settings.GrooveTrackId);
    }

    [Fact]
    public void Region_playback_follows_a_groove_and_deleting_it_returns_the_region_to_the_grid() {
        var region = Region(0, 3840);
        Apply(new AddNotesCommand(region, [new(60, 100, 1, 480, 100)]));
        Apply(
            new SetGrooveCommand(
                new(
                    "grv_late",
                    "Late",
                    3840,
                    480,
                    [..Enumerable.Range(0, 8).Select(static n => new GroovePoint(n * 480 + n % 2 * 100, 1))]
                )
            )
        );
        Apply(new SetMidiRegionCommand(region, QuantizeStrength: 1, GrooveId: "grv_late"));
        Assert.Equal(580L * 25, NoteOns()[0].SamplePos);
        Apply(new DeleteGrooveCommand("grv_late"));
        Assert.Null(state.MidiRegions[region].Playback.GrooveId);
        Assert.Equal(480L * 25, NoteOns()[0].SamplePos);
        Refused(new SetMidiRegionCommand(region, GrooveId: "grv_late"));
    }

    [Fact]
    public void A_groove_template_is_checked() {
        Refused(new SetGrooveCommand(new("grv_a", "A", 960, 240, [new(0, 1)])));
        Refused(new SetGrooveCommand(new("grv_a", "A", 1000, 240, [])));
        Refused(new SetGrooveCommand(new("grv_a", "A", 480, 240, [new(0, 1), new(900, 1)])));
        Refused(new SetGrooveCommand(new("grv_a", "A", 480, 240, [new(0, 2), new(240, 1)])));
        Refused(new SetGrooveCommand(new("../a", "A", 480, 240, [new(0, 1), new(240, 1)])));
        Apply(new SetGrooveCommand(new("grv_a", " A ", 480, 240, [new(10, 1), new(250, 0.5)])));
        Assert.Equal("A", state.Grooves["grv_a"].Name);
    }

    [Fact]
    public void An_articulation_set_is_checked_and_deleting_it_leaves_tracks_without_one() {
        Apply(new AddTrackCommand(Kind: TrackKind.Instrument), new AddTrackCommand());
        var (inst, audio) = (state.Tracks[0].Id, state.Tracks[1].Id);
        var set = Strings();
        Apply(new SetArticulationSetCommand(set), new AssignArticulationSetCommand(inst, set.Id));
        Assert.Equal(set.Id, state.FindTrack(inst)!.ArticulationSetId);
        Refused(new AssignArticulationSetCommand(audio, set.Id));
        Refused(new AssignArticulationSetCommand(inst, "ars_none"));
        Refused(new SetArticulationSetCommand(set with { Articulations = [new(0, "Zero", null)] }));
        Refused(new SetArticulationSetCommand(set with { Articulations = [new(1, "A", null), new(1, "B", null)] }));
        Refused(new SetArticulationSetCommand(set with { Outputs = [new(9, ArticulationOutputKind.Note, 24, 100)] }));
        Refused(new SetArticulationSetCommand(set with { Outputs = [new(1, ArticulationOutputKind.Channel, 0, 17)] }));
        Refused(new SetArticulationSetCommand(set with { Outputs = [new(1, ArticulationOutputKind.Note, 24, 0)] }));
        Apply(new DeleteArticulationSetCommand(set.Id));
        Assert.Null(state.FindTrack(inst)!.ArticulationSetId);
    }

    [Fact]
    public void Articulation_outputs_play_just_before_each_note_at_its_tick_on_its_channel() {
        Apply(new AddTrackCommand(Kind: TrackKind.Instrument));
        var track = state.Tracks[0].Id;
        Apply(new SetArticulationSetCommand(Strings()), new AssignArticulationSetCommand(track, "ars_strings"));
        var region = Region(0, 3840, track);
        Apply(
            new AddNotesCommand(
                region,
                [
                    new(60, 100, 2, 0, 480, 1), // CC 32 = 5 and keyswitch C0 (24) at velocity 100
                    new(62, 90, 2, 480, 480, 2), // program 7
                    new(64, 80, 2, 960, 480, 3), // channel 5
                    new(65, 70, 2, 1440, 480) // none
                ]
            )
        );
        Assert.Equal(
            [
                (0L, PlaybackEventKind.ControlChange, 32, 5, 1), (0, PlaybackEventKind.NoteOn, 24, 100, 1),
                (0, PlaybackEventKind.NoteOn, 60, 100, 1), (25, PlaybackEventKind.NoteOff, 24, 0, 1),
                (12_000, PlaybackEventKind.NoteOff, 60, 0, 1), (12_000, PlaybackEventKind.ProgramChange, 7, 0, 1),
                (12_000, PlaybackEventKind.NoteOn, 62, 90, 1), (24_000, PlaybackEventKind.NoteOff, 62, 0, 1),
                (24_000, PlaybackEventKind.NoteOn, 64, 80, 4), (36_000, PlaybackEventKind.NoteOff, 64, 0, 4),
                (36_000, PlaybackEventKind.NoteOn, 65, 70, 1), (48_000, PlaybackEventKind.NoteOff, 65, 0, 1)
            ],
            Events().Select(static e => (e.SamplePos, e.Kind, (int)e.Data1, (int)e.Data2, (int)e.Channel))
        );

        // Without the set, the notes play as they are.
        Apply(new AssignArticulationSetCommand(track, null));
        Assert.Equal(8, Events().Length);
    }

    [Fact]
    public void A_lane_set_is_checked_stored_and_deleted() {
        var set = new LaneSet(
            "lns_a",
            "Drums",
            [new(LaneKind.Velocity, null, "Velocity", 240), new(LaneKind.Note, 36, "Kick", 120)]
        );
        Apply(new SetLaneSetCommand(set));
        Assert.Equal(2, state.LaneSets["lns_a"].Lanes.Count);
        Refused(new SetLaneSetCommand(set with { Lanes = [new(LaneKind.ControlChange, null, "CC", 240)] }));
        Refused(new SetLaneSetCommand(set with { Lanes = [new(LaneKind.PitchBend, 1, "Bend", 240)] }));
        Refused(new SetLaneSetCommand(set with { Lanes = [new(LaneKind.Velocity, null, "V", 0)] }));
        Apply(new DeleteLaneSetCommand("lns_a"));
        Assert.Empty(state.LaneSets);
        Refused(new DeleteLaneSetCommand("lns_a"));
    }

    [Fact]
    public void A_step_goes_into_its_region_or_makes_one_and_a_tie_lengthens_it() {
        Apply(new AddTrackCommand(Kind: TrackKind.Instrument));
        var track = state.Tracks[0].Id;
        Apply(new StepNotesCommand([60, 64, 67], 90, TrackId: track, PositionTick: 4000, LengthTicks: 240));
        var region = Assert.Single(state.MidiRegions.Values);
        Assert.Equal((3840L, 3840L), (region.StartTick, region.LengthTicks));
        Assert.Equal(
            [(60, 90, 160L, 240L), (64, 90, 160, 240), (67, 90, 160, 240)],
            region.Notes.Select(static n => (n.Pitch, n.Velocity, n.StartTick, n.LengthTicks))
        );

        // Past the region's end: it grows to the next bar line.
        Apply(new StepNotesCommand([72], RegionId: region.Id, PositionTick: 7680, LengthTicks: 480));
        Assert.Equal(7680, state.MidiRegions[region.Id].LengthTicks);
        Assert.Equal(100, Notes(region.Id)[^1].Velocity);
        Apply(new StepTieCommand(region.Id, [Notes(region.Id)[^1].Id], 480));
        Assert.Equal(960, Notes(region.Id)[^1].LengthTicks);

        Refused(new StepNotesCommand([60], RegionId: region.Id, PositionTick: 0, LengthTicks: 240));
        Refused(new StepNotesCommand([60, 60], RegionId: region.Id, PositionTick: 4000, LengthTicks: 240));
        Refused(new StepNotesCommand([60], RegionId: region.Id));
        Refused(new StepTieCommand(region.Id));
    }

    [Fact]
    public void A_file_version_points_the_file_elsewhere_and_regions_must_fit_it() {
        var (file, region) = AudioRegion(48_000, 10_000, 20_000);
        Apply(new SetFileVersionCommand(file, 1, "Audio Files/Take.v1.wav", 40_000));
        Assert.Equal(
            (1, "Audio Files/Take.v1.wav", 40_000L),
            (state.Files[file].Version, state.Files[file].Path, state.Files[file].Frames)
        );
        Refused(new SetFileVersionCommand(file, 2, "Audio Files/Take.v2.wav", 25_000));
        Refused(new SetFileVersionCommand(file, 2, "../Take.v2.wav"));
        Refused(new SetFileVersionCommand(file, 2, "/tmp/Take.v2.wav"));
        Refused(new SetFileVersionCommand(file, 2, "Audio Files/Take.v2.aiff"));
        Refused(new SetFileVersionCommand(file, -1, "Audio Files/Take.v2.wav"));
        Assert.NotNull(region);
    }

    [Fact]
    public void A_trim_clips_the_regions_of_the_file_and_keeps_where_their_audio_plays() {
        var (file, region) = AudioRegion(96_000, 10_000, 20_000);
        var before = state.Regions[region];
        // Keep frames 15 000 … 50 000: the region loses its first 5000 frames and starts 200 ticks later.
        var ops = Edits.FileLengthChanged(state, file, AudioEditKind.Trim, 15_000, 50_000, 48_000);
        var trimmed = Assert.IsType<RegionUpserted>(Assert.Single(ops)).Region;
        Assert.Equal((0L, 15_000L, before.StartTick + 200), (trimmed.FileOffset, trimmed.Length, trimmed.StartTick));

        // Keeping only frames after it, the region would vanish.
        Assert.Throws<EditException>(() => Edits.FileLengthChanged(
                state,
                file,
                AudioEditKind.Trim,
                40_000,
                50_000,
                48_000
            )
        );
        // A conversion scales its frames, at the project's rate only.
        Apply(new SetFileVersionCommand(file, 0, state.Files[file].Path, 96_000, 96_000));
        var converted = Assert.IsType<RegionUpserted>(
                Assert.Single(Edits.FileLengthChanged(state, file, AudioEditKind.ConvertRate, 0, 96_000, 48_000))
            )
            .Region;
        Assert.Equal((5_000L, 10_000L), (converted.FileOffset, converted.Length));
        Assert.Throws<EditException>(() => Edits.FileLengthChanged(
                state,
                file,
                AudioEditKind.ConvertRate,
                0,
                96_000,
                44_100
            )
        );
        Assert.Empty(Edits.FileLengthChanged(state, file, AudioEditKind.Gain, 0, 10, 48_000));
    }

    [Fact]
    public void A_script_sets_a_Scripter_slot_and_its_run_time_parameters_keep_their_values_by_id() {
        Apply(new AddTrackCommand(Kind: TrackKind.Instrument));
        var track = state.Tracks[0].Id;
        Apply(
            new AddInsertCommand(Edits.ScripterId, track, Chain: InsertChain.MidiEffects),
            new AddInsertCommand("anode.arpeggiator", track, Chain: InsertChain.MidiEffects)
        );
        var (scripter, arp) = (state.Tracks[0].Strip.MidiEffects[0].Id, state.Tracks[0].Strip.MidiEffects[1].Id);
        Apply(new SetScriptCommand(track, scripter, "function HandleMIDI(e) { e.send(); }"));
        Assert.Equal("function HandleMIDI(e) { e.send(); }", state.Tracks[0].Strip.MidiEffects[0].Source);
        Refused(new SetScriptCommand(track, arp, "x"));

        state = state.Apply(Edits.WithRuntimeParameters(state, track, scripter, RandomProjects.ScriptParameters)!);
        Assert.Equal(12, state.Tracks[0].Strip.MidiEffects[0].Parameters["Interval"]);
        // Automatable and mappable by id, in the run-time list's range.
        Apply(new SetInsertParameterCommand(scripter, "Interval", 7, track, InsertChain.MidiEffects));
        Refused(new SetInsertParameterCommand(scripter, "Interval", 99, track, InsertChain.MidiEffects));
        Refused(new SetInsertParameterCommand(scripter, "Nope", 1, track, InsertChain.MidiEffects));
        Assert.True(Edits.TargetExists(state, track, $"midiEffect:{scripter}:Interval"));
        Assert.False(Edits.TargetExists(state, track, $"midiEffect:{scripter}:Nope"));

        // A recompile: Interval keeps its value, Gate comes at its default, and Delay's value stays for later.
        state = state.Apply(
            Edits.WithRuntimeParameters(
                state,
                track,
                scripter,
                [
                    new("Interval", "Interval", -24, 24, 12, string.Empty, 48, null),
                    new("Gate", "Gate", 0, 1, 0.5, string.Empty, 0, null)
                ]
            )!
        );
        Assert.Equal(
            [
                KeyValuePair.Create("Delay", 100.0), KeyValuePair.Create("Gate", 0.5),
                KeyValuePair.Create("Interval", 7.0)
            ],
            state.Tracks[0].Strip.MidiEffects[0].Parameters.OrderBy(static p => p.Key, StringComparer.Ordinal)
        );
        Assert.Null(Edits.WithRuntimeParameters(state, track, "ins_none", []));
        // A failed compile reports no parameters; the values wait for the fixed script.
        state = state.Apply(Edits.WithRuntimeParameters(state, track, scripter, [])!);
        Assert.Empty(state.Tracks[0].Strip.MidiEffects[0].RuntimeParameters!);
        Assert.Equal(7, state.Tracks[0].Strip.MidiEffects[0].Parameters["Interval"]);
        // The engine gets the script with its slot.
        var strip = Strips.Engine(state, state.Tracks[0], EngineIds.Empty.With(state));
        Assert.Equal("function HandleMIDI(e) { e.send(); }", strip.MidiEffects![0].Source);
    }

    [Fact]
    public void Every_Phase_7_command_and_field_survives_JSON() {
        var (s, _, random) = RandomProjects.Build(5, 40);
        foreach (var type in RandomProjects.CommandTypes) {
            if (RandomProjects.Command(type, s, random) is { } command) {
                var json = JsonSerializer.Serialize(command, ContractsJsonContext.Default.EditCommand);
                Assert.Equal(
                    json,
                    JsonSerializer.Serialize(
                        JsonSerializer.Deserialize(json, ContractsJsonContext.Default.EditCommand),
                        ContractsJsonContext.Default.EditCommand
                    )
                );
            }
        }
    }

    [Fact]
    public void The_fallback_algorithms_are_installed_for_the_tests() =>
        Assert.Same(FallbackMidiAlgorithms.Instance, MidiAlgorithms.Current);

    static ArticulationSet Strings() =>
        new(
            "ars_strings",
            "Strings",
            [new(1, "Legato", "leg"), new(2, "Pizzicato", "pz"), new(3, "Tremolo", null)],
            [new(1, ArticulationSwitchKind.Note, 24, null), new(2, ArticulationSwitchKind.ControlChange, 32, 2)],
            [
                new(1, ArticulationOutputKind.Note, 24, 100), new(1, ArticulationOutputKind.ControlChange, 32, 5),
                new(2, ArticulationOutputKind.Program, 0, 7), new(3, ArticulationOutputKind.Channel, 0, 5)
            ]
        );

    (string File, string Region) AudioRegion(long frames, long offset, long length) {
        state = state.Apply(
            new AudioFileUpserted(new("aud_take", "Take", "Audio Files/Take.wav", 48_000, 1, frames, 24, null, 0))
        );
        Apply(new AddTrackCommand());
        var before = state.Regions.Keys.ToHashSet();
        Apply(new AddRegionCommand(state.Tracks[^1].Id, "aud_take", 3840, offset, length));
        return ("aud_take", state.Regions.Keys.Single(k => !before.Contains(k)));
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

    IReadOnlyList<Note> Notes(string region) => state.MidiRegions[region].Notes;

    IReadOnlyList<ControlEvent> Controls(string region) => state.MidiRegions[region].Controls;

    PlaybackEvent[] Events() => new ModelCompiler().Compile(state, EngineIds.Empty.With(state)).Tracks[0].Events;

    PlaybackEvent[] NoteOns() => [..Events().Where(static e => e.Kind == PlaybackEventKind.NoteOn)];

    void Apply(params EditCommand[] commands) =>
        state = Edits.Apply(
                state,
                commands,
                ids,
                RandomProjects.Plugins,
                RandomProjects.Patches,
                RandomProjects.Analysis
            )
            .State;

    void Refused(EditCommand command) =>
        Assert.Throws<EditException>(() =>
            Edits.Apply(state, command, ids, RandomProjects.Plugins, RandomProjects.Patches, RandomProjects.Analysis)
        );
}
