using Anode.Contracts;

namespace Anode.Model.Editing;

/// <summary>
///     Track alternatives (docs/06-phase-5.md, "Track alternatives"). A track's live regions, MIDI regions and take
///     folders are its active alternative; <c>alternatives</c> lists every alternative in menu order, the active one
///     (<c>alternativeId</c>) as an entry without content (its content is the live one) and the others with their
///     snapshots. Selecting swaps in one step; snapshots keep their ids, so ids stay unique project-wide.
/// </summary>
public static partial class Edits {
    public const int MaxAlternatives = 64;

    static List<DeltaOp> NewTrackAlternative(ProjectState state, NewTrackAlternativeCommand c, IIdSource ids) {
        var track = AlternatingTrackOf(state, c.TrackId);
        var entries = track.Alternatives.ToList();
        var active = track.AlternativeId;
        if (active is null) {
            // The track's first alternative also names what it holds now.
            active = ids.Next(IdPrefix.Alternative);
            entries.Add(new(active, AlternativeName(0), [], [], []));
        }

        if (entries.Count >= MaxAlternatives) {
            throw new EditException($"A track has at most {MaxAlternatives} alternatives.");
        }

        var name = c.Name is null ? AlternativeName(entries.Count) : Name(c.Name, "alternative name");
        var created = new TrackAlternative(ids.Next(IdPrefix.Alternative), name, [], [], []);
        var ops = new List<DeltaOp>();
        var copies = new List<DeltaOp>();
        if (c.Duplicate) {
            var next = state.LastEventId;
            foreach (var item in LiveItems(state, track.Id)) {
                copies.Add(Timeline.Copy(state, item, 0, null, ids, ref next));
            }
        }

        var snapshot = Snapshot(state, track.Id, entries.Single(e => e.Id == active));
        entries[entries.FindIndex(e => e.Id == active)] = snapshot;
        entries.Add(created);
        ops.AddRange(LiveItems(state, track.Id).Select(Timeline.Remove));
        ops.Add(new TrackUpserted(track with { Alternatives = entries, AlternativeId = created.Id }, state.IndexOfTrack(track.Id)));
        ops.AddRange(copies);
        return ops;
    }

    static List<DeltaOp> SelectTrackAlternative(ProjectState state, SelectTrackAlternativeCommand c) {
        var track = AlternatingTrackOf(state, c.TrackId);
        var target = AlternativeOf(track, c.AlternativeId);
        return target.Id == track.AlternativeId ? [] : Selected(state, track, target);
    }

    /// <summary>The swap that makes <paramref name="target" /> the track's active alternative.</summary>
    static List<DeltaOp> Selected(ProjectState state, Track track, TrackAlternative target) {
        var entries = track.Alternatives.ToList();
        var active = entries.FindIndex(e => e.Id == track.AlternativeId);
        if (active >= 0) {
            entries[active] = Snapshot(state, track.Id, entries[active]);
        }

        entries[entries.FindIndex(e => e.Id == target.Id)] = target with { Regions = [], MidiRegions = [], TakeFolders = [] };
        var ops = new List<DeltaOp>();
        ops.AddRange(LiveItems(state, track.Id).Select(Timeline.Remove));
        ops.Add(new TrackUpserted(track with { Alternatives = entries, AlternativeId = target.Id }, state.IndexOfTrack(track.Id)));
        ops.AddRange(target.Regions.Select(static r => new RegionUpserted(r)));
        ops.AddRange(target.MidiRegions.Select(static r => new MidiRegionUpserted(r)));
        ops.AddRange(target.TakeFolders.Select(static f => new TakeFolderUpserted(f)));
        return ops;
    }

    static DeltaOp[] SetTrackAlternative(ProjectState state, SetTrackAlternativeCommand c) {
        var track = AlternatingTrackOf(state, c.TrackId);
        var alternative = AlternativeOf(track, c.AlternativeId);
        var name = Name(c.Name ?? string.Empty, "alternative name");
        return [
            new TrackUpserted(
                track with {
                    Alternatives = [..track.Alternatives.Select(e => e.Id == alternative.Id ? e with { Name = name } : e)]
                },
                state.IndexOfTrack(track.Id)
            )
        ];
    }

    static List<DeltaOp> DeleteTrackAlternative(ProjectState state, DeleteTrackAlternativeCommand c) {
        var track = AlternatingTrackOf(state, c.TrackId);
        var alternative = AlternativeOf(track, c.AlternativeId);
        if (track.Alternatives.Count < 2) {
            throw new EditException("A track keeps its last alternative.");
        }

        var ops = new List<DeltaOp>();
        if (alternative.Id == track.AlternativeId) {
            // Deleting the active alternative selects another first (the one after it, else the one before).
            var index = track.Alternatives.ToList().FindIndex(e => e.Id == alternative.Id);
            var other = track.Alternatives[index + 1 < track.Alternatives.Count ? index + 1 : index - 1];
            ops.AddRange(Selected(state, track, other));
            state = state.Apply(ops);
            track = state.FindTrack(track.Id)!;
        }

        ops.Add(
            new TrackUpserted(
                track with { Alternatives = [..track.Alternatives.Where(e => e.Id != alternative.Id)] },
                state.IndexOfTrack(track.Id)
            )
        );
        return ops;
    }

    /// <summary>An alternative holding the track's live content now (keeping its id and name).</summary>
    static TrackAlternative Snapshot(ProjectState state, string trackId, TrackAlternative entry) =>
        entry with {
            Regions = [..state.RegionsOf(trackId)],
            MidiRegions = [..state.MidiRegionsOf(trackId)],
            TakeFolders = [..state.TakeFoldersOf(trackId)]
        };

    /// <summary>The track's live regions (audio and MIDI) and take folders.</summary>
    static TimelineItem[] LiveItems(ProjectState state, string trackId) =>
        Timeline.Items(state)
            .Where(i => i.Kind is TimelineKind.AudioRegion or TimelineKind.MidiRegion or TimelineKind.TakeFolder
                && Timeline.TrackOf(state, i) == trackId
            )
            .ToArray();

    /// <summary>"A", "B", … then "Alternative 27", …: the default name of the alternative at <paramref name="index" />.</summary>
    static string AlternativeName(int index) => index < 26 ? ((char)('A' + index)).ToString() : $"Alternative {index + 1}";

    static Track AlternatingTrackOf(ProjectState state, string? id) {
        var track = TrackOf(state, id ?? string.Empty);
        return track.Kind is TrackKind.Audio or TrackKind.Instrument or TrackKind.Midi
            ? track
            : throw new EditException($"A {Kind(track.Kind)} track has no alternatives: it holds no regions.");
    }

    static TrackAlternative AlternativeOf(Track track, string? id) =>
        track.Alternatives.FirstOrDefault(e => e.Id == id)
        ?? throw new EditException($"Track '{track.Id}' has no alternative '{id}'.");
}
