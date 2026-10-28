using Anode.Contracts;

namespace Anode.Model.Editing;

public enum TimelineKind {
    AudioRegion,
    MidiRegion,
    TakeFolder,
    Marker,
    Section
}

/// <summary>Something placed on the timeline: a region of either kind, a take folder, a marker or a section.</summary>
public readonly record struct TimelineItem(TimelineKind Kind, string Id, long Start);

/// <summary>
///     Moves, copies and removes timeline items of every kind alike: what arrangement edits (sections) and region
///     copies are made of.
/// </summary>
static class Timeline {
    /// <summary>Every item of the project, in a stable order.</summary>
    public static IEnumerable<TimelineItem> Items(ProjectState state) {
        foreach (var r in state.Regions.Values) {
            yield return new(TimelineKind.AudioRegion, r.Id, r.StartTick);
        }

        foreach (var r in state.MidiRegions.Values) {
            yield return new(TimelineKind.MidiRegion, r.Id, r.StartTick);
        }

        foreach (var f in state.TakeFolders.Values) {
            yield return new(TimelineKind.TakeFolder, f.Id, f.StartTick);
        }

        foreach (var m in state.Markers.Values) {
            yield return new(TimelineKind.Marker, m.Id, m.Tick);
        }

        foreach (var s in state.Sections.Values) {
            yield return new(TimelineKind.Section, s.Id, s.StartTick);
        }
    }

    /// <summary>The item <paramref name="id" /> that a region edit may name (audio, MIDI or a take folder), or null.</summary>
    public static TimelineItem? Region(ProjectState state, string? id) {
        if (id is null) {
            return null;
        }

        if (state.Regions.TryGetValue(id, out var audio)) {
            return new(TimelineKind.AudioRegion, id, audio.StartTick);
        }

        if (state.MidiRegions.TryGetValue(id, out var midi)) {
            return new(TimelineKind.MidiRegion, id, midi.StartTick);
        }

        return state.TakeFolders.TryGetValue(id, out var folder)
            ? new(TimelineKind.TakeFolder, id, folder.StartTick)
            : null;
    }

    /// <summary>The track an item is on (null for markers and sections).</summary>
    public static string? TrackOf(ProjectState state, TimelineItem item) =>
        item.Kind switch {
            TimelineKind.AudioRegion => state.Regions[item.Id].TrackId,
            TimelineKind.MidiRegion => state.MidiRegions[item.Id].TrackId,
            TimelineKind.TakeFolder => state.TakeFolders[item.Id].TrackId,
            _ => null
        };

    /// <summary>The item moved to start at <paramref name="start" /> (and onto <paramref name="trackId" /> when given).</summary>
    public static DeltaOp Move(ProjectState state, TimelineItem item, long start, string? trackId = null) {
        switch (item.Kind) {
            case TimelineKind.AudioRegion:
                var region = state.Regions[item.Id];
                return new RegionUpserted(region with { StartTick = start, TrackId = trackId ?? region.TrackId });
            case TimelineKind.MidiRegion:
                var midi = state.MidiRegions[item.Id];
                return new MidiRegionUpserted(midi with { StartTick = start, TrackId = trackId ?? midi.TrackId });
            case TimelineKind.TakeFolder:
                var folder = Shifted(state.TakeFolders[item.Id], start - item.Start);
                return new TakeFolderUpserted(folder with { TrackId = trackId ?? folder.TrackId });
            case TimelineKind.Marker:
                return new MarkerUpserted(state.Markers[item.Id] with { Tick = start });
            default:
                return new SectionUpserted(state.Sections[item.Id] with { StartTick = start });
        }
    }

    public static DeltaOp Remove(TimelineItem item) =>
        item.Kind switch {
            TimelineKind.AudioRegion => new RegionRemoved(item.Id),
            TimelineKind.MidiRegion => new MidiRegionRemoved(item.Id),
            TimelineKind.TakeFolder => new TakeFolderRemoved(item.Id),
            TimelineKind.Marker => new MarkerRemoved(item.Id),
            _ => new SectionRemoved(item.Id)
        };

    /// <summary>
    ///     A copy of the item <paramref name="offset" /> ticks later (on <paramref name="trackId" /> when given), with
    ///     ids of its own: a MIDI copy's notes and controller events are numbered on from <paramref name="next" />.
    /// </summary>
    public static DeltaOp Copy(
        ProjectState state,
        TimelineItem item,
        long offset,
        string? trackId,
        IIdSource ids,
        ref long next
    ) {
        switch (item.Kind) {
            case TimelineKind.AudioRegion: {
                var r = state.Regions[item.Id];
                return new RegionUpserted(
                    r with { Id = ids.Next(IdPrefix.Region), StartTick = r.StartTick + offset, TrackId = trackId ?? r.TrackId }
                );
            }
            case TimelineKind.MidiRegion: {
                var m = state.MidiRegions[item.Id];
                var notes = new Note[m.Notes.Count];
                for (var i = 0; i < notes.Length; i++) {
                    notes[i] = m.Notes[i] with { Id = ++next };
                }

                var controls = new ControlEvent[m.Controls.Count];
                for (var i = 0; i < controls.Length; i++) {
                    controls[i] = m.Controls[i] with { Id = ++next };
                }

                return new MidiRegionUpserted(
                    m with {
                        Id = ids.Next(IdPrefix.MidiRegion),
                        StartTick = m.StartTick + offset,
                        TrackId = trackId ?? m.TrackId,
                        Notes = Edits.Sorted(notes),
                        Controls = Edits.Sorted(controls)
                    }
                );
            }
            case TimelineKind.TakeFolder: {
                var f = Shifted(state.TakeFolders[item.Id], offset);
                var renamed = f.Takes.ToDictionary(static t => t.Id, _ => ids.Next(IdPrefix.Take), StringComparer.Ordinal);
                return new TakeFolderUpserted(
                    f with {
                        Id = ids.Next(IdPrefix.TakeFolder),
                        TrackId = trackId ?? f.TrackId,
                        Takes = [..f.Takes.Select(t => t with { Id = renamed[t.Id] })],
                        Comp = [..f.Comp.Select(s => s with { TakeId = renamed[s.TakeId] })]
                    }
                );
            }
            case TimelineKind.Marker:
                var marker = state.Markers[item.Id];
                return new MarkerUpserted(marker with { Id = ids.Next(IdPrefix.Marker), Tick = marker.Tick + offset });
            default:
                var section = state.Sections[item.Id];
                return new SectionUpserted(
                    section with { Id = ids.Next(IdPrefix.Section), StartTick = section.StartTick + offset }
                );
        }
    }

    /// <summary>A take folder <paramref name="offset" /> ticks later: its takes and comp with it.</summary>
    public static TakeFolder Shifted(TakeFolder folder, long offset) =>
        offset == 0
            ? folder
            : folder with {
                StartTick = folder.StartTick + offset,
                Takes = [..folder.Takes.Select(t => t with { StartTick = t.StartTick + offset })],
                Comp = [
                    ..folder.Comp.Select(s => s with { StartTick = s.StartTick + offset, EndTick = s.EndTick + offset })
                ]
            };
}
