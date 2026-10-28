using Anode.Model.Audio;

namespace Anode.Model.Tests;

/// <summary>The resampler: accuracy against ideal sines, the passband, the stopband, lengths and alignment.</summary>
public sealed class SampleRateConverterTests {
    /// <summary>Frames left out at each end, where the kernel runs past the input.</summary>
    const int Edge = 1000;

    [Theory]
    [InlineData(48_000, 44_100)]
    [InlineData(44_100, 96_000)]
    [InlineData(44_100, 48_000)]
    [InlineData(96_000, 44_100)]
    [InlineData(48_000, 96_000)]
    public void A_1_kHz_sine_matches_the_ideal_one_at_the_new_rate(int from, int to) {
        var input = Signals.Sine(from, 1000, -6, from);
        var output = SampleRateConverter.Convert([input], from, to)[0];
        var ideal = Signals.Sine(to, 1000, -6, output.Length);
        double signal = 0, noise = 0;
        for (var i = Edge; i < output.Length - Edge; i++) {
            signal += (double)ideal[i] * ideal[i];
            noise += ((double)output[i] - ideal[i]) * ((double)output[i] - ideal[i]);
        }

        var snr = 10 * Math.Log10(signal / noise);
        Assert.True(snr > 90, $"SNR {snr:F1} dB");
    }

    [Theory]
    [InlineData(48_000, 44_100)]
    [InlineData(44_100, 48_000)]
    [InlineData(96_000, 44_100)]
    public void A_20_kHz_tone_passes(int from, int to) {
        var output = SampleRateConverter.Convert([Signals.Sine(from, 20_000, -6, from / 2)], from, to)[0];
        var level = Signals.Db(Signals.Rms(output, Edge, output.Length - Edge) * Math.Sqrt(2)) + 6;
        Assert.InRange(level, -0.5, 0.5);
    }

    [Theory]
    [InlineData(23_000)]
    [InlineData(22_100)]
    [InlineData(30_000)]
    public void Tones_above_the_new_Nyquist_frequency_are_removed(double frequency) {
        var from = frequency > 24_000 ? 96_000 : 48_000;
        var output = SampleRateConverter.Convert([Signals.Sine(from, frequency, 0, from / 2)], from, 44_100)[0];
        var level = Signals.Db(Signals.Rms(output, Edge, output.Length - Edge) * Math.Sqrt(2));
        Assert.True(level < -80, $"{level:F1} dB");
    }

    [Theory]
    [InlineData(48_000, 44_100, 48_000, 44_100)]
    [InlineData(48_000, 44_100, 1, 1)]
    [InlineData(48_000, 44_100, 7, 6)]
    [InlineData(44_100, 48_000, 44_101, 48_001)]
    [InlineData(44_100, 96_000, 1000, 2177)]
    [InlineData(44_100, 22_050, 3, 2)]
    public void The_output_has_round_frames_times_to_over_from_frames(int from, int to, int frames, int expected) {
        Assert.Equal(expected, SampleRateConverter.Convert([new float[frames]], from, to)[0].Length);
        Assert.Equal(expected, SampleRateConverter.OutputFrames(frames, from, to));
    }

    [Fact]
    public void The_same_rate_copies() {
        var input = Signals.Sine(48_000, 1000, -6, 1000);
        var output = SampleRateConverter.Convert([input], 48_000, 48_000);
        Assert.Equal(input, output[0]);
        Assert.NotSame(input, output[0]);
    }

    [Fact]
    public void Every_channel_is_converted_and_nothing_moves_in_time() {
        // An impulse at input frame 4800 (0.1 s) lands at output frame 4410 at 44.1 kHz.
        var impulse = new float[9600];
        impulse[4800] = 1;
        var output = SampleRateConverter.Convert([impulse, new float[9600]], 48_000, 44_100);
        Assert.Equal(2, output.Length);
        var peakAt = Array.IndexOf(output[0], output[0].Max());
        Assert.Equal(4410, peakAt);
        Assert.All(output[1], static s => Assert.Equal(0f, s));
    }

    [Fact]
    public void Unusual_ratios_still_work() {
        // 44,100 to 47,999 has 47,999 phases: the kernel table is interpolated.
        var input = Signals.Sine(44_100, 1000, -6, 44_100 / 2);
        var output = SampleRateConverter.Convert([input], 44_100, 47_999)[0];
        var ideal = Signals.Sine(47_999, 1000, -6, output.Length);
        double signal = 0, noise = 0;
        for (var i = Edge; i < output.Length - Edge; i++) {
            signal += (double)ideal[i] * ideal[i];
            noise += ((double)output[i] - ideal[i]) * ((double)output[i] - ideal[i]);
        }

        Assert.True(10 * Math.Log10(signal / noise) > 90);
    }
}
