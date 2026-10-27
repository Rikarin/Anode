using Anode.Contracts;
using Anode.Model.Editing;
using System.Text.Json;

namespace Anode.Model.Tests;

/// <summary>
///     Random projects and random edits for the properties: a project is built from a seed by applying random commands
///     (the ones that do not apply are skipped), so every state is one the server could reach.
/// </summary>
static class RandomProjects {
    /// <summary>Every command type of the contract, so a new one cannot go untested.</summary>
    public static IReadOnlyList<Type> CommandTypes { get; } = [
        ..typeof(EditCommand).GetCustomAttributes(
                typeof(System.Text.Json.Serialization.JsonDerivedTypeAttribute),
                false
            )
            .Cast<System.Text.Json.Serialization.JsonDerivedTypeAttribute>()
            .Select(static a => a.DerivedType)
    ];

    public static (ProjectState State, SequentialIds Ids, Random Random) Build(int seed, int edits = 24) {
        var random = new Random(seed);
        var ids = new SequentialIds();
        var state = ProjectDefaults.NewProject("prj_test", "Test", 48_000);
        for (var i = 0; i < 3; i++) {
            state = state.Apply(
                new AudioFileUpserted(
                    new(
                        ids.Next(IdPrefix.File),
                        $"File {i}",
                        $"Audio Files/File {i}.wav",
                        48_000,
                        1 + i % 2,
                        random.Next(1_000, 400_000),
                        24
                    )
                )
            );
        }

        for (var i = 0; i < edits; i++) {
            var type = i < 4 ? typeof(AddTrackCommand) : i < 10 ? typeof(AddRegionCommand) : Pick(random, CommandTypes);
            if (Command(type, state, random) is { } command) {
                try {
                    state = Edits.Apply(state, command, ids).State;
                } catch (EditException) {
                    // not every random edit applies
                }
            }
        }

        return (state, ids, random);
    }

    /// <summary>A random command of <paramref name="type" /> for <paramref name="s" />, or null when none can be made.</summary>
    public static EditCommand? Command(Type type, ProjectState s, Random r) {
        var tracks = s.Tracks;
        var regions = s.Regions.Values.ToArray();
        var files = s.Files.Values.ToArray();
        return type.Name switch {
            nameof(AddTrackCommand) => new AddTrackCommand(
                MaybeText($"Track {r.Next(100)}"),
                Maybe(r.Next(0, tracks.Count + 1)),
                r.Next(2) == 0
            ),
            nameof(RemoveTrackCommand) => Track() is { } t ? new RemoveTrackCommand(t) : null,
            nameof(MoveTrackCommand) => Track() is { } t ? new MoveTrackCommand(t, r.Next(0, tracks.Count)) : null,
            nameof(SetTrackCommand) => Track() is { } t
                ? new SetTrackCommand(
                    t,
                    MaybeText($"Renamed {r.Next(100)}"),
                    MaybeText("#123456"),
                    r.Next(2) == 0 ? null : new TrackInput(r.Next(0, 9), r.Next(2) == 0),
                    Maybe(r.Next(2) == 0),
                    Maybe(r.Next(2) == 0)
                )
                : null,
            nameof(SetStripCommand) => new SetStripCommand(
                r.Next(3) == 0 ? null : Track(),
                Maybe(Math.Round(r.NextDouble() * 150 - 144, 2)),
                Maybe(Math.Round(r.NextDouble() * 2 - 1, 3)),
                Maybe(r.Next(2) == 0),
                Maybe(r.Next(2) == 0)
            ),
            nameof(AddRegionCommand) => Track() is { } t && files.Length > 0 ? AddRegion(t, Pick(r, files), r) : null,
            nameof(MoveRegionCommand) => regions.Length > 0
                ? new MoveRegionCommand(Pick(r, regions).Id, r.NextInt64(0, 960 * 64), r.Next(2) == 0 ? null : Track())
                : null,
            nameof(ResizeRegionCommand) => regions.Length > 0 ? Resize(Pick(r, regions), s, r) : null,
            nameof(SplitRegionCommand) => regions.Length > 0 ? Split(Pick(r, regions), s, r) : null,
            nameof(SetRegionCommand) => regions.Length > 0 ? SetRegion(Pick(r, regions), r) : null,
            nameof(DeleteRegionsCommand) => regions.Length > 0
                ? new DeleteRegionsCommand([..Enumerable.Range(0, r.Next(1, 4)).Select(_ => Pick(r, regions).Id)])
                : null,
            nameof(SetTempoCommand) => new SetTempoCommand(Math.Round(40 + r.NextDouble() * 200, 1)),
            nameof(SetSignatureCommand) => new SetSignatureCommand(r.Next(1, 13), Pick(r, [2, 4, 8, 16])),
            nameof(SetCycleCommand) => Cycle(r),
            nameof(SetMetronomeCommand) => new SetMetronomeCommand(
                Maybe(r.Next(2) == 0),
                Maybe(r.Next(2) == 0),
                Maybe((double)r.Next(-40, 1))
            ),
            nameof(RenameProjectCommand) => new RenameProjectCommand($"Song {r.Next(1000)}"),
            _ => throw new InvalidOperationException($"No generator for {type.Name}.")
        };
        string? Track() => tracks.Count == 0 ? null : Pick(r, tracks).Id;
        T? Maybe<T>(T value) where T : struct => r.Next(2) == 0 ? null : value;
        string? MaybeText(string value) => r.Next(2) == 0 ? null : value;
    }

    /// <summary>The project as canonical JSON: two states are the same project when these are equal.</summary>
    public static string Json(ProjectState state) =>
        JsonSerializer.Serialize(state.ToProject(string.Empty, 0, false), ContractsJsonContext.Default.Project);

    public static T Pick<T>(Random r, IReadOnlyList<T> items) => items[r.Next(items.Count)];

    static AddRegionCommand AddRegion(string track, AudioFile file, Random r) {
        if (r.Next(2) == 0) {
            return new(track, file.Id, r.NextInt64(0, 960 * 64));
        }

        var offset = r.NextInt64(0, file.Frames / 2);
        return new(track, file.Id, r.NextInt64(0, 960 * 64), offset, r.NextInt64(1, file.Frames - offset + 1));
    }

    static ResizeRegionCommand Resize(AudioRegion region, ProjectState s, Random r) {
        var frames = s.Files[region.FileId].Frames;
        var offset = r.NextInt64(0, frames / 2);
        return new(region.Id, r.NextInt64(0, 960 * 64), offset, r.NextInt64(1, frames - offset + 1));
    }

    static SplitRegionCommand Split(AudioRegion region, ProjectState s, Random r) {
        var end = s.TempoMap.TickAtSample(Edits.EndSample(s, region), s.SampleRate);
        // Mostly inside, sometimes on or outside an edge.
        var at = r.Next(5) == 0 ? region.StartTick + r.NextInt64(-10, 10) : r.NextInt64(region.StartTick, end + 1);
        return new(region.Id, Math.Max(0, at));
    }

    static SetRegionCommand SetRegion(AudioRegion region, Random r) =>
        new(
            region.Id,
            r.Next(2) == 0 ? null : $"Region {r.Next(100)}",
            r.Next(2) == 0 ? null : Math.Round(r.NextDouble() * 48 - 24, 2),
            r.Next(2) == 0 ? null : r.NextInt64(0, region.Length / 2 + 1),
            r.Next(2) == 0 ? null : r.NextInt64(0, region.Length / 2 + 1)
        );

    static SetCycleCommand Cycle(Random r) {
        var start = r.NextInt64(0, 960 * 32);
        return new(r.Next(2) == 0 ? null : r.Next(2) == 0, start, start + r.NextInt64(1, 960 * 32));
    }
}
