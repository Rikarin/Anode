using Anode.Dsp.Editing;
using Anode.Dsp.Tests.Analysis;
using Anode.Dsp.Tests.Support;

namespace Anode.Dsp.Tests.Editing;

/// <summary>
///     Loop search on periodic tones (a whole number of periods, to a tenth of a frame), a loop planted in noise (to the
///     frame), and noise (no good loop).
/// </summary>
public sealed class LoopSearchTests(ITestOutputHelper output) {
    const int Rate = Material.Rate;

    /// <summary>A tone of a few harmonics, so the waveform is not a plain sine.</summary>
    static float[] Tone(double frequency, double seconds) {
        var samples = new float[(int)(seconds * Rate)];
        for (var i = 0; i < samples.Length; i++) {
            var t = 2 * Math.PI * frequency * i / Rate;
            samples[i] = (float)(0.4 * Math.Sin(t) + 0.2 * Math.Sin(2 * t + 0.3) + 0.1 * Math.Sin(3 * t + 1.1));
        }

        return samples;
    }

    [Theory]
    [InlineData(480.0)] // a period of exactly 100 frames
    [InlineData(440.0)] // 109.09 frames
    [InlineData(97.3)] // 493.3 frames
    [InlineData(1_234.5)] // 38.88 frames
    public void A_periodic_tone_loops_on_a_whole_number_of_periods(double frequency) {
        var tone = Tone(frequency, 3);
        const long start = 12_345;
        const long end = start + 2 * Rate - 37; // not a whole number of periods of any of these
        var period = Rate / frequency;

        var loops = LoopSearch.Find(tone, Rate, start, end, 5, Rate / 10);

        Assert.Equal(5, loops.Length);
        var best = loops[0];
        var length = best.EndFrame - best.StartFrame;
        var periods = length / period;
        var error = Math.Abs(periods - Math.Round(periods)) * period; // frames off a whole number of periods
        output.WriteLine(
            $"{frequency} Hz: loop {best.StartFrame}…{best.EndFrame} ({periods:0.000} periods, {error:0.000} frames off), "
            + $"{end - best.EndFrame} frames before the range's end, score {best.Score:0.0000}"
        );
        Assert.Equal(start, best.StartFrame);
        Assert.True(error <= 0.25, $"{error} frames off a whole number of periods");
        if (period == Math.Round(period)) {
            // Every whole period is seamless: the longest loop wins, so the best ends within a period of the range's.
            Assert.True(end - best.EndFrame < period, $"{end - best.EndFrame} frames before the end");
        }

        Assert.True(best.Score > 0.95, $"score {best.Score}");
        for (var i = 1; i < loops.Length; i++) {
            Assert.True(loops[i].Score <= loops[i - 1].Score);
            Assert.True(loops[i].EndFrame - loops[i].StartFrame >= Rate / 10);
            for (var j = 0; j < i; j++) {
                Assert.True(Math.Abs(loops[i].EndFrame - loops[j].EndFrame) >= Rate / 1000);
            }
        }
    }

    [Fact]
    public void A_loop_planted_in_noise_is_found_to_the_frame() {
        var noise = Material.Noise(2);
        const int s = 20_000;
        const int truth = 61_234;
        const int half = 2_000;
        Array.Copy(noise, s - half, noise, truth - half, 2 * half);

        var loops = LoopSearch.Find(noise, Rate, s, 90_000, 3, 1_000);

        output.WriteLine($"planted: best {loops[0].EndFrame} (truth {truth}), score {loops[0].Score:0.0000}");
        Assert.Equal(truth, loops[0].EndFrame);
        Assert.True(loops[0].Score > 0.95);
        Assert.True(loops[1].Score < 0.5, $"runner-up {loops[1].Score}");
    }

    [Fact]
    public void Noise_has_no_good_loop() {
        var noise = Material.Noise(2, seed: 11);
        var loops = LoopSearch.Find(noise, Rate, 1_000, 90_000, 3, 4_800);
        output.WriteLine($"noise: best score {loops[0].Score:0.0000}");
        Assert.True(loops[0].Score < 0.3, $"score {loops[0].Score}");
    }

    [Fact]
    public void Search_is_deterministic_and_respects_the_range() {
        var tone = TestWav.Sine(440, Rate, 1);
        var a = LoopSearch.Find(tone, Rate, 100, 20_000, 10, 500);
        var b = LoopSearch.Find(tone, Rate, 100, 20_000, 10, 500);
        Assert.Equal(a, b);
        Assert.All(a, static l => Assert.InRange(l.EndFrame, 600, 20_000));
        Assert.Empty(LoopSearch.Find(tone, Rate, 100, 400, 3, 500));
        Assert.Empty(LoopSearch.Find(tone, Rate, 100, 20_000, 0, 500));
        Assert.Throws<ArgumentOutOfRangeException>(() => LoopSearch.Find(tone, Rate, 0, Rate + 1, 1, 10));
    }
}
