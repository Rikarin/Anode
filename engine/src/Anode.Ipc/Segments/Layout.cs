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
    ProgramChange = 7,

    /// <summary>
    ///     An automation value for target <see cref="PlaybackEvent.Target" /> at this sample, in
    ///     <see cref="PlaybackEvent.Value" />; the value ramps linearly to the target's next event (curves are sampled by
    ///     the server). Data1 = 1: a step, no ramp from the previous event.
    /// </summary>
    Automation = 8
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

    /// <summary>For <see cref="PlaybackEventKind.Automation" />: the index of its target in the model's automation targets.</summary>
    public int Target;
}

/// <summary>
///     How a warped playlist entry renders (format 3, docs/06-phase-5.md): the flex algorithm of its track (Polyphonic
///     for regions that only follow the tempo, or for Varispeed's speed-only stretch of unflexed tracks; Speed for
///     Varispeed's speed-and-pitch). <see cref="None" /> plays the file 1:1, as format 2 did.
/// </summary>
public enum FlexAlgorithm : byte {
    None = 0,
    Polyphonic = 1,
    Monophonic = 2,
    Slicing = 3,
    Rhythmic = 4,
    Speed = 5,
    Tempophone = 6,
    Pitch = 7
}

/// <summary>
///     One audio region of a track's playlist: it spans <see cref="Length" /> project samples from project sample
///     <see cref="Start" />, playing file <see cref="FileId" /> at linear <see cref="Gain" />, with fades of
///     <see cref="FadeIn" /> and <see cref="FadeOut" /> project samples shaped by <see cref="FadeInCurve" /> and
///     <see cref="FadeOutCurve" /> (−1…1, 0 linear; as <c>AudioRegion</c> defines them). Entries are sorted by start;
///     two may overlap only as a crossfade (the earlier fading out over the overlap, the later fading in), so at most
///     two play at once. 72 bytes, no implicit padding.
/// </summary>
/// <remarks>
///     With <see cref="WarpCount" /> 0 the entry is unwarped: offset n plays file frame <see cref="FileOffset" /> + n,
///     as in format 2. Otherwise it is warped: its <see cref="WarpCount" /> warp points from index
///     <see cref="WarpStart" /> of the segment's warp array map offsets to fractional file frames (linear between
///     them; the first at offset 0, the last at <see cref="Length" />), rendered by <see cref="Algorithm" />; its
///     <see cref="PitchCount" /> pitch points from <see cref="PitchStart" /> (possibly none) shift the pitch, formants
///     and gain. Indices are into the whole segment's arrays (the writer rebases each track's own).
/// </remarks>
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
    public int WarpStart;
    public int WarpCount;
    public int PitchStart;
    public int PitchCount;
    public FlexAlgorithm Algorithm;
    public byte Reserved0;
    public short Reserved1;
    public int Reserved2;
}

/// <summary>
///     A point of a warped entry's warp map: project sample <see cref="Offset" /> from the entry's start plays file
///     frame <see cref="FileFrame" /> (fractional). An entry's points are sorted by offset, strictly increasing, the
///     first at 0 and the last at its length; file frames between points are linear in the offset. 16 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct WarpPoint {
    public long Offset;
    public double FileFrame;
}

/// <summary>
///     A point of a warped entry's pitch curve, at file frame <see cref="FileFrame" /> (sorted, about every 5 ms):
///     the pitch shift in cents, the formant shift in cents and a gain in dB there, linear between points and held
///     beyond the ends. 24 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct PitchPoint {
    public double FileFrame;
    public float Cents;
    public float FormantCents;
    public float GainDb;
    public int Reserved;
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
///     start, overlapping only as crossfades), warp points and pitch points (the warped entries' ranges); every array
///     starts on a 64-byte boundary. A whole model is one segment of every track; a per-track segment has one track
///     entry.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct SegmentHeader {
    public const uint MagicValue = 0x4D504E41; // "ANPM", Anode playback model

    // 2: playlist entries carry fade curves and may overlap as crossfades. 3: entries carry warp and pitch ranges and
    // an algorithm; the warp and pitch arrays (docs/06-phase-5.md).
    public const ushort CurrentFormat = 3;
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
    public long WarpOffset;
    public long WarpCount;
    public long PitchOffset;
    public long PitchCount;
    public long LengthSamples;
    public ulong Checksum;
}

public static unsafe class Layout {
    public const int Alignment = 64;

    public static long Align(long value) => (value + Alignment - 1) & ~(long)(Alignment - 1);

    /// <summary>Byte offsets of the five arrays and the total length for the given element counts.</summary>
    public static (long Tracks, long Events, long Playlists, long Warps, long Pitches, long Total) Offsets(
        int trackCount,
        long eventCount,
        long playlistCount,
        long warpCount = 0,
        long pitchCount = 0
    ) {
        var tracks = Align(sizeof(SegmentHeader));
        var events = Align(tracks + (long)trackCount * sizeof(TrackEntry));
        var playlists = Align(events + eventCount * sizeof(PlaybackEvent));
        var warps = Align(playlists + playlistCount * sizeof(AudioPlaylistEntry));
        var pitches = Align(warps + warpCount * sizeof(WarpPoint));
        var total = Align(pitches + pitchCount * sizeof(PitchPoint));
        return (tracks, events, playlists, warps, pitches, total);
    }
}
