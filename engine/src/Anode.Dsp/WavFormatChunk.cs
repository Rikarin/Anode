using System.Buffers.Binary;

namespace Anode.Dsp;

/// <summary>The <c>WAVE</c> tag and the <c>fmt </c> chunk's fields every WAV header here starts with.</summary>
public static class WavFormatChunk {
    public const ushort Pcm = 1;
    public const ushort IeeeFloat = 3;
    public const ushort Extensible = 0xFFFE;

    /// <summary>
    ///     Writes bytes 8…35 of a RIFF header: <c>WAVE</c>, <c>fmt </c>, the chunk's size and its fields up to the bit
    ///     depth. What follows them (<c>cbSize</c>, <c>fact</c>, <c>data</c>) is the caller's.
    /// </summary>
    public static void Write(Span<byte> header, int chunkSize, ushort tag, int channels, int sampleRate, int bits) {
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], (uint)chunkSize);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], tag);
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], (uint)((long)sampleRate * channels * bits / 8));
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], (ushort)(channels * bits / 8));
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], (ushort)bits);
    }
}
