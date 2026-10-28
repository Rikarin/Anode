using Anode.Dsp.Analysis;

namespace Anode.Dsp.Tests.Analysis;

/// <summary>Transient detection (docs/06-phase-5.md, "Analysis"): every hit within 3 ms, nothing else.</summary>
public sealed class TransientTests {
    const int Rate = Material.Rate;
    const long Tolerance = Rate * 3 / 1_000;

    /// <summary>Each of <paramref name="truth" /> is matched within 3 ms, and nothing else is found.</summary>
    static void AssertMatches(long[] truth, long[] found) {
        Assert.Equal(truth.Length, found.Length);
        for (var i = 0; i < truth.Length; i++) {
            Assert.True(
                Math.Abs(found[i] - truth[i]) <= Tolerance,
                $"onset {i}: found {found[i]}, expected {truth[i]} (±{Tolerance})"
            );
        }
    }

    [Fact]
    public void Clicks_are_found_to_the_millisecond() {
        var at = Enumerable.Range(0, 20).Select(static i => (long)(i * 0.25 * Rate + i * 37)).ToArray();
        var found = Transients.Detect(Material.Clicks(6 * Rate, at), Rate);
        AssertMatches(at, found);
        Assert.All(found.Zip(at), static p => Assert.InRange(p.First - p.Second, -Rate / 2_000, Rate / 2_000));
    }

    [Theory]
    [InlineData(70)]
    [InlineData(100)]
    [InlineData(140)]
    [InlineData(180)]
    public void Every_kick_and_hat_of_a_loop_is_found(double bpm) {
        var loop = Material.DrumLoop(bpm, 8);
        AssertMatches(loop.Onsets, Transients.Detect(loop.Audio, Rate));
    }

    [Fact]
    public void A_humanised_loop_is_found_as_played() {
        var loop = Material.DrumLoop(100, 8, humaniseMs: 10);
        AssertMatches(loop.Onsets, Transients.Detect(loop.Audio, Rate));
    }

    [Fact]
    public void Notes_starting_after_silence_are_found_and_vibrato_is_not() {
        var phrase = Material.Phrase();
        var found = Transients.Detect(phrase.Audio, Rate);

        // Every note's start: within 3 ms after silence, within 10 ms where it follows another legato (no attack).
        Assert.Equal(phrase.Notes.Length, found.Length);
        for (var i = 0; i < found.Length; i++) {
            var legato = i > 0 && phrase.Notes[i - 1].End == phrase.Notes[i].Start;
            Assert.InRange(Math.Abs(found[i] - phrase.Notes[i].Start), 0, legato ? Rate / 100 : Tolerance);
        }
    }

    [Fact]
    public void Silence_has_no_transients_and_noise_only_its_start() {
        Assert.Empty(Transients.Detect(new float[Rate * 3], Rate));
        Assert.Empty(Transients.Detect([], Rate));
        Assert.Equal([0L], Transients.Detect(Material.Noise(5), Rate));
        Assert.Equal([0L], Transients.Detect(Material.Noise(5), Rate, 1));
    }

    [Fact]
    public void Sensitivity_trades_soft_hits_for_false_ones() {
        // Loud clicks and clicks 30 dB softer under a steady noise floor.
        var loud = Enumerable.Range(0, 8).Select(static i => (long)((0.1 + i * 0.5) * Rate)).ToArray();
        var soft = loud.Select(static t => t + Rate / 4).ToArray();
        var audio = Material.Clicks(5 * Rate, loud);
        var quiet = Material.Clicks(5 * Rate, soft, 0.8 * 0.0316, 2);
        var noise = Material.Noise(5, 0.001);
        for (var i = 0; i < audio.Length; i++) {
            audio[i] += quiet[i] + noise[i];
        }

        var low = Transients.Detect(audio, Rate, 0);
        var middle = Transients.Detect(audio, Rate);
        var high = Transients.Detect(audio, Rate, 1);
        Assert.True(low.Length <= middle.Length && middle.Length <= high.Length);
        Assert.All(loud, t => Assert.Contains(low, f => Math.Abs(f - t) <= Tolerance));
        Assert.All(loud.Concat(soft), t => Assert.Contains(high, f => Math.Abs(f - t) <= Tolerance));
    }

    [Fact]
    public void Other_sample_rates_work_alike() {
        const int rate = 44_100;
        var at = Enumerable.Range(0, 10).Select(i => (long)(i * 0.3 * rate + 11 * i)).ToArray();
        var found = Transients.Detect(Material.Clicks(4 * rate, at), rate);
        Assert.Equal(at.Length, found.Length);
        Assert.All(found.Zip(at), p => Assert.InRange(Math.Abs(p.First - p.Second), 0, rate * 3 / 1_000));
    }
}
