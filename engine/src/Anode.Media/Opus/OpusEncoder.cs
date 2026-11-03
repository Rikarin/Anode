using Anode.Contracts;

namespace Anode.Media.Opus;

/// <summary>
///     Listen-in's Opus encoder (docs/adr/0013-video-and-listen-in.md, workstream E): 48 kHz stereo, 20 ms frames
///     (<see cref="ListenIn.FrameSamples" />), <see cref="ListenIn.Bitrate" /> bit/s, the <c>audio</c> application,
///     restricted low delay off. Concentus (the C# port) if its licence fits, else <c>libopus</c> through a small shim.
///     Not thread-safe: one per stream.
/// </summary>
public sealed class OpusEncoder : IDisposable {
    /// <summary>The largest packet <see cref="Encode" /> writes (RFC 6716's limit for one frame).</summary>
    public const int MaxPacketBytes = 1275;

    public OpusEncoder(int bitrate = ListenIn.Bitrate) {
        throw Pending.WorkstreamE();
    }

    /// <summary>
    ///     Encodes one frame: <see cref="ListenIn.FrameSamples" /> interleaved stereo float samples into
    ///     <paramref name="packet" /> (at least <see cref="MaxPacketBytes" />); returns the packet's length.
    /// </summary>
    public int Encode(ReadOnlySpan<float> interleaved, Span<byte> packet) => throw Pending.WorkstreamE();

    public void Dispose() { }
}

/// <summary>A decoder for tests (the round trip), from the same library.</summary>
public sealed class OpusDecoder : IDisposable {
    public OpusDecoder() {
        throw Pending.WorkstreamE();
    }

    /// <summary>Decodes one packet into interleaved stereo floats; returns the samples per channel.</summary>
    public int Decode(ReadOnlySpan<byte> packet, Span<float> interleaved) => throw Pending.WorkstreamE();

    public void Dispose() { }
}
