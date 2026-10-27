using System.Runtime.InteropServices;

namespace Anode.Spikes.BulkIpc;

/// <summary>One compiled event of a track, at an absolute sample position. 24 bytes, no implicit padding.</summary>
[StructLayout(LayoutKind.Sequential)]
struct PlaybackEvent {
    public long SamplePos;
    public int Track;
    public byte Kind;
    public byte Data1;
    public byte Data2;
    public byte Reserved;
    public float Value;
    public int Reserved2;
}

/// <summary>One audio region of a track's playlist: play <c>Length</c> samples of a file from <c>FileOffset</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
struct AudioPlaylistEntry {
    public long Start;
    public long FileOffset;
    public long Length;
    public int FileId;
    public float Gain;
    public float FadeIn;
    public float FadeOut;
}

/// <summary>Where one track's slices of the event and playlist arrays are, in elements.</summary>
[StructLayout(LayoutKind.Sequential)]
struct TrackEntry {
    public int TrackId;
    public int Flags;
    public long EventStart;
    public long EventCount;
    public long PlaylistStart;
    public long PlaylistCount;
}

/// <summary>
///     The first bytes of a segment. A segment is: header, track table, events (each track's slice sorted by
///     <see cref="PlaybackEvent.SamplePos" />), playlist entries (each track's slice sorted by start); every array starts
///     on a 64-byte boundary. A whole model is one segment of every track; a per-track segment has one track entry.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
struct SegmentHeader {
    public const uint MagicValue = 0x4D504E41; // "ANPM", Anode playback model
    public const ushort CurrentFormat = 1;
    public const int FlagChecksum = 1;

    public uint Magic;
    public ushort Format;
    public ushort HeaderSize;
    public long TotalLength;
    public long ModelVersion;
    public int TrackCount;
    public int Flags;
    public long TrackTableOffset;
    public long EventsOffset;
    public long EventCount;
    public long PlaylistOffset;
    public long PlaylistCount;
    public long LengthSamples;
    public ulong Checksum;
}

static unsafe class Layout {
    public const int Alignment = 64;

    public static long Align(long value) => (value + Alignment - 1) & ~(long)(Alignment - 1);

    /// <summary>Byte offsets of the three arrays and the total length for the given element counts.</summary>
    public static (long Tracks, long Events, long Playlists, long Total) Offsets(
        int trackCount,
        long eventCount,
        long playlistCount
    ) {
        var tracks = Align(sizeof(SegmentHeader));
        var events = Align(tracks + (long)trackCount * sizeof(TrackEntry));
        var playlists = Align(events + eventCount * sizeof(PlaybackEvent));
        var total = Align(playlists + playlistCount * sizeof(AudioPlaylistEntry));
        return (tracks, events, playlists, total);
    }

    public static void AssertSizes() {
        if (sizeof(PlaybackEvent) != 24 || sizeof(AudioPlaylistEntry) != 40 || sizeof(TrackEntry) != 40) {
            throw new InvalidOperationException("Unexpected struct layout.");
        }
    }
}
