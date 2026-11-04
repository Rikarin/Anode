using Anode.Contracts;

namespace Anode.Model.Editing;

/// <summary>
///     <c>pasteRegions</c> (docs/04-phase-3.md, "Regions"): regions off the editor's clipboard. A cut has deleted them,
///     so nothing here can be looked up: each is checked as a new one, and only then given ids of its own.
/// </summary>
public static partial class Edits {
    static List<DeltaOp> PasteRegions(
        ProjectState state,
        PasteRegionsCommand c,
        IIdSource ids,
        SessionPlayerCatalog styles
    ) {
        var count = PastedCount(c);
        if (count is < 1 or > MaxBatch) {
            throw new EditException($"Paste 1…{MaxBatch} regions.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var notes = new Dictionary<long, long>();
        var next = state.LastEventId;
        var ops = new List<DeltaOp>((int)count);
        foreach (var region in c.Regions ?? []) {
            ops.Add(new RegionUpserted(PastedAudio(state, region, ids, seen)));
        }

        foreach (var region in c.MidiRegions ?? []) {
            var track = MidiTrackOf(state, region?.TrackId ?? string.Empty);
            CheckPlayback(region!.Playback ?? ProjectDefaults.Playback);
            ops.Add(new MidiRegionUpserted(ImportedMidi(state, region!, track, ids, seen, notes, ref next)));
        }

        foreach (var folder in c.TakeFolders ?? []) {
            ops.Add(new TakeFolderUpserted(PastedFolder(state, folder, ids, seen)));
        }

        foreach (var region in c.PatternRegions ?? []) {
            ops.Add(new PatternRegionUpserted(PastedPattern(state, region, ids, seen)));
        }

        foreach (var region in c.SessionPlayerRegions ?? []) {
            ops.Add(new SessionPlayerRegionUpserted(PastedPlayer(state, region, ids, seen, styles)));
        }

        return ops;
    }

    static long PastedCount(PasteRegionsCommand c) =>
        (long)(c.Regions?.Count ?? 0)
        + (c.MidiRegions?.Count ?? 0)
        + (c.TakeFolders?.Count ?? 0)
        + (c.PatternRegions?.Count ?? 0)
        + (c.SessionPlayerRegions?.Count ?? 0);

    /// <summary>An audio region as it was copied: unlike an import it keeps its flex, tuning and pitch notes.</summary>
    static AudioRegion PastedAudio(ProjectState state, AudioRegion? source, IIdSource ids, HashSet<string> seen) {
        var track = AudioTrackOf(state, source?.TrackId ?? string.Empty);
        var region = ImportedAudio(state, source!, track, ids, seen);
        if (!Enum.IsDefined(source!.Follow)) {
            throw new EditException($"Unknown follow mode {source.Follow}.");
        }

        if (source.Follow != FlexFollow.Off && FileOf(state, source.FileId).Tempo is null) {
            throw new EditException($"Following the tempo needs the beats of file '{source.FileId}': it has none.");
        }

        if (source.Transpose is < -MaxRegionTranspose or > MaxRegionTranspose) {
            throw new EditException(
                $"A region transposes −{MaxRegionTranspose}…{MaxRegionTranspose} semitones, not {source.Transpose}."
            );
        }

        if (source.FineTune is < -MaxFineTune or > MaxFineTune) {
            throw new EditException(
                $"A region's fine tune is −{MaxFineTune}…{MaxFineTune} cents, not {source.FineTune}."
            );
        }

        CheckPitchNotes(source, source.PitchNotes);
        CheckLoop(source.LoopTicks);
        CheckStretch(source.Stretch);
        // Under another tempo a marker's tick may pass the region's plain end: a copy keeps what its source had.
        return region with {
            Follow = source.Follow,
            FlexMarkers = CheckedFlexMarkers(source, source.FlexMarkers, MaxTick),
            Transpose = source.Transpose,
            FineTune = source.FineTune,
            PitchNotes = [..source.PitchNotes],
            LoopTicks = source.LoopTicks,
            Stretch = source.Stretch
        };
    }

    /// <summary>A take folder on an audio track: takes of the project's files, a comp of them.</summary>
    static TakeFolder PastedFolder(ProjectState state, TakeFolder? source, IIdSource ids, HashSet<string> seen) {
        var track = AudioTrackOf(state, source?.TrackId ?? string.Empty);
        CheckPastedPlace(source!.Id, source.StartTick, source.LengthTicks, seen);
        CheckCrossfade(state, source.Crossfade);
        if (source.Takes is not { Count: > 0 and <= MaxTakes } takes) {
            throw new EditException($"A take folder has 1…{MaxTakes} takes.");
        }

        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var take in takes) {
            if (take?.Id is not { Length: > 0 } || !taken.Add(take.Id)) {
                throw new EditException("Each take has its own id.");
            }

            CheckTick(take.StartTick, "take start");
            CheckSpan(FileOf(state, take.FileId ?? string.Empty), take.FileOffset, take.Length);
            _ = Name(take.Name ?? string.Empty, "take name");
        }

        CheckComp(source, source.Comp);
        return Timeline.WithNewIds(
            source with {
                TrackId = track.Id,
                Name = Name(source.Name ?? string.Empty, "take folder name"),
                Takes = [..takes],
                Comp = [..source.Comp]
            },
            ids
        );
    }

    static PatternRegion PastedPattern(ProjectState state, PatternRegion? source, IIdSource ids, HashSet<string> seen) {
        var track = MidiTrackOf(state, source?.TrackId ?? string.Empty);
        CheckPastedPlace(source!.Id, source.StartTick, source.LengthTicks, seen);
        return source with {
            Id = ids.Next(IdPrefix.PatternRegion),
            TrackId = track.Id,
            Name = Name(source.Name ?? string.Empty, "region name"),
            Pattern = CheckPattern(source.Pattern)
        };
    }

    /// <summary>A Session Player region on a track of its player: its seed stays, so it plays the original's notes.</summary>
    static SessionPlayerRegion PastedPlayer(
        ProjectState state,
        SessionPlayerRegion? source,
        IIdSource ids,
        HashSet<string> seen,
        SessionPlayerCatalog styles
    ) {
        var (track, player) = SessionPlayerTrackOf(state, source?.TrackId);
        CheckPastedPlace(source!.Id, source.StartTick, source.LengthTicks, seen);
        return source with {
            Id = ids.Next(IdPrefix.SessionPlayerRegion),
            TrackId = track.Id,
            Name = Name(source.Name ?? string.Empty, "region name"),
            Parameters = CheckParameters(state, styles, player, track.Id, source.Parameters)
        };
    }

    /// <summary>A region's own id (once in the paste) and its place inside the project.</summary>
    static void CheckPastedPlace(string? id, long start, long length, HashSet<string> seen) {
        if (id is not { Length: > 0 } || !seen.Add(id)) {
            throw new EditException("Each pasted region has its own id.");
        }

        CheckTick(start, "region start");
        CheckLength(length, "region");
        CheckTick(start + length, "region end");
    }
}
