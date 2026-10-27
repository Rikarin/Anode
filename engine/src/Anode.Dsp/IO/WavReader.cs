using System.Buffers.Binary;

namespace Anode.Dsp.IO;

/// <summary>
///     A decoded WAV file: its channels as floats in −1 … 1, its rate, and what its <c>smpl</c> chunk says (the MIDI
///     unity note and its fine tune, and the first loop), with −1 where it has none.
/// </summary>
public sealed record WavData(
    int SampleRate,
    float[][] Channels,
    int RootNote,
    double RootCents,
    long LoopStart,
    long LoopEnd) {
    public int Frames => Channels[0].Length;
}

/// <summary>
///     Reads WAV files whole into memory: PCM 8/16/24/32-bit and 32/64-bit float, plain or
///     <c>WAVE_FORMAT_EXTENSIBLE</c>, any number of channels; chunks other than <c>fmt </c>, <c>data</c> and
///     <c>smpl</c> are skipped.
/// </summary>
public static class WavReader {
    public static WavData Read(string path) => Read(File.ReadAllBytes(path));

    public static WavData Read(ReadOnlySpan<byte> file) {
        if (file.Length < 12 || !file[..4].SequenceEqual("RIFF"u8) || !file[8..12].SequenceEqual("WAVE"u8)) {
            throw new InvalidDataException("Not a RIFF/WAVE file.");
        }

        int tag = 0, channels = 0, rate = 0, bits = 0;
        ReadOnlySpan<byte> data = default;
        var hasData = false;
        var root = -1;
        double cents = 0;
        long loopStart = -1, loopEnd = -1;
        var at = 12;
        while (at + 8 <= file.Length) {
            var id = file.Slice(at, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(file[(at + 4)..]);
            var body = file.Slice(at + 8, (int)Math.Min(size, (uint)(file.Length - at - 8)));
            if (id.SequenceEqual("fmt "u8) && body.Length >= 16) {
                tag = BinaryPrimitives.ReadUInt16LittleEndian(body);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(body[4..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(body[14..]);
                if (tag == 0xFFFE && body.Length >= 26) {
                    tag = BinaryPrimitives.ReadUInt16LittleEndian(body[24..]); // the sub-format GUID's first two bytes
                }
            } else if (id.SequenceEqual("data"u8)) {
                data = body;
                hasData = true;
            } else if (id.SequenceEqual("smpl"u8) && body.Length >= 36) {
                root = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(body[12..]), 127);
                cents = BinaryPrimitives.ReadUInt32LittleEndian(body[16..]) / 4_294_967_296.0 * 100;
                var loops = BinaryPrimitives.ReadUInt32LittleEndian(body[28..]);
                if (loops > 0 && body.Length >= 60) {
                    loopStart = BinaryPrimitives.ReadUInt32LittleEndian(body[44..]);
                    loopEnd = BinaryPrimitives.ReadUInt32LittleEndian(body[48..]);
                }
            }

            at += 8 + (int)Math.Min(size, int.MaxValue - 16) + (int)(size & 1);
        }

        if (!hasData || channels < 1 || rate < 1) {
            throw new InvalidDataException("WAV file without a format or data.");
        }

        var bytes = (tag, bits) switch {
            (1, 8) => 1,
            (1, 16) => 2,
            (1, 24) => 3,
            (1, 32) or (3, 32) => 4,
            (3, 64) => 8,
            _ => throw new InvalidDataException($"Unsupported WAV encoding (format {tag}, {bits} bits).")
        };
        var frames = data.Length / (bytes * channels);
        var output = new float[channels][];
        for (var c = 0; c < channels; c++) {
            output[c] = new float[frames];
        }

        for (var f = 0; f < frames; f++) {
            for (var c = 0; c < channels; c++) {
                var s = data[((f * channels + c) * bytes)..];
                output[c][f] = (tag, bytes) switch {
                    (1, 1) => (s[0] - 128) / 128f,
                    (1, 2) => BinaryPrimitives.ReadInt16LittleEndian(s) / 32_768f,
                    (1, 3) => (((s[0] << 8) | (s[1] << 16) | (s[2] << 24)) >> 8) / 8_388_608f,
                    (1, _) => (float)(BinaryPrimitives.ReadInt32LittleEndian(s) / 2_147_483_648.0),
                    (_, 4) => BinaryPrimitives.ReadSingleLittleEndian(s),
                    _ => (float)BinaryPrimitives.ReadDoubleLittleEndian(s)
                };
            }
        }

        return new(rate, output, root, cents, loopStart, loopEnd);
    }
}
