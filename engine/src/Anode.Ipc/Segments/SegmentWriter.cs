namespace Anode.Ipc.Segments;

/// <summary>
///     Writes compiled tracks into a shared-memory segment (server side; docs/adr/0006-bulk-ipc.md, spike S3). The
///     caller owns the returned segment: it keeps it until the host has acknowledged the mapping, then disposes it,
///     which removes the name; the host's mapping keeps the memory alive.
/// </summary>
public static unsafe class SegmentWriter {
    /// <summary>The bytes a segment of <paramref name="tracks" /> (and the tempo points and cells) needs.</summary>
    public static long Size(
        IReadOnlyList<TrackModel> tracks,
        IReadOnlyList<TempoMapPoint>? tempo = null,
        IReadOnlyList<CellModel>? cells = null
    ) {
        var counts = Count(tracks, cells);
        return Layout.Offsets(
                tracks.Count,
                counts.Events,
                counts.Playlists,
                counts.Warps,
                counts.Pitches,
                tempo?.Count ?? 0,
                cells?.Count ?? 0
            )
            .Total;
    }

    /// <summary>
    ///     Writes <paramref name="tracks" />, then the cells' content, into <paramref name="into" /> (a pooled segment the
    ///     host has released) or a new segment named <c>{prefix}-{id}</c>, with <paramref name="tempo" /> (format 4;
    ///     none for a per-track segment) and the cell table of <paramref name="cells" />.
    /// </summary>
    public static SharedSegment Write(
        IReadOnlyList<TrackModel> tracks,
        long version,
        long lengthSamples,
        string prefix,
        long id,
        bool checksum,
        SharedSegment? into = null,
        IReadOnlyList<TempoMapPoint>? tempo = null,
        IReadOnlyList<CellModel>? cells = null
    ) {
        tempo ??= [];
        cells ??= [];
        var counts = Count(tracks, cells);
        var offsets = Layout.Offsets(
            tracks.Count,
            counts.Events,
            counts.Playlists,
            counts.Warps,
            counts.Pitches,
            tempo.Count,
            cells.Count
        );
        var segment = into
            ?? SharedSegment.Create(DefaultBackend, SharedSegment.DefaultDirectory(), prefix, id, offsets.Total);
        if (segment.Length < offsets.Total) {
            throw new InvalidOperationException("The model does not fit the pooled segment.");
        }

        var basePointer = segment.Pointer;
        var header = (SegmentHeader*)basePointer;
        var table = (TrackEntry*)(basePointer + offsets.Tracks);
        var cellTable = (CellEntry*)(basePointer + offsets.Cells);
        var arrays = new Arrays(
            (PlaybackEvent*)(basePointer + offsets.Events),
            (AudioPlaylistEntry*)(basePointer + offsets.Playlists),
            (WarpPoint*)(basePointer + offsets.Warps),
            (PitchPoint*)(basePointer + offsets.Pitches)
        );
        long e = 0, p = 0, w = 0, q = 0;
        for (var i = 0; i < tracks.Count; i++) {
            var track = tracks[i];
            table[i] = new() {
                TrackId = track.TrackId,
                EventStart = e,
                EventCount = track.Events.Length,
                PlaylistStart = p,
                PlaylistCount = track.Playlist.Length
            };
            arrays.Copy(track.Events, track.Playlist, track.Warp, track.Pitch, ref e, ref p, ref w, ref q);
        }

        for (var i = 0; i < cells.Count; i++) {
            var cell = cells[i];
            cellTable[i] = new() {
                Index = cell.Index,
                TrackId = cell.TrackId,
                EventStart = e,
                EventCount = cell.Events.Length,
                PlaylistStart = p,
                PlaylistCount = cell.Playlist.Length,
                Length = cell.Length,
                Mode = cell.Mode
            };
            arrays.Copy(cell.Events, cell.Playlist, cell.Warp, cell.Pitch, ref e, ref p, ref w, ref q);
        }

        for (var i = 0; i < tempo.Count; i++) {
            ((TempoMapPoint*)(basePointer + offsets.Tempo))[i] = tempo[i];
        }

        *header = new() {
            Magic = SegmentHeader.MagicValue,
            Format = SegmentHeader.CurrentFormat,
            HeaderSize = (ushort)sizeof(SegmentHeader),
            TotalLength = offsets.Total,
            ModelVersion = version,
            TrackCount = tracks.Count,
            Flags = checksum ? SegmentHeader.FlagChecksum : 0,
            TrackTableOffset = offsets.Tracks,
            EventsOffset = offsets.Events,
            EventCount = counts.Events,
            PlaylistOffset = offsets.Playlists,
            PlaylistCount = counts.Playlists,
            WarpOffset = offsets.Warps,
            WarpCount = counts.Warps,
            PitchOffset = offsets.Pitches,
            PitchCount = counts.Pitches,
            TempoOffset = offsets.Tempo,
            TempoCount = tempo.Count,
            CellOffset = offsets.Cells,
            CellCount = cells.Count,
            LengthSamples = lengthSamples
        };
        if (checksum) {
            header->Checksum = Checksum.Compute(basePointer + offsets.Tracks, offsets.Total - offsets.Tracks);
        }

        return segment;
    }

    /// <summary>The element counts of the shared arrays: every track's slices, then every cell's.</summary>
    static (long Events, long Playlists, long Warps, long Pitches) Count(
        IReadOnlyList<TrackModel> tracks,
        IReadOnlyList<CellModel>? cells
    ) {
        long events = 0, playlists = 0, warps = 0, pitches = 0;
        foreach (var t in tracks) {
            events += t.Events.Length;
            playlists += t.Playlist.Length;
            warps += t.Warp?.Length ?? 0;
            pitches += t.Pitch?.Length ?? 0;
        }

        foreach (var c in cells ?? []) {
            events += c.Events.Length;
            playlists += c.Playlist.Length;
            warps += c.Warp?.Length ?? 0;
            pitches += c.Pitch?.Length ?? 0;
        }

        return (events, playlists, warps, pitches);
    }

    /// <summary>The segment's shared arrays, which tracks' and cells' slices are copied into in turn.</summary>
    readonly struct Arrays(
        PlaybackEvent* events,
        AudioPlaylistEntry* playlists,
        WarpPoint* warps,
        PitchPoint* pitches) {
        /// <summary>
        ///     Copies one slice at the cursors and advances them. A slice's warp and pitch ranges index its own arrays;
        ///     the segment's hold every slice's, so they are rebased.
        /// </summary>
        public void Copy(
            PlaybackEvent[] sliceEvents,
            AudioPlaylistEntry[] slicePlaylist,
            WarpPoint[]? sliceWarp,
            PitchPoint[]? slicePitch,
            ref long e,
            ref long p,
            ref long w,
            ref long q
        ) {
            sliceEvents.AsSpan().CopyTo(new(events + e, sliceEvents.Length));
            var playlist = new Span<AudioPlaylistEntry>(playlists + p, slicePlaylist.Length);
            slicePlaylist.AsSpan().CopyTo(playlist);
            foreach (ref var entry in playlist) {
                entry.WarpStart += entry.WarpCount > 0 ? (int)w : 0;
                entry.PitchStart += entry.PitchCount > 0 ? (int)q : 0;
            }

            sliceWarp.AsSpan().CopyTo(new(warps + w, sliceWarp?.Length ?? 0));
            slicePitch.AsSpan().CopyTo(new(pitches + q, slicePitch?.Length ?? 0));
            e += sliceEvents.Length;
            p += slicePlaylist.Length;
            w += sliceWarp?.Length ?? 0;
            q += slicePitch?.Length ?? 0;
        }
    }

    /// <summary>
    ///     POSIX shared memory on macOS (file-backed mappings stall the audio thread there, spike S3); the
    ///     MemoryMappedFile backends elsewhere (<c>/dev/shm</c> on Linux, named mappings on Windows).
    /// </summary>
    public static SegmentBackend DefaultBackend =>
        OperatingSystem.IsMacOS() ? SegmentBackend.PosixShm : SegmentBackend.File;
}
