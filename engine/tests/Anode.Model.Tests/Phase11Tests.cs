using Anode.Contracts;
using Anode.Dsp.Analysis.Chords;
using Anode.Dsp.Mastering;
using Anode.Ipc;
using Anode.Model.Editing;
using Anode.Model.Storage;
using System.Text.Json.Nodes;

namespace Anode.Model.Tests;

/// <summary>
///     Phase 11's model (docs/12-phase-11.md, workstream B): the chord track and Chord ID's result, Pitch Source and keys,
///     the Mastering Assistant's settings and analysis, the Environment and its validation, and package schema 11 with
///     its migration from 10. Also what the contract, the DSP and the IPC each spell out (chord qualities, mastering
///     bands and parameters, Environment kinds), and the contract's own arithmetic (pitch classes, root intervals,
///     section kinds). Session Players: <see cref="Phase11PlayerTests" />; the compiler:
///     <see cref="Phase11CompilerTests" />.
/// </summary>
public sealed class Phase11Tests : IDisposable {
    readonly string folder = Directory.CreateTempSubdirectory("anode-phase11-").FullName;
    readonly SequentialIds ids = new();
    ProjectState state = ProjectDefaults.NewProject("prj_1", "Song", 48_000);

    public void Dispose() => Directory.Delete(folder, true);

    static PitchName N(PitchStep step, int alter = 0) => new(step, alter);

    void Apply(params EditCommand[] commands) =>
        state = Edits.Apply(state, commands, ids, RandomProjects.Plugins, RandomProjects.Patches).State;

    EditException Refused(EditCommand command) =>
        Assert.Throws<EditException>(() => Edits.Apply(state, command, ids, RandomProjects.Plugins));

    [Fact]
    public void A_schema_10_package_reads_with_an_empty_chord_track_and_Environment_and_nothing_following() {
        // Twenty edits: a project of the earlier phases only.
        var (built, _, _) = RandomProjects.Build(7, 20);
        state = built;
        ProjectPackage.Save(folder, state);
        var path = Path.Combine(folder, ProjectPackage.ProjectFileName);
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(PackageMigrations.CurrentSchemaVersion, (int)root["schemaVersion"]!);
        root["schemaVersion"] = 10;
        root.Remove("chordTrack");
        root.Remove("environment");
        File.WriteAllText(path, root.ToJsonString());
        foreach (var file in Directory.EnumerateFiles(Path.Combine(folder, ProjectPackage.TracksFolder))) {
            var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            json["schemaVersion"] = 10;
            json.Remove("sessionPlayerRegions");
            foreach (var list in (string[])["regions", "midiRegions"]) {
                foreach (var region in (json[list] as JsonArray ?? []).OfType<JsonObject>()) {
                    region.Remove("pitchSource");
                    region.Remove("key");
                }
            }

            File.WriteAllText(file, json.ToJsonString());
        }

        var loaded = ProjectPackage.Load(folder);
        Assert.Equal(RandomProjects.Json(state), RandomProjects.Json(loaded));
        Assert.Equal((0, 0), (loaded.ChordTrack.Chords.Count, loaded.ChordTrack.Scales.Count));
        Assert.Equal(
            (0, 0, 0),
            (loaded.Environment.Layers.Count, loaded.Environment.Objects.Count, loaded.Environment.Cables.Count)
        );
        Assert.Empty(loaded.SessionPlayerRegions);
        Assert.All(
            loaded.MidiRegions.Values,
            static r => Assert.Equal((PitchSourceMode.Off, null), (r.PitchSource, r.Key))
        );
        Assert.All(
            loaded.Regions.Values,
            static r => Assert.Equal((PitchSourceMode.Off, null), (r.PitchSource, r.Key))
        );
    }

    [Fact]
    public void A_package_keeps_what_its_commands_could_make_and_leaves_the_rest_out() {
        Apply(
            new AddTrackCommand(Kind: TrackKind.Instrument),
            new AddChordsCommand([new(0, N(PitchStep.C), ChordQualities.Major), new(3840, N(PitchStep.A), "m7")])
        );
        var track = state.Tracks[0].Id;
        Apply(
            new AddMidiRegionCommand(track, 0, 3840),
            new AddEnvironmentObjectCommand(EnvironmentObjectKind.PhysicalInput, 0, 0),
            new AddEnvironmentObjectCommand(EnvironmentObjectKind.TrackInput, 100, 0, TrackId: track)
        );
        var (input, target) = (state.Environment.Objects[0].Id, state.Environment.Objects[1].Id);
        Apply(new ConnectEnvironmentCommand(input, 0, target, 0));
        ProjectPackage.Save(folder, state);
        Assert.Equal(RandomProjects.Json(state), RandomProjects.Json(ProjectPackage.Load(folder)));

        // A damaged file: an unknown quality, a cable from an outlet the kind lacks, a track input to no track.
        var path = Path.Combine(folder, ProjectPackage.ProjectFileName);
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        root["chordTrack"]!["chords"]!.AsArray()[1]!["quality"] = "min";
        root["environment"]!["cables"]!.AsArray()[0]!["outlet"] = 3;
        root["environment"]!["objects"]!.AsArray()[1]!["trackId"] = "trk_gone";
        File.WriteAllText(path, root.ToJsonString());
        var loaded = ProjectPackage.Load(folder);
        Assert.Single(loaded.ChordTrack.Chords);
        Assert.Single(loaded.Environment.Objects);
        Assert.Empty(loaded.Environment.Cables);
    }

    [Fact]
    public void Chords_are_one_per_tick_in_order_and_edits_keep_them_so() {
        Apply(
            new AddChordsCommand(
                [
                    new(3840, N(PitchStep.F), ChordQualities.MajorSeventh),
                    new(0, N(PitchStep.C), ChordQualities.Major),
                    new(7680, N(PitchStep.G), ChordQualities.Seventh, N(PitchStep.B))
                ]
            )
        );
        Assert.Equal([0L, 3840, 7680], state.ChordTrack.Chords.Select(static c => c.Tick));
        // A chord at the tick of another replaces it.
        Apply(new AddChordsCommand([new(3840, N(PitchStep.D), ChordQualities.MinorSeventh)]));
        Assert.Equal(3, state.ChordTrack.Chords.Count);
        Assert.Equal(N(PitchStep.D), state.ChordTrack.Chords[1].Root);
        var (c, d) = (state.ChordTrack.Chords[0], state.ChordTrack.Chords[1]);
        Assert.Contains("one tick", Refused(new EditChordsCommand([d with { Tick = 0 }])).Message);
        // Two chords swap places in one edit.
        Apply(new EditChordsCommand([c with { Tick = 3840 }, d with { Tick = 0 }]));
        Assert.Equal(N(PitchStep.D), state.ChordTrack.Chords[0].Root);
        Refused(new AddChordsCommand([new(0, N(PitchStep.C), "min")]));
        Refused(new AddChordsCommand([new(0, N(PitchStep.C, 3), ChordQualities.Major)]));
        Refused(new DeleteChordsCommand(["crd_none"]));
        Apply(
            new SetScalesCommand([new(7680, N(PitchStep.A), ScaleKind.Dorian), new(0, N(PitchStep.C), ScaleKind.Major)])
        );
        Assert.Equal([0L, 7680], state.ChordTrack.Scales.Select(static s => s.Tick));
        Refused(
            new SetScalesCommand([new(0, N(PitchStep.C), ScaleKind.Major), new(0, N(PitchStep.D), ScaleKind.Minor)])
        );
    }

    [Theory]
    [InlineData(ChordIdWrite.Replace)]
    [InlineData(ChordIdWrite.Merge)]
    [InlineData(ChordIdWrite.None)]
    public void Chord_ID_writes_the_chord_track_by_its_write_and_sets_keys_in_one_step(ChordIdWrite write) {
        Apply(new AddTrackCommand(Kind: TrackKind.Instrument));
        Apply(
            new AddMidiRegionCommand(state.Tracks[0].Id, 0, 15_360),
            new AddChordsCommand(
                [new(0, N(PitchStep.C), ChordQualities.Major), new(7680, N(PitchStep.G), ChordQualities.Major)]
            )
        );
        var region = state.MidiRegions.Keys.Single();
        var before = state;
        var result = Edits.Apply(
            state,
            new ApplyChordIdCommand(
                new(
                    0,
                    7680,
                    write,
                    [
                        new(0, 1920, N(PitchStep.A), ChordQualities.Minor, null, 0.9),
                        // Within a beat of the chord at 0: merge leaves it out.
                        new(480, 1440, N(PitchStep.E), ChordQualities.Minor, null, 0.4),
                        new(1920, 1920, N(PitchStep.D), ChordQualities.Minor, null, 0.8),
                        new(3840, 3840, N(PitchStep.F), ChordQualities.Major, null, 0.8)
                    ],
                    [new(region, new(N(PitchStep.A), KeyMode.Minor), 0.7)]
                )
            ),
            ids
        );
        state = result.State;
        var expected = write switch {
            ChordIdWrite.Replace => new[] { "a", "e", "d", "f", "g" },
            ChordIdWrite.Merge => ["c", "d", "f", "g"],
            _ => ["c", "g"]
        };
        Assert.Equal(expected, state.ChordTrack.Chords.Select(static c => c.Root.Step.ToString().ToLowerInvariant()));
        Assert.Equal(new(N(PitchStep.A), KeyMode.Minor), state.MidiRegions[region].Key);
        Assert.Equal(RandomProjects.Json(before), RandomProjects.Json(state.Apply(result.Inverse)));
    }

    [Fact]
    public void Pitch_Source_and_keys_are_set_on_MIDI_and_audio_regions_and_audio_follows_by_root_only() {
        Apply(new AddTrackCommand(Kind: TrackKind.Instrument), new AddTrackCommand(Kind: TrackKind.Audio));
        state = state.Apply(
            new AudioFileUpserted(new("aud_1", "Bass", "Audio Files/Bass.wav", 48_000, 1, 480_000, 24, null, 0))
        );
        Apply(
            new AddMidiRegionCommand(state.Tracks[0].Id, 0, 3840),
            new AddRegionCommand(state.Tracks[1].Id, "aud_1", 0)
        );
        var (midi, audio) = (state.MidiRegions.Keys.Single(), state.Regions.Keys.Single());
        Apply(new SetPitchSourceCommand([midi, audio], PitchSourceMode.Root));
        Assert.Equal(PitchSourceMode.Root, state.Regions[audio].PitchSource);
        Apply(new SetPitchSourceCommand([midi], PitchSourceMode.Chords));
        Assert.Contains(
            "audio region",
            Refused(new SetPitchSourceCommand([midi, audio], PitchSourceMode.Chords)).Message
        );
        Apply(new SetRegionKeyCommand([midi, audio], new(N(PitchStep.E, -1), KeyMode.Major)));
        Assert.Equal(3, Edits.KeyRootOf(state, state.Regions[audio].Key, 0));
        Apply(new SetRegionKeyCommand([audio], null));
        Assert.Null(state.Regions[audio].Key);
        // Without a key of its own, the project's at its start.
        Assert.Equal(0, Edits.KeyRootOf(state, null, 0));
        Refused(new SetPitchSourceCommand(["mid_none"], PitchSourceMode.Root));
    }

    [Fact]
    public void The_Mastering_Assistant_keeps_its_settings_as_parameters_and_its_analysis_in_its_insert() {
        Assert.Contains(
            "no Mastering Assistant",
            Refused(new SetMasteringCommand(Character: MasteringCharacter.Valve)).Message
        );
        var (added, assistant) = Edits.AddMasteringAssistant(state, ids);
        state = state.Apply(added);
        Assert.Equal(MasteringSettings.Default, Edits.MasteringSettingsOf(state.Master.Inserts[^1]));
        Apply(new SetMasteringCommand(Character: MasteringCharacter.Valve, TargetLufs: -10, MatchedBypass: true));
        var settings = Edits.MasteringSettingsOf(Edits.MasteringAssistantOf(state)!);
        Assert.Equal(
            (MasteringCharacter.Valve, -10.0, true),
            (settings.Character, settings.TargetLufs, settings.MatchedBypass)
        );
        Assert.Equal(1, state.Master.Inserts[^1].Parameters[MasteringParameters.Character]);
        Refused(new SetMasteringCommand(TargetLufs: -30));
        Refused(new SetMasteringCommand(CeilingDbtp: 0.5));
        Refused(new SetMasteringCommand(Width: 3));
        var analysis = RandomProjects.MasteringAnalysis(new(1));
        Apply(new SetMasteringAnalysisCommand(assistant.Id, analysis));
        Assert.Equal(analysis.IntegratedLufs, Edits.MasteringAssistantOf(state)!.Mastering!.IntegratedLufs);
        Refused(new SetMasteringAnalysisCommand(assistant.Id, analysis with { SpectrumDb = [0, 1] }));
        Refused(new SetMasteringAnalysisCommand("ins_none", analysis));
        // Settings and analysis survive the package.
        ProjectPackage.Save(folder, state);
        Assert.Equal(RandomProjects.Json(state), RandomProjects.Json(ProjectPackage.Load(folder)));
    }

    [Fact]
    public void The_Environment_refuses_cycles_duplicates_and_outlets_and_inlets_its_kinds_lack() {
        Apply(
            new AddEnvironmentObjectCommand(EnvironmentObjectKind.PhysicalInput, 0, 0),
            new AddEnvironmentObjectCommand(EnvironmentObjectKind.Transformer, 100, 0),
            new AddEnvironmentObjectCommand(EnvironmentObjectKind.Monitor, 200, 0),
            new AddEnvironmentObjectCommand(EnvironmentObjectKind.SequencerInput, 300, 0)
        );
        // The first object makes the first layer.
        Assert.Equal("Layer 1", Assert.Single(state.Environment.Layers).Name);
        var (input, transformer, monitor, sequencer) = (
            state.Environment.Objects[0].Id, state.Environment.Objects[1].Id, state.Environment.Objects[2].Id,
            state.Environment.Objects[3].Id);
        Apply(
            new ConnectEnvironmentCommand(input, 0, transformer, 0),
            new ConnectEnvironmentCommand(transformer, 1, monitor, 0),
            new ConnectEnvironmentCommand(monitor, 0, sequencer, 0)
        );
        Assert.Contains("loop", Refused(new ConnectEnvironmentCommand(monitor, 0, transformer, 0)).Message);
        Assert.Contains("loop", Refused(new ConnectEnvironmentCommand(monitor, 0, monitor, 0)).Message);
        Assert.Contains("already", Refused(new ConnectEnvironmentCommand(input, 0, transformer, 0)).Message);
        Assert.Contains("outlets", Refused(new ConnectEnvironmentCommand(transformer, 2, monitor, 0)).Message);
        Assert.Contains("inlets", Refused(new ConnectEnvironmentCommand(monitor, 0, input, 0)).Message);
        Refused(new AddEnvironmentObjectCommand(EnvironmentObjectKind.SequencerInput, 0, 0));
        Refused(new AddEnvironmentObjectCommand(EnvironmentObjectKind.PhysicalInput, 0, 0));
        Apply(new AddEnvironmentObjectCommand(EnvironmentObjectKind.PhysicalInput, 0, 100, Port: "Keys"));
        // Settings only on the kinds that use them, in their ranges.
        Refused(new AddEnvironmentObjectCommand(EnvironmentObjectKind.Monitor, 0, 0, Port: "Keys"));
        Refused(
            new AddEnvironmentObjectCommand(
                EnvironmentObjectKind.Fader,
                0,
                0,
                Parameters: new Dictionary<string, double> { ["channel"] = 17 }
            )
        );
        Refused(
            new AddEnvironmentObjectCommand(
                EnvironmentObjectKind.Transformer,
                0,
                0,
                Operations: [new(TransformField.Position, TransformOperationKind.Add, 10)]
            )
        );
        Refused(new AddEnvironmentObjectCommand(EnvironmentObjectKind.DelayLine, 0, 0, Taps: [new(0, 0, 0)]));
        // A new fader holds its kind's parameters at their defaults.
        Apply(new AddEnvironmentObjectCommand(EnvironmentObjectKind.Fader, 0, 0));
        Assert.Equal(7, state.Environment.Objects[^1].Parameters["number"]);
        // Removing an object takes its cables; removing a layer, its objects.
        Apply(new RemoveEnvironmentObjectsCommand([transformer]));
        Assert.Single(state.Environment.Cables);
        Apply(new RemoveEnvironmentLayerCommand(state.Environment.Layers[0].Id));
        Assert.Equal(
            (0, 0, 0),
            (state.Environment.Layers.Count, state.Environment.Objects.Count, state.Environment.Cables.Count)
        );
    }

    [Fact]
    public void Environment_objects_name_tracks_and_controls_that_exist_and_lose_them_with_their_track() {
        Apply(new AddTrackCommand(Kind: TrackKind.Instrument), new AddTrackCommand(Kind: TrackKind.Audio));
        var (instrument, audio) = (state.Tracks[0].Id, state.Tracks[1].Id);
        Refused(new AddEnvironmentObjectCommand(EnvironmentObjectKind.TrackInput, 0, 0, TrackId: audio));
        Refused(
            new AddEnvironmentObjectCommand(
                EnvironmentObjectKind.ControlTarget,
                0,
                0,
                TrackId: audio,
                Address: "insert:x:y"
            )
        );
        Apply(
            new AddEnvironmentObjectCommand(EnvironmentObjectKind.TrackInput, 0, 0, TrackId: instrument),
            new AddEnvironmentObjectCommand(
                EnvironmentObjectKind.ControlTarget,
                0,
                0,
                TrackId: audio,
                Address: "volume"
            )
        );
        var control = state.Environment.Objects[1].Id;
        // An empty address clears it.
        Apply(new SetEnvironmentObjectCommand(control, Address: string.Empty));
        Assert.Null(state.Environment.Objects[1].Address);
        Apply(new SetEnvironmentObjectCommand(control, Address: "pan"), new RemoveTrackCommand(instrument));
        Assert.Null(state.Environment.Objects[0].TrackId);
        Apply(new RemoveTrackCommand(audio));
        Assert.Equal((null, null), (state.Environment.Objects[1].TrackId, state.Environment.Objects[1].Address));
    }

    [Fact]
    public void The_chord_qualities_agree_across_the_contract_and_Chord_ID() {
        Assert.Equal(ChordQualities.All, ChordTemplates.Qualities);
        for (var i = 0; i < ChordTemplates.Qualities.Count; i++) {
            Assert.Equal(ChordQualities.Intervals(ChordQualities.All[i]), ChordTemplates.Intervals(i).ToArray());
        }

        Assert.Equal(ChordQualities.All.Count, ChordQualities.All.Distinct().Count());
        Assert.True(ChordQualities.IsKnown("m7b5"));
        Assert.False(ChordQualities.IsKnown("min"));
        Assert.Empty(ChordQualities.Intervals("min"));
    }

    [Fact]
    public void The_Mastering_Assistant_agrees_across_the_contract_and_the_DSP() {
        Assert.Equal(30, Contracts.MasteringBands.CentresHz.Count);
        Assert.Equal(
            Contracts.MasteringBands.CentresHz,
            Dsp.Mastering.MasteringBands.CentresHz.Select(static f => (double)f)
        );
        Assert.Equal(Enum.GetNames<MasteringCharacter>(), Enum.GetNames<MasteringCharacterKind>());
        Assert.Equal(MasteringParameters.PluginId, MasteringAssistant.Id);
        Assert.Equal(
            [
                MasteringParameters.Character, MasteringParameters.TargetLufs, MasteringParameters.CeilingDbtp,
                MasteringParameters.Eq, MasteringParameters.Dynamics, MasteringParameters.Width,
                MasteringParameters.MatchedBypass
            ],
            MasteringAssistant.Info.Parameters.Select(static p => p.Id)
        );
        var defaults = MasteringAssistant.Info.Parameters.ToDictionary(static p => p.Id, static p => (double)p.Default);
        var settings = MasteringSettings.Default;
        Assert.Equal(
            (settings.TargetLufs, settings.CeilingDbtp, settings.Eq, settings.Dynamics, settings.Width),
            (defaults["targetLufs"], defaults["ceilingDbtp"], defaults["eq"], defaults["dynamics"], defaults["width"])
        );
    }

    [Fact]
    public void The_Environment_kinds_agree_across_the_contract_and_the_IPC() {
        Assert.Equal(Enum.GetNames<EnvironmentObjectKind>(), Enum.GetNames<EngineEnvironmentKind>());
        Assert.Equal(Enum.GetValues<EnvironmentObjectKind>(), EnvironmentObjectKinds.All.Select(static k => k.Kind));
        Assert.Equal(17, EnvironmentObjectKinds.Of(EnvironmentObjectKind.ChannelSplitter).Outlets.Count);
        Assert.Empty(EnvironmentObjectKinds.Of(EnvironmentObjectKind.PhysicalInput).Inlets);
        Assert.Empty(EnvironmentObjectKinds.Of(EnvironmentObjectKind.SequencerInput).Outlets);
        Assert.Equal(2, EnvironmentObjectKinds.Of(EnvironmentObjectKind.Transformer).Outlets.Count);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(0, 7, -5)]
    [InlineData(0, 5, 5)]
    [InlineData(0, 6, -6)]
    [InlineData(9, 0, 3)]
    [InlineData(11, 1, 2)]
    [InlineData(2, 11, -3)]
    public void Pitch_Source_transposes_by_the_shortest_interval(int from, int to, int interval) =>
        Assert.Equal(interval, PitchSources.RootInterval(from, to));

    [Fact]
    public void Spelled_pitches_have_their_classes() {
        Assert.Equal(0, PitchSources.PitchClass(new(PitchStep.C, 0)));
        Assert.Equal(11, PitchSources.PitchClass(new(PitchStep.C, -1)));
        Assert.Equal(0, PitchSources.PitchClass(new(PitchStep.B, 1)));
        Assert.Equal(10, PitchSources.PitchClass(new(PitchStep.B, -1)));
        Assert.Equal(6, PitchSources.PitchClass(new(PitchStep.F, 1)));
    }

    [Theory]
    [InlineData("Intro", SectionKind.Intro)]
    [InlineData("Chorus 2", SectionKind.Chorus)]
    [InlineData("pre-chorus", SectionKind.Chorus)]
    [InlineData("Bridge", SectionKind.Bridge)]
    [InlineData("Ending", SectionKind.Outro)]
    [InlineData("Solo", SectionKind.Verse)]
    [InlineData(null, SectionKind.Verse)]
    public void Section_kinds_come_from_section_names(string? name, SectionKind kind) =>
        Assert.Equal(kind, SectionKinds.Of(name));

    [Fact]
    public void Every_kit_piece_has_notes_and_none_shares_one() {
        var notes = Enum.GetValues<DrumPiece>().SelectMany(DrumKitNotes.Of).ToList();
        Assert.Equal(notes.Count, notes.Distinct().Count());
        Assert.All(Enum.GetValues<DrumPiece>(), static p => Assert.NotEmpty(DrumKitNotes.Of(p)));
        Assert.Equal(DrumKitNotes.Kick, DrumKitNotes.Of(DrumPiece.Kick)[0]);
    }
}
