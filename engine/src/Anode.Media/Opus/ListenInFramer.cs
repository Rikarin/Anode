using Anode.Contracts;

namespace Anode.Media.Opus;

/// <summary>
///     Turns the master tap's stereo (at the project's rate, with transport positions) into listen-in frames
///     (docs/11-phase-10.md, workstream E): resampled to 48 kHz when the project runs at another rate, cut into 20 ms
///     frames, Opus-encoded, each prefixed with the <see cref="ListenIn" /> header (sequence, the transport sample of its
///     first sample at the project's rate, flags). One per listener connection; not thread-safe.
/// </summary>
public sealed class ListenInFramer : IDisposable {
    public ListenInFramer(int projectSampleRate) {
        throw Pending.WorkstreamE();
    }

    /// <summary>The first (text) message of the socket.</summary>
    public ListenInFormat Format => throw Pending.WorkstreamE();

    /// <summary>
    ///     Adds tap audio: <paramref name="interleaved" /> stereo frames whose first is project sample
    ///     <paramref name="transportSample" /> (−1 while stopped); <paramref name="discontinuity" /> after a locate or
    ///     an overrun.
    /// </summary>
    public void Write(ReadOnlySpan<float> interleaved, long transportSample, bool playing, bool discontinuity) =>
        throw Pending.WorkstreamE();

    /// <summary>
    ///     The next complete frame (header and packet) into <paramref name="frame" /> (at least
    ///     <see cref="ListenIn.HeaderBytes" /> + <see cref="OpusEncoder.MaxPacketBytes" />); its length, or 0 when fewer
    ///     than 20 ms are buffered.
    /// </summary>
    public int Read(Span<byte> frame) => throw Pending.WorkstreamE();

    public void Dispose() { }
}
