using Microsoft.Win32.SafeHandles;
using System.Buffers.Binary;

namespace Anode.Engine.Streaming;

/// <summary>How a WAV file's samples are encoded.</summary>
public enum SampleEncoding {
    Pcm16,
    Pcm24,
    Pcm32,
    Float32
}

/// <summary>
///     A WAV file's format and where its audio is: PCM 16/24/32-bit or 32-bit float, any channel count, plain or
///     <c>WAVE_FORMAT_EXTENSIBLE</c>; other chunks (JUNK, bext, LIST) are skipped.
/// </summary>
public sealed record WavFormat(SampleEncoding Encoding, int Channels, int SampleRate, long DataOffset, long Frames) {
    public int BytesPerSample => Encoding switch {
        SampleEncoding.Pcm16 => 2,
        SampleEncoding.Pcm24 => 3,
        _ => 4
    };

    public int BytesPerFrame => BytesPerSample * Channels;

    /// <summary>Walks the RIFF chunks of an open file.</summary>
    public static WavFormat Read(SafeFileHandle handle) {
        Span<byte> chunk = stackalloc byte[12];
        Span<byte> fmt = stackalloc byte[40];
        var length = RandomAccess.GetLength(handle);
        if (RandomAccess.Read(handle, chunk, 0) < 12
            || !chunk[..4].SequenceEqual("RIFF"u8)
            || !chunk[8..12].SequenceEqual("WAVE"u8)) {
            throw new InvalidDataException("Not a RIFF/WAVE file.");
        }

        long at = 12;
        SampleEncoding? encoding = null;
        int channels = 0, rate = 0, blockAlign = 0;
        while (at + 8 <= length) {
            RandomAccess.Read(handle, chunk[..8], at);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..8]);
            if (chunk[..4].SequenceEqual("fmt "u8)) {
                fmt.Clear();
                RandomAccess.Read(handle, fmt[..(int)Math.Min(size, 40)], at + 8);
                var tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..]);
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(fmt[12..]);
                var bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);
                if (tag == 0xFFFE && size >= 40) {
                    tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt[24..]); // the sub-format GUID's first two bytes
                }

                encoding = (tag, bits) switch {
                    (1, 16) => SampleEncoding.Pcm16,
                    (1, 24) => SampleEncoding.Pcm24,
                    (1, 32) => SampleEncoding.Pcm32,
                    (3, 32) => SampleEncoding.Float32,
                    _ => throw new InvalidDataException($"Unsupported WAV encoding (format {tag}, {bits} bits).")
                };
            } else if (chunk[..4].SequenceEqual("data"u8)) {
                if (encoding is not { } e || channels < 1) {
                    throw new InvalidDataException("WAV data before its format.");
                }

                var format = new WavFormat(e, channels, rate, at + 8, 0);
                if (blockAlign != format.BytesPerFrame) {
                    throw new InvalidDataException("WAV block alignment does not match its format.");
                }

                // A writer that died leaves the size at 0 or too large: the file's length is the truth.
                var bytes = Math.Min(size == 0 ? long.MaxValue : size, length - (at + 8));
                return format with { Frames = bytes / format.BytesPerFrame };
            }

            at += 8 + size + (size & 1);
        }

        throw new InvalidDataException("WAV file without a data chunk.");
    }

    /// <summary>Decodes whole frames of <paramref name="source" /> into interleaved floats in [−1, 1).</summary>
    public void Decode(ReadOnlySpan<byte> source, Span<float> destination) {
        var samples = source.Length / BytesPerSample;
        switch (Encoding) {
            case SampleEncoding.Pcm16:
                for (var i = 0; i < samples; i++) {
                    destination[i] = BinaryPrimitives.ReadInt16LittleEndian(source[(2 * i)..]) * (1f / 32_768f);
                }

                break;
            case SampleEncoding.Pcm24:
                for (var i = 0; i < samples; i++) {
                    destination[i] = Int24(source, 3 * i) * (1f / 8_388_608f);
                }

                break;
            case SampleEncoding.Pcm32:
                for (var i = 0; i < samples; i++) {
                    destination[i] = (float)(BinaryPrimitives.ReadInt32LittleEndian(source[(4 * i)..]) * (1.0 / 2_147_483_648.0));
                }

                break;
            default:
                for (var i = 0; i < samples; i++) {
                    destination[i] = BinaryPrimitives.ReadSingleLittleEndian(source[(4 * i)..]);
                }

                break;
        }
    }

    /// <summary>A little-endian signed 24-bit sample.</summary>
    public static int Int24(ReadOnlySpan<byte> s, int at) => ((s[at] << 8) | (s[at + 1] << 16) | (s[at + 2] << 24)) >> 8;
}
