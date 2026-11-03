using Anode.Contracts;
using Concentus.Enums;

namespace Anode.Media.Opus;

/// <summary>
///     Listen-in's Opus encoder (docs/adr/0013-video-and-listen-in.md): Concentus (the managed C# port of libopus,
///     BSD-3-Clause; never its optional native library), 48 kHz stereo, 20 ms frames (<see cref="ListenIn.FrameSamples" />),
///     <see cref="ListenIn.Bitrate" /> bit/s constrained VBR, the <c>restricted low delay</c> application (CELT only:
///     2.5 ms of look-ahead instead of 6.5 ms, the right mode for music at this rate), complexity 5. Not thread-safe: one
///     per stream.
/// </summary>
public sealed class OpusEncoder : IDisposable {
    /// <summary>The largest packet <see cref="Encode" /> writes (RFC 6716's limit for one frame).</summary>
    public const int MaxPacketBytes = 1275;

    /// <summary>The encoder's complexity (0…10): 5 keeps four listeners well inside one core with the managed port.</summary>
    const int Complexity = 5;

    readonly Concentus.Structs.OpusEncoder encoder;

    public OpusEncoder(int bitrate = ListenIn.Bitrate) {
        ArgumentOutOfRangeException.ThrowIfLessThan(bitrate, 6_000);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bitrate, 510_000);
#pragma warning disable CS0618 // The factory may load a native libopus; the managed encoder is the same everywhere.
        encoder = new(ListenIn.SampleRate, ListenIn.Channels, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
#pragma warning restore CS0618
        encoder.Bitrate = bitrate;
        encoder.UseVBR = true;
        encoder.UseConstrainedVBR = true;
        encoder.Complexity = Complexity;
        encoder.SignalType = OpusSignal.OPUS_SIGNAL_MUSIC;
    }

    /// <summary>
    ///     Encodes one frame: <see cref="ListenIn.FrameSamples" /> interleaved stereo float samples into
    ///     <paramref name="packet" /> (at least <see cref="MaxPacketBytes" />); returns the packet's length.
    /// </summary>
    public int Encode(ReadOnlySpan<float> interleaved, Span<byte> packet) {
        ArgumentOutOfRangeException.ThrowIfNotEqual(interleaved.Length, ListenIn.FrameSamples * ListenIn.Channels);
        return encoder.Encode(interleaved, ListenIn.FrameSamples, packet, Math.Min(packet.Length, MaxPacketBytes));
    }

    public void Dispose() { }
}

/// <summary>A decoder for tests (the round trip), from the same library.</summary>
public sealed class OpusDecoder : IDisposable {
    readonly Concentus.Structs.OpusDecoder decoder;

    public OpusDecoder() {
#pragma warning disable CS0618 // As the encoder: the managed decoder.
        decoder = new(ListenIn.SampleRate, ListenIn.Channels);
#pragma warning restore CS0618
    }

    /// <summary>Decodes one packet into interleaved stereo floats; returns the samples per channel.</summary>
    public int Decode(ReadOnlySpan<byte> packet, Span<float> interleaved) =>
        decoder.Decode(packet, interleaved, interleaved.Length / ListenIn.Channels);

    public void Dispose() { }
}
