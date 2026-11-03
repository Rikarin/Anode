using System.Buffers.Binary;

namespace Anode.Dsp.IO;

/// <summary>
///     Reads AIFF and AIFF-C files whole into memory, as <see cref="WavReader" /> reads WAVs: big-endian PCM of 8 … 32
///     bits (AIFF, AIFF-C <c>NONE</c> and <c>twos</c>), little-endian PCM (<c>sowt</c>) and 32/64-bit float
///     (<c>fl32</c>, <c>fl64</c>), any number of channels. An <c>INST</c> chunk's base note and detune become the root
///     note
///     and its cents (its loops are not read); other chunks are skipped.
/// </summary>
public static class AiffReader {
    public static WavData Read(string path) => Read(File.ReadAllBytes(path));

    public static WavData Read(ReadOnlySpan<byte> file) {
        if (file.Length < 12 || !file[..4].SequenceEqual("FORM"u8)) {
            throw new InvalidDataException("Not an AIFF file.");
        }

        var aifc = file.Slice(8, 4).SequenceEqual("AIFC"u8);
        if (!aifc && !file.Slice(8, 4).SequenceEqual("AIFF"u8)) {
            throw new InvalidDataException("Not an AIFF file.");
        }

        int channels = 0, bits = 0;
        long frames = 0;
        double rate = 0;
        var encoding = "NONE";
        var root = -1;
        double cents = 0;
        var data = ReadOnlySpan<byte>.Empty;
        var haveData = false;
        for (var at = 12; at + 8 <= file.Length;) {
            var id = file.Slice(at, 4);
            var size = BinaryPrimitives.ReadUInt32BigEndian(file[(at + 4)..]);
            var body = at + 8;
            if (body + (long)size > file.Length) {
                size = (uint)(file.Length - body);
            }

            var chunk = file.Slice(body, (int)size);
            if (id.SequenceEqual("COMM"u8) && chunk.Length >= 18) {
                channels = BinaryPrimitives.ReadInt16BigEndian(chunk);
                frames = BinaryPrimitives.ReadUInt32BigEndian(chunk[2..]);
                bits = BinaryPrimitives.ReadInt16BigEndian(chunk[6..]);
                rate = Extended(chunk.Slice(8, 10));
                if (aifc && chunk.Length >= 22) {
                    encoding = System.Text.Encoding.ASCII.GetString(chunk.Slice(18, 4));
                }
            } else if (id.SequenceEqual("SSND"u8) && chunk.Length >= 8) {
                var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(chunk);
                data = chunk[Math.Min(chunk.Length, 8 + offset)..];
                haveData = true;
            } else if (id.SequenceEqual("INST"u8) && chunk.Length >= 2) {
                root = chunk[0] <= 127 ? chunk[0] : -1;
                cents = (sbyte)chunk[1];
            }

            at = body + (int)size + (int)(size & 1);
        }

        if (channels <= 0 || !haveData || rate is < 1 or > 1_000_000) {
            throw new InvalidDataException("The AIFF file has no audio.");
        }

        var (bytes, read) = Decoder(encoding, bits);
        frames = Math.Min(frames, data.Length / (bytes * channels));
        var output = new float[channels][];
        for (var c = 0; c < channels; c++) {
            output[c] = new float[frames];
        }

        for (var i = 0; i < frames; i++) {
            for (var c = 0; c < channels; c++) {
                output[c][i] = read(data.Slice((i * channels + c) * bytes, bytes));
            }
        }

        return new((int)Math.Round(rate), output, root, cents, -1, -1);
    }

    delegate float Sample(ReadOnlySpan<byte> bytes);

    static (int Bytes, Sample Read) Decoder(string encoding, int bits) =>
        encoding switch {
            "NONE" or "twos" => ((bits + 7) / 8, BigEndian),
            "sowt" => ((bits + 7) / 8, LittleEndian),
            "fl32" or "FL32" => (4, static b => BinaryPrimitives.ReadSingleBigEndian(b)),
            "fl64" or "FL64" => (8, static b => (float)BinaryPrimitives.ReadDoubleBigEndian(b)),
            _ => throw new InvalidDataException($"AIFF-C encoding '{encoding}' is not supported.")
        };

    static float BigEndian(ReadOnlySpan<byte> b) {
        int value = (sbyte)b[0];
        for (var i = 1; i < b.Length; i++) {
            value = value << 8 | b[i];
        }

        return value / (float)(1L << (8 * b.Length - 1));
    }

    static float LittleEndian(ReadOnlySpan<byte> b) {
        int value = (sbyte)b[^1];
        for (var i = b.Length - 2; i >= 0; i--) {
            value = value << 8 | b[i];
        }

        return value / (float)(1L << (8 * b.Length - 1));
    }

    /// <summary>An IEEE 754 80-bit extended number (the sample rate), big-endian.</summary>
    static double Extended(ReadOnlySpan<byte> b) {
        var exponent = ((b[0] & 0x7F) << 8) | b[1];
        var mantissa = BinaryPrimitives.ReadUInt64BigEndian(b[2..]);
        if (exponent == 0 && mantissa == 0) {
            return 0;
        }

        var value = mantissa * Math.Pow(2, exponent - 16_383 - 63);
        return (b[0] & 0x80) != 0 ? -value : value;
    }
}
