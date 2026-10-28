using System.Buffers.Binary;

namespace Anode.Model.Audio;

/// <summary>
///     Reads whole AIFF and AIFF-C files: big-endian PCM of 8, 16, 24 or 32 bits (AIFF, or AIFF-C <c>NONE</c> and
///     <c>twos</c>), little-endian PCM (<c>sowt</c>) and 32- or 64-bit floats (<c>fl32</c>, <c>fl64</c>). Samples come
///     back planar, as floats of full scale ±1, with the sample rate and the stored bit depth.
/// </summary>
public static class AiffReader {
    public static (float[][] Channels, int SampleRate, int BitDepth) Read(string path) {
        var bytes = File.ReadAllBytes(path);
        var file = bytes.AsSpan();
        if (file.Length < 12 || !file[..4].SequenceEqual("FORM"u8)) {
            throw new FormatException("Not an AIFF file.");
        }

        var isAifc = file[8..12].SequenceEqual("AIFC"u8);
        if (!isAifc && !file[8..12].SequenceEqual("AIFF"u8)) {
            throw new FormatException("Not an AIFF or AIFF-C file.");
        }

        Common? common = null;
        var position = 12;
        while (position + 8 <= file.Length) {
            var id = file.Slice(position, 4);
            var size = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(file[(position + 4)..]), int.MaxValue);
            var body = file.Slice(position + 8, Math.Min(size, file.Length - position - 8));
            if (id.SequenceEqual("COMM"u8)) {
                common = ReadCommon(body, isAifc);
            } else if (id.SequenceEqual("SSND"u8)) {
                if (common is not { } c) {
                    throw new FormatException("The AIFF file's sound data comes before its format.");
                }

                var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(body);
                return (Decode(body[(8 + offset)..], c), c.Rate, c.Bits);
            }

            position += 8 + size + (size & 1);
        }

        throw new FormatException(
            common is null ? "The AIFF file has no COMM chunk." : "The AIFF file has no sound data."
        );
    }

    /// <summary>The 80-bit IEEE 754 extended value (big-endian) at <paramref name="at" />.</summary>
    internal static double ReadExtended(ReadOnlySpan<byte> at) {
        var head = BinaryPrimitives.ReadUInt16BigEndian(at);
        var mantissa = BinaryPrimitives.ReadUInt64BigEndian(at[2..]);
        var exponent = (head & 0x7FFF) - 16383 - 63;
        var value = mantissa * Math.Pow(2, exponent);
        return (head & 0x8000) != 0 ? -value : value;
    }

    static Common ReadCommon(ReadOnlySpan<byte> body, bool isAifc) {
        if (body.Length < 18) {
            throw new FormatException("The AIFF file's COMM chunk is malformed.");
        }

        var channels = BinaryPrimitives.ReadUInt16BigEndian(body);
        var frames = BinaryPrimitives.ReadUInt32BigEndian(body[2..]);
        var bits = BinaryPrimitives.ReadUInt16BigEndian(body[6..]);
        var rate = (int)Math.Round(ReadExtended(body[8..]));
        var kind = isAifc && body.Length >= 22 ? System.Text.Encoding.ASCII.GetString(body.Slice(18, 4)) : "NONE";
        var encoding = kind switch {
            "NONE" or "twos" when bits is >= 1 and <= 32 => Encoding.BigEndian,
            "sowt" when bits is >= 1 and <= 32 => Encoding.LittleEndian,
            "fl32" or "FL32" => Encoding.Float32,
            "fl64" or "FL64" => Encoding.Float64,
            _ => throw new FormatException($"AIFF compression '{kind}' at {bits} bits is not supported.")
        };
        if (channels == 0 || rate <= 0) {
            throw new FormatException($"The AIFF file has {channels} channels at {rate} Hz.");
        }

        return new(channels, frames, encoding == Encoding.Float64 ? 64 : bits, rate, encoding);
    }

    static float[][] Decode(ReadOnlySpan<byte> data, Common c) {
        var size = c.Encoding switch {
            Encoding.Float32 => 4,
            Encoding.Float64 => 8,
            _ => (c.Bits + 7) / 8
        };
        var frames = (int)Math.Min(c.Frames, data.Length / (size * c.Channels));
        var channels = new float[c.Channels][];
        for (var ch = 0; ch < c.Channels; ch++) {
            channels[ch] = new float[frames];
        }

        var scale = 1.0 / Math.Pow(2, size * 8 - 1);
        for (var i = 0; i < frames; i++) {
            for (var ch = 0; ch < c.Channels; ch++) {
                var at = data.Slice((i * c.Channels + ch) * size, size);
                channels[ch][i] = c.Encoding switch {
                    Encoding.Float32 => BinaryPrimitives.ReadSingleBigEndian(at),
                    Encoding.Float64 => (float)BinaryPrimitives.ReadDoubleBigEndian(at),
                    _ => (float)(Integer(at, c.Encoding == Encoding.BigEndian) * scale)
                };
            }
        }

        return channels;
    }

    /// <summary>A signed integer of <c>at.Length</c> bytes, left-justified in a long so the sign carries.</summary>
    static long Integer(ReadOnlySpan<byte> at, bool bigEndian) {
        long value = 0;
        for (var b = 0; b < at.Length; b++) {
            value = (value << 8) | at[bigEndian ? b : at.Length - 1 - b];
        }

        var shift = 64 - 8 * at.Length;
        return (value << shift) >> shift;
    }

    enum Encoding {
        BigEndian,
        LittleEndian,
        Float32,
        Float64
    }

    readonly record struct Common(int Channels, uint Frames, int Bits, int Rate, Encoding Encoding);
}
