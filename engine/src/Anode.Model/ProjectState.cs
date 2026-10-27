using Anode.Contracts;
using Anode.Model.Time;
using System.Collections.Immutable;

namespace Anode.Model;

/// <summary>
///     An immutable snapshot of a project's content (docs/adr/0004-state-ownership.md): what an edit changes, what undo
///     restores and what the package stores. The revision, the dirty flag and the folder belong to the open session,
///     not to the snapshot. Regions and files are keyed (and listed) by id; tracks keep their order.
/// </summary>
public sealed record ProjectState(
    string Id,
    string Name,
    int SampleRate,
    ProjectSettings Settings,
    ChannelStrip Master,
    ImmutableList<Track> Tracks,
    ImmutableSortedDictionary<string, AudioRegion> Regions,
    ImmutableSortedDictionary<string, AudioFile> Files
) {
    /// <summary>The project's tempo map (one tempo in Phase 1).</summary>
    public TempoMap TempoMap => TempoMap.Constant(Settings.Tempo);

    /// <summary>The position of the track <paramref name="id" /> in the track list, or −1.</summary>
    public int IndexOfTrack(string id) {
        for (var i = 0; i < Tracks.Count; i++) {
            if (Tracks[i].Id == id) {
                return i;
            }
        }

        return -1;
    }

    public Track? FindTrack(string id) => IndexOfTrack(id) is >= 0 and var i ? Tracks[i] : null;

    /// <summary>The regions of a track, by start.</summary>
    public IEnumerable<AudioRegion> RegionsOf(string trackId) =>
        Regions.Values.Where(r => r.TrackId == trackId)
            .OrderBy(static r => r.StartTick)
            .ThenBy(
                static r => r.Id,
                StringComparer.Ordinal
            );

    /// <summary>The contract form, as <c>GET /api/v1/project</c> answers it.</summary>
    public Project ToProject(string path, long revision, bool dirty) =>
        new(
            Id,
            Name,
            path,
            revision,
            dirty,
            SampleRate,
            Settings,
            Master,
            [..Tracks],
            [..Regions.Values],
            [..Files.Values]
        );

    /// <summary>Applies one delta operation (an edit's primitive step, also what clients apply).</summary>
    public ProjectState Apply(DeltaOp op) =>
        op switch {
            TrackUpserted(var track, var index) => WithTrack(track, index),
            TrackRemoved(var id) => this with {
                Tracks = Tracks.RemoveAll(t => t.Id == id),
                Regions = Regions.RemoveRange(Regions.Values.Where(r => r.TrackId == id).Select(static r => r.Id))
            },
            RegionUpserted(var region) => this with { Regions = Regions.SetItem(region.Id, region) },
            RegionRemoved(var id) => this with { Regions = Regions.Remove(id) },
            AudioFileUpserted(var file) => this with { Files = Files.SetItem(file.Id, file) },
            SettingsChanged(var settings) => this with { Settings = settings },
            MasterChanged(var strip) => this with { Master = strip },
            ProjectRenamed(var name) => this with { Name = name },
            _ => throw new ArgumentException($"Unknown delta operation {op.GetType().Name}.", nameof(op))
        };

    public ProjectState Apply(IEnumerable<DeltaOp> ops) {
        var state = this;
        foreach (var op in ops) {
            state = state.Apply(op);
        }

        return state;
    }

    /// <summary>
    ///     The operation that undoes <paramref name="op" /> applied to this state, or null when it has nothing to undo
    ///     (a file added to the pool stays in the pool).
    /// </summary>
    public DeltaOp? Inverse(DeltaOp op) =>
        op switch {
            TrackUpserted(var track, _) => IndexOfTrack(track.Id) is >= 0 and var i
                ? new TrackUpserted(Tracks[i], i)
                : new TrackRemoved(track.Id),
            TrackRemoved(var id) => IndexOfTrack(id) is >= 0 and var i ? new TrackUpserted(Tracks[i], i) : null,
            RegionUpserted(var region) => Regions.TryGetValue(region.Id, out var old)
                ? new RegionUpserted(old)
                : new RegionRemoved(region.Id),
            RegionRemoved(var id) => Regions.TryGetValue(id, out var old) ? new RegionUpserted(old) : null,
            AudioFileUpserted(var file) => Files.TryGetValue(file.Id, out var old) ? new AudioFileUpserted(old) : null,
            SettingsChanged => new SettingsChanged(Settings),
            MasterChanged => new MasterChanged(Master),
            ProjectRenamed => new ProjectRenamed(Name),
            _ => throw new ArgumentException($"Unknown delta operation {op.GetType().Name}.", nameof(op))
        };

    ProjectState WithTrack(Track track, int index) {
        var tracks = Tracks.RemoveAll(t => t.Id == track.Id);
        return this with { Tracks = tracks.Insert(Math.Clamp(index, 0, tracks.Count), track) };
    }
}
