using System.Diagnostics;

namespace Anode.Spikes.BulkIpc;

/// <summary>
///     What anode-server's compiler would produce: per-track sorted event arrays (already in samples) and audio
///     playlists, here random but realistic in shape (ten minutes, events spread over every track).
/// </summary>
sealed class CompiledModel {
    public const long DefaultLengthSamples = 48_000L * 600;
    const int PlaylistEntriesPerTrack = 64;

    public PlaybackEvent[][] Events { get; }
    public AudioPlaylistEntry[][] Playlists { get; }
    public long LengthSamples { get; }
    public long EventCount { get; }

    CompiledModel(PlaybackEvent[][] events, AudioPlaylistEntry[][] playlists, long lengthSamples, long eventCount) {
        Events = events;
        Playlists = playlists;
        LengthSamples = lengthSamples;
        EventCount = eventCount;
    }

    public static CompiledModel Generate(int tracks, long totalEvents, int seed) {
        var random = new Random(seed);
        var length = DefaultLengthSamples;
        var events = new PlaybackEvent[tracks][];
        var playlists = new AudioPlaylistEntry[tracks][];
        for (var t = 0; t < tracks; t++) {
            var count = (int)(totalEvents / tracks + (t < totalEvents % tracks ? 1 : 0));
            var track = new PlaybackEvent[count];
            var meanGap = Math.Max(1, length / Math.Max(1, count));
            long position = 0;
            for (var i = 0; i < count; i++) {
                position = Math.Min(length - 1, position + random.NextInt64(0, 2 * meanGap));
                track[i] = new() {
                    SamplePos = position,
                    Track = t,
                    Kind = (byte)(i & 1),
                    Data1 = (byte)random.Next(128),
                    Data2 = (byte)random.Next(128),
                    Value = random.NextSingle()
                };
            }

            events[t] = track;
            var playlist = new AudioPlaylistEntry[PlaylistEntriesPerTrack];
            var slot = length / PlaylistEntriesPerTrack;
            for (var i = 0; i < playlist.Length; i++) {
                playlist[i] = new() {
                    Start = i * slot,
                    FileOffset = random.NextInt64(0, 1L << 30),
                    Length = slot * 9 / 10,
                    FileId = t * 1000 + i,
                    Gain = 1f,
                    FadeIn = 0.01f,
                    FadeOut = 0.01f
                };
            }

            playlists[t] = playlist;
        }

        return new(events, playlists, length, totalEvents);
    }

    /// <summary>An edit: a different value on some of the track's events, so every version differs.</summary>
    public void Edit(int track, long version) {
        var events = Events[track];
        for (var i = (int)(version % 7); i < events.Length; i += 97) {
            events[i].Value = version;
        }
    }

    public long Bytes(int firstTrack, int trackCount) {
        long events = 0, playlists = 0;
        for (var t = firstTrack; t < firstTrack + trackCount; t++) {
            events += Events[t].Length;
            playlists += Playlists[t].Length;
        }

        return Layout.Offsets(trackCount, events, playlists).Total;
    }

    /// <summary>
    ///     Writes tracks <paramref name="firstTrack" />.. into a new segment (or into the pooled segment
    ///     <paramref name="into" />, which the host has released). The segment (its name and the writer's
    ///     view) stays until the caller disposes it, which the protocol does once the host has mapped it: unmapping is
    ///     off the swap's critical path.
    /// </summary>
    public unsafe (SharedSegment Segment, double CreateMs, double CopyMs, double ChecksumMs) Write(
        SegmentBackend backend,
        string directory,
        string prefix,
        long id,
        int firstTrack,
        int trackCount,
        long version,
        bool checksum,
        SharedSegment? into = null
    ) {
        var t0 = Stopwatch.GetTimestamp();
        long eventCount = 0, playlistCount = 0;
        for (var t = firstTrack; t < firstTrack + trackCount; t++) {
            eventCount += Events[t].Length;
            playlistCount += Playlists[t].Length;
        }

        var offsets = Layout.Offsets(trackCount, eventCount, playlistCount);
        var segment = into ?? SharedSegment.Create(backend, directory, prefix, id, offsets.Total);
        if (segment.Length < offsets.Total) {
            throw new InvalidOperationException("The model does not fit the pooled segment.");
        }

        var t1 = Stopwatch.GetTimestamp();

        var basePointer = segment.Pointer;
        var header = (SegmentHeader*)basePointer;
        var table = (TrackEntry*)(basePointer + offsets.Tracks);
        var eventsOut = (PlaybackEvent*)(basePointer + offsets.Events);
        var playlistsOut = (AudioPlaylistEntry*)(basePointer + offsets.Playlists);
        long e = 0, p = 0;
        for (var i = 0; i < trackCount; i++) {
            var events = Events[firstTrack + i];
            var playlist = Playlists[firstTrack + i];
            table[i] = new() {
                TrackId = firstTrack + i,
                EventStart = e,
                EventCount = events.Length,
                PlaylistStart = p,
                PlaylistCount = playlist.Length
            };
            events.AsSpan().CopyTo(new(eventsOut + e, events.Length));
            playlist.AsSpan().CopyTo(new(playlistsOut + p, playlist.Length));
            e += events.Length;
            p += playlist.Length;
        }

        *header = new() {
            Magic = SegmentHeader.MagicValue,
            Format = SegmentHeader.CurrentFormat,
            HeaderSize = (ushort)sizeof(SegmentHeader),
            TotalLength = offsets.Total,
            ModelVersion = version,
            TrackCount = trackCount,
            Flags = checksum ? SegmentHeader.FlagChecksum : 0,
            TrackTableOffset = offsets.Tracks,
            EventsOffset = offsets.Events,
            EventCount = eventCount,
            PlaylistOffset = offsets.Playlists,
            PlaylistCount = playlistCount,
            LengthSamples = LengthSamples
        };
        var t2 = Stopwatch.GetTimestamp();
        if (checksum) {
            header->Checksum = Checksum.Compute(basePointer + offsets.Tracks, offsets.Total - offsets.Tracks);
        }

        var t3 = Stopwatch.GetTimestamp();
        return (segment, Ms(t0, t1), Ms(t1, t2), Ms(t2, t3));
    }

    static double Ms(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;
}
