using Anode.Dsp.Metering;
using Anode.Dsp.Spectral;
using Anode.Dsp.Tests.Support;

namespace Anode.Dsp.Tests;

/// <summary>The real FFT and the partitioned convolver behind Space and the Linear Phase EQ.</summary>
public sealed class SpectralTests {
    static float[] Noise(int frames, uint seed, float amplitude = 0.5f) {
        var x = new float[frames];
        for (var i = 0; i < frames; i++) {
            seed ^= seed << 13;
            seed ^= seed >> 17;
            seed ^= seed << 5;
            x[i] = amplitude * (seed * (2f / uint.MaxValue) - 1);
        }

        return x;
    }

    [Theory]
    [InlineData(4)]
    [InlineData(16)]
    [InlineData(256)]
    [InlineData(4_096)]
    public void Real_fft_matches_the_complex_one_and_round_trips(int size) {
        var fft = new RealFft(size);
        var x = Noise(size, 7);
        var re = new float[fft.Bins];
        var im = new float[fft.Bins];
        fft.Forward(x, re, im);
        var expectedRe = x.Select(static s => (double)s).ToArray();
        var expectedIm = new double[size];
        SplitFft.Forward(expectedRe, expectedIm);
        for (var k = 0; k < fft.Bins; k++) {
            Assert.Equal(expectedRe[k], re[k], 1e-4 * Math.Sqrt(size));
            Assert.Equal(expectedIm[k], im[k], 1e-4 * Math.Sqrt(size));
        }

        var back = new float[size];
        fft.Inverse(re, im, back);
        for (var n = 0; n < size; n++) {
            Assert.Equal(x[n], back[n], 1e-5);
        }
    }

    [Theory]
    [InlineData(64, 3_000, 64)]
    [InlineData(128, 5_000, 1)]
    [InlineData(64, 50, 17)]
    [InlineData(256, 256, 100)]
    public void Convolution_equals_direct_convolution(int block, int taps, int bufferSize) {
        var ir = Noise(taps, 99, 0.3f);
        for (var i = 0; i < taps; i++) {
            ir[i] *= MathF.Exp(-3f * i / taps);
        }

        var x = Noise(12_000, 5);
        var convolver = new PartitionedConvolver();
        convolver.Allocate(block, taps);
        convolver.SetFilter(ir);
        var y = new float[x.Length];
        for (var at = 0; at < x.Length; at += bufferSize) {
            for (var i = at; i < Math.Min(at + bufferSize, x.Length); i++) {
                y[i] = convolver.Tick(x[i]);
            }
        }

        var worst = 0.0;
        for (var n = 0; n < x.Length; n++) {
            double sum = 0;
            for (var k = 0; k < taps && k <= n; k++) {
                sum += (double)ir[k] * x[n - k];
            }

            worst = Math.Max(worst, Math.Abs(sum - y[n]));
        }

        Assert.True(worst < 1e-5, $"worst error {worst:E2}");
    }

    [Fact]
    public void Partition_gains_scale_the_tail() {
        var ir = new float[256];
        ir[0] = 1;
        ir[200] = 0.5f;
        var convolver = new PartitionedConvolver();
        convolver.Allocate(64, 256);
        convolver.SetFilter(ir);
        convolver.SetPartitionGain(3, 0.5f);
        var y = new float[400];
        for (var i = 0; i < y.Length; i++) {
            y[i] = convolver.Tick(i == 0 ? 1 : 0);
        }

        Assert.Equal(1, y[0], 6);
        Assert.Equal(0.25, y[200], 6);
        Assert.Equal(4, convolver.Partitions);
    }

    [Fact]
    public void Ticking_allocates_nothing() {
        var convolver = new PartitionedConvolver();
        convolver.Allocate(128, 10_000);
        convolver.SetFilter(Noise(10_000, 3));
        var other = Noise(3_000, 1);
        var allocated = AllocationProbe.Measure(() => {
                for (var i = 0; i < 5_000; i++) {
                    convolver.Tick(i % 7 * 0.1f);
                }

                convolver.SetFilter(other);
            }
        );
        Assert.Equal(0, allocated);
    }
}
