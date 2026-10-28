namespace Anode.Ipc.Segments;

/// <summary>
///     Writes compiled tracks into a shared-memory segment (server side; docs/adr/0006-bulk-ipc.md, spike S3). The
///     caller owns the returned segment: it keeps it until the host has acknowledged the mapping, then disposes it,
///     which removes the name; the host's mapping keeps the memory alive.
/// </summary>
public static unsafe class SegmentWriter {
    /// <summary>The bytes a segment of <paramref name="tracks" /> needs.</summary>
    public static long Size(IReadOnlyList<TrackModel> tracks) {
        long events = 0, playlists = 0, warps = 0, pitches = 0;
        foreach (var t in tracks) {
            events += t.Events.Length;
            playlists += t.Playlist.Length;
            warps += t.Warp?.Length ?? 0;
            pitches += t.Pitch?.Length ?? 0;
        }

        return Layout.Offsets(tracks.Count, events, playlists, warps, pitches).Total;
    }

    /// <summary>
    ///     Writes <paramref name="tracks" /> into <paramref name="into" /> (a pooled segment the host has released) or
    ///     a new segment named <c>{prefix}-{id}</c>.
    /// </summary>
    public static SharedSegment Write(
        IReadOnlyList<TrackModel> tracks,
        long version,
        long lengthSamples,
        string prefix,
        long id,
        bool checksum,
        SharedSegment? into = null
    ) {
        long eventCount = 0, playlistCount = 0, warpCount = 0, pitchCount = 0;
        foreach (var t in tracks) {
            eventCount += t.Events.Length;
            playlistCount += t.Playlist.Length;
            warpCount += t.Warp?.Length ?? 0;
            pitchCount += t.Pitch?.Length ?? 0;
        }

        var offsets = Layout.Offsets(tracks.Count, eventCount, playlistCount, warpCount, pitchCount);
        var segment = into
            ?? SharedSegment.Create(DefaultBackend, SharedSegment.DefaultDirectory(), prefix, id, offsets.Total);
        if (segment.Length < offsets.Total) {
            throw new InvalidOperationException("The model does not fit the pooled segment.");
        }

        var basePointer = segment.Pointer;
        var header = (SegmentHeader*)basePointer;
        var table = (TrackEntry*)(basePointer + offsets.Tracks);
        var eventsOut = (PlaybackEvent*)(basePointer + offsets.Events);
        var playlistsOut = (AudioPlaylistEntry*)(basePointer + offsets.Playlists);
        var warpsOut = (WarpPoint*)(basePointer + offsets.Warps);
        var pitchesOut = (PitchPoint*)(basePointer + offsets.Pitches);
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
            track.Events.AsSpan().CopyTo(new(eventsOut + e, track.Events.Length));
            var playlist = new Span<AudioPlaylistEntry>(playlistsOut + p, track.Playlist.Length);
            track.Playlist.AsSpan().CopyTo(playlist);
            foreach (ref var entry in playlist) {
                // A track's warp and pitch ranges index its own arrays; the segment's hold every track's.
                entry.WarpStart += entry.WarpCount > 0 ? (int)w : 0;
                entry.PitchStart += entry.PitchCount > 0 ? (int)q : 0;
            }

            track.Warp.AsSpan().CopyTo(new(warpsOut + w, track.Warp?.Length ?? 0));
            track.Pitch.AsSpan().CopyTo(new(pitchesOut + q, track.Pitch?.Length ?? 0));
            e += track.Events.Length;
            p += track.Playlist.Length;
            w += track.Warp?.Length ?? 0;
            q += track.Pitch?.Length ?? 0;
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
            EventCount = eventCount,
            PlaylistOffset = offsets.Playlists,
            PlaylistCount = playlistCount,
            WarpOffset = offsets.Warps,
            WarpCount = warpCount,
            PitchOffset = offsets.Pitches,
            PitchCount = pitchCount,
            LengthSamples = lengthSamples
        };
        if (checksum) {
            header->Checksum = Checksum.Compute(basePointer + offsets.Tracks, offsets.Total - offsets.Tracks);
        }

        return segment;
    }

    /// <summary>
    ///     POSIX shared memory on macOS (file-backed mappings stall the audio thread there, spike S3); the
    ///     MemoryMappedFile backends elsewhere (<c>/dev/shm</c> on Linux, named mappings on Windows).
    /// </summary>
    public static SegmentBackend DefaultBackend =>
        OperatingSystem.IsMacOS() ? SegmentBackend.PosixShm : SegmentBackend.File;
}
