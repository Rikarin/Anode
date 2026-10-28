using Anode.Model.Audio;
using System.Numerics;

namespace Anode.Model.Tests;

/// <summary>Integrated loudness against the EBU Tech 3341 (2016) test signals, generated here.</summary>
public sealed class LoudnessTests {
    static double Stereo(float[] samples, int rate) => Loudness.Integrated([samples, (float[])samples.Clone()], rate);

    [Theory]
    [InlineData(48_000)]
    [InlineData(44_100)]
    public void Case_1_a_stereo_sine_at_minus_23_dBFS_reads_minus_23_LUFS(int rate) =>
        Assert.Equal(-23.0, Stereo(Signals.Sine(rate, 1000, -23, 20.0), rate), 0.1);

    [Theory]
    [InlineData(48_000)]
    [InlineData(44_100)]
    public void Case_2_a_stereo_sine_at_minus_33_dBFS_reads_minus_33_LUFS(int rate) =>
        Assert.Equal(-33.0, Stereo(Signals.Sine(rate, 1000, -33, 20.0), rate), 0.1);

    [Theory]
    [InlineData(48_000)]
    [InlineData(44_100)]
    public void Case_3_the_relative_gate_drops_the_quiet_ends(int rate) =>
        Assert.Equal(-23.0, Stereo(Signals.Sequence(rate, (-36, 10), (-23, 60), (-36, 10)), rate), 0.1);

    [Fact]
    public void Case_4_the_absolute_gate_drops_the_near_silence() {
        var samples = Signals.Sequence(48_000, (-72, 10), (-36, 10), (-23, 60), (-36, 10), (-72, 10));
        Assert.Equal(-23.0, Stereo(samples, 48_000), 0.1);
    }

    [Theory]
    [InlineData(48_000)]
    [InlineData(44_100)]
    public void Case_5_a_louder_middle_reads_minus_23_LUFS(int rate) =>
        Assert.Equal(-23.0, Stereo(Signals.Sequence(rate, (-26, 20), (-20, 20.1), (-26, 20)), rate), 0.1);

    [Fact]
    public void A_full_scale_sine_in_one_channel_reads_minus_3_LUFS() {
        var samples = Signals.Sine(48_000, 1000, 0, 5.0);
        Assert.Equal(-3.01, Loudness.Integrated([samples], 48_000), 0.05);
        Assert.Equal(-3.01, Loudness.Integrated([samples, new float[samples.Length]], 48_000), 0.05);
    }

    [Theory]
    [InlineData(25)]
    [InlineData(100)]
    [InlineData(500)]
    [InlineData(2000)]
    [InlineData(5000)]
    [InlineData(10_000)]
    [InlineData(20_000)]
    public void Tones_are_weighted_as_the_K_filter_says(double frequency) {
        const int rate = 48_000;
        var reference = Stereo(Signals.Sine(rate, 1000, -20, 5.0), rate);
        var tone = Stereo(Signals.Sine(rate, frequency, -20, 5.0), rate);
        var expected = KWeightingDb(frequency, rate) - KWeightingDb(1000, rate);
        Assert.Equal(expected, tone - reference, 0.3);
    }

    [Fact]
    public void The_weighting_is_the_same_at_44_1_kHz() {
        // The coefficients are derived for the rate, so the response matches the analogue one away from Nyquist.
        var at48 = Stereo(Signals.Sine(48_000, 100, -20, 5.0), 48_000)
            - Stereo(Signals.Sine(48_000, 1000, -20, 5.0), 48_000);
        var at44 = Stereo(Signals.Sine(44_100, 100, -20, 5.0), 44_100)
            - Stereo(Signals.Sine(44_100, 1000, -20, 5.0), 44_100);
        Assert.Equal(at48, at44, 0.05);
    }

    [Fact]
    public void Silence_and_short_files_read_negative_infinity() {
        Assert.Equal(double.NegativeInfinity, Stereo(new float[48_000 * 3], 48_000));
        Assert.Equal(double.NegativeInfinity, Stereo(Signals.Sine(48_000, 1000, -80, 3.0), 48_000));
        Assert.Equal(double.NegativeInfinity, Stereo(Signals.Sine(48_000, 1000, -10, 0.3), 48_000));
    }

    /// <summary>The response of BS.1770-4's 48 kHz K-weighting coefficient table, in dB.</summary>
    static double KWeightingDb(double frequency, int rate) {
        var z = Complex.FromPolarCoordinates(1, -2 * Math.PI * frequency / rate);
        var shelf = (1.53512485958697 + -2.69169618940638 * z + 1.19839281085285 * z * z)
            / (1 + -1.69065929318241 * z + 0.73248077421585 * z * z);
        var highPass = (1 - 2 * z + z * z) / (1 + -1.99004745483398 * z + 0.99007225036621 * z * z);
        return 20 * Math.Log10((shelf * highPass).Magnitude);
    }
}
