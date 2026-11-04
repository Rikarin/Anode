using System.Runtime.InteropServices;

namespace Anode.Ipc.Telemetry;

/// <summary>The transport's state as the host reports it.</summary>
public enum TelemetryTransportState {
    Stopped = 0,
    Playing = 1,
    Recording = 2
}

/// <summary>
///     The head of the host's telemetry region: a seqlock (odd while the host writes), the transport, and the meters
///     that follow it (<see cref="MeterEntry" /> × <see cref="TrackCount" />, then the master). The host writes it from
///     its control thread about 60 times a second from values the audio thread publishes; readers retry while
///     <see cref="Sequence" /> is odd or changed under them.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct TelemetryHeader {
    public const uint MagicValue = 0x4C544E41; // "ANTL", Anode telemetry
    public const int MaxTracks = 1024;

    public uint Magic;
    public int TrackCount;
    public long Sequence;
    public long PositionSamples;

    /// <summary>The host's monotonic clock when <see cref="PositionSamples" /> was the playhead, in nanoseconds.</summary>
    public long HostTimeNs;

    public TelemetryTransportState State;
    public int SampleRate;

    /// <summary>Callbacks since start, and the ones that overran or found a ring empty.</summary>
    public long Callbacks;

    public long Overloads;
    public long Underruns;
    public long ModelVersion;

    /// <summary>
    ///     Phase 11: events the Environment dropped since start (its queue was full, or a delay or arpeggio could not be
    ///     scheduled in the block).
    /// </summary>
    public long EnvironmentDropped;
}

/// <summary>
///     Peak levels since the previous write (linear, 0…), per track and for the master (track id −1). Phase 10: a meter of
///     more than two channels (a surround output, bus or master) has their count in <see cref="Channels" /> and each
///     channel's peak, in its layout's (ADM) order, in the <see cref="ChannelMeterEntry" /> of the same index (the first
///     two are also <see cref="PeakLeft" /> and <see cref="PeakRight" />); 0 for a mono or stereo meter. A strip's meter
///     type asks for more (<see cref="Levels" />): RMS, linear, over the type's window, or momentary loudness.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct MeterEntry {
    /// <summary><see cref="Levels" />: <see cref="RmsLeft" /> and <see cref="RmsRight" /> mean something.</summary>
    public const int HasRms = 1;

    /// <summary><see cref="Levels" />: <see cref="Lufs" /> means something.</summary>
    public const int HasLufs = 2;

    public int TrackId;
    public float PeakLeft;
    public float PeakRight;
    public int Channels;
    public float RmsLeft;
    public float RmsRight;
    public float Lufs;
    public int Levels;
}

/// <summary>A surround meter's peaks per channel (Phase 10), at the index of its <see cref="MeterEntry" />.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct ChannelMeterEntry {
    /// <summary>The most channels a format has (9.1.6).</summary>
    public const int MaxChannels = 16;

    public fixed float Peaks[MaxChannels];
}

/// <summary>
///     Phase 11 (docs/12-phase-11.md): an Environment monitor's last events, in slot
///     <c>EngineEnvironmentObject.TelemetrySlot</c>. <see cref="Count" /> grows with every event the monitor passes
///     (monotonic); event n is at <c>n % <see cref="Capacity" /></c> of <see cref="Events" />, so the last
///     <c>min(Count, Capacity)</c> are there. <see cref="Object" /> is the object's index plus one (0: the slot is free,
///     as a new region is zeroed).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct EnvironmentMonitorEntry {
    public const int Capacity = 8;

    public int Object;
    public int Reserved;
    public long Count;

    /// <summary>
    ///     Each event as two values: <c>Events[2i]</c> its project sample (−1 stopped), <c>Events[2i + 1]</c> its
    ///     bytes, the status in bits 0–7, data 1 in bits 8–15 and data 2 in bits 16–23.
    /// </summary>
    public fixed long Events[2 * Capacity];
}

/// <summary>Phase 11: an Environment fader's value (0…127), in slot <c>EngineEnvironmentObject.TelemetrySlot</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct EnvironmentFaderEntry {
    /// <summary>The object's index plus one (0: the slot is free).</summary>
    public int Object;

    public float Value;
}

/// <summary>
///     Phase 11: the master's Mastering Assistant (the last on the master; <see cref="Active" /> 0 when there is none):
///     its output's momentary, short-term and integrated loudness (LUFS since play started, −144 in silence), true peak
///     (dBTP since play started) and the gain reductions of its compressor and limiter now (dB, 0 or less).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct MasteringEntry {
    public int Active;
    public float MomentaryLufs;
    public float ShortTermLufs;
    public float IntegratedLufs;
    public float TruePeakDbtp;
    public float CompressorReductionDb;
    public float LimiterReductionDb;
    public int Reserved;
}

public static unsafe class TelemetryLayout {
    /// <summary>The most Environment monitors and faders that report (Phase 11; more run, unreported).</summary>
    public const int MaxMonitors = 32;

    public const int MaxFaders = 64;

    /// <summary>Where the channel meters start (after the header and every <see cref="MeterEntry" />).</summary>
    public static long ChannelMetersOffset =>
        sizeof(TelemetryHeader) + (long)(TelemetryHeader.MaxTracks + 1) * sizeof(MeterEntry);

    /// <summary>Where the Environment's monitors start (Phase 11, after the channel meters).</summary>
    public static long MonitorsOffset =>
        ChannelMetersOffset + (long)(TelemetryHeader.MaxTracks + 1) * sizeof(ChannelMeterEntry);

    /// <summary>Where the Environment's faders start (after <see cref="MaxMonitors" /> monitors).</summary>
    public static long FadersOffset => MonitorsOffset + (long)MaxMonitors * sizeof(EnvironmentMonitorEntry);

    /// <summary>Where the Mastering Assistant's entry is (after <see cref="MaxFaders" /> faders).</summary>
    public static long MasteringOffset => FadersOffset + (long)MaxFaders * sizeof(EnvironmentFaderEntry);

    /// <summary>
    ///     Bytes of a region with room for <see cref="TelemetryHeader.MaxTracks" /> tracks and the master, (Phase 10)
    ///     their channel meters, and (Phase 11) the Environment's monitors and faders and the Mastering Assistant.
    /// </summary>
    public static long Size => MasteringOffset + sizeof(MasteringEntry);
}
