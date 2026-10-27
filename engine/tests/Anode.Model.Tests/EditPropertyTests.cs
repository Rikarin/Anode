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
            RandomProjects.PendingPhase2.Contains(command),
            "Phase 2 command the model does not implement yet (docs/03-phase-2.md, workstream B)."
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
                    result = Edits.Apply(state, edit, ids);
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
                    var result = Edits.Apply(state, batch, ids);
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
                            var result = Edits.Apply(state, command, ids);
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
                    AudioFiles = [..files.Values.OrderBy(static f => f.Id, StringComparer.Ordinal)]
                },
                ContractsJsonContext.Default.Project
            );
    }
}
