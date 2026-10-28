namespace Anode.Ipc.Segments;

/// <summary>What the audio thread reads of one track: pointers into a mapped segment.</summary>
public readonly unsafe struct TrackView(
    PlaybackEvent* events,
    long eventCount,
    AudioPlaylistEntry* playlist,
    long playlistCount
) {
    public PlaybackEvent* Events { get; } = events;
    public long EventCount { get; } = eventCount;
    public AudioPlaylistEntry* Playlist { get; } = playlist;
    public long PlaylistCount { get; } = playlistCount;
}

/// <summary>
///     A segment the host has mapped (read-only) and validated. The engine reference-counts it by the published models
///     that use it, on its control thread.
/// </summary>
public sealed unsafe class MappedSegment : IDisposable {
    readonly SharedSegment segment;

    public long Id { get; }
    public int RootRefs { get; set; }

    /// <summary>A pooled segment stays mapped when no root uses it; the server writes the next model into it.</summary>
    public bool Pooled { get; set; }

    public long Length => segment.Length;
    public SegmentHeader* Header => (SegmentHeader*)segment.Pointer;

    MappedSegment(long id, SharedSegment segment) {
        Id = id;
        this.segment = segment;
    }

    public static MappedSegment Map(long id, string name, long length) => new(id, SharedSegment.Open(name, length));

    /// <summary>Checks the header and the track table, so every pointer the audio thread derives is in bounds.</summary>
    public void ValidateLayout() {
        if (segment.Length < sizeof(SegmentHeader)) {
            throw new InvalidDataException("Segment smaller than its header.");
        }

        var h = Header;
        if (h->Magic != SegmentHeader.MagicValue
            || h->Format != SegmentHeader.CurrentFormat
            || h->HeaderSize != sizeof(SegmentHeader)
            || h->TotalLength > segment.Length) {
            throw new InvalidDataException("Not a compatible playback model segment.");
        }

        if (h->TrackCount is < 1 or > 4096
            || h->EventCount < 0
            || h->PlaylistCount < 0
            || h->EventCount > segment.Length / sizeof(PlaybackEvent)
            || h->PlaylistCount > segment.Length / sizeof(AudioPlaylistEntry)) {
            throw new InvalidDataException("Segment counts out of range.");
        }

        var expected = Layout.Offsets(h->TrackCount, h->EventCount, h->PlaylistCount);
        if (expected.Tracks != h->TrackTableOffset
            || expected.Events != h->EventsOffset
            || expected.Playlists != h->PlaylistOffset
            || expected.Total != h->TotalLength) {
            throw new InvalidDataException("Segment offsets do not match its counts.");
        }

        var table = (TrackEntry*)((byte*)h + h->TrackTableOffset);
        for (var i = 0; i < h->TrackCount; i++) {
            var t = table[i];
            if (t.EventStart < 0
                || t.EventCount < 0
                || t.EventStart + t.EventCount > h->EventCount
                || t.PlaylistStart < 0
                || t.PlaylistCount < 0
                || t.PlaylistStart + t.PlaylistCount > h->PlaylistCount) {
                throw new InvalidDataException($"Track entry {i} out of bounds.");
            }
        }
    }

    /// <summary>
    ///     Touches every page, so the audio thread does not take the page faults of a model it has just been given
    ///     (true of shared memory; not of an APFS file on macOS, see docs/spikes/S3-bulk-ipc.md). Cheaper than
    ///     <see cref="Verify" /> and needed even when that is skipped. Returns the sum of the bytes read, so the reads
    ///     cannot be elided.
    /// </summary>
    public long Prefault() {
        var page = Environment.SystemPageSize;
        var p = segment.Pointer;
        long sum = 0;
        for (long offset = 0; offset < segment.Length; offset += page) {
            sum += p[offset];
        }

        return sum;
    }

    /// <summary>The checksum (when the writer set one) and the sort order of every track's events and playlist.</summary>
    public void Verify() {
        var h = Header;
        if ((h->Flags & SegmentHeader.FlagChecksum) != 0) {
            var payload = (byte*)h + h->TrackTableOffset;
            if (Checksum.Compute(payload, h->TotalLength - h->TrackTableOffset) != h->Checksum) {
                throw new InvalidDataException("Segment checksum mismatch.");
            }
        }

        for (var i = 0; i < h->TrackCount; i++) {
            var t = Track(i);
            for (long e = 1; e < t.EventCount; e++) {
                if (t.Events[e].SamplePos < t.Events[e - 1].SamplePos) {
                    throw new InvalidDataException($"Track {i} events are not sorted.");
                }
            }

            CheckPlaylist(i, t);
        }
    }

    public bool TryLock() => segment.TryLock();

    public int TrackId(int index) => ((TrackEntry*)((byte*)Header + Header->TrackTableOffset))[index].TrackId;

    public TrackView Track(int index) {
        var h = Header;
        var entry = ((TrackEntry*)((byte*)h + h->TrackTableOffset))[index];
        return new(
            (PlaybackEvent*)((byte*)h + h->EventsOffset) + entry.EventStart,
            entry.EventCount,
            (AudioPlaylistEntry*)((byte*)h + h->PlaylistOffset) + entry.PlaylistStart,
            entry.PlaylistCount
        );
    }

    public void Dispose() => segment.Dispose();

    /// <summary>A track's playlist is sorted by start and overlaps only as crossfades, never three entries at once.</summary>
    static void CheckPlaylist(int track, TrackView t) {
        for (long e = 1; e < t.PlaylistCount; e++) {
            var previous = t.Playlist[e - 1];
            var current = t.Playlist[e];
            var overlap = previous.Start + previous.Length - current.Start;
            var threeAtOnce = e >= 2 && t.Playlist[e - 2].Start + t.Playlist[e - 2].Length > current.Start;
            var notACrossfade = overlap > 0 && (overlap > previous.FadeOut || overlap > current.FadeIn || threeAtOnce);
            if (current.Start < previous.Start || notACrossfade) {
                throw new InvalidDataException(
                    $"Track {track} playlist entries are not sorted, or overlap other than as a crossfade."
                );
            }
        }
    }
}
