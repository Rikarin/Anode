using Anode.Contracts;

namespace Anode.Model.Audio;

/// <summary>
///     Word-length reduction from floats (full scale ±1) to integers of 16 or 24 bits (full scale <c>2^(bits-1)</c>,
///     clipped to <c>[-2^(bits-1), 2^(bits-1) - 1]</c>).
/// </summary>
/// <remarks>
///     <see cref="DitherMode.None" /> rounds to nearest. <see cref="DitherMode.Tpdf" /> adds triangular dither of ±1
///     LSB peak (the sum of two independent uniform values in [-0.5, 0.5) LSB) before rounding, which makes the error
///     white, independent of the signal and of constant power (LSB²/4). <see cref="DitherMode.Shaped" /> adds the same
///     dither inside a first-order error-feedback loop (the previous sample's total error is subtracted), so the error
///     is shaped by <c>1 - z^-1</c>: less of it at low frequencies, more towards the Nyquist frequency. Every channel
///     has its own state and its own noise; the same seed gives the same output.
/// </remarks>
public static class Dither {
    public static int[][] Quantize(float[][] channels, int bits, DitherMode mode, int seed = 0) {
        if (bits is not (16 or 24)) {
            throw new ArgumentOutOfRangeException(nameof(bits), bits, "Quantize to 16 or 24 bits.");
        }

        var result = new int[channels.Length][];
        for (var c = 0; c < channels.Length; c++) {
            var noise = new Noise(((ulong)(uint)seed << 32) | (uint)c);
            result[c] = QuantizeChannel(channels[c], bits, mode, ref noise);
        }

        return result;
    }

    static int[] QuantizeChannel(float[] samples, int bits, DitherMode mode, ref Noise noise) {
        double scale = 1 << (bits - 1);
        var min = -(1 << (bits - 1));
        var max = (1 << (bits - 1)) - 1;
        var output = new int[samples.Length];
        var error = 0.0;
        for (var i = 0; i < samples.Length; i++) {
            var wanted = samples[i] * scale;
            var value = mode switch {
                DitherMode.None => wanted,
                DitherMode.Tpdf => wanted + noise.Triangular(),
                _ => wanted - error + noise.Triangular()
            };
            var rounded = Math.Floor(value + 0.5);
            if (mode == DitherMode.Shaped) {
                // The total error this sample made, fed back into the next one (bounded, should a clip make it large).
                error = Math.Clamp(rounded - (wanted - error), -2, 2);
            }

            output[i] = (int)Math.Clamp(rounded, min, max);
        }

        return output;
    }

    /// <summary>SplitMix64: small, fast and the same everywhere.</summary>
    struct Noise(ulong state) {
        ulong state = state;

        /// <summary>The sum of two uniform values in [-0.5, 0.5): triangular over (-1, 1).</summary>
        public double Triangular() => Uniform() + Uniform();

        double Uniform() => (Next() >> 11) * (1.0 / (1UL << 53)) - 0.5;

        ulong Next() {
            state += 0x9E3779B97F4A7C15;
            var z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
            return z ^ (z >> 31);
        }
    }
}
