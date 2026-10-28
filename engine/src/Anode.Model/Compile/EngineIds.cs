using System.Collections.Immutable;

namespace Anode.Model.Compile;

/// <summary>
///     The stable <c>int</c> ids the engine knows tracks and files by (docs/02-phase-1.md, "Ids"): assigned in order of
///     first sight from 0 and never reused while the project is open (a folder stack has none: the engine has no track for it), so the host's state and meters stay attached to
///     the right track across edits, undo and reconnects. Immutable: <see cref="With" /> returns an extended map.
/// </summary>
public sealed class EngineIds {
    readonly ImmutableDictionary<string, int> tracks;
    readonly ImmutableDictionary<int, string> trackIds;
    readonly ImmutableDictionary<string, int> files;
    readonly int nextTrack;
    readonly int nextFile;

    EngineIds(
        ImmutableDictionary<string, int> tracks,
        ImmutableDictionary<int, string> trackIds,
        ImmutableDictionary<string, int> files,
        int nextTrack,
        int nextFile
    ) {
        this.tracks = tracks;
        this.trackIds = trackIds;
        this.files = files;
        this.nextTrack = nextTrack;
        this.nextFile = nextFile;
    }

    public static EngineIds Empty { get; } = new(
        ImmutableDictionary.Create<string, int>(StringComparer.Ordinal),
        ImmutableDictionary<int, string>.Empty,
        ImmutableDictionary.Create<string, int>(StringComparer.Ordinal),
        0,
        0
    );

    /// <summary>This map with an id for every track and file of <paramref name="state" /> (itself when it has them).</summary>
    public EngineIds With(ProjectState state) {
        var t = tracks;
        var ti = trackIds;
        var f = files;
        var nt = nextTrack;
        var nf = nextFile;
        foreach (var track in state.Tracks) {
            if (Stacks.IsPlayable(track) && !t.ContainsKey(track.Id)) {
                t = t.Add(track.Id, nt);
                ti = ti.Add(nt, track.Id);
                nt++;
            }
        }

        foreach (var id in state.Files.Keys) {
            if (!f.ContainsKey(id)) {
                f = f.Add(id, nf++);
            }
        }

        return nt == nextTrack && nf == nextFile ? this : new(t, ti, f, nt, nf);
    }

    public int Track(string id) =>
        tracks.TryGetValue(id, out var engineId)
            ? engineId
            : throw new KeyNotFoundException($"No engine id for '{id}'.");

    public int File(string id) =>
        files.TryGetValue(id, out var engineId)
            ? engineId
            : throw new KeyNotFoundException($"No engine id for '{id}'.");

    /// <summary>The domain id of engine track <paramref name="engineId" />, or null.</summary>
    public string? TrackOf(int engineId) => trackIds.GetValueOrDefault(engineId);
}
