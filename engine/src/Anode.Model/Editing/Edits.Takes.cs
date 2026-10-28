using Anode.Contracts;
using Anode.Model.Compile;

namespace Anode.Model.Editing;

/// <summary>Take folders and comps, and region glue and copy (docs/04-phase-3.md, "Takes and comping", "Regions").</summary>
public static partial class Edits {
    public const int MaxCompSegments = 10_000;
    public const int MaxTakes = 1_000;

    /// <summary>The longest comp crossfade, in seconds.</summary>
    public const int MaxCrossfadeSeconds = 10;

    static DeltaOp[] SetComp(ProjectState state, SetCompCommand c) {
        var folder = FolderOf(state, c.FolderId);
        if (c.Segments is not { Count: <= MaxCompSegments } segments) {
            throw new EditException($"A comp has 0…{MaxCompSegments} segments.");
        }

        var end = folder.StartTick + folder.LengthTicks;
        for (var i = 0; i < segments.Count; i++) {
            var s = segments[i] ?? throw new EditException("A comp segment is null.");
            if (s.StartTick < folder.StartTick || s.EndTick > end || s.EndTick <= s.StartTick) {
                throw new EditException(
                    $"A comp segment lies inside its folder ({folder.StartTick}…{end}) and ends after it starts."
                );
            }

            if (i > 0 && s.StartTick < segments[i - 1].EndTick) {
                throw new EditException("Comp segments are in order and do not overlap.");
            }

            if (!folder.Takes.Any(t => t.Id == s.TakeId)) {
                throw new EditException($"Take folder '{folder.Id}' has no take '{s.TakeId}'.");
            }
        }

        return [new TakeFolderUpserted(folder with { Comp = [..segments] })];
    }

    static DeltaOp[] SelectTake(ProjectState state, SelectTakeCommand c) {
        var folder = FolderOf(state, c.FolderId);
        var take = TakeOf(folder, c.TakeId);
        return [
            new TakeFolderUpserted(
                folder with { Comp = [new(folder.StartTick, folder.StartTick + folder.LengthTicks, take.Id)] }
            )
        ];
    }

    static DeltaOp[] SetTakeFolder(ProjectState state, SetTakeFolderCommand c) {
        var folder = FolderOf(state, c.FolderId);
        if (c.Crossfade is { } crossfade
            && (crossfade < 0 || crossfade > (long)state.SampleRate * MaxCrossfadeSeconds)) {
            throw new EditException($"A comp crossfade is 0…{MaxCrossfadeSeconds} s of frames, not {crossfade}.");
        }

        if (c.TakeName is not null && c.TakeId is null) {
            throw new EditException("Name the take to rename.");
        }

        var takes = folder.Takes;
        if (c.TakeId is not null) {
            var take = TakeOf(folder, c.TakeId);
            if (c.TakeName is not null) {
                var name = Name(c.TakeName, "take name");
                takes = [..takes.Select(t => t.Id == take.Id ? t with { Name = name } : t)];
            }
        }

        return [
            new TakeFolderUpserted(
                folder with {
                    Name = c.Name is null ? folder.Name : Name(c.Name, "take folder name"),
                    Crossfade = c.Crossfade ?? folder.Crossfade,
                    Muted = c.Muted ?? folder.Muted,
                    Takes = takes
                }
            )
        ];
    }

    static DeltaOp[] DeleteTake(ProjectState state, DeleteTakeCommand c) {
        var folder = FolderOf(state, c.FolderId);
        var take = TakeOf(folder, c.TakeId);
        if (folder.Takes.Count == 1) {
            throw new EditException("A take folder keeps at least one take: delete the folder instead.");
        }

        return [
            new TakeFolderUpserted(
                folder with {
                    Takes = [..folder.Takes.Where(t => t.Id != take.Id)],
                    Comp = [..folder.Comp.Where(s => s.TakeId != take.Id)]
                }
            )
        ];
    }

    /// <summary>The folder's comp as regions, crossfaded where the compiler crossfades it.</summary>
    static List<DeltaOp> FlattenTakeFolder(ProjectState state, FlattenTakeFolderCommand c, IIdSource ids) {
        var folder = FolderOf(state, c.FolderId);
        var map = state.TempoMap;
        var ops = new List<DeltaOp> { new TakeFolderRemoved(folder.Id) };
        var takes = folder.Takes.ToDictionary(static t => t.Id, StringComparer.Ordinal);
        foreach (var piece in Comps.Pieces(state, folder)) {
            // A region starts on a tick: from the first tick at or after the piece's start (within a tick of it),
            // with the file following exactly and the fade-in as much shorter, so a crossfade stays one.
            var tick = Recordings.CeilingTick(map, Math.Max(0, piece.Start), state.SampleRate);
            var shift = map.SampleAt(tick, state.SampleRate) - piece.Start;
            var offset = piece.FileOffset + shift;
            var length = piece.Length - shift;
            if (length <= 0 || offset < 0 || !state.Files.TryGetValue(piece.FileId, out var file)) {
                continue;
            }

            length = Math.Min(length, file.Frames - offset);
            if (length <= 0) {
                continue;
            }

            var fadeIn = Math.Min(Math.Max(0, piece.FadeIn - shift), length);
            ops.Add(
                new RegionUpserted(
                    new(
                        ids.Next(IdPrefix.Region),
                        folder.TrackId,
                        piece.FileId,
                        takes[piece.TakeId].Name,
                        tick,
                        offset,
                        length,
                        0,
                        fadeIn,
                        Math.Min(piece.FadeOut, length - fadeIn),
                        0,
                        0,
                        folder.Muted,
                        FlexFollow.Off,
                        [],
                        0,
                        0,
                        []
                    )
                )
            );
        }

        return ops;
    }

    /// <summary>Audio regions of one track become the takes of a new folder; the comp plays the last of them.</summary>
    static List<DeltaOp> PackTakeFolder(ProjectState state, PackTakeFolderCommand c, IIdSource ids) {
        if (c.RegionIds is not { Count: > 0 and <= MaxTakes }) {
            throw new EditException($"Pack 1…{MaxTakes} regions.");
        }

        var regions = c.RegionIds.Distinct(StringComparer.Ordinal)
            .Select(id => RegionOf(state, id ?? string.Empty))
            .OrderBy(static r => r.StartTick)
            .ThenBy(static r => r.Id, StringComparer.Ordinal)
            .ToArray();
        var trackId = regions[0].TrackId;
        if (regions.Any(r => r.TrackId != trackId)) {
            throw new EditException("The regions to pack are on one track.");
        }

        var map = state.TempoMap;
        var start = regions[0].StartTick;
        var end = regions.Max(r => map.TickAtSample(EndSample(state, r), state.SampleRate));
        var takes = regions.Select(r => new Take(
                    ids.Next(IdPrefix.Take),
                    r.Name,
                    r.FileId,
                    r.StartTick,
                    r.FileOffset,
                    r.Length
                )
            )
            .ToArray();
        var last = takes[^1];
        var lastEnd = Math.Min(end, map.TickAtSample(EndSample(state, regions[^1]), state.SampleRate));
        var folder = new TakeFolder(
            ids.Next(IdPrefix.TakeFolder),
            trackId,
            c.Name is null ? TrackOf(state, trackId).Name : Name(c.Name, "take folder name"),
            start,
            Math.Max(1, end - start),
            takes,
            lastEnd > last.StartTick ? [new(last.StartTick, lastEnd, last.Id)] : [],
            ProjectDefaults.CompCrossfade(state.SampleRate),
            false
        );
        return [..regions.Select(static r => new RegionRemoved(r.Id)), new TakeFolderUpserted(folder)];
    }

    /// <summary>
    ///     Glues regions of one track: MIDI regions into the first, with every note and event in place; audio regions
    ///     only when they play one contiguous stretch of one file.
    /// </summary>
    static List<DeltaOp> JoinRegions(ProjectState state, JoinRegionsCommand c) {
        if (c.RegionIds is not { Count: >= 2 } named) {
            throw new EditException("Join at least two regions.");
        }

        var distinct = named.Distinct(StringComparer.Ordinal).ToArray();
        if (distinct.Length < 2) {
            throw new EditException("Join at least two regions.");
        }

        return distinct.All(id => id is not null && state.MidiRegions.ContainsKey(id))
            ? JoinMidi(state, distinct)
            : JoinAudio(state, distinct);
    }

    static List<DeltaOp> JoinMidi(ProjectState state, string[] ids) {
        var regions = ids.Select(id => state.MidiRegions[id])
            .OrderBy(static r => r.StartTick)
            .ThenBy(static r => r.Id, StringComparer.Ordinal)
            .ToArray();
        var first = regions[0];
        if (regions.Any(r => r.TrackId != first.TrackId)) {
            throw new EditException("The regions to join are on one track.");
        }

        var end = regions.Max(static r => r.StartTick + r.LengthTicks);
        var notes = regions.SelectMany(r => r.Notes.Select(n => n with {
                    StartTick = n.StartTick + r.StartTick - first.StartTick
                }
            )
        );
        var controls = regions.SelectMany(r => r.Controls.Select(e => e with {
                    Tick = e.Tick + r.StartTick - first.StartTick
                }
            )
        );
        var joined = first with {
            LengthTicks = end - first.StartTick, Notes = Sorted(notes), Controls = Sorted(controls)
        };
        if ((long)joined.Notes.Count + joined.Controls.Count > MaxEventsPerRegion) {
            throw new EditException($"A MIDI region holds at most {MaxEventsPerRegion} notes and events.");
        }

        return [..regions.Skip(1).Select(static r => new MidiRegionRemoved(r.Id)), new MidiRegionUpserted(joined)];
    }

    static List<DeltaOp> JoinAudio(ProjectState state, string[] ids) {
        var regions = ids.Select(id => state.MidiRegions.ContainsKey(id ?? string.Empty)
                    ? throw new EditException("Join regions of one kind: audio or MIDI.")
                    : RegionOf(state, id ?? string.Empty)
            )
            .OrderBy(static r => r.StartTick)
            .ThenBy(static r => r.Id, StringComparer.Ordinal)
            .ToArray();
        var first = regions[0];
        var map = state.TempoMap;
        for (var i = 1; i < regions.Length; i++) {
            var (previous, r) = (regions[i - 1], regions[i]);
            var gap = map.SampleAt(r.StartTick, state.SampleRate) - EndSample(state, previous);
            if (r.TrackId != first.TrackId
                || r.FileId != first.FileId
                || r.FileOffset != previous.FileOffset + previous.Length
                || Math.Abs(gap) > 1) {
                throw new EditException(
                    "Audio regions join only when they play one contiguous stretch of one file (others need a bounce)."
                );
            }
        }

        var last = regions[^1];
        var length = last.FileOffset + last.Length - first.FileOffset;
        return [
            ..regions.Skip(1).Select(static r => new RegionRemoved(r.Id)),
            new RegionUpserted(first with { Length = length, FadeOut = Math.Min(last.FadeOut, length - first.FadeIn) })
        ];
    }

    static List<DeltaOp> CopyRegions(ProjectState state, CopyRegionsCommand c, IIdSource ids) {
        if (c.RegionIds is not { Count: > 0 and <= MaxBatch }) {
            throw new EditException($"Copy 1…{MaxBatch} regions.");
        }

        Track? target = c.TrackId is null ? null : TrackOf(state, c.TrackId);
        var ops = new List<DeltaOp>();
        var next = state.LastEventId;
        foreach (var id in c.RegionIds.Distinct(StringComparer.Ordinal)) {
            var item = Timeline.Region(state, id) ?? throw new EditException($"No region '{id}'.");
            CheckTick(item.Start + c.OffsetTicks, "copy's start");
            if (target is not null) {
                var fits = item.Kind == TimelineKind.MidiRegion
                    ? IsMidiKind(target.Kind)
                    : target.Kind == TrackKind.Audio;
                if (!fits) {
                    throw new EditException($"Track '{target.Id}' cannot hold region '{id}'.");
                }
            }

            ops.Add(Timeline.Copy(state, item, c.OffsetTicks, target?.Id, ids, ref next));
        }

        return ops;
    }

    static TakeFolder FolderOf(ProjectState state, string? id) =>
        state.TakeFolders.TryGetValue(id ?? string.Empty, out var folder)
            ? folder
            : throw new EditException($"No take folder '{id}'.");

    static Take TakeOf(TakeFolder folder, string? id) =>
        folder.Takes.FirstOrDefault(t => t.Id == id)
        ?? throw new EditException($"Take folder '{folder.Id}' has no take '{id}'.");
}
