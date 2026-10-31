using System.Buffers.Binary;
using System.Text;

namespace Anode.Stems.Tests;

/// <summary>Test signals, measures and files.</summary>
static class Signals {
    public const int Rate = 44100;

    /// <summary>A sum of sines (frequency in Hz, amplitude), with a per-channel phase offset.</summary>
    public static float[][] Sines(int frames, int channels, params (double Frequency, double Amplitude)[] partials) {
        var result = new float[channels][];
        for (var c = 0; c < channels; c++) {
            var channel = new float[frames];
            for (var n = 0; n < frames; n++) {
                var value = 0.0;
                foreach (var (frequency, amplitude) in partials) {
                    value += amplitude * Math.Sin(2 * Math.PI * frequency * n / Rate + 0.7 * c + frequency * 1e-3);
                }

                channel[n] = (float)value;
            }

            result[c] = channel;
        }

        return result;
    }

    /// <summary>Deterministic band-limited noise: sines at many frequencies below <paramref name="top" /> Hz.</summary>
    public static float[][] Noise(int frames, int channels, double top = 15000, int partials = 60) {
        var random = new Random(7);
        var list = new (double, double)[partials];
        for (var i = 0; i < partials; i++) {
            list[i] = (30 + random.NextDouble() * (top - 30), 0.3 / Math.Sqrt(partials));
        }

        return Sines(frames, channels, list);
    }

    public static float[][] Slice(float[][] channels, int start, int length) =>
        [..channels.Select(c => c.AsSpan(start, length).ToArray())];

    public static double MaxDifference(float[] a, float[] b, int start, int end) {
        var max = 0.0;
        for (var n = start; n < end; n++) {
            max = Math.Max(max, Math.Abs(a[n] - b[n]));
        }

        return max;
    }

    public static double Peak(float[] a) => a.Max(static x => Math.Abs(x));

    /// <summary>Signal-to-distortion ratio in dB over all channels: 10 log₁₀(Σ s² / Σ (s − ŝ)²).</summary>
    public static double Sdr(float[][] reference, float[][] estimate) {
        double signal = 0, error = 0;
        for (var c = 0; c < reference.Length; c++) {
            for (var n = 0; n < reference[c].Length; n++) {
                signal += (double)reference[c][n] * reference[c][n];
                var d = (double)reference[c][n] - estimate[c][n];
                error += d * d;
            }
        }

        return 10 * Math.Log10((signal + 1e-12) / (error + 1e-12));
    }

    /// <summary>Reads a WAV file of 16-bit, 24-bit or 32-bit float PCM as planar floats.</summary>
    public static (float[][] Channels, int Rate) ReadWav(string path) {
        var bytes = File.ReadAllBytes(path);
        if (Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" || Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE") {
            throw new InvalidDataException($"{path} is not a WAV file.");
        }

        int channels = 0, rate = 0, bits = 0, format = 0;
        var at = 12;
        while (at + 8 <= bytes.Length) {
            var id = Encoding.ASCII.GetString(bytes, at, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at + 4));
            var body = bytes.AsSpan(at + 8, Math.Min(size, bytes.Length - at - 8));
            if (id == "fmt ") {
                format = BinaryPrimitives.ReadUInt16LittleEndian(body);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
                rate = BinaryPrimitives.ReadInt32LittleEndian(body[4..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(body[14..]);
                if (format == 0xFFFE) {
                    format = BinaryPrimitives.ReadUInt16LittleEndian(body[24..]);
                }
            } else if (id == "data") {
                var width = bits / 8;
                var frames = body.Length / (width * channels);
                var result = new float[channels][];
                for (var c = 0; c < channels; c++) {
                    result[c] = new float[frames];
                }

                for (var n = 0; n < frames; n++) {
                    for (var c = 0; c < channels; c++) {
                        var sample = body.Slice((n * channels + c) * width, width);
                        result[c][n] = (format, bits) switch {
                            (3, 32) => BinaryPrimitives.ReadSingleLittleEndian(sample),
                            (1, 16) => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768f,
                            (1, 24) => ((sample[0] | (sample[1] << 8) | (sample[2] << 16)) << 8 >> 8) / 8388608f,
                            _ => throw new InvalidDataException($"{path}: format {format}, {bits} bits.")
                        };
                    }
                }

                return (result, rate);
            }

            at += 8 + size + (size & 1);
        }

        throw new InvalidDataException($"{path} has no data.");
    }

    /// <summary>Reads a little-endian float32 <c>.npy</c> array (C order) as a flat array.</summary>
    public static float[] ReadNpy(string path) {
        var bytes = File.ReadAllBytes(path);
        // Magic (6 bytes), version (2), header length (2 bytes in version 1, 4 after), header.
        var headerLength = bytes[6] == 1
            ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8)) + 10
            : BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8)) + 12;
        var header = Encoding.ASCII.GetString(bytes, 0, headerLength);
        if (!header.Contains("'<f4'", StringComparison.Ordinal)
            || header.Contains("'fortran_order': True", StringComparison.Ordinal)) {
            throw new InvalidDataException($"{path}: not a C-order float32 array.");
        }

        var data = bytes.AsSpan(headerLength);
        var result = new float[data.Length / 4];
        for (var i = 0; i < result.Length; i++) {
            result[i] = BinaryPrimitives.ReadSingleLittleEndian(data[(4 * i)..]);
        }

        return result;
    }

    /// <summary>The repository root (the folder holding <c>engine</c> and <c>scripts</c>).</summary>
    public static string RepositoryRoot() {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null) {
            if (Directory.Exists(Path.Combine(directory.FullName, "engine"))
                && Directory.Exists(Path.Combine(directory.FullName, "scripts"))) {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("No repository root above the test binaries.");
    }

    public static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
