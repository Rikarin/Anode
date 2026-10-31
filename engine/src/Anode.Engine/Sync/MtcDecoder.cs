using Anode.Ipc;

namespace Anode.Engine.Sync;

/// <summary>
///     A timecode position read from MTC: seconds of timecode, when the frame it completes arrived, and whether it came
///     from a full frame (a locate) rather than quarter frames.
/// </summary>
public readonly record struct MtcPosition(double Seconds, long TimeNs, bool FullFrame);

/// <summary>
///     MTC in (docs/09-phase-8.md, "Sync"): assembles quarter frames (eight make a position, two frames behind the
///     sender, compensated) and full frames into positions for the chase; stops when they stop. Runs on the host's MIDI
///     thread: no allocation.
/// </summary>
public sealed class MtcDecoder {
    public MtcDecoder(EngineMtcRate rate) {
        throw Pending.WorkstreamA();
    }

    /// <summary>Whether quarter frames are arriving (none for two frames' time: stopped).</summary>
    public bool Running => throw Pending.WorkstreamA();

    /// <summary>One message (a quarter frame or a full-frame SysEx) that arrived at <paramref name="timeNs" />.</summary>
    public MtcPosition? Receive(ReadOnlySpan<byte> message, long timeNs) => throw Pending.WorkstreamA();

    /// <summary>Whether timecode stopped arriving by <paramref name="nowNs" /> (updates <see cref="Running" />).</summary>
    public bool Check(long nowNs) => throw Pending.WorkstreamA();
}
