using Anode.Dsp.Editing;
using Anode.Dsp.Tests.Analysis;
using static Anode.Dsp.Tests.Support.Analysis;

namespace Anode.Dsp.Tests.Editing;

/// <summary>The Audio File Editor's operations (docs/08-phase-7.md), each against samples worked by hand.</summary>
public sealed class AudioEditTests {
    const float Tolerance = 1e-6f;

    static float[][] Channels(params float[][] channels) => channels;

    static void Equal(float[] expected, float[] actual) {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++) {
            Assert.True(Math.Abs(expected[i] - actual[i]) < Tolerance, $"[{i}]: {actual[i]} ≠ {expected[i]}");
        }
    }

    [Fact]
    public void Normalize_scales_the_range_to_the_peak_of_every_channel() {
        var c = Channels([0.1f, -0.25f, 0.2f, 0.9f], [0.05f, 0.1f, -0.125f, 0.9f]);
        AudioEdits.Normalize(c, 0, 3, 0); // peak 0.25 (left, frame 1) → 1: × 4
        Equal([0.4f, -1f, 0.8f, 0.9f], c[0]);
        Equal([0.2f, 0.4f, -0.5f, 0.9f], c[1]);

        var half = Channels([0.2f, -0.4f]);
        AudioEdits.Normalize(half, 0, 2, 20 * Math.Log10(0.5));
        Equal([0.25f, -0.5f], half[0]);
    }

    [Fact]
    public void Normalize_leaves_silence() {
        var c = Channels([0f, 0f, 0f]);
        AudioEdits.Normalize(c, 0, 3, -1);
        Equal([0f, 0f, 0f], c[0]);
    }

    [Fact]
    public void Gain_changes_the_level_by_decibels_without_clipping() {
        var c = Channels([0.5f, -0.75f, 0.25f]);
        AudioEdits.Gain(c, 0, 2, 20 * Math.Log10(2));
        Equal([1f, -1.5f, 0.25f], c[0]);
    }

    [Fact]
    public void A_linear_fade_in_rises_from_silence_as_a_region_fade() {
        var c = Channels([1f, 1f, 1f, 1f, 1f, 1f], [-2f, -2f, -2f, -2f, -2f, -2f]);
        AudioEdits.FadeIn(c, 1, 5, 0); // x = i/4
        Equal([1f, 0f, 0.25f, 0.5f, 0.75f, 1f], c[0]);
        Equal([-2f, 0f, -0.5f, -1f, -1.5f, -2f], c[1]);
    }

    [Fact]
    public void A_curved_fade_follows_x_to_the_two_to_the_minus_two_c() {
        var c = Channels([1f, 1f, 1f, 1f]);
        AudioEdits.FadeIn(c, 0, 4, 0.5); // exponent 2^-1: √x
        Equal([0f, 0.5f, (float)Math.Sqrt(0.5), (float)Math.Sqrt(0.75)], c[0]);

        var d = Channels([1f, 1f, 1f, 1f]);
        AudioEdits.FadeIn(d, 0, 4, -0.5); // exponent 2: x²
        Equal([0f, 0.0625f, 0.25f, 0.5625f], d[0]);
    }

    [Fact]
    public void A_fade_out_is_the_mirror_and_ends_silent() {
        var c = Channels([1f, 1f, 1f, 1f]);
        AudioEdits.FadeOut(c, 0, 4, 0);
        Equal([0.75f, 0.5f, 0.25f, 0f], c[0]);

        var d = Channels([1f, 1f, 1f, 1f]);
        AudioEdits.FadeOut(d, 0, 4, -0.5);
        Equal([0.5625f, 0.25f, 0.0625f, 0f], d[0]);
    }

    [Fact]
    public void Reverse_invert_and_silence_act_on_the_range_only() {
        var c = Channels([1f, 2f, 3f, 4f, 5f], [10f, 20f, 30f, 40f, 50f]);
        AudioEdits.Reverse(c, 1, 4);
        Equal([1f, 4f, 3f, 2f, 5f], c[0]);
        Equal([10f, 40f, 30f, 20f, 50f], c[1]);

        AudioEdits.Invert(c, 0, 2);
        Equal([-1f, -4f, 3f, 2f, 5f], c[0]);

        AudioEdits.Silence(c, 3, 5);
        Equal([-1f, -4f, 3f, 0f, 0f], c[0]);
        Equal([-10f, -40f, 30f, 0f, 0f], c[1]);
    }

    [Fact]
    public void Remove_dc_subtracts_each_channels_mean_over_the_range() {
        var c = Channels([9f, 1f, 2f, 3f], [9f, -1f, -1f, 2f]);
        AudioEdits.RemoveDc(c, 1, 4);
        Equal([9f, -1f, 0f, 1f], c[0]);
        Equal([9f, -1f, -1f, 2f], c[1]); // mean 0
    }

    [Fact]
    public void Trim_returns_the_range_as_new_arrays() {
        var c = Channels([1f, 2f, 3f, 4f], [5f, 6f, 7f, 8f]);
        var trimmed = AudioEdits.Trim(c, 1, 3);
        Equal([2f, 3f], trimmed[0]);
        Equal([6f, 7f], trimmed[1]);
        trimmed[0][0] = 99;
        Assert.Equal(2f, c[0][1]);
    }

    [Fact]
    public void Convert_rate_keeps_a_tone_and_rounds_the_length() {
        var tone = Support.TestWav.Sine(1_000, 48_000, 1);
        var converted = AudioEdits.ConvertRate([tone, tone], 48_000, 44_100);
        Assert.Equal(2, converted.Length);
        Assert.Equal(44_100, converted[0].Length);
        Assert.True(Math.Abs(Cents(PeakFrequency(converted[0], 44_100, 2_000, 16_384), 1_000)) < 2);
        Assert.Equal(4, AudioEdits.ConvertRate([new float[7]], 44_100, 22_050)[0].Length); // round(3.5): halves up
    }

    [Fact]
    public void A_range_outside_the_file_is_refused() {
        var c = Channels([1f, 2f]);
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioEdits.Gain(c, 1, 3, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioEdits.Silence(c, -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioEdits.Trim(c, 2, 1));
        AudioEdits.Reverse(c, 2, 2); // empty: nothing
        Equal([1f, 2f], c[0]);
    }

    [Fact]
    public void Zero_crossings_are_sign_changes_and_exact_zeros() {
        float[] x = [0.5f, 0.2f, -0.1f, -0.3f, 0f, 0.4f, 0.6f, -0.2f];
        Assert.Equal([2L, 4L, 7L], ZeroCrossings.Find(x, 0, 8));
        Assert.Equal([4L], ZeroCrossings.Find(x, 3, 7));
        Assert.Equal(7, ZeroCrossings.Nearest(x, 6, 3));
        Assert.Equal(2, ZeroCrossings.Nearest(x, 3, 1)); // 2 and 4 are as near: the earlier
        Assert.Equal(4, ZeroCrossings.Nearest(x, 4, 0));
        Assert.Equal(-1, ZeroCrossings.Nearest(x, 0, 1));
        Assert.Equal(-1, ZeroCrossings.Nearest(x, 6, 0));
    }

    [Fact]
    public void Groove_onsets_are_the_transients_with_their_relative_peaks() {
        const int rate = Material.Rate;
        long[] loud = [0, 24_000, 48_000, 72_000];
        long[] soft = [16_000, 40_000, 64_000, 88_000]; // swung eighths, a third of a beat late
        var audio = Material.Clicks(96_000, loud);
        var quiet = Material.Clicks(96_000, soft, 0.4, 2);
        for (var i = 0; i < audio.Length; i++) {
            audio[i] += quiet[i];
        }

        var onsets = GrooveFromAudio.Onsets(audio, rate);
        long[] truth = [..loud.Concat(soft).Order()];
        Assert.Equal(truth.Length, onsets.Length);
        for (var i = 0; i < truth.Length; i++) {
            Assert.True(Math.Abs(onsets[i].Frame - truth[i]) <= rate / 500, $"{onsets[i].Frame} vs {truth[i]}");
        }

        // The noise bursts' peaks vary, but every loud hit is stronger than every soft one, at about twice.
        var loudest = onsets.Where(o => loud.Contains(truth[Array.IndexOf(onsets, o)])).ToArray();
        var softest = onsets.Except(loudest).ToArray();
        Assert.True(loudest.Min(static o => o.Strength) > softest.Max(static o => o.Strength));
        Assert.InRange(softest.Average(static o => o.Strength) / loudest.Average(static o => o.Strength), 0.35, 0.65);
        Assert.Equal(1.0, onsets.Max(static o => o.Strength));
        Assert.Empty(GrooveFromAudio.Onsets(new float[48_000], rate));
    }
}
