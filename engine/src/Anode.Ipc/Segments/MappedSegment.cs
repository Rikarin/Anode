namespace Anode.Ipc.Segments;

/// <summary>
///     What the audio thread reads of one track: pointers into a mapped segment. <see cref="Warp" /> and
///     <see cref="Pitch" /> are the whole segment's warp and pitch arrays, which warped entries index (format 3).
/// </summary>
public readonly unsafe struct TrackView(
    PlaybackEvent* events,
    long eventCount,
    AudioPlaylistEntry* playlist,
    long playlistCount,
    WarpPoint* warp = null,
    long warpCount = 0,
    PitchPoint* pitch = null,
    long pitchCount = 0
) {
    public PlaybackEvent* Events { get; } = events;
    public long EventCount { get; } = eventCount;
    public AudioPlaylistEntry* Playlist { get; } = playlist;
    public long PlaylistCount { get; } = playlistCount;
    public WarpPoint* Warp { get; } = warp;
    public long WarpCount { get; } = warpCount;
    public PitchPoint* Pitch { get; } = pitch;
    public long PitchCount { get; } = pitchCount;
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

    /// <summary>
    ///     Checks the header, the track table and the warped entries (their ranges and warp maps), so every pointer the
    ///     audio and disk threads derive is in bounds.
    /// </summary>
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
            || h->PlaylistCount > segment.Length / sizeof(AudioPlaylistEntry)
            || h->WarpCount < 0
            || h->PitchCount < 0
            || h->WarpCount > segment.Length / sizeof(WarpPoint)
            || h->PitchCount > segment.Length / sizeof(PitchPoint)) {
            throw new InvalidDataException("Segment counts out of range.");
        }

        var expected = Layout.Offsets(h->TrackCount, h->EventCount, h->PlaylistCount, h->WarpCount, h->PitchCount);
        if (expected.Tracks != h->TrackTableOffset
            || expected.Events != h->EventsOffset
            || expected.Playlists != h->PlaylistOffset
            || expected.Warps != h->WarpOffset
            || expected.Pitches != h->PitchOffset
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

        for (var i = 0; i < h->TrackCount; i++) {
            CheckFlex(i, Track(i));
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
            entry.PlaylistCount,
            (WarpPoint*)((byte*)h + h->WarpOffset),
            h->WarpCount,
            (PitchPoint*)((byte*)h + h->PitchOffset),
            h->PitchCount
        );
    }

    public void Dispose() => segment.Dispose();

    /// <summary>
    ///     Every warped entry's warp and pitch ranges lie inside the segment's arrays, and its warp map is one a flex
    ///     renderer can play (<see cref="AudioPlaylistEntry" />): an algorithm, at least two points, the first at offset
    ///     0 and the last at the entry's length, offsets strictly increasing, file frames finite; its pitch points are
    ///     finite and sorted by file frame. Part of the layout check (not <see cref="Verify" />, which large segments
    ///     skip), since the disk thread indexes the arrays by these ranges.
    /// </summary>
    static void CheckFlex(int track, TrackView t) {
        for (long e = 0; e < t.PlaylistCount; e++) {
            var entry = t.Playlist[e];
            if (entry.WarpCount < 0
                || entry.PitchCount < 0
                || (entry.WarpCount > 0
                    && (entry.WarpStart < 0 || (long)entry.WarpStart + entry.WarpCount > t.WarpCount))
                || (entry.PitchCount > 0
                    && (entry.PitchStart < 0 || (long)entry.PitchStart + entry.PitchCount > t.PitchCount))) {
                throw new InvalidDataException(
                    $"Track {track} playlist entry {e} has warp or pitch points out of bounds."
                );
            }

            if (entry.WarpCount == 0) {
                continue;
            }

            if (!WarpMapValid(entry, t.Warp + entry.WarpStart)
                || !PitchValid(t.Pitch + entry.PitchStart, entry.PitchCount)) {
                throw new InvalidDataException(
                    $"Track {track} playlist entry {e} has an invalid warp map or pitch curve."
                );
            }
        }
    }

    static bool WarpMapValid(in AudioPlaylistEntry entry, WarpPoint* warp) {
        if (entry.Algorithm is FlexAlgorithm.None or > FlexAlgorithm.Pitch
            || entry.WarpCount < 2
            || warp[0].Offset != 0
            || warp[entry.WarpCount - 1].Offset != entry.Length) {
            return false;
        }

        for (var k = 0; k < entry.WarpCount; k++) {
            if (!double.IsFinite(warp[k].FileFrame) || (k > 0 && warp[k].Offset <= warp[k - 1].Offset)) {
                return false;
            }
        }

        return true;
    }

    static bool PitchValid(PitchPoint* pitch, int count) {
        for (var k = 0; k < count; k++) {
            var p = pitch[k];
            if (!double.IsFinite(p.FileFrame)
                || !float.IsFinite(p.Cents)
                || !float.IsFinite(p.FormantCents)
                || !float.IsFinite(p.GainDb)
                || (k > 0 && p.FileFrame < pitch[k - 1].FileFrame)) {
                return false;
            }
        }

        return true;
    }

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
