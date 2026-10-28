using Anode.Contracts;
using Anode.Model.Editing;
using Anode.Model.History;
using Anode.Model.Storage;
using CsCheck;
using System.Text.Json;

namespace Anode.Model.Tests;

/// <summary>
///     The properties every edit has (docs/01-implementation-plan.md §5.5): its inverse restores the project exactly;
///     the project survives the package format; and the deltas, applied in order as a client applies them, reproduce
///     the server's snapshot through edits, undo and redo.
/// </summary>
public sealed class EditPropertyTests {
    public static TheoryData<string> Commands() => [..RandomProjects.CommandTypes.Select(static t => t.Name)];

    [Theory]
    [MemberData(nameof(Commands))]
    public void Applying_then_inverting_a_command_is_the_identity(string command) {
        Assert.SkipWhen(
            RandomProjects.PendingPhase5.Contains(command),
            "Phase 5 command the model does not implement yet (docs/06-phase-5.md, workstream B)."
        );
        var type = RandomProjects.CommandTypes.Single(t => t.Name == command);
        var applied = 0;
        Gen.Int.Sample(
            seed => {
                var (state, ids, random) = RandomProjects.Build(seed);
                if (RandomProjects.Command(type, state, random) is not { } edit) {
                    return;
                }

                EditResult result;
                try {
                    result = Edits.Apply(state, edit, ids, RandomProjects.Plugins, RandomProjects.Patches);
                } catch (EditException) {
                    return;
                }

                Interlocked.Increment(ref applied);
                Assert.Equal(RandomProjects.Json(state), RandomProjects.Json(result.State.Apply(result.Inverse)));
                Assert.Equal(RandomProjects.Json(result.State), RandomProjects.Json(state.Apply(result.Forward)));
            },
            iter: 300
        );
        Assert.True(applied > 30, $"Only {applied} random {command}s applied; the generator needs looking at.");
    }

    [Fact]
    public void A_batch_inverts_as_a_whole() =>
        Gen.Int.Sample(
            static seed => {
                var (state, ids, random) = RandomProjects.Build(seed);
                var batch = Enumerable.Range(0, random.Next(2, 8))
                    .Select(_ => RandomProjects.Command(
                            RandomProjects.Pick(random, RandomProjects.CommandTypes),
                            state,
                            random
                        )
                    )
                    .OfType<EditCommand>()
                    .ToArray();
                if (batch.Length == 0) {
                    return;
                }

                try {
                    var result = Edits.Apply(state, batch, ids, RandomProjects.Plugins, RandomProjects.Patches);
                    Assert.Equal(RandomProjects.Json(state), RandomProjects.Json(result.State.Apply(result.Inverse)));
                } catch (EditException) {
                    // a batch with an edit that does not apply changes nothing: nothing to check
                }
            },
            iter: 300
        );

    [Fact]
    public void Commands_survive_JSON() =>
        Gen.Int.Sample(
            static seed => {
                var (state, _, random) = RandomProjects.Build(seed, 12);
                var commands = RandomProjects.CommandTypes
                    .Select(t => RandomProjects.Command(t, state, random))
                    .OfType<EditCommand>()
                    .ToArray();
                var batch = new CommandBatch(commands, "Label");
                var json = JsonSerializer.Serialize(batch, ContractsJsonContext.Default.CommandBatch);
                var back = JsonSerializer.Deserialize(json, ContractsJsonContext.Default.CommandBatch)!;
                Assert.Equal(json, JsonSerializer.Serialize(back, ContractsJsonContext.Default.CommandBatch));
                Assert.Equal(commands.Select(static c => c.GetType()), back.Commands.Select(static c => c.GetType()));
            },
            iter: 100
        );

    [Fact]
    public void A_project_survives_the_package_format() {
        var folder = Directory.CreateTempSubdirectory("anode-package-").FullName;
        try {
            Gen.Int.Sample(
                seed => {
                    var (state, _, _) = RandomProjects.Build(seed, 40);
                    ProjectPackage.Save(folder, state);
                    Assert.Equal(RandomProjects.Json(state), RandomProjects.Json(ProjectPackage.Load(folder)));
                },
                iter: 60,
                threads: 1
            );
        } finally {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Replaying_the_deltas_reproduces_the_snapshot() =>
        Gen.Int.Sample(
            static seed => {
                var (state, ids, random) = RandomProjects.Build(seed, 8);
                var client = new ClientModel(state.ToProject(string.Empty, 1, false));
                var history = new EditHistory(TimeProvider.System);
                for (var step = 0; step < 40; step++) {
                    IReadOnlyList<DeltaOp> ops;
                    var roll = random.Next(10);
                    if (roll == 0 && history.PeekUndo() is { } undo) {
                        ops = undo.Inverse;
                        history.Undone(step);
                    } else if (roll == 1 && history.PeekRedo() is { } redo) {
                        ops = redo.Forward;
                        history.Redone(step);
                    } else {
                        var type = RandomProjects.Pick(random, RandomProjects.CommandTypes);
                        if (RandomProjects.Command(type, state, random) is not { } command) {
                            continue;
                        }

                        try {
                            var result =
                                Edits.Apply(state, command, ids, RandomProjects.Plugins, RandomProjects.Patches);
                            history.Push(type.Name, step, new(result.Forward, result.Inverse), null);
                            ops = result.Forward;
                        } catch (EditException) {
                            continue;
                        }
                    }

                    state = state.Apply(ops);
                    client.Apply(ops);
                    Assert.Equal(RandomProjects.Json(state), client.Json());
                }
            },
            iter: 100
        );

    /// <summary>A client's normalised store, applying deltas as the editor's project store does.</summary>
    sealed class ClientModel(Project project) {
        readonly List<Track> tracks = [..project.Tracks];
        readonly Dictionary<string, AudioRegion> regions = project.Regions.ToDictionary(static r => r.Id);
        readonly Dictionary<string, AudioFile> files = project.AudioFiles.ToDictionary(static f => f.Id);
        readonly Dictionary<string, MidiRegion> midiRegions = project.MidiRegions.ToDictionary(static r => r.Id);
        readonly Dictionary<string, TakeFolder> folders = project.TakeFolders.ToDictionary(static f => f.Id);
        readonly Dictionary<string, Marker> markers = project.Markers.ToDictionary(static m => m.Id);
        readonly Dictionary<string, ArrangementMarker> sections = project.Arrangement.ToDictionary(static s => s.Id);
        readonly Dictionary<string, AutomationLane> lanes = project.Automation.ToDictionary(static l => l.Id);
        readonly Dictionary<int, MixerGroup> groups = project.Groups.ToDictionary(static g => g.Number);
        string notes = project.Notes;
        string name = project.Name;
        ProjectSettings settings = project.Settings;
        ChannelStrip master = project.Master;

        public void Apply(IEnumerable<DeltaOp> ops) {
            foreach (var op in ops) {
                switch (op) {
                    case TrackUpserted(var track, var index):
                        tracks.RemoveAll(t => t.Id == track.Id);
                        tracks.Insert(Math.Min(index, tracks.Count), track);
                        break;
                    case TrackRemoved(var id):
                        tracks.RemoveAll(t => t.Id == id);
                        foreach (var gone in midiRegions.Values.Where(r => r.TrackId == id).ToArray()) {
                            midiRegions.Remove(gone.Id);
                        }

                        foreach (var gone in regions.Values.Where(r => r.TrackId == id).ToArray()) {
                            regions.Remove(gone.Id);
                        }

                        foreach (var gone in folders.Values.Where(f => f.TrackId == id).ToArray()) {
                            folders.Remove(gone.Id);
                        }

                        foreach (var gone in lanes.Values.Where(l => l.TrackId == id).ToArray()) {
                            lanes.Remove(gone.Id);
                        }

                        break;
                    case RegionUpserted(var region):
                        regions[region.Id] = region;
                        break;
                    case RegionRemoved(var id):
                        regions.Remove(id);
                        break;
                    case AudioFileUpserted(var file):
                        files[file.Id] = file;
                        break;
                    case SettingsChanged(var s):
                        settings = s;
                        break;
                    case MasterChanged(var strip):
                        master = strip;
                        break;
                    case ProjectRenamed(var n):
                        name = n;
                        break;
                    case MidiRegionUpserted(var region):
                        midiRegions[region.Id] = region;
                        break;
                    case MidiRegionRemoved(var id):
                        midiRegions.Remove(id);
                        break;
                    case TakeFolderUpserted(var folder):
                        folders[folder.Id] = folder;
                        break;
                    case TakeFolderRemoved(var id):
                        folders.Remove(id);
                        break;
                    case MarkerUpserted(var marker):
                        markers[marker.Id] = marker;
                        break;
                    case MarkerRemoved(var id):
                        markers.Remove(id);
                        break;
                    case SectionUpserted(var section):
                        sections[section.Id] = section;
                        break;
                    case SectionRemoved(var id):
                        sections.Remove(id);
                        break;
                    case NotesChanged(var text):
                        notes = text;
                        break;
                    case AutomationLaneUpserted(var lane):
                        lanes[lane.Id] = lane;
                        break;
                    case AutomationLaneRemoved(var id):
                        lanes.Remove(id);
                        break;
                    case GroupUpserted(var group):
                        groups[group.Number] = group;
                        break;
                    case GroupRemoved(var number):
                        groups.Remove(number);
                        break;
                }
            }
        }

        public string Json() =>
            JsonSerializer.Serialize(
                project with {
                    Name = name,
                    Path = string.Empty,
                    Revision = 0,
                    Dirty = false,
                    Settings = settings,
                    Master = master,
                    Tracks = tracks,
                    Regions = [..regions.Values.OrderBy(static r => r.Id, StringComparer.Ordinal)],
                    AudioFiles = [..files.Values.OrderBy(static f => f.Id, StringComparer.Ordinal)],
                    MidiRegions = [..midiRegions.Values.OrderBy(static r => r.Id, StringComparer.Ordinal)],
                    TakeFolders = [..folders.Values.OrderBy(static f => f.Id, StringComparer.Ordinal)],
                    Markers = [..markers.Values.OrderBy(static m => m.Id, StringComparer.Ordinal)],
                    Arrangement = [..sections.Values.OrderBy(static s => s.Id, StringComparer.Ordinal)],
                    Notes = notes,
                    Groups = [..groups.Values.OrderBy(static g => g.Number)],
                    Automation = [..lanes.Values.OrderBy(static l => l.Id, StringComparer.Ordinal)]
                },
                ContractsJsonContext.Default.Project
            );
    }
}
