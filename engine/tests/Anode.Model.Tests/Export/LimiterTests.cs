using Anode.Model.Audio;

namespace Anode.Model.Tests;

/// <summary>
///     The true-peak limiter: the ceiling holds, quiet material passes untouched and nothing moves in time.
/// </summary>
public sealed class LimiterTests {
    const int Rate = 48_000;

    /// <summary>
    ///     Sine bursts and noise, reaching <paramref name="overDb" /> above <paramref name="ceilingDb" />.
    /// </summary>
    static float[][] Loud(double ceilingDb, double overDb, int seed) {
        var random = new Random(seed);
        var frames = Rate * 2;
        var left = new float[frames];
        var right = new float[frames];
        var peak = Signals.Amplitude(ceilingDb + overDb);
        for (var i = 0; i < frames; i++) {
            var burst = (i / 4800 % 2 == 0 ? 1.0 : 0.15) * Math.Sin(2 * Math.PI * 3000 * i / Rate);
            var noise = random.NextDouble() * 2 - 1;
            left[i] = (float)(peak * (0.6 * burst + 0.4 * noise));
            right[i] = (float)(peak * (0.5 * Math.Sin(2 * Math.PI * 11_025 * i / Rate) + 0.5 * noise));
        }

        return [left, right];
    }

    [Theory]
    [InlineData(-1.0, 12.0, 1)]
    [InlineData(-1.0, 6.0, 2)]
    [InlineData(-0.1, 3.0, 3)]
    [InlineData(-6.0, 12.0, 4)]
    public void Loud_material_ends_at_or_below_the_ceiling(double ceilingDb, double overDb, int seed) {
        var channels = Loud(ceilingDb, overDb, seed);
        Assert.True(TruePeak.MeasureDb(channels) > ceilingDb + 1);
        Limiter.Apply(channels, Rate, ceilingDb);
        Assert.Equal(Rate * 2, channels[0].Length);
        Assert.Equal(Rate * 2, channels[1].Length);
        Assert.True(TruePeak.MeasureDb(channels) <= ceilingDb + 0.01, $"{TruePeak.MeasureDb(channels)} dBTP");
    }

    [Fact]
    public void Inter_sample_overs_are_caught() {
        // Samples at -2.0 dBFS, true peak at +1 dBTP: a sample-peak limiter would let it through.
        var samples = Signals.Sine(Rate, Rate / 4.0, 1.0, Rate, Math.PI / 4);
        var channels = new[] { samples, (float[])samples.Clone() };
        Limiter.Apply(channels, Rate, -1.0);
        Assert.True(TruePeak.MeasureDb(channels) <= -0.99);
    }

    [Fact]
    public void Material_below_the_ceiling_passes_unchanged() {
        var channels = Loud(-6.0, 0.0, 5);
        Signals.Faded(channels[0], 480);
        Signals.Faded(channels[1], 480);
        var scale = (float)Math.Min(1, Signals.Amplitude(-1.5) / TruePeak.Measure(channels));
        foreach (var channel in channels) {
            for (var i = 0; i < channel.Length; i++) {
                channel[i] *= scale;
            }
        }

        var before = channels.Select(static c => (float[])c.Clone()).ToArray();
        Limiter.Apply(channels, Rate, -1.0);
        for (var c = 0; c < 2; c++) {
            for (var i = 0; i < channels[c].Length; i++) {
                Assert.Equal(before[c][i], channels[c][i]);
            }
        }
    }

    [Fact]
    public void A_short_burst_keeps_its_place_in_time() {
        // A quiet tone with one loud burst in the middle: the limited output still correlates best at lag 0.
        var samples = Signals.Sine(Rate, 440, -20, Rate);
        for (var i = 24_000; i < 24_480; i++) {
            samples[i] *= 30;
        }

        var before = (float[])samples.Clone();
        var channels = new[] { samples };
        Limiter.Apply(channels, Rate, -1.0);
        Assert.True(TruePeak.MeasureDb(channels) <= -0.99);
        Assert.Equal(0, BestLag(before, channels[0], 64));

        // Away from the burst (beyond the lookahead and the release) the tone is untouched.
        for (var i = 0; i < 23_000; i++) {
            Assert.Equal(before[i], channels[0][i]);
        }
    }

    [Fact]
    public void Silence_and_empty_input_are_fine() {
        var silence = new[] { new float[1000] };
        Limiter.Apply(silence, Rate, -1.0);
        Assert.All(silence[0], static s => Assert.Equal(0f, s));
        Limiter.Apply([], Rate, -1.0);
        Limiter.Apply([[]], Rate, -1.0);
    }

    static int BestLag(float[] a, float[] b, int maxLag) {
        var best = 0;
        var bestValue = double.NegativeInfinity;
        for (var lag = -maxLag; lag <= maxLag; lag++) {
            var sum = 0.0;
            for (var i = maxLag; i < a.Length - maxLag; i++) {
                sum += (double)a[i] * b[i + lag];
            }

            if (sum > bestValue) {
                bestValue = sum;
                best = lag;
            }
        }

        return best;
    }
}
