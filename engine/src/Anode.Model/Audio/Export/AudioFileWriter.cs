using Anode.Contracts;
using System.Buffers.Binary;

namespace Anode.Model.Audio;

/// <summary>
///     Writes whole audio files from planar samples: integers of 16 or 24 bits (as <see cref="Dither.Quantize" />
///     returns them) or 32-bit floats (full scale ±1).
/// </summary>
/// <remarks>
///     WAV is RIFF/WAVE: PCM (format 1) for integers, IEEE float (format 3, an 18-byte format chunk and a
///     <c>fact</c> chunk) for floats; <see cref="WavReader" /> reads both. AIFF is big-endian FORM/AIFF with COMM (the
///     rate as an 80-bit extended float) and SSND (offset and block size 0) for integers, and AIFF-C (FORM/AIFC with
///     FVER, and COMM naming the <c>fl32</c> compression, "32-bit floating point") for floats.
/// </remarks>
public static class AudioFileWriter {
    const uint AifcVersion1 = 0xA2805140;
    const string FloatName = "32-bit floating point";

    /// <summary>
    ///     Writes integer samples of <paramref name="bits" /> (16 or 24) bits; values outside the range are clipped.
    /// </summary>
    public static void WriteInteger(string path, AudioFormat format, int sampleRate, int bits, int[][] channels) {
        if (bits is not (16 or 24)) {
            throw new ArgumentOutOfRangeException(nameof(bits), bits, "Integer files have 16 or 24 bits.");
        }

        var frames = Validate(sampleRate, channels.Length, channels.Length == 0 ? 0 : channels[0].Length);
        foreach (var channel in channels) {
            if (channel.Length != frames) {
                throw new ArgumentException("Every channel has the same length.", nameof(channels));
            }
        }

        var bigEndian = format == AudioFormat.Aiff;
        var data = Interleave(
            channels.Length,
            frames,
            bits / 8,
            (c, i, at) => WriteInt(at, channels[c][i], bits, bigEndian)
        );
        Write(path, format, sampleRate, channels.Length, frames, bits, false, data);
    }

    /// <summary>Writes 32-bit float samples.</summary>
    public static void WriteFloat(string path, AudioFormat format, int sampleRate, float[][] channels) {
        var frames = Validate(sampleRate, channels.Length, channels.Length == 0 ? 0 : channels[0].Length);
        foreach (var channel in channels) {
            if (channel.Length != frames) {
                throw new ArgumentException("Every channel has the same length.", nameof(channels));
            }
        }

        var data = format == AudioFormat.Aiff
            ? Interleave(
                channels.Length,
                frames,
                4,
                (c, i, at) => BinaryPrimitives.WriteSingleBigEndian(at, channels[c][i])
            )
            : Interleave(
                channels.Length,
                frames,
                4,
                (c, i, at) => BinaryPrimitives.WriteSingleLittleEndian(at, channels[c][i])
            );
        Write(path, format, sampleRate, channels.Length, frames, 32, true, data);
    }

    /// <summary>
    ///     The 80-bit IEEE 754 extended encoding of <paramref name="value" /> (big-endian), as AIFF stores rates.
    /// </summary>
    internal static void WriteExtended(Span<byte> at, double value) {
        at[..10].Clear();
        if (value <= 0) {
            return;
        }

        var exponent = (int)Math.Floor(Math.Log2(value));
        var mantissa = value / Math.Pow(2, exponent);
        if (mantissa >= 2) {
            mantissa /= 2;
            exponent++;
        } else if (mantissa < 1) {
            mantissa *= 2;
            exponent--;
        }

        BinaryPrimitives.WriteUInt16BigEndian(at, (ushort)(exponent + 16383));

        // The integer bit is explicit: mantissa in [1, 2) times 2^63.
        BinaryPrimitives.WriteUInt64BigEndian(at[2..], (ulong)Math.Round(mantissa * 9223372036854775808.0));
    }

    static int Validate(int sampleRate, int channelCount, int frames) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        if (channelCount is 0 or > 64) {
            throw new ArgumentException($"Cannot write {channelCount} channels (1 to 64).");
        }

        return frames;
    }

    static void WriteInt(Span<byte> at, int value, int bits, bool bigEndian) {
        var max = (1 << (bits - 1)) - 1;
        var v = Math.Clamp(value, -max - 1, max);
        var size = bits / 8;
        for (var b = 0; b < size; b++) {
            at[bigEndian ? size - 1 - b : b] = (byte)(v >> (8 * b));
        }
    }

    static byte[] Interleave(int channelCount, int frames, int size, SampleWriter write) {
        var data = new byte[(long)channelCount * frames * size];
        var span = data.AsSpan();
        for (var i = 0; i < frames; i++) {
            for (var c = 0; c < channelCount; c++) {
                write(c, i, span.Slice((i * channelCount + c) * size, size));
            }
        }

        return data;
    }

    static void Write(
        string path,
        AudioFormat format,
        int rate,
        int channelCount,
        int frames,
        int bits,
        bool isFloat,
        byte[] data
    ) {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        var header = format == AudioFormat.Aiff
            ? AiffHeader(rate, channelCount, frames, bits, isFloat, data.Length)
            : WavHeader(rate, channelCount, frames, bits, isFloat, data.Length);
        stream.Write(header);
        stream.Write(data);
        if ((data.Length & 1) != 0) {
            stream.WriteByte(0);
        }
    }

    static byte[] WavHeader(int rate, int channelCount, int frames, int bits, bool isFloat, int dataBytes) {
        var formatSize = isFloat ? 18 : 16;
        var factSize = isFloat ? 12 : 0;
        var header = new byte[12 + 8 + formatSize + factSize + 8];
        var at = header.AsSpan();
        var padded = dataBytes + (dataBytes & 1);
        "RIFF"u8.CopyTo(at);
        BinaryPrimitives.WriteUInt32LittleEndian(at[4..], (uint)(header.Length - 8 + padded));
        "WAVE"u8.CopyTo(at[8..]);
        "fmt "u8.CopyTo(at[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(at[16..], (uint)formatSize);
        BinaryPrimitives.WriteUInt16LittleEndian(at[20..], (ushort)(isFloat ? 3 : 1));
        BinaryPrimitives.WriteUInt16LittleEndian(at[22..], (ushort)channelCount);
        BinaryPrimitives.WriteUInt32LittleEndian(at[24..], (uint)rate);
        BinaryPrimitives.WriteUInt32LittleEndian(at[28..], (uint)(rate * channelCount * bits / 8));
        BinaryPrimitives.WriteUInt16LittleEndian(at[32..], (ushort)(channelCount * bits / 8));
        BinaryPrimitives.WriteUInt16LittleEndian(at[34..], (ushort)bits);
        var next = 20 + formatSize; // cbSize (when there) is already 0
        if (isFloat) {
            "fact"u8.CopyTo(at[next..]);
            BinaryPrimitives.WriteUInt32LittleEndian(at[(next + 4)..], 4);
            BinaryPrimitives.WriteUInt32LittleEndian(at[(next + 8)..], (uint)frames);
            next += 12;
        }

        "data"u8.CopyTo(at[next..]);
        BinaryPrimitives.WriteUInt32LittleEndian(at[(next + 4)..], (uint)dataBytes);
        return header;
    }

    static byte[] AiffHeader(int rate, int channelCount, int frames, int bits, bool isFloat, int dataBytes) {
        // COMM: channels, frames, bits, rate; AIFF-C adds the compression type and its Pascal-string name (padded
        // even).
        var nameBytes = 1 + FloatName.Length;
        var commSize = isFloat ? 18 + 4 + nameBytes + (nameBytes & 1) : 18;
        var fverBytes = isFloat ? 12 : 0;
        var header = new byte[12 + fverBytes + 8 + commSize + 16];
        var at = header.AsSpan();
        var padded = dataBytes + (dataBytes & 1);
        "FORM"u8.CopyTo(at);
        BinaryPrimitives.WriteUInt32BigEndian(at[4..], (uint)(header.Length - 8 + padded));
        (isFloat ? "AIFC"u8 : "AIFF"u8).CopyTo(at[8..]);
        var next = 12;
        if (isFloat) {
            "FVER"u8.CopyTo(at[next..]);
            BinaryPrimitives.WriteUInt32BigEndian(at[(next + 4)..], 4);
            BinaryPrimitives.WriteUInt32BigEndian(at[(next + 8)..], AifcVersion1);
            next += 12;
        }

        "COMM"u8.CopyTo(at[next..]);
        BinaryPrimitives.WriteUInt32BigEndian(at[(next + 4)..], (uint)commSize);
        BinaryPrimitives.WriteUInt16BigEndian(at[(next + 8)..], (ushort)channelCount);
        BinaryPrimitives.WriteUInt32BigEndian(at[(next + 10)..], (uint)frames);
        BinaryPrimitives.WriteUInt16BigEndian(at[(next + 14)..], (ushort)bits);
        WriteExtended(at[(next + 16)..], rate);
        if (isFloat) {
            "fl32"u8.CopyTo(at[(next + 26)..]);
            at[next + 30] = (byte)FloatName.Length;
            for (var i = 0; i < FloatName.Length; i++) {
                at[next + 31 + i] = (byte)FloatName[i];
            }
        }

        next += 8 + commSize;
        "SSND"u8.CopyTo(at[next..]);
        BinaryPrimitives.WriteUInt32BigEndian(at[(next + 4)..], (uint)(8 + dataBytes));

        // Offset and block size stay 0.
        return header;
    }

    delegate void SampleWriter(int channel, int frame, Span<byte> at);
}
