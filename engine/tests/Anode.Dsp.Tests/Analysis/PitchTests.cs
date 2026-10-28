using Anode.Dsp.Analysis;

namespace Anode.Dsp.Tests.Analysis;

/// <summary>
///     Pitch tracking, note segmentation and the polyphony estimate (docs/06-phase-5.md, "Analysis"): pitch within 5 cents
///     on voiced hops, voicing F1, note boundaries within 20 ms.
/// </summary>
public sealed class PitchTests {
    const int Rate = Material.Rate;
    const int Hop = 240; // 5 ms

    /// <summary>
    ///     The voiced hops' errors in cents and the voicing F1. Errors skip each note's first and last 40 ms, where the
    ///     analysis window (up to 40 ms at 50 Hz) straddles the note's attack or release.
    /// </summary>
    internal static (List<double> Errors, double F1) Score(Material.Melody melody, PitchTrack track) {
        var errors = new List<double>();
        int both = 0, onlyFound = 0, onlyTrue = 0;
        var edge = 0.04 * Rate;
        for (var k = 0; k < track.Cents.Length; k++) {
            var at = (long)k * track.Hop;
            var truth = at < melody.Cents.Length ? melody.Cents[at] : double.NaN;
            var found = track.Cents[k];
            var isTrue = !double.IsNaN(truth);
            var isFound = !float.IsNaN(found);
            if (isTrue && isFound) {
                both++;
                if (melody.Notes.All(n => Math.Abs(at - n.Start) >= edge && Math.Abs(at - n.End) >= edge)) {
                    errors.Add(Math.Abs(found - truth));
                }
            } else if (isFound) {
                onlyFound++;
            } else if (isTrue) {
                onlyTrue++;
            }
        }

        return (errors, 2.0 * both / (2 * both + onlyFound + onlyTrue));
    }

    [Fact]
    public void A_phrase_with_vibrato_and_glides_is_within_5_cents() {
        var phrase = Material.Phrase();
        var track = PitchTracker.Track(phrase.Audio, Rate, Hop);
        Assert.Equal(Hop, track.Hop);
        Assert.Equal((phrase.Audio.Length + Hop - 1) / Hop, track.Cents.Length);
        Assert.Equal(track.Cents.Length, track.Voicing.Length);
        var (errors, f1) = Score(phrase, track);
        Assert.True(errors.Count > 500);
        Assert.All(errors, static e => Assert.InRange(e, 0, 5));
        Assert.InRange(f1, 0.97, 1);
    }

    [Theory]
    [InlineData(3_300)] // A1, 55 Hz
    [InlineData(4_500)]
    [InlineData(6_013)]
    [InlineData(6_900)]
    [InlineData(8_100)]
    [InlineData(9_300)] // A6, 1,760 Hz
    public void Steady_tones_across_the_range_are_within_5_cents(double cents) {
        foreach (var partials in new[] { 1, 6 }) {
            var tone = Material.Tones(1, [new(0.1, 0.8, cents)], partials);
            var (errors, f1) = Score(tone, PitchTracker.Track(tone.Audio, Rate, Hop));
            Assert.All(errors, static e => Assert.InRange(e, 0, 5));
            Assert.InRange(f1, 0.95, 1);
        }
    }

    [Fact]
    public void Other_hops_and_sample_rates_work_alike() {
        const int rate = 44_100;
        var tone = Material.Tones(1, [new(0.1, 0.8, 6_900)]);
        var audio = tone.Audio; // 440 Hz at 48 kHz reads as 404.25 Hz at 44.1 kHz
        var expected = 6_900 + 1_200 * Math.Log2(44_100.0 / 48_000);
        var track = PitchTracker.Track(audio, rate, 256);
        var middle = track.Cents[(int)(0.5 * rate / 256)];
        Assert.InRange(middle, expected - 5, expected + 5);
    }

    [Fact]
    public void Silence_and_noise_are_unvoiced() {
        var silent = PitchTracker.Track(new float[Rate], Rate, Hop);
        Assert.All(silent.Cents, static c => Assert.True(float.IsNaN(c)));
        Assert.All(silent.Voicing, static v => Assert.Equal(0, v));

        var noise = PitchTracker.Track(Material.Noise(2), Rate, Hop);
        Assert.InRange(noise.Cents.Count(static c => !float.IsNaN(c)), 0, noise.Cents.Length / 20);
        Assert.Empty(PitchTracker.Track([], Rate, Hop).Cents);
    }

    [Fact]
    public void Notes_are_segmented_within_20_ms() {
        var phrase = Material.Phrase();
        var notes = NoteSegmenter.Segment(PitchTracker.Track(phrase.Audio, Rate, Hop), Rate);
        Assert.Equal(phrase.Notes.Length, notes.Length);
        const long tolerance = Rate / 50;
        for (var i = 0; i < notes.Length; i++) {
            var (start, end, cents) = phrase.Notes[i];
            Assert.InRange(notes[i].StartFrame, start - tolerance, start + tolerance);
            Assert.InRange(notes[i].EndFrame, end - tolerance, end + tolerance);
            Assert.InRange(notes[i].Cents, cents - 10, cents + 10);
            Assert.InRange(notes[i].Confidence, 0.8, 1);
        }
    }

    [Fact]
    public void Repeated_notes_split_at_their_gaps_and_steps_without_gaps_split_too() {
        var melody = Material.Tones(
            2,
            [
                new(0.1, 0.3, 6_000),
                new(0.45, 0.3, 6_000), // the same pitch after a 50 ms gap
                new(0.75, 0.3, 6_100), // a semitone up, legato
                new(1.05, 2 / 5.5, 5_800, 30) // a minor third down with two cycles of vibrato, legato
            ]
        );
        var notes = NoteSegmenter.Segment(PitchTracker.Track(melody.Audio, Rate, Hop), Rate);
        Assert.Equal(4, notes.Length);
        for (var i = 0; i < notes.Length; i++) {
            Assert.InRange(notes[i].StartFrame, melody.Notes[i].Start - Rate / 50, melody.Notes[i].Start + Rate / 50);
            Assert.InRange(notes[i].Cents, melody.Notes[i].Cents - 10, melody.Notes[i].Cents + 10);
        }
    }

    [Fact]
    public void A_chord_and_noise_are_polyphonic_and_a_melody_is_not() {
        var phrase = Material.Phrase();
        Assert.False(Polyphony.IsPolyphonic(phrase.Audio, Rate, PitchTracker.Track(phrase.Audio, Rate, Hop)));
        var chord = Material.Chord(2);
        Assert.True(Polyphony.IsPolyphonic(chord, Rate, PitchTracker.Track(chord, Rate, Hop)));
        var noise = Material.Noise(1);
        Assert.True(Polyphony.IsPolyphonic(noise, Rate, PitchTracker.Track(noise, Rate, Hop)));
        Assert.False(Polyphony.IsPolyphonic(new float[Rate], Rate, PitchTracker.Track(new float[Rate], Rate, Hop)));
    }
}
