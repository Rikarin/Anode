using System.Runtime.InteropServices;

namespace Anode.Ipc.Segments;

/// <summary>What a <see cref="PlaybackEvent" /> means.</summary>
/// <remarks>
///     MIDI kinds carry the message's data bytes in <see cref="PlaybackEvent.Data1" /> and
///     <see cref="PlaybackEvent.Data2" /> and its channel (0…15) in <see cref="PlaybackEvent.Channel" />; at the same
///     sample, note-offs sort before note-ons so a repeated note retriggers.
/// </remarks>
public enum PlaybackEventKind : byte {
    /// <summary>A metronome click; <see cref="PlaybackEvent.Data1" /> is 1 on a bar's downbeat, else 0.</summary>
    Click = 1,

    /// <summary>Data1 = note, Data2 = velocity 1…127.</summary>
    NoteOn = 2,

    /// <summary>Data1 = note, Data2 = release velocity.</summary>
    NoteOff = 3,

    /// <summary>Data1 = controller, Data2 = value.</summary>
    ControlChange = 4,

    /// <summary>Data1 = LSB, Data2 = MSB (14 bits, 8192 = centre).</summary>
    PitchBend = 5,

    /// <summary>Data1 = pressure.</summary>
    ChannelPressure = 6,

    /// <summary>Data1 = program.</summary>
    ProgramChange = 7
}

/// <summary>One compiled event of a track, at an absolute sample position. 24 bytes, no implicit padding.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct PlaybackEvent {
    public long SamplePos;
    public int Track;
    public PlaybackEventKind Kind;
    public byte Data1;
    public byte Data2;

    /// <summary>MIDI channel 0…15 of a MIDI kind; 0 otherwise.</summary>
    public byte Channel;

    public float Value;
    public int Reserved2;
}

/// <summary>
///     One audio region of a track's playlist: play <see cref="Length" /> frames of file <see cref="FileId" /> from
///     frame <see cref="FileOffset" />, starting at project sample <see cref="Start" />, at linear <see cref="Gain" />,
///     with fades of <see cref="FadeIn" /> and <see cref="FadeOut" /> frames shaped by <see cref="FadeInCurve" /> and
///     <see cref="FadeOutCurve" /> (−1…1, 0 linear; as <c>AudioRegion</c> defines them). Entries are sorted by start;
///     two may overlap only as a crossfade (the earlier fading out over the overlap, the later fading in), so at most
///     two play at once. 48 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct AudioPlaylistEntry {
    public long Start;
    public long FileOffset;
    public long Length;
    public int FileId;
    public float Gain;
    public int FadeIn;
    public int FadeOut;
    public float FadeInCurve;
    public float FadeOutCurve;
}

/// <summary>Where one track's slices of the event and playlist arrays are, in elements. 40 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct TrackEntry {
    /// <summary>The metronome's events live under this track id.</summary>
    public const int MetronomeTrack = -2;

    public int TrackId;
    public int Flags;
    public long EventStart;
    public long EventCount;
    public long PlaylistStart;
    public long PlaylistCount;
}

/// <summary>
///     The first bytes of a segment (docs/adr/0006-bulk-ipc.md). A segment is: header, track table, events (each
///     track's slice sorted by <see cref="PlaybackEvent.SamplePos" />), playlist entries (each track's slice sorted by
///     start, not overlapping); every array starts on a 64-byte boundary. A whole model is one segment of every track;
///     a per-track segment has one track entry.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct SegmentHeader {
    public const uint MagicValue = 0x4D504E41; // "ANPM", Anode playback model
    public const ushort CurrentFormat = 2; // 2: playlist entries carry fade curves and may overlap as crossfades
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

public static unsafe class Layout {
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
}
