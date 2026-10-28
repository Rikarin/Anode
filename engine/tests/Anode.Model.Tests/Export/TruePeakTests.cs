using Anode.Model.Audio;

namespace Anode.Model.Tests;

/// <summary>True peak against EBU Tech 3341-style signals, generated here.</summary>
public sealed class TruePeakTests {
    const int Rate = 48_000;

    /// <summary>
    ///     Sines whose peaks fall halfway between samples (Tech 3341's inter-sample cases): fs/4 at 45°, fs/6 at 60°
    ///     and fs/8 at 67.5° all have a true peak of the full amplitude, while the samples read up to 3 dB lower.
    /// </summary>
    [Theory]
    [InlineData(4, 45.0, 0.0)]
    [InlineData(6, 60.0, 0.0)]
    [InlineData(8, 67.5, 0.0)]
    [InlineData(4, 45.0, -6.0)]
    [InlineData(6, 60.0, -6.0)]
    [InlineData(8, 67.5, -6.0)]
    public void Inter_sample_peaks_are_found(int divisor, double phaseDegrees, double dbtp) {
        // As in the Tech 3341 files, the sines start and stop abruptly.
        var samples = Signals.Sine(Rate, (double)Rate / divisor, dbtp, Rate, phaseDegrees * Math.PI / 180);
        var measured = TruePeak.MeasureDb([samples]);
        Assert.InRange(measured, dbtp - 0.4, dbtp + 0.2); // the tolerance Tech 3341 gives
        Assert.InRange(measured, dbtp - 0.05, dbtp + 0.05); // what this meter achieves
    }

    [Fact]
    public void A_quarter_rate_sine_with_a_sample_peak_of_minus_3_dB_reads_0_dBTP() {
        var samples = Signals.Sine(Rate, Rate / 4.0, 0, Rate, Math.PI / 4);
        Assert.Equal(-3.01, Signals.Db(SamplePeak(samples)), 0.01);
        Assert.Equal(0.0, TruePeak.MeasureDb([samples]), 0.05);
    }

    [Theory]
    [InlineData(48_000)]
    [InlineData(44_100)]
    public void A_1_kHz_sine_at_minus_6_dBFS_reads_minus_6_dBTP(int rate) {
        var samples = Signals.Sine(rate, 1000, -6, rate);
        Assert.Equal(-6.0, TruePeak.MeasureDb([samples, samples]), 0.2);
    }

    [Fact]
    public void The_loudest_channel_counts() {
        var quiet = Signals.Sine(Rate, 1000, -20, 4800);
        var loud = Signals.Sine(Rate, 1000, -3, 4800);
        Assert.Equal(-3.0, TruePeak.MeasureDb([quiet, loud]), 0.05);
        Assert.Equal(TruePeak.Measure([loud]), TruePeak.Measure([quiet, loud]));
    }

    [Fact]
    public void A_faded_sine_reads_the_same_as_an_abrupt_one() {
        var abrupt = Signals.Sine(Rate, Rate / 6.0, -1, Rate, Math.PI / 3);
        var faded = Signals.Faded((float[])abrupt.Clone(), 480);
        Assert.Equal(TruePeak.MeasureDb([abrupt]), TruePeak.MeasureDb([faded]), 0.02);
    }

    [Fact]
    public void Silence_reads_negative_infinity() {
        Assert.Equal(double.NegativeInfinity, TruePeak.MeasureDb([new float[1000]]));
        Assert.Equal(0.0, TruePeak.Measure([new float[1000], new float[1000]]));
        Assert.Equal(double.NegativeInfinity, TruePeak.MeasureDb([]));
    }

    [Fact]
    public void The_true_peak_is_never_below_the_sample_peak() {
        var random = new Random(3341);
        for (var trial = 0; trial < 20; trial++) {
            var samples = new float[2000];
            for (var i = 0; i < samples.Length; i++) {
                samples[i] = (float)(random.NextDouble() * 2 - 1);
            }

            Assert.True(TruePeak.MeasureDb([samples]) >= Signals.Db(SamplePeak(samples)) - 0.01);
        }
    }

    [Fact]
    public void A_single_full_scale_sample_reads_its_own_level() {
        // An impulse's reconstruction peaks on the sample itself.
        var samples = new float[256];
        samples[128] = 0.5f;
        Assert.Equal(0.5, TruePeak.Measure([samples]), 1e-6);
    }

    static double SamplePeak(float[] samples) {
        var peak = 0.0;
        foreach (var s in samples) {
            peak = Math.Max(peak, Math.Abs(s));
        }

        return peak;
    }
}
