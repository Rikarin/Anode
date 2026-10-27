namespace Anode.Ipc.Segments;

/// <summary>
///     Writes compiled tracks into a shared-memory segment (server side; docs/adr/0006-bulk-ipc.md, spike S3). The
///     caller owns the returned segment: it keeps it until the host has acknowledged the mapping, then disposes it,
///     which removes the name; the host's mapping keeps the memory alive.
/// </summary>
public static unsafe class SegmentWriter {
    /// <summary>The bytes a segment of <paramref name="tracks" /> needs.</summary>
    public static long Size(IReadOnlyList<TrackModel> tracks) {
        long events = 0, playlists = 0;
        foreach (var t in tracks) {
            events += t.Events.Length;
            playlists += t.Playlist.Length;
        }

        return Layout.Offsets(tracks.Count, events, playlists).Total;
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
        long eventCount = 0, playlistCount = 0;
        foreach (var t in tracks) {
            eventCount += t.Events.Length;
            playlistCount += t.Playlist.Length;
        }

        var offsets = Layout.Offsets(tracks.Count, eventCount, playlistCount);
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
        long e = 0, p = 0;
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
            track.Playlist.AsSpan().CopyTo(new(playlistsOut + p, track.Playlist.Length));
            e += track.Events.Length;
            p += track.Playlist.Length;
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
