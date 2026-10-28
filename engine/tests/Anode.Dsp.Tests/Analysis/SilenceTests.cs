using Anode.Dsp.Analysis;

namespace Anode.Dsp.Tests.Analysis;

/// <summary>Silence detection (Strip Silence; docs/06-phase-5.md, "Analysis"): an RMS gate.</summary>
public sealed class SilenceTests {
    const int Rate = Material.Rate;

    static float[] Bursts(params (double Start, double Length, double Amplitude)[] bursts) {
        var audio = new float[Rate * 3];
        foreach (var (start, length, amplitude) in bursts) {
            for (var n = (int)(start * Rate); n < (int)((start + length) * Rate); n++) {
                audio[n] = (float)(amplitude * Math.Sin(2 * Math.PI * 220 * n / Rate));
            }
        }

        return audio;
    }

    static long Ms(double ms) => (long)Math.Round(ms * Rate / 1_000);

    [Fact]
    public void A_phrase_splits_at_its_silences() {
        var phrase = Material.Phrase();
        var ranges = Silence.Detect(phrase.Audio, Rate, -40, 50, 0, 0);

        // Legato notes share a range; every other note has its own.
        var groups = new List<(long Start, long End)>();
        foreach (var (start, end, _) in phrase.Notes) {
            if (groups.Count > 0 && groups[^1].End == start) {
                groups[^1] = (groups[^1].Start, end);
            } else {
                groups.Add((start, end));
            }
        }

        Assert.Equal(groups.Count, ranges.Length);
        for (var i = 0; i < groups.Count; i++) {
            Assert.InRange(ranges[i].Start, groups[i].Start - Ms(10), groups[i].Start + Ms(10));
            Assert.InRange(ranges[i].End, groups[i].End - Ms(10), groups[i].End + Ms(10));
        }
    }

    [Fact]
    public void Ranges_are_widened_merged_and_short_ones_dropped() {
        // −6 dBFS bursts at 0.5 s (300 ms) and 1.0 s (300 ms); a 20 ms click at 2 s; a −50 dBFS hum at 2.5 s.
        var audio = Bursts((0.5, 0.3, 0.7), (1.0, 0.3, 0.7), (2.0, 0.02, 0.7), (2.5, 0.3, 0.0045));
        var plain = Silence.Detect(audio, Rate, -40, 50, 0, 0);
        Assert.Equal(2, plain.Length);
        Assert.InRange(plain[0].Start, Ms(495), Ms(500));
        Assert.InRange(plain[0].End, Ms(800), Ms(805));

        // A 250 ms post-release reaches the second burst's pre-attack: one range.
        var merged = Silence.Detect(audio, Rate, -40, 50, 20, 250);
        Assert.Single(merged);
        Assert.InRange(merged[0].Start, Ms(475), Ms(480));
        Assert.InRange(merged[0].End, Ms(1_550), Ms(1_555));

        // No minimum time keeps the click; a lower threshold finds the hum.
        Assert.Equal(3, Silence.Detect(audio, Rate, -40, 0, 0, 0).Length);
        Assert.Equal(4, Silence.Detect(audio, Rate, -60, 0, 0, 0).Length);
    }

    [Fact]
    public void Edges_clamp_to_the_file() {
        var loud = Bursts((0, 3, 0.5));
        Assert.Equal([(0L, (long)loud.Length)], Silence.Detect(loud, Rate, -40, 50, 100, 100));
        Assert.Empty(Silence.Detect(new float[Rate], Rate, -40, 50, 10, 10));
        Assert.Empty(Silence.Detect([], Rate, -40, 50, 10, 10));
    }
}
