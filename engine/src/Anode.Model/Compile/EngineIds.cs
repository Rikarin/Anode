using Anode.Ipc;
using System.Collections.Immutable;

namespace Anode.Model.Compile;

/// <summary>
///     The stable <c>int</c> ids the engine knows tracks, files and automation targets by (docs/02-phase-1.md, "Ids";
///     docs/05-phase-4.md, "Automation"): assigned in order of first sight from 0 and never reused while the project is
///     open (a folder stack and a VCA have none: the engine has no track for them), so the host's state and meters stay
///     attached to the right track across edits, undo and reconnects. Immutable: <see cref="With" /> returns an
///     extended map.
/// </summary>
public sealed class EngineIds {
    readonly ImmutableDictionary<string, int> tracks;
    readonly ImmutableDictionary<int, string> trackIds;
    readonly ImmutableDictionary<string, int> files;
    readonly ImmutableDictionary<string, int> targets;
    readonly int nextTrack;
    readonly int nextFile;

    EngineIds(
        ImmutableDictionary<string, int> tracks,
        ImmutableDictionary<int, string> trackIds,
        ImmutableDictionary<string, int> files,
        ImmutableDictionary<string, int> targets,
        int nextTrack,
        int nextFile
    ) {
        this.tracks = tracks;
        this.trackIds = trackIds;
        this.files = files;
        this.targets = targets;
        this.nextTrack = nextTrack;
        this.nextFile = nextFile;
    }

    public static EngineIds Empty { get; } = new(
        ImmutableDictionary.Create<string, int>(StringComparer.Ordinal),
        ImmutableDictionary<int, string>.Empty,
        ImmutableDictionary.Create<string, int>(StringComparer.Ordinal),
        ImmutableDictionary.Create<string, int>(StringComparer.Ordinal),
        0,
        0
    );

    /// <summary>
    ///     This map with an id for every track, file and automated target of <paramref name="state" /> (itself when it
    ///     has them).
    /// </summary>
    public EngineIds With(ProjectState state) {
        var t = tracks;
        var ti = trackIds;
        var f = files;
        var a = targets;
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

        foreach (var (trackId, target) in Automated(state)) {
            var key = TargetKey(trackId, target);
            if (!a.ContainsKey(key)) {
                a = a.Add(key, a.Count);
            }
        }

        return nt == nextTrack && nf == nextFile && a.Count == targets.Count ? this : new(t, ti, f, a, nt, nf);
    }

    /// <summary>Every automated target of the project: (track id, null for the master; lane target).</summary>
    public static IEnumerable<(string? TrackId, string Target)> Automated(ProjectState state) {
        foreach (var target in AutomationEvents.Targets(state, null)) {
            yield return (null, target);
        }

        foreach (var track in state.Tracks) {
            if (Stacks.IsPlayable(track)) {
                foreach (var target in AutomationEvents.Targets(state, track)) {
                    yield return (track.Id, target);
                }
            }
        }
    }

    /// <summary>What every automated target of <paramref name="state" /> drives, by index.</summary>
    public IReadOnlyList<EngineAutomationTarget> AutomationTargets(ProjectState state) =>
        [
            ..Automated(state)
                .Select(x => AutomationEvents.Describe(
                        Target(x.TrackId, x.Target),
                        x.TrackId is null ? AutomationEvents.MasterTrack : Track(x.TrackId),
                        x.Target
                    )
                )
                .OrderBy(static x => x.Index)
        ];

    public int Track(string id) =>
        tracks.TryGetValue(id, out var engineId)
            ? engineId
            : throw new KeyNotFoundException($"No engine id for '{id}'.");

    public int File(string id) =>
        files.TryGetValue(id, out var engineId)
            ? engineId
            : throw new KeyNotFoundException($"No engine id for '{id}'.");

    /// <summary>The index of automation target <paramref name="target" /> of a track (null: the master).</summary>
    public int Target(string? trackId, string target) =>
        targets.TryGetValue(TargetKey(trackId, target), out var index)
            ? index
            : throw new KeyNotFoundException($"No automation target '{target}' of '{trackId ?? "master"}'.");

    static string TargetKey(string? trackId, string target) => (trackId ?? "master") + "|" + target;

    /// <summary>The domain id of engine track <paramref name="engineId" />, or null.</summary>
    public string? TrackOf(int engineId) => trackIds.GetValueOrDefault(engineId);
}
