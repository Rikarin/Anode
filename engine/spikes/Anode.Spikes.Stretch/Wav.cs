using System.Buffers.Binary;
using System.Text;

namespace Anode.Spikes.Stretch;

/// <summary>Minimal RIFF/WAVE: writes 32-bit float, reads 16/24/32-bit PCM and 32-bit float (plain or extensible).</summary>
static class Wav {
    public static void Write(string path, float[][] audio, int sampleRate) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var channels = audio.Length;
        var frames = audio[0].Length;
        var dataBytes = frames * channels * 4;
        using var stream = File.Create(path);
        using var w = new BinaryWriter(stream);
        w.Write("RIFF"u8);
        w.Write(36 + dataBytes);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)3); // IEEE float
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(sampleRate * channels * 4);
        w.Write((short)(channels * 4));
        w.Write((short)32);
        w.Write("data"u8);
        w.Write(dataBytes);
        for (var i = 0; i < frames; i++) {
            for (var c = 0; c < channels; c++) {
                w.Write(audio[c][i]);
            }
        }
    }

    public static (float[][] Audio, int SampleRate) Read(string path) {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 12
            || Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF"
            || Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE") {
            throw new InvalidDataException($"{path}: not a RIFF/WAVE file");
        }

        int format = 0, channels = 0, rate = 0, bits = 0;
        var pos = 12;
        while (pos + 8 <= bytes.Length) {
            var id = Encoding.ASCII.GetString(bytes, pos, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(pos + 4));
            var body = bytes.AsSpan(pos + 8, Math.Min(size, bytes.Length - pos - 8));
            if (id == "fmt ") {
                format = BinaryPrimitives.ReadUInt16LittleEndian(body);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
                rate = BinaryPrimitives.ReadInt32LittleEndian(body[4..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(body[14..]);
                if (format == 0xFFFE && body.Length >= 26) {
                    format = BinaryPrimitives.ReadUInt16LittleEndian(body[24..]); // sub-format GUID's first field
                }
            } else if (id == "data") {
                return (Decode(body, format, channels, bits, path), rate);
            }

            pos += 8 + size + (size & 1);
        }

        throw new InvalidDataException($"{path}: no data chunk");
    }

    static float[][] Decode(ReadOnlySpan<byte> data, int format, int channels, int bits, string path) {
        if (channels < 1 || !(format == 1 && bits is 16 or 24 or 32 || format == 3 && bits == 32)) {
            throw new InvalidDataException($"{path}: unsupported WAV format {format}/{bits}-bit/{channels} ch");
        }

        var stride = bits / 8;
        var frames = data.Length / (stride * channels);
        var audio = new float[channels][];
        for (var c = 0; c < channels; c++) {
            audio[c] = new float[frames];
        }

        for (var i = 0; i < frames; i++) {
            for (var c = 0; c < channels; c++) {
                var offset = (i * channels + c) * stride;
                var s = data[offset..];
                audio[c][i] = (format, bits) switch {
                    (3, _) => BinaryPrimitives.ReadSingleLittleEndian(s),
                    (_, 16) => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f,
                    (_, 24) => ((s[0] << 8) | (s[1] << 16) | (s[2] << 24)) / 2147483648f,
                    _ => BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648f
                };
            }
        }

        return audio;
    }
}
