using Anode.Dsp.Analysis;

namespace Anode.Dsp.Tests.Analysis;

/// <summary>Beat tracking (docs/06-phase-5.md, "Analysis"): tempo within 0.5 bpm, beats within 10 ms, downbeats.</summary>
public sealed class BeatTrackerTests {
    const int Rate = Material.Rate;
    const long Tolerance = Rate / 100;

    /// <summary>Every beat of <paramref name="truth" /> has a tracked beat within 10 ms, and no other beat is tracked.</summary>
    static void AssertBeats(long[] truth, long[] found) {
        Assert.Equal(truth.Length, found.Length);
        for (var i = 0; i < truth.Length; i++) {
            Assert.True(Math.Abs(found[i] - truth[i]) <= Tolerance, $"beat {i}: found {found[i]}, expected {truth[i]}");
        }
    }

    [Theory]
    [InlineData(70)]
    [InlineData(90)]
    [InlineData(100)]
    [InlineData(110)]
    [InlineData(128)]
    [InlineData(140)]
    [InlineData(160)]
    public void A_steady_loop_has_its_tempo_beats_and_downbeats(double bpm) {
        var loop = Material.DrumLoop(bpm, 8);
        var track = BeatTracker.Track(loop.Audio, Rate);
        Assert.InRange(track.Bpm, bpm - 0.5, bpm + 0.5);
        Assert.False(track.Variable);
        AssertBeats(loop.Beats, track.Beats);
        AssertBeats(loop.Downbeats, track.Downbeats);
        Assert.InRange(track.Confidence, 0.9, 1);
    }

    [Theory]
    [InlineData(100, 1)]
    [InlineData(100, 2)]
    [InlineData(128, 3)]
    public void A_humanised_loop_keeps_its_tempo(double bpm, int seed) {
        var loop = Material.DrumLoop(bpm, 8, humaniseMs: 10, seed: seed);
        var track = BeatTracker.Track(loop.Audio, Rate);
        Assert.InRange(track.Bpm, bpm - 0.5, bpm + 0.5);
        Assert.False(track.Variable);
        AssertBeats(loop.Beats, track.Beats);
        AssertBeats(loop.Downbeats, track.Downbeats);
    }

    [Theory]
    [InlineData(90, 110)]
    [InlineData(120, 100)]
    public void A_tempo_ramp_is_followed_and_flagged(double from, double to) {
        var loop = Material.DrumLoop(from, 16, to);
        var track = BeatTracker.Track(loop.Audio, Rate);
        Assert.True(track.Variable);
        AssertBeats(loop.Beats, track.Beats);
        AssertBeats(loop.Downbeats, track.Downbeats);
        var average = 60.0 * Rate * (loop.Beats.Length - 1) / (loop.Beats[^1] - loop.Beats[0]);
        Assert.InRange(track.Bpm, average - 0.5, average + 0.5);
    }

    [Fact]
    public void Octave_errors_are_rare_between_60_and_180_bpm() {
        var tempi = Enumerable.Range(0, 25).Select(static i => 60 + 5.0 * i).ToArray();
        var errors = tempi.Count(static bpm =>
            Math.Abs(BeatTracker.Track(Material.DrumLoop(bpm, 8).Audio, Rate).Bpm - bpm) > 0.5
        );
        Assert.InRange(errors, 0, 5); // measured: 60 and 65 doubled, 170 to 180 halved (docs/analysis/README.md)
    }

    [Fact]
    public void Silence_and_noise_have_no_beats() {
        foreach (var audio in new[] { new float[Rate * 10], [], Material.Noise(1) }) {
            var track = BeatTracker.Track(audio, Rate);
            Assert.Empty(track.Beats);
            Assert.Empty(track.Downbeats);
            Assert.Equal(0, track.Confidence);
        }
    }
}
