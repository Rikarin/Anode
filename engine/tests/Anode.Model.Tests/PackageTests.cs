using Anode.Contracts;
using Anode.Model.Editing;
using Anode.Model.Storage;
using Anode.Model.Time;
using System.Text.Json.Nodes;

namespace Anode.Model.Tests;

/// <summary>The project package on disk: layout, schema versions, and damage.</summary>
public sealed class PackageTests : IDisposable {
    readonly string folder = Directory.CreateTempSubdirectory("anode-package-").FullName;

    public void Dispose() => Directory.Delete(folder, true);

    [Fact]
    public void A_package_is_a_root_file_and_a_file_per_track() {
        var (state, _, _) = RandomProjects.Build(7, 30);
        ProjectPackage.Save(folder, state);
        Assert.True(File.Exists(Path.Combine(folder, "project.json")));
        Assert.True(Directory.Exists(Path.Combine(folder, "Audio Files")));
        Assert.True(Directory.Exists(Path.Combine(folder, "Peaks")));
        Assert.Equal(
            state.Tracks.Select(static t => t.Id + ".json").Order(),
            Directory.EnumerateFiles(Path.Combine(folder, "tracks")).Select(Path.GetFileName).Order()
        );
        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "project.json")))!;
        Assert.Equal(PackageMigrations.CurrentSchemaVersion, root["schemaVersion"]!.GetValue<int>());
        Assert.Empty(Directory.EnumerateFiles(folder, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Saving_removes_the_files_of_removed_tracks() {
        var (state, ids, _) = RandomProjects.Build(3, 30);
        ProjectPackage.Save(folder, state);
        var fewer = Edits.Apply(state, new RemoveTrackCommand(state.Tracks[0].Id), ids).State;
        ProjectPackage.Save(folder, fewer);
        Assert.Equal(fewer.Tracks.Count, Directory.EnumerateFiles(Path.Combine(folder, "tracks")).Count());
        Assert.Equal(RandomProjects.Json(fewer), RandomProjects.Json(ProjectPackage.Load(folder)));
    }

    [Fact]
    public void A_file_without_a_schema_version_is_schema_1_and_a_newer_one_is_refused() {
        var (state, _, _) = RandomProjects.Build(5);
        ProjectPackage.Save(folder, state);
        var path = Path.Combine(folder, "project.json");
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        root.Remove("schemaVersion");
        File.WriteAllText(path, root.ToJsonString());
        Assert.Equal(RandomProjects.Json(state), RandomProjects.Json(ProjectPackage.Load(folder)));

        root["schemaVersion"] = PackageMigrations.CurrentSchemaVersion + 1;
        File.WriteAllText(path, root.ToJsonString());
        var e = Assert.Throws<PackageException>(() => ProjectPackage.Load(folder));
        Assert.Contains("newer Anode", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_schema_4_package_without_the_Phase_5_fields_reads_them_at_their_defaults() {
        var (state, _, _) = RandomProjects.Build(11, 30);
        ProjectPackage.Save(folder, state);
        var path = Path.Combine(folder, "project.json");
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        root["settings"]!.AsObject().Remove("smartTempo");
        root["settings"]!.AsObject().Remove("varispeed");
        foreach (var tempo in root["settings"]!["tempoEvents"]!.AsArray()) {
            tempo!.AsObject().Remove("shape");
        }

        File.WriteAllText(path, root.ToJsonString());
        foreach (var file in Directory.EnumerateFiles(Path.Combine(folder, "tracks"))) {
            var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            foreach (var field in new[] { "flexOn", "flexMode", "alternatives" }) {
                json["track"]!.AsObject().Remove(field);
            }

            foreach (var region in json["regions"]!.AsArray()) {
                foreach (var field in new[] { "follow", "flexMarkers", "transpose", "fineTune", "pitchNotes" }) {
                    region!.AsObject().Remove(field);
                }
            }

            File.WriteAllText(file, json.ToJsonString());
        }

        var loaded = ProjectPackage.Load(folder);
        Assert.Equal(RandomProjects.Json(state), RandomProjects.Json(loaded));
        Assert.Equal(ProjectDefaults.SmartTempo, loaded.Settings.SmartTempo);
        Assert.All(loaded.Tracks, static t => Assert.Equal((false, FlexMode.Polyphonic), (t.FlexOn, t.FlexMode)));
    }

    [Fact]
    public void A_Phase_1_package_opens_as_audio_tracks_without_MIDI_and_saves_as_the_current_schema() {
        CopyFixture("Phase1.anode");
        var state = ProjectPackage.Load(folder);
        Assert.Equal(("prj_phase1fixture", "Phase 1 Song", 96.0), (state.Id, state.Name, state.Settings.Tempo));
        Assert.False(state.Settings.MergeMidiRecording);
        Assert.Equal(["Vocal", "Guitar"], state.Tracks.Select(static t => t.Name));
        Assert.All(
            state.Tracks,
            static t => {
                Assert.Equal(TrackKind.Audio, t.Kind);
                Assert.Null(t.Instrument);
                Assert.Null(t.MidiOutput);
                Assert.Equal(new(null, 0), t.MidiInput);
            }
        );
        Assert.Equal((-2.5, -0.25), (state.Tracks[0].Strip.VolumeDb, state.Tracks[0].Strip.Pan));
        Assert.Equal(new(3, true), state.Tracks[1].Input);
        var region = Assert.Single(state.Regions.Values);
        Assert.Equal((2880L, 96_000L, 480L), (region.StartTick, region.Length, region.FadeOut));
        Assert.Empty(state.MidiRegions);

        // Saved again, it is the current schema (with a MIDI region list per track) and reads back the same.
        ProjectPackage.Save(folder, state);
        var track = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "tracks", "trk_vocal0000001.json")))!;
        Assert.Equal(PackageMigrations.CurrentSchemaVersion, track["schemaVersion"]!.GetValue<int>());
        Assert.Empty(track["midiRegions"]!.AsArray());
        Assert.Equal(RandomProjects.Json(state), RandomProjects.Json(ProjectPackage.Load(folder)));
    }

    [Fact]
    public void A_Phase_2_package_opens_with_global_tracks_of_its_tempo_and_signature_and_saves_as_the_current_schema() {
        CopyFixture("Phase2.anode");
        var state = ProjectPackage.Load(folder);
        var settings = state.Settings;
        Assert.Equal((132.0, new TimeSignature(6, 8)), (settings.Tempo, settings.Signature));
        Assert.Equal([new TempoEvent(0, 132, TempoCurve.Step)], settings.TempoEvents);
        Assert.Equal([new SignatureEvent(0, 6, 8)], settings.Signatures);
        Assert.Equal([new KeyEvent(0, 0, KeyMode.Major)], settings.Keys);
        // Four bars of 6/8 for the punch locators.
        Assert.Equal(new(CycleRecordMode.TakeFolder, false, false, 0, 4 * 2880), settings.Recording);
        Assert.True(settings.MergeMidiRecording);
        Assert.Empty(state.TakeFolders);
        Assert.Empty(state.Markers);
        Assert.Empty(state.Sections);
        Assert.Empty(state.Screensets);
        Assert.Equal(string.Empty, state.Notes);
        Assert.All(
            state.Tracks,
            static t => Assert.Equal(((string?)null, false, string.Empty), (t.ParentId, t.Collapsed, t.Notes))
        );
        var region = Assert.Single(state.Regions.Values);
        Assert.Equal(
            (0.0, 0.0, false, 100L, 200L),
            (region.FadeInCurve, region.FadeOutCurve, region.Muted, region.FadeIn, region.FadeOut)
        );
        var midi = Assert.Single(state.MidiRegions.Values);
        Assert.False(midi.Muted);
        Assert.Equal(2, midi.Notes.Count);
        Assert.Equal(3, state.LastEventId);
        Assert.Equal(TempoMap.Constant(132).SecondsAt(960), state.TempoMap.SecondsAt(960));

        ProjectPackage.Save(folder, state);
        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "project.json")))!;
        Assert.Equal(PackageMigrations.CurrentSchemaVersion, root["schemaVersion"]!.GetValue<int>());
        Assert.Single(root["settings"]!["tempoEvents"]!.AsArray());
        var track = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "tracks", "trk_drums0000001.json")))!;
        Assert.Equal(PackageMigrations.CurrentSchemaVersion, track["schemaVersion"]!.GetValue<int>());
        Assert.Empty(track["takeFolders"]!.AsArray());
        Assert.Equal(RandomProjects.Json(state), RandomProjects.Json(ProjectPackage.Load(folder)));
    }

    [Fact]
    public void A_Phase_3_package_opens_with_plain_channel_strips_and_saves_as_schema_4() {
        CopyFixture("Phase3.anode");
        var state = ProjectPackage.Load(folder);
        Assert.Equal(new(PdcMode.All, false, 10), state.Settings.Mixer);
        Assert.Empty(state.Groups);
        Assert.Empty(state.Automation);
        Assert.Equal(
            (-1.5, 0, 0, "master"),
            (state.Master.VolumeDb, state.Master.Inserts.Count, state.Master.Groups.Count, state.Master.Output)
        );
        Assert.Equal(3, state.Tracks.Count);
        Assert.All(
            state.Tracks,
            static t => {
                Assert.Empty(t.Strip.Inserts);
                Assert.Empty(t.Strip.Sends);
                Assert.Empty(t.Strip.Groups);
                Assert.Equal(("master", (string?)null), (t.Strip.Output, t.Strip.VcaId));
                Assert.Equal((0, AutomationMode.Read, (string?)null), (t.InputBus, t.AutomationMode, t.FrozenFileId));
            }
        );
        // What Phase 3 had is all there.
        var vocal = state.FindTrack("trk_vocal0000002")!;
        Assert.Equal((-4.5, -0.25, "trk_stack0000001"), (vocal.Strip.VolumeDb, vocal.Strip.Pan, vocal.ParentId));
        Assert.True(state.FindTrack("trk_keys00000003")!.Strip.Solo);
        Assert.Equal(2, state.Settings.TempoEvents.Count);
        Assert.True(state.Settings.Recording.Autopunch);
        Assert.Single(state.Regions);
        Assert.Single(state.MidiRegions);
        Assert.Single(state.Markers);
        Assert.Single(state.Sections);
        Assert.Single(state.Screensets);
        Assert.Equal("Phase 3 fixture", state.Notes);

        ProjectPackage.Save(folder, state);
        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "project.json")))!;
        Assert.Equal(4, root["schemaVersion"]!.GetValue<int>());
        Assert.Equal("all", root["settings"]!["mixer"]!["pdc"]!.GetValue<string>());
        Assert.Empty(root["master"]!["inserts"]!.AsArray());
        Assert.Empty(root["groups"]!.AsArray());
        var track = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "tracks", "trk_vocal0000002.json")))!;
        Assert.Equal(4, track["schemaVersion"]!.GetValue<int>());
        Assert.Equal("read", track["track"]!["automationMode"]!.GetValue<string>());
        Assert.Empty(track["automation"]!.AsArray());
        Assert.Equal(RandomProjects.Json(state), RandomProjects.Json(ProjectPackage.Load(folder)));
    }

    [Fact]
    public void The_mixer_survives_the_package_and_broken_references_are_dropped() {
        var ids = new SequentialIds();
        var state = Edits.Apply(
                ProjectDefaults.NewProject("prj_1", "Song", 48_000),
                [
                    new AddTrackCommand("Gtr"), new AddAuxCommand(2), new AddVcaCommand(),
                    new SetGroupCommand(3, "Guitars")
                ],
                ids,
                RandomProjects.Plugins
            )
            .State;
        var (gtr, aux, vca) = (state.Tracks[0].Id, state.Tracks[1].Id, state.Tracks[2].Id);
        state = Edits.Apply(
                state,
                [
                    new AddInsertCommand("anode.test-compressor", gtr), new AddInsertCommand("com.example.fx"),
                    new AddSendCommand(gtr, 2, -6), new SetOutputCommand(aux, "output:2"),
                    new AssignVcaCommand(gtr, vca), new SetTrackGroupsCommand(gtr, [3]),
                    new SetAutomationCommand("volume", [new(0, -6, 0), new(3840, 0, 0.5)], gtr),
                    new SetAutomationCommand("pan", [new(0, -1, 0)]),
                    new SetAutomationModeCommand(AutomationMode.Latch, gtr),
                    new SetMixerSettingsCommand(PdcMode.Tracks, true, 5)
                ],
                ids,
                RandomProjects.Plugins
            )
            .State;
        var insert = state.FindTrack(gtr)!.Strip.Inserts[0].Id;
        state = Edits.Apply(state, new SetInsertCommand(insert, gtr, Sidechain: aux), ids, RandomProjects.Plugins)
            .State;
        ProjectPackage.Save(folder, state);
        var loaded = ProjectPackage.Load(folder);
        Assert.Equal(RandomProjects.Json(state), RandomProjects.Json(loaded));
        Assert.Equal(2, loaded.Automation.Count);

        // A track file gone: what pointed at it lets go (VCA, sidechain), its lanes go with it.
        File.Delete(Path.Combine(folder, "tracks", vca + ".json"));
        File.Delete(Path.Combine(folder, "tracks", aux + ".json"));
        var damaged = ProjectPackage.Load(folder);
        var strip = damaged.FindTrack(gtr)!.Strip;
        Assert.Null(strip.VcaId);
        Assert.Null(strip.Inserts[0].Sidechain);
        Assert.Equal(2, damaged.Automation.Count);
    }

    [Fact]
    public void Insert_states_are_files_per_track_and_insert() {
        var ids = new SequentialIds();
        var state = Edits.Apply(
                ProjectDefaults.NewProject("prj_1", "Song", 48_000),
                [new AddTrackCommand("A"), new AddInsertCommand("com.example.fx")],
                ids,
                RandomProjects.Plugins
            )
            .State;
        var track = state.Tracks[0].Id;
        state = Edits.Apply(state, new AddInsertCommand("com.example.fx", track), ids, RandomProjects.Plugins).State;
        var (own, master) = (state.Tracks[0].Strip.Inserts[0].Id, state.Master.Inserts[0].Id);
        var keys = new Dictionary<string, byte[]> {
            [ProjectPackage.StateKey(track, own)] = [1], [ProjectPackage.StateKey(null, master)] = [2]
        };
        ProjectPackage.Save(folder, state);
        ProjectPackage.SaveStates(folder, keys);
        Assert.True(File.Exists(Path.Combine(folder, "Plugin States", $"{track}.{own}.bin")));
        Assert.True(File.Exists(Path.Combine(folder, "Plugin States", $"master.{master}.bin")));
        var loaded = ProjectPackage.LoadStates(folder, state);
        Assert.Equal([1], loaded[$"{track}.{own}"]);
        Assert.Equal([2], loaded[$"master.{master}"]);
        Assert.Throws<ArgumentException>(static () => ProjectPackage.StateFileName("../x"));
    }

    [Fact]
    public void Screensets_and_notes_are_saved_with_the_project() {
        var (state, _, _) = RandomProjects.Build(17, 30);
        state = state with {
            Notes = "Bridge needs work", Screensets = state.Screensets.SetItem(2, new(2, "Mixing", true, "{}"))
        };
        ProjectPackage.Save(folder, state);
        var loaded = ProjectPackage.Load(folder);
        Assert.Equal("Bridge needs work", loaded.Notes);
        Assert.Equal(new(2, "Mixing", true, "{}"), Assert.Single(loaded.Screensets.Values));
    }

    [Fact]
    public void MIDI_regions_live_in_their_track_file_and_note_ids_keep_growing_after_a_reopen() {
        var ids = new SequentialIds();
        var state = ProjectDefaults.NewProject("prj_1", "Song", 48_000);
        state = Edits.Apply(state, new AddTrackCommand(Kind: TrackKind.Instrument), ids, RandomProjects.Plugins).State;
        var track = state.Tracks[0].Id;
        state = Edits.Apply(state, new AddMidiRegionCommand(track, 0, 3840), ids).State;
        var region = state.MidiRegions.Keys.Single();
        state = Edits.Apply(
                state,
                [
                    new AddNotesCommand(region, [new(60, 100, 1, 0, 480), new(64, 90, 1, 480, 480)]),
                    new AddControlsCommand(region, [new(ControlKind.PitchBend, 1, 0, -100, 240)])
                ],
                ids
            )
            .State;
        ProjectPackage.Save(folder, state);
        var file = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "tracks", track + ".json")))!;
        Assert.Equal(2, file["midiRegions"]![0]!["notes"]!.AsArray().Count);
        Assert.Equal("anode.synth", file["track"]!["instrument"]!["pluginId"]!.GetValue<string>());

        var loaded = ProjectPackage.Load(folder);
        Assert.Equal(RandomProjects.Json(state), RandomProjects.Json(loaded));
        Assert.Equal(3, loaded.LastEventId);
        var more = Edits.Apply(loaded, new AddNotesCommand(region, [new(67, 80, 1, 960, 480)]), ids).State;
        Assert.Equal([1L, 2, 4], more.MidiRegions[region].Notes.Select(static n => n.Id));
    }

    [Fact]
    public void Instrument_states_are_files_per_track_and_stale_ones_go() {
        var ids = new SequentialIds();
        var state = Edits.Apply(
                ProjectDefaults.NewProject("prj_1", "Song", 48_000),
                [new AddTrackCommand(Kind: TrackKind.Instrument), new AddTrackCommand(Kind: TrackKind.Instrument)],
                ids,
                RandomProjects.Plugins
            )
            .State;
        var (a, b) = (state.Tracks[0].Id, state.Tracks[1].Id);
        ProjectPackage.Save(folder, state);
        ProjectPackage.SaveStates(folder, new Dictionary<string, byte[]> { [a] = [1, 2, 3], [b] = [4] });
        Assert.True(File.Exists(Path.Combine(folder, "Plugin States", a + ".bin")));
        var states = ProjectPackage.LoadStates(folder, ProjectPackage.Load(folder));
        Assert.Equal([1, 2, 3], states[a]);
        Assert.Equal([4], states[b]);

        ProjectPackage.SaveStates(folder, new Dictionary<string, byte[]> { [b] = [5] });
        Assert.False(File.Exists(Path.Combine(folder, "Plugin States", a + ".bin")));
        Assert.Equal([b], ProjectPackage.LoadStates(folder, state).Keys);
    }

    void CopyFixture(string name) {
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) {
            var target = Path.Combine(folder, Path.GetRelativePath(source, path));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(path, target, true);
        }
    }

    [Fact]
    public void A_damaged_root_is_refused_and_a_damaged_track_left_out() {
        var (state, _, _) = RandomProjects.Build(11, 30);
        Assert.NotEmpty(state.Tracks);
        ProjectPackage.Save(folder, state);
        File.WriteAllText(Path.Combine(folder, "tracks", state.Tracks[0].Id + ".json"), "{ not json");
        var loaded = ProjectPackage.Load(folder);
        Assert.Equal(state.Tracks.Count - 1, loaded.Tracks.Count);
        Assert.DoesNotContain(loaded.Regions.Values, r => r.TrackId == state.Tracks[0].Id);

        File.WriteAllText(Path.Combine(folder, "project.json"), "[]");
        Assert.Throws<PackageException>(() => ProjectPackage.Load(folder));
        File.Delete(Path.Combine(folder, "project.json"));
        Assert.Throws<PackageException>(() => ProjectPackage.Load(folder));
    }
}
