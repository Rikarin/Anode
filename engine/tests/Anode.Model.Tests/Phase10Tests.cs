using Anode.Contracts;
using Anode.Dsp.Spatial;
using Anode.Ipc;
using Anode.Model.Compile;
using Anode.Model.Editing;
using Anode.Model.Storage;
using CsCheck;
using System.Text.Json.Nodes;

namespace Anode.Model.Tests;

/// <summary>
///     Phase 10 in the model (docs/11-phase-10.md): the surround format, strip formats and panners (output formats kept
///     equal to their destinations), the movie and scene markers, imported formats, panners and automation, schema 10,
///     and the compiler's formats, panners and panner automation. Also the speaker layouts, which the contract, the DSP
///     and the IPC each spell out.
/// </summary>
public sealed class Phase10Tests : IDisposable {
    readonly string folder = Directory.CreateTempSubdirectory("anode-phase10-").FullName;
    readonly SequentialIds ids = new();
    ProjectState state = ProjectDefaults.NewProject("prj_1", "Song", 48_000);

    public void Dispose() => Directory.Delete(folder, true);

    EditResult Apply(params EditCommand[] commands) {
        var result = Edits.Apply(state, commands, ids, RandomProjects.Plugins);
        state = result.State;
        return result;
    }

    Track T(string name) => state.Tracks.Single(t => t.Name == name);

    void Refused(EditCommand command, string message) {
        var e = Assert.Throws<EditException>(() => Edits.Apply(state, command, ids, RandomProjects.Plugins));
        Assert.Contains(message, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void New_projects_are_stereo_without_a_movie() {
        Apply(new AddTrackCommand("Synth", Kind: TrackKind.Instrument));
        var project = state.ToProject(string.Empty, 0, false);
        Assert.Equal(ChannelFormat.Stereo, project.Settings.SurroundFormat);
        Assert.Null(project.Movie);
        foreach (var strip in new[] { project.Master, project.Tracks[0].Strip }) {
            Assert.Equal((ChannelFormat.Stereo, ChannelFormat.Stereo), (strip.InputFormat, strip.OutputFormat));
            Assert.Equal(Panner.Default, strip.Panner);
        }

        Assert.Empty(Edits.Conform(state));
    }

    [Fact]
    public void The_surround_format_carries_the_master_and_what_feeds_it_and_keeps_panner_settings() {
        Apply(
            new AddTrackCommand("Gtr"),
            new AddTrackCommand("Keys"),
            new AddAuxCommand(2, "Verb")
        );
        Apply(new SetPannerCommand(T("Gtr").Id, Angle: 45, Azimuth: 90));
        Apply(new SetOutputCommand(T("Keys").Id, "bus:2"));
        var result = Apply(new SetSurroundFormatCommand(ChannelFormat.Surround714));
        Assert.Equal(
            (ChannelFormat.Surround714, ChannelFormat.Surround714),
            (state.Master.InputFormat, state.Master.OutputFormat)
        );
        Assert.Equal(ChannelFormat.Surround714, T("Gtr").Strip.OutputFormat);
        Assert.Equal(ChannelFormat.Surround714, T("Verb").Strip.OutputFormat);
        // Keys feeds the stereo bus of Verb: unchanged.
        Assert.Equal(ChannelFormat.Stereo, T("Keys").Strip.OutputFormat);
        Assert.Equal(PannerMode.Surround, T("Gtr").Strip.Panner.Mode);
        Assert.Equal((45.0, 90.0), (T("Gtr").Strip.Panner.Surround.Angle, T("Gtr").Strip.Panner.Object.Azimuth));
        Assert.Equal(PannerMode.Stereo, T("Keys").Strip.Panner.Mode);
        Assert.Empty(Edits.Conform(state));

        // An object, then back to stereo: the object becomes the stereo pan; undo restores the object.
        Apply(new SetPannerCommand(T("Gtr").Id, PannerMode.Object));
        var before = state;
        result = Apply(new SetSurroundFormatCommand(ChannelFormat.Stereo));
        Assert.Equal(PannerMode.Stereo, T("Gtr").Strip.Panner.Mode);
        Assert.Equal(ChannelFormat.Stereo, T("Gtr").Strip.OutputFormat);
        Assert.Equal(RandomProjects.Json(before), RandomProjects.Json(state.Apply(result.Inverse)));
    }

    [Fact]
    public void An_aux_input_format_is_its_bus_format_and_routing_changes_follow_it() {
        Apply(
            new SetSurroundFormatCommand(ChannelFormat.Surround51),
            new AddTrackCommand("Gtr"),
            new AddAuxCommand(3, "Verb")
        );
        var gtr = T("Gtr").Id;
        Apply(new SetOutputCommand(gtr, "bus:3"));
        Assert.Equal(
            (ChannelFormat.Stereo, PannerMode.Stereo),
            (T("Gtr").Strip.OutputFormat, T("Gtr").Strip.Panner.Mode)
        );

        Apply(new SetStripFormatCommand(T("Verb").Id, ChannelFormat.Surround51));
        Assert.Equal(
            (ChannelFormat.Surround51, PannerMode.Surround),
            (T("Gtr").Strip.OutputFormat, T("Gtr").Strip.Panner.Mode)
        );

        Apply(new SetOutputCommand(gtr, "output:2"));
        Assert.Equal(ChannelFormat.Stereo, T("Gtr").Strip.OutputFormat);
        Apply(new SetOutputCommand(gtr, "bus:3"), new RemoveTrackCommand(T("Verb").Id));
        // A bus without an aux is stereo.
        Assert.Equal(ChannelFormat.Stereo, T("Gtr").Strip.OutputFormat);
        Apply(new SetOutputCommand(gtr, "master"));
        Assert.Equal(ChannelFormat.Surround51, T("Gtr").Strip.OutputFormat);
    }

    [Fact]
    public void Output_formats_equal_their_destinations_through_random_edits() =>
        Gen.Int.Sample(
            static seed => {
                var (state, ids, random) = RandomProjects.Build(seed);
                Assert.Empty(Edits.Conform(state));
                for (var i = 0; i < 20; i++) {
                    var type = i % 3 == 0
                        ? RandomProjects.Pick(
                            random,
                            [
                                typeof(SetSurroundFormatCommand),
                                typeof(SetStripFormatCommand),
                                typeof(SetOutputCommand),
                                typeof(AddAuxCommand),
                                typeof(RemoveTrackCommand),
                                typeof(SetPannerCommand)
                            ]
                        )
                        : RandomProjects.Pick(random, RandomProjects.CommandTypes);
                    if (RandomProjects.Command(type, state, random) is not { } command) {
                        continue;
                    }

                    try {
                        state = Edits.Apply(
                                state,
                                command,
                                ids,
                                RandomProjects.Plugins,
                                RandomProjects.Patches,
                                RandomProjects.Analysis,
                                RandomProjects.Notation
                            )
                            .State;
                    } catch (EditException) {
                        continue;
                    }

                    Assert.Empty(Edits.Conform(state));
                    Assert.True(
                        state.Tracks.Count(static t => t.Strip.Panner.Mode == PannerMode.Object) <= Edits.MaxObjects
                    );
                }
            },
            iter: 60
        );

    [Fact]
    public void Panners_are_checked_against_their_output_and_ranges() {
        Apply(
            new AddTrackCommand("Gtr"),
            new AddTrackCommand("Synth", Kind: TrackKind.Instrument),
            new AddAuxCommand(4, "Bus")
        );
        var gtr = T("Gtr").Id;
        Refused(new SetPannerCommand(gtr, PannerMode.Surround), "surround output");
        Refused(new SetPannerCommand(gtr, PannerMode.Object), "surround project");
        Apply(new SetSurroundFormatCommand(ChannelFormat.Surround714));
        Refused(new SetPannerCommand(gtr, PannerMode.Stereo), "Surround Panner");
        Refused(new SetPannerCommand(gtr, Angle: 181), "angle");
        Refused(new SetPannerCommand(gtr, LfeLevelDb: 1), "LFE");
        Refused(new SetPannerCommand(gtr, Distance: double.NaN), "distance");
        Refused(new SetStripFormatCommand(T("Synth").Id, ChannelFormat.Surround51), "mono or stereo");
        Apply(new SetStripFormatCommand(T("Bus").Id, ChannelFormat.Surround714), new SetOutputCommand(gtr, "bus:4"));
        Assert.Equal(PannerMode.Surround, T("Gtr").Strip.Panner.Mode);
        Refused(new SetPannerCommand(gtr, PannerMode.Object), "master");

        Apply(
            new SetOutputCommand(gtr, "master"),
            new SetPannerCommand(gtr, PannerMode.Object, Azimuth: -135, ObjectElevation: 30)
        );
        Assert.Equal(new(-135, 30, 1, 0), T("Gtr").Strip.Panner.Object);
        // An object sent to a bus leaves object mode.
        Apply(new SetOutputCommand(gtr, "bus:4"));
        Assert.Equal(PannerMode.Surround, T("Gtr").Strip.Panner.Mode);
    }

    [Fact]
    public void A_project_has_at_most_118_objects() {
        Apply(new SetSurroundFormatCommand(ChannelFormat.Surround714));
        for (var i = 0; i < Edits.MaxObjects; i++) {
            Apply(new AddTrackCommand($"O{i}"));
            Apply(new SetPannerCommand(state.Tracks[^1].Id, PannerMode.Object));
        }

        Apply(new AddTrackCommand("One more"));
        Refused(new SetPannerCommand(T("One more").Id, PannerMode.Object), "118");
    }

    [Fact]
    public void Panner_parameters_are_automation_and_Smart_Control_targets() {
        Apply(new AddTrackCommand("Gtr"), new AddVcaCommand("V"));
        var gtr = T("Gtr").Id;
        Apply(new SetAutomationCommand(PannerTargets.ObjectAzimuth, [new(0, 30, 0), new(3840, -110, 0)], gtr));
        Refused(new SetAutomationCommand(PannerTargets.SurroundLfe, [new(0, 3, 0)], gtr), "-144…0");
        Refused(
            new SetAutomationCommand(PannerTargets.SurroundAngle, [new(0, 0, 0)], T("V").Id),
            "not an automation target"
        );
        Refused(new SetAutomationCommand(PannerTargets.SurroundAngle, [new(0, 0, 0)]), "not an automation target");

        var track = Edits.WithParameter(T("Gtr"), PannerTargets.SurroundDiversity, 2, RandomProjects.Plugins);
        Assert.Equal(1, track.Strip.Panner.Surround.Diversity);
        Assert.Equal(-144, AutomationPass.Controls(T("Gtr"))[PannerTargets.SurroundLfe]);
    }

    [Fact]
    public void Moving_a_panner_in_touch_records_its_lanes() {
        Apply(new AddTrackCommand("Gtr"), new SetSurroundFormatCommand(ChannelFormat.Surround51));
        var gtr = T("Gtr").Id;
        Apply(new SetAutomationModeCommand(AutomationMode.Touch, gtr));
        var pass = new AutomationPass(1, 0);
        var before = state;
        EditCommand[] move = [new SetPannerCommand(gtr, Angle: 60, Diversity: 0.5)];
        Assert.True(AutomationPass.Records(move));
        Assert.False(AutomationPass.Records([new SetPannerCommand(gtr, PannerMode.Object)]));
        // A drag is one undo step; a mode change is not a drag.
        Assert.Equal("Change Panner", History.UndoLabels.For(new(move)));
        Assert.Equal(
            History.UndoLabels.CoalesceKey(new(move)),
            History.UndoLabels.CoalesceKey(new([new SetPannerCommand(gtr, Angle: 70, Diversity: 0.4)]))
        );
        Assert.Null(History.UndoLabels.CoalesceKey(new([new SetPannerCommand(gtr, PannerMode.Surround)])));
        var after = Edits.Apply(state, move, ids).State;
        var ops = pass.Record(before, after, 960, ids);
        Assert.Equal(
            [PannerTargets.SurroundAngle, PannerTargets.SurroundDiversity],
            ops.Cast<AutomationLaneUpserted>().Select(static o => o.Lane.Target).Order(StringComparer.Ordinal)
        );
    }

    [Fact]
    public void The_movie_is_set_moved_and_removed_with_undo() {
        var movie = new Movie(
            "Movies/Picture.mov",
            "Picture",
            true,
            0,
            0,
            new(25, 1),
            3600,
            60,
            1500,
            1920,
            1080,
            "h264",
            new("aac", 2, 48_000)
        );
        Refused(new SetMoviePositionCommand(960), "no movie");
        Refused(new RemoveMovieCommand(), "no movie");
        Refused(new SetMovieCommand(movie with { FrameRate = new(25, 0) }), "frame rate");
        Refused(new SetMovieCommand(movie with { DurationSeconds = 0 }), "lasts");
        var set = Apply(new SetMovieCommand(movie));
        Assert.Equal(movie, state.Movie);
        Assert.Equal(new MovieChanged(movie), Assert.Single(set.Forward));
        Assert.Equal(movie, state.ToProject(string.Empty, 0, false).Movie);

        Refused(new SetMoviePositionCommand(OffsetSeconds: 60), "offset");
        Refused(new SetMoviePositionCommand(-1), "movie start");
        var moved = Apply(new SetMoviePositionCommand(3840, 1.5));
        Assert.Equal((3840L, 1.5), (state.Movie!.StartTick, state.Movie.OffsetSeconds));
        Assert.Equal(movie, state.Apply(moved.Inverse).Movie);
        var removed = Apply(new RemoveMovieCommand());
        Assert.Null(state.Movie);
        Assert.Equal(3840, state.Apply(removed.Inverse).Movie!.StartTick);
    }

    [Fact]
    public void Scene_markers_are_named_in_time_order_and_replace_the_previous_ones() {
        Apply(new AddMarkerCommand("Verse", 0));
        Apply(new AddSceneMarkersCommand([7680, 3840, 3840]));
        Assert.Equal(
            [
                ("Verse", 0L, MarkerKind.Marker), ("Scene 1", 3840, MarkerKind.Scene),
                ("Scene 2", 7680, MarkerKind.Scene)
            ],
            state.Markers.Values.OrderBy(static m => m.Tick).Select(static m => (m.Name, m.Tick, m.Kind))
        );
        Apply(new AddSceneMarkersCommand([1920]));
        Assert.Equal(
            ["Scene 1", "Verse"],
            state.Markers.Values.Select(static m => m.Name).Order(StringComparer.Ordinal)
        );
        Apply(new AddSceneMarkersCommand([960], false));
        Assert.Equal(2, state.Markers.Values.Count(static m => m.Kind == MarkerKind.Scene));

        var verse = state.Markers.Values.Single(static m => m.Name == "Verse");
        Apply(new SetMarkerCommand(verse.Id, Kind: MarkerKind.Scene));
        Assert.Equal(MarkerKind.Scene, state.Markers[verse.Id].Kind);
        Refused(new AddMarkerCommand("Bad", 0, Kind: (MarkerKind)9), "marker kind");
    }

    [Fact]
    public void Imported_tracks_bring_formats_panners_and_automation() {
        Apply(new SetSurroundFormatCommand(ChannelFormat.Surround714));
        var objectPanner = Panner.Default with { Mode = PannerMode.Object, Object = new(30, 0, 1, 0) };
        Apply(
            new ImportTracksCommand(
                new(
                    [
                        new("bed", "Bed", TrackKind.Audio, null, [], [], ChannelFormat.Surround714),
                        new(
                            "obj",
                            "Object 1",
                            TrackKind.Audio,
                            null,
                            [],
                            [],
                            ChannelFormat.Mono,
                            objectPanner,
                            [
                                new(
                                    "l1",
                                    "ignored",
                                    PannerTargets.ObjectAzimuth,
                                    [new(0, 30, 0), new(3840, -135, 0)]
                                )
                            ]
                        )
                    ]
                )
            )
        );
        Assert.Equal(
            (ChannelFormat.Surround714, ChannelFormat.Surround714, PannerMode.Surround),
            (T("Bed").Strip.InputFormat, T("Bed").Strip.OutputFormat, T("Bed").Strip.Panner.Mode)
        );
        Assert.Equal(objectPanner, T("Object 1").Strip.Panner);
        Assert.Equal(ChannelFormat.Mono, T("Object 1").Strip.InputFormat);
        var lane = state.LaneOf(T("Object 1").Id, PannerTargets.ObjectAzimuth)!;
        Assert.Equal([30.0, -135], lane.Points.Select(static p => p.Value));

        Refused(
            new ImportTracksCommand(
                new(
                    [
                        new(
                            "x",
                            "X",
                            TrackKind.Audio,
                            null,
                            [],
                            [],
                            Panner: objectPanner with { Object = new(0, 91, 1, 0) }
                        )
                    ]
                )
            ),
            "object elevation"
        );
        Refused(
            new ImportTracksCommand(
                new(
                    [
                        new(
                            "x",
                            "X",
                            TrackKind.Audio,
                            null,
                            [],
                            [],
                            Automation: [new("l", "t", "pan", [new(0, 0, 0)]), new("m", "t", "pan", [new(0, 1, 0)])]
                        )
                    ]
                )
            ),
            "one lane per target"
        );
    }

    [Fact]
    public void A_schema_9_package_reads_stereo_without_a_movie() {
        var (random, randomIds, _) = RandomProjects.Build(5, 10);
        state = Edits.Apply(random, new AddMarkerCommand("Verse", 3840), randomIds).State;
        ProjectPackage.Save(folder, state);
        var path = Path.Combine(folder, "project.json");
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(PackageMigrations.CurrentSchemaVersion, (int)root["schemaVersion"]!);
        root["schemaVersion"] = 9;
        root["settings"]!.AsObject().Remove("surroundFormat");
        Strip(root["master"]!.AsObject());
        foreach (var marker in root["markers"]!.AsArray()) {
            marker!.AsObject().Remove("kind");
        }

        File.WriteAllText(path, root.ToJsonString());
        foreach (var file in Directory.EnumerateFiles(Path.Combine(folder, "tracks"))) {
            var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            json["schemaVersion"] = 9;
            Strip(json["track"]!["strip"]!.AsObject());
            File.WriteAllText(file, json.ToJsonString());
        }

        var loaded = ProjectPackage.Load(folder);
        Assert.Equal(RandomProjects.Json(state), RandomProjects.Json(loaded));
        Assert.Equal(ChannelFormat.Stereo, loaded.Settings.SurroundFormat);
        Assert.Null(loaded.Movie);
        Assert.All(
            loaded.Tracks.Select(static t => t.Strip).Append(loaded.Master),
            static s => Assert.Equal(
                (ChannelFormat.Stereo, ChannelFormat.Stereo, Panner.Default),
                (s.InputFormat, s.OutputFormat, s.Panner)
            )
        );
        Assert.All(loaded.Markers.Values, static m => Assert.Equal(MarkerKind.Marker, m.Kind));

        static void Strip(JsonObject strip) {
            strip.Remove("inputFormat");
            strip.Remove("outputFormat");
            strip.Remove("panner");
        }
    }

    [Fact]
    public void Surround_content_and_the_movie_survive_the_package_and_bad_formats_conform() {
        Apply(
            new AddTrackCommand("Gtr"),
            new SetSurroundFormatCommand(ChannelFormat.Surround51),
            new SetMovieCommand(
                new(
                    "/Volumes/Media/Picture.mov",
                    "Picture",
                    false,
                    1920,
                    0.5,
                    new(24000, 1001),
                    0,
                    10,
                    240,
                    640,
                    360,
                    "prores",
                    null
                )
            ),
            new AddSceneMarkersCommand([960])
        );
        Apply(new SetPannerCommand(T("Gtr").Id, PannerMode.Object, Azimuth: 110, Size: 0.3));
        ProjectPackage.Save(folder, state);
        Assert.Equal(RandomProjects.Json(state), RandomProjects.Json(ProjectPackage.Load(folder)));

        // A file whose output format disagrees with the routing reads conformed.
        var file = Directory.EnumerateFiles(Path.Combine(folder, "tracks")).Single();
        var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        json["track"]!["strip"]!["outputFormat"] = "mono";
        File.WriteAllText(file, json.ToJsonString());
        Assert.Equal(ChannelFormat.Surround51, ProjectPackage.Load(folder).Tracks[0].Strip.OutputFormat);
    }

    [Fact]
    public void The_compiler_sends_formats_panners_and_panner_automation_in_engine_units() {
        Apply(new AddTrackCommand("Gtr"), new SetSurroundFormatCommand(ChannelFormat.Surround714));
        var gtr = T("Gtr").Id;
        Apply(
            new SetStripFormatCommand(gtr, ChannelFormat.Mono),
            new SetPannerCommand(gtr, Angle: 110, Diversity: 0.8, CentreLevelDb: -6, LfeLevelDb: -144, Spread: 90),
            new SetAutomationCommand(PannerTargets.SurroundLfe, [new(0, -144, 0), new(3840, 0, 0)], gtr),
            new SetAutomationCommand(PannerTargets.ObjectAzimuth, [new(0, 30, 0), new(3840, -135, 0)], gtr)
        );
        var engine = EngineIds.Empty.With(state);
        var strip = Strips.Engine(state, T("Gtr"), engine);
        Assert.Equal(
            (EngineChannelFormat.Mono, EngineChannelFormat.Surround714),
            (strip.InputFormat, strip.OutputFormat)
        );
        var panner = strip.Panner!;
        Assert.Equal(EnginePannerMode.Surround, panner.Mode);
        Assert.Equal(
            (110f, 0.8f, 0f, 90f),
            (panner.Surround.Angle, panner.Surround.Diversity, panner.Surround.LfeGain, panner.Surround.Spread)
        );
        Assert.InRange(panner.Surround.CentreGain, 0.501f, 0.502f);
        Assert.Equal(new(0, 0, 1, 0), panner.Object);
        var master = Strips.Engine(state, null, engine);
        Assert.Equal(
            (EngineChannelFormat.Surround714, EngineChannelFormat.Surround714, null),
            (master.InputFormat, master.OutputFormat, master.Panner)
        );
        Assert.True(Strips.StructureDiffers(strip, strip with { Panner = null }));
        Assert.True(Strips.StructureDiffers(strip, strip with { InputFormat = EngineChannelFormat.Stereo }));
        Assert.False(
            Strips.StructureDiffers(
                strip,
                strip with { Panner = panner with { Surround = panner.Surround with { Angle = 0 } } }
            )
        );

        var targets = engine.AutomationTargets(state);
        var lfe = targets.Single(static t => t.Kind == "surround");
        var azimuth = targets.Single(static t => t.Kind == "object");
        Assert.Equal(("lfe", "azimuth"), (lfe.ParameterId, azimuth.ParameterId));
        var model = new ModelCompiler().Compile(state, engine);
        var events = model.Tracks[0]
            .Events.Where(static e => e.Kind == Ipc.Segments.PlaybackEventKind.Automation)
            .ToArray();
        var lfeEvents = events.Where(e => e.Target == lfe.Index).ToArray();
        Assert.Equal(0f, lfeEvents[0].Value);
        Assert.InRange(lfeEvents[^1].Value, 0.999f, 1.001f);
        var azimuthEvents = events.Where(e => e.Target == azimuth.Index).ToArray();
        Assert.Equal((30f, -135f), (azimuthEvents[0].Value, azimuthEvents[^1].Value));

        // Back to stereo: the stereo pan (no panner).
        Apply(new SetSurroundFormatCommand(ChannelFormat.Stereo));
        Assert.Null(Strips.Engine(state, T("Gtr"), EngineIds.Empty.With(state)).Panner);
    }

    [Fact]
    public void The_speaker_layouts_agree_across_the_contract_the_DSP_and_the_IPC() {
        Assert.Equal(Enum.GetNames<ChannelFormat>(), Enum.GetNames<SpeakerFormat>());
        Assert.Equal(Enum.GetNames<ChannelFormat>(), Enum.GetNames<EngineChannelFormat>());
        Assert.Equal(
            [1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 16],
            SpeakerLayouts.All.Select(static l => l.ChannelCount)
        );
        foreach (var layout in SpeakerLayouts.All) {
            var dsp = Dsp.Spatial.SpeakerLayout.Of((SpeakerFormat)layout.Format);
            Assert.Equal(layout.ChannelCount, dsp.ChannelCount);
            Assert.Equal(layout.Speakers.ToList().FindIndex(static s => s.Lfe), dsp.LfeIndex);
            Assert.Equal(layout.HasHeight, dsp.HasHeight);
            for (var c = 0; c < layout.ChannelCount; c++) {
                var speaker = layout.Speakers[c];
                Assert.Equal(
                    (speaker.Label, speaker.Azimuth, speaker.Elevation),
                    (dsp.Label(c), (double)dsp.Azimuths[c], (double)dsp.Elevations[c])
                );
            }

            Assert.Equal(layout.Speakers.Count, layout.Speakers.Select(static s => s.Label).Distinct().Count());
        }

        Assert.Equal(["M+030", "M-030"], SpeakerLayouts.Of(ChannelFormat.Stereo).Speakers.Select(static s => s.Label));
        Assert.Equal(7, SpeakerLayouts.Of(ChannelFormat.Surround714).IndexOf(SpeakerLabels.RightRear));
    }

    [Fact]
    public void A_listen_in_frame_header_round_trips() {
        var frame = new byte[ListenIn.HeaderBytes + 3];
        ListenIn.WriteHeader(frame, 7, 123_456_789_012, 3, ListenIn.FlagPlaying);
        frame.AsSpan()[^3..].Fill(9);
        Assert.True(ListenIn.TryReadHeader(frame, out var sequence, out var sample, out var flags, out var packet));
        Assert.Equal((7u, 123_456_789_012L, ListenIn.FlagPlaying), (sequence, sample, flags));
        Assert.Equal([9, 9, 9], packet.ToArray());
        Assert.False(ListenIn.TryReadHeader(frame.AsSpan(0, ListenIn.HeaderBytes + 2), out _, out _, out _, out _));
    }
}
