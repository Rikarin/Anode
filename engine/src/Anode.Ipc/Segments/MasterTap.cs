using System.Runtime.InteropServices;

namespace Anode.Ipc.Segments;

/// <summary>
///     The head of the host's master tap ring (segment format 5, docs/11-phase-10.md): the master's stereo for listen-in,
///     written by the audio callback while <see cref="SetMasterTapMessage" /> has it on, read by the server. Single
///     writer, single reader, lock-free; the writer never waits. The host creates the region with its telemetry region
///     and names it in its hello (<see cref="HelloMessage.MasterTapName" />); it is <see cref="MasterTapLayout.Size" />
///     bytes.
/// </summary>
/// <remarks>
///     <para>
///         Layout, each array on a 64-byte boundary (<see cref="MasterTapLayout.Offsets" />): this header; the sample
///         ring, <see cref="CapacityFrames" /> interleaved stereo float frames (L, R); the stamp ring,
///         <see cref="StampCapacity" /> <see cref="TapStamp" />s. Both capacities are powers of two: frame n (counting
///         every frame written since the host started) is at ring index <c>n &amp; (CapacityFrames − 1)</c>, stamp k at
///         <c>k &amp; (StampCapacity − 1)</c>.
///     </para>
///     <para>
///         Writer, per audio block of m frames: write the frames from <see cref="WriteFrame" />, write a stamp
///         (<see cref="TapStamp.Frame" /> = the block's first frame, its transport sample and flags) at
///         <see cref="StampCount" />, then publish with release stores: <see cref="StampCount" /> + 1, then
///         <see cref="WriteFrame" /> + m. While the tap is off nothing is written (the counters stand still).
///     </para>
///     <para>
///         Reader: acquire-load <see cref="WriteFrame" /> (then <see cref="StampCount" />); frames from its own read
///         position up to it are valid unless more than <see cref="CapacityFrames" /> behind (an overrun: it skips to
///         <c>WriteFrame − CapacityFrames / 2</c> and marks a discontinuity), and it re-checks <see cref="WriteFrame" />
///         after copying (frames overwritten meanwhile are dropped). A frame's transport sample is its latest stamp at or
///         before it: <c>stamp.TransportSample + (n − stamp.Frame)</c> while that stamp is playing.
///     </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct MasterTapHeader {
    public const uint MagicValue = 0x50544E41; // "ANTP", Anode tap

    /// <summary>The shared-memory format this ring belongs to (<see cref="SegmentHeader.CurrentFormat" />, 5).</summary>
    public const ushort CurrentFormat = 5;

    /// <summary>2^16 frames: 1.37 s at 48 kHz, 0.34 s at 192 kHz.</summary>
    public const int DefaultCapacityFrames = 1 << 16;

    /// <summary>Stamps: one per block, more than the frames' worth at 32-frame blocks.</summary>
    public const int DefaultStampCapacity = 1 << 11;

    public uint Magic;
    public ushort Format;
    public ushort HeaderSize;

    /// <summary>The device's (the project's) rate; the server resamples to 48 kHz for Opus.</summary>
    public int SampleRate;

    /// <summary>Always 2.</summary>
    public int Channels;

    public int CapacityFrames;
    public int StampCapacity;

    /// <summary>Frames written since the host started (release-stored after the frames and the stamp).</summary>
    public long WriteFrame;

    /// <summary>Stamps written since the host started (release-stored before <see cref="WriteFrame" />).</summary>
    public long StampCount;

    public long SamplesOffset;
    public long StampsOffset;
    public long TotalLength;
}

/// <summary>
///     Where the transport was for a block of the tap: the ring frame <see cref="Frame" /> it starts at was project
///     sample <see cref="TransportSample" /> (−1 while stopped); <see cref="Flags" /> are
///     <see cref="FlagPlaying" /> and <see cref="FlagDiscontinuity" /> (a locate or cycle jump at this block). 24 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct TapStamp {
    public const int FlagPlaying = 1;
    public const int FlagDiscontinuity = 2;

    public long Frame;
    public long TransportSample;
    public int Flags;
    public int Reserved;
}

public static unsafe class MasterTapLayout {
    /// <summary>Byte offsets of the sample and stamp rings and the total length for the given capacities.</summary>
    public static (long Samples, long Stamps, long Total) Offsets(
        int capacityFrames = MasterTapHeader.DefaultCapacityFrames,
        int stampCapacity = MasterTapHeader.DefaultStampCapacity
    ) {
        var samples = Layout.Align(sizeof(MasterTapHeader));
        var stamps = Layout.Align(samples + (long)capacityFrames * 2 * sizeof(float));
        var total = Layout.Align(stamps + (long)stampCapacity * sizeof(TapStamp));
        return (samples, stamps, total);
    }

    /// <summary>Bytes of a ring with the default capacities.</summary>
    public static long Size => Offsets().Total;
}
