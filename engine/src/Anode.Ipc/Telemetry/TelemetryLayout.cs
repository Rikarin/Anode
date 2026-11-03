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
}

/// <summary>
///     Peak levels since the previous write (linear, 0…), per track and for the master (track id −1). Phase 10: a meter of
///     more than two channels (a surround output, bus or master) has their count in <see cref="Channels" /> and each
///     channel's peak, in its layout's (ADM) order, in the <see cref="ChannelMeterEntry" /> of the same index (the first
///     two are also <see cref="PeakLeft" /> and <see cref="PeakRight" />); 0 for a mono or stereo meter.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct MeterEntry {
    public int TrackId;
    public float PeakLeft;
    public float PeakRight;
    public int Channels;
}

/// <summary>A surround meter's peaks per channel (Phase 10), at the index of its <see cref="MeterEntry" />.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct ChannelMeterEntry {
    /// <summary>The most channels a format has (9.1.6).</summary>
    public const int MaxChannels = 16;

    public fixed float Peaks[MaxChannels];
}

public static unsafe class TelemetryLayout {
    /// <summary>Where the channel meters start (after the header and every <see cref="MeterEntry" />).</summary>
    public static long ChannelMetersOffset =>
        sizeof(TelemetryHeader) + (long)(TelemetryHeader.MaxTracks + 1) * sizeof(MeterEntry);

    /// <summary>
    ///     Bytes of a region with room for <see cref="TelemetryHeader.MaxTracks" /> tracks and the master, and (Phase 10)
    ///     their channel meters.
    /// </summary>
    public static long Size => ChannelMetersOffset + (long)(TelemetryHeader.MaxTracks + 1) * sizeof(ChannelMeterEntry);
}
