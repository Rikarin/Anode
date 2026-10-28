using Anode.Contracts;
using Anode.Model.Audio;

namespace Anode.Model.Tests;

/// <summary>Word-length reduction: the statistics of each dither mode.</summary>
public sealed class DitherTests {
    const int Frames = 1 << 20;

    /// <summary>A sine of slowly varying level and some noise: covers many values between the LSBs.</summary>
    static float[] Music(int seed) {
        var random = new Random(seed);
        var samples = new float[Frames];
        for (var i = 0; i < samples.Length; i++) {
            var level = 0.3 + 0.2 * Math.Sin(2 * Math.PI * i / Frames);
            samples[i] = (float)(level * Math.Sin(2 * Math.PI * 997 * i / 48_000) + 0.01 * (random.NextDouble() - 0.5));
        }

        return samples;
    }

    /// <summary>The error in LSBs: output minus input, both scaled to the integer range.</summary>
    static double[] Error(float[] input, int[] output, int bits) {
        var scale = Math.Pow(2, bits - 1);
        var error = new double[input.Length];
        for (var i = 0; i < input.Length; i++) {
            error[i] = output[i] - input[i] * scale;
        }

        return error;
    }

    static double Mean(double[] values) => values.Average();

    static double Variance(double[] values) {
        var mean = Mean(values);
        return values.Sum(v => (v - mean) * (v - mean)) / values.Length;
    }

    static double Correlation(double[] a, float[] b) {
        var meanA = Mean(a);
        var meanB = b.Average(static v => (double)v);
        double ab = 0, aa = 0, bb = 0;
        for (var i = 0; i < a.Length; i++) {
            var x = a[i] - meanA;
            var y = b[i] - meanB;
            ab += x * y;
            aa += x * x;
            bb += y * y;
        }

        return ab / Math.Sqrt(aa * bb);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    public void Tpdf_error_is_zero_mean_quarter_LSB_squared_and_independent_of_the_signal(int bits) {
        var input = Music(1);
        var error = Error(input, Dither.Quantize([input], bits, DitherMode.Tpdf, 7)[0], bits);
        Assert.InRange(Mean(error), -0.005, 0.005);
        Assert.InRange(Variance(error), 0.25 * 0.97, 0.25 * 1.03);
        Assert.InRange(Correlation(error, input), -0.01, 0.01);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(0.75)]
    public void Tpdf_error_power_does_not_depend_on_the_level(double lsbs) {
        // Without dither a constant input gives a constant output and an error that depends on the level.
        var input = new float[1 << 18];
        Array.Fill(input, (float)((1000 + lsbs) / 32768));
        var output = Dither.Quantize([input], 16, DitherMode.Tpdf, 11)[0];
        var error = Error(input, output, 16);
        Assert.InRange(Mean(error), -0.01, 0.01);
        Assert.InRange(Mean([..error.Select(static e => e * e)]), 0.25 * 0.96, 0.25 * 1.04);
        Assert.True(output.Distinct().Count() > 1);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    public void None_rounds_to_the_nearest_value(int bits) {
        var input = Music(2);
        var error = Error(input, Dither.Quantize([input], bits, DitherMode.None)[0], bits);
        Assert.All(error, static e => Assert.InRange(e, -0.5, 0.5));
    }

    [Fact]
    public void Shaped_noise_rises_with_frequency() {
        var input = Music(3);
        var output = Dither.Quantize([input], 16, DitherMode.Shaped, 5)[0];
        var error = Error(input, output, 16);
        var (low, high) = QuarterBandEnergies(error, 1024, 16);
        Assert.True(high > 4 * low, $"low {low}, high {high}");
    }

    [Theory]
    [InlineData(DitherMode.None)]
    [InlineData(DitherMode.Tpdf)]
    [InlineData(DitherMode.Shaped)]
    public void Output_stays_in_range_even_at_full_scale(DitherMode mode) {
        var input = Signals.Sine(48_000, 1000, 3, 48_000);
        foreach (var bits in new[] { 16, 24 }) {
            var max = (1 << (bits - 1)) - 1;
            var output = Dither.Quantize([input], bits, mode, 1)[0];
            Assert.All(output, s => Assert.InRange(s, -max - 1, max));
            Assert.Contains(max, output);
            Assert.Contains(-max - 1, output);
        }
    }

    [Theory]
    [InlineData(DitherMode.Tpdf)]
    [InlineData(DitherMode.Shaped)]
    public void The_same_seed_gives_the_same_output_and_channels_differ(DitherMode mode) {
        var input = Signals.Sine(48_000, 1000, -60, 48_000);
        var a = Dither.Quantize([input, input], 16, mode, 42);
        var b = Dither.Quantize([input, input], 16, mode, 42);
        var c = Dither.Quantize([input, input], 16, mode, 43);
        Assert.Equal(a[0], b[0]);
        Assert.Equal(a[1], b[1]);
        Assert.NotEqual(a[0], c[0]);
        Assert.NotEqual(a[0], a[1]);
    }

    [Fact]
    public void Only_16_and_24_bits_are_offered() =>
        Assert.Throws<ArgumentOutOfRangeException>(static () => Dither.Quantize([new float[4]], 20, DitherMode.None));

    /// <summary>
    ///     The error's energy in the lowest and the highest quarter of the band, by a plain DFT over blocks.
    /// </summary>
    static (double Low, double High) QuarterBandEnergies(double[] signal, int size, int blocks) {
        var cos = new double[size];
        var sin = new double[size];
        for (var n = 0; n < size; n++) {
            cos[n] = Math.Cos(2 * Math.PI * n / size);
            sin[n] = Math.Sin(2 * Math.PI * n / size);
        }

        double low = 0, high = 0;
        for (var block = 0; block < blocks; block++) {
            for (var k = 1; k < size / 2; k++) {
                double re = 0, im = 0;
                for (var n = 0; n < size; n++) {
                    var turn = k * n % size;
                    re += signal[block * size + n] * cos[turn];
                    im -= signal[block * size + n] * sin[turn];
                }

                var power = re * re + im * im;
                if (k < size / 8) {
                    low += power;
                } else if (k >= 3 * size / 8) {
                    high += power;
                }
            }
        }

        return (low, high);
    }
}
