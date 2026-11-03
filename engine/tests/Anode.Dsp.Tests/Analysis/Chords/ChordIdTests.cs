using Anode.Dsp.Analysis.Chords;
using static Anode.Dsp.Tests.Analysis.Chords.ChordMaterial;

namespace Anode.Dsp.Tests.Analysis.Chords;

/// <summary>
///     Chord ID (docs/12-phase-11.md, exit criterion step 1; docs/generative/README.md): MIDI and audio recognition on
///     synthesized material and key detection, against the golden path's thresholds (7 of 8 on its progression).
/// </summary>
public sealed class ChordIdTests {
    const double Bpm = 120;
    const long BarFrames = Rate * 2;

    static IReadOnlyList<RecognisedChord> Midi(ChordNote[] notes, int bars, DetectedMusicalKey? key = null) =>
        MidiChordRecognizer.Recognise(notes, BeatTicks(bars), Beat, key);

    static IReadOnlyList<RecognisedChord> Audio(float[] audio, int bars, bool trackBeats = false) =>
        AudioChordRecognizer.Recognise(audio, Rate, trackBeats ? [] : BeatFrames(bars, Bpm));

    [Theory]
    [InlineData(Voicing.Piano)]
    [InlineData(Voicing.Close)]
    [InlineData(Voicing.UpperInversions)]
    [InlineData(Voicing.FirstInversion)]
    public void Midi_finds_the_golden_paths_progression_with_its_basses(Voicing voicing) {
        var found = Midi(Notes(Progression, voicing), 8);
        // A first inversion of a seventh chord is a sixth chord's root position (Am7/C is C6): the same notes count.
        Assert.True(
            Correct(Heard(Progression, voicing), found, Bar, true, voicing == Voicing.FirstInversion) >= 7,
            Describe(found, Bar)
        );
        Assert.All(found, static c => Assert.Equal(0, c.Start % Beat));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Midi_finds_the_progression_through_loose_timing_and_passing_notes(int seed) {
        var found = Midi(Humanised(Notes(Progression, Voicing.Piano), seed), 8);
        Assert.True(Correct(Progression, found, Bar, true) >= 7, Describe(found, Bar));
    }

    [Fact]
    public void Midi_finds_random_progressions() {
        var (right, total) = RandomMidi(20, 8);
        Assert.True(right >= 0.9 * total, $"{right} of {total}");
    }

    /// <summary>MIDI accuracy over <paramref name="runs" /> random progressions of <paramref name="bars" /> chords.</summary>
    internal static (int Right, int Total) RandomMidi(int runs, int bars) {
        var right = 0;
        for (var seed = 0; seed < runs; seed++) {
            var truth = RandomProgression(new(seed), bars);
            var voicing = (Voicing)(seed % 3);
            right += Correct(truth, Midi(Notes(truth, voicing), bars), Bar, true, true);
        }

        return (right, runs * bars);
    }

    [Fact]
    public void Midi_merges_repeated_chords_and_skips_silence() {
        Truth[] truth = [new(0, Q(string.Empty), -1), new(0, Q(string.Empty), -1), new(7, Q("7"), -1)];
        var notes = Notes(truth, Voicing.Piano).Where(static n => n.StartTick != Bar).ToArray();
        var found = Midi(notes, 3);
        Assert.Equal(2, found.Count);
        Assert.Equal((0L, (long)Bar), (found[0].Start, found[0].End));
        Assert.Equal((2L * Bar, 3L * Bar), (found[1].Start, found[1].End));
        Assert.Equal((7, Q("7"), -1), (found[1].Root, found[1].Quality, found[1].Bass));
        Assert.All(found, static c => Assert.InRange(c.Confidence, 0.9, 1));
        Assert.Empty(MidiChordRecognizer.Recognise([], BeatTicks(2), Beat));
    }

    [Fact]
    public void Midi_reports_nothing_shorter_than_its_resolution() {
        // A chord a beat, read at a resolution of two beats.
        Truth[] truth = [
            new(0, Q(string.Empty), -1), new(5, Q(string.Empty), -1), new(7, Q(string.Empty), -1),
            new(0, Q(string.Empty), -1)
        ];
        var notes = Notes(truth, Voicing.Piano)
            .Select(static n => n with { StartTick = n.StartTick / 4, LengthTicks = Beat - 20 })
            .ToArray();
        var found = MidiChordRecognizer.Recognise(notes, BeatTicks(1), 2 * Beat);
        Assert.All(found, static c => Assert.True(c.End - c.Start >= 2 * Beat));
    }

    [Theory]
    [InlineData(Voicing.Piano)]
    [InlineData(Voicing.Close)]
    [InlineData(Voicing.UpperInversions)]
    public void Audio_finds_the_golden_paths_progression_on_the_electric_piano(Voicing voicing) {
        var audio = Render(Notes(Progression, voicing), Bpm);
        var found = Audio(audio, 8);
        Assert.True(Correct(Progression, found, BarFrames, false) >= 7, Describe(found, BarFrames));
        // The bass too, which the golden path asks of MIDI only.
        Assert.True(Correct(Progression, found, BarFrames, true) >= 7, Describe(found, BarFrames));
    }

    [Theory]
    [InlineData(20)]
    [InlineData(15)]
    public void Audio_finds_the_progression_through_noise(double snrDb) {
        var audio = WithNoise(Render(Notes(Progression, Voicing.Piano), Bpm), snrDb, 5);
        var found = Audio(audio, 8);
        Assert.True(Correct(Progression, found, BarFrames, false) >= 7, Describe(found, BarFrames));
    }

    [Fact]
    public void Audio_finds_the_progression_over_drums_on_the_beat_trackers_beats() {
        var audio = WithDrums(Render(Notes(Progression, Voicing.Piano), Bpm), Bpm, 8, 0.5f);
        var found = Audio(audio, 8, true);
        Assert.True(Correct(Progression, found, BarFrames, false) >= 7, Describe(found, BarFrames));
    }

    [Fact]
    public void Audio_hears_a_detuned_piano() {
        // A quarter tone flat is the worst case for the tuning estimate; 30 cents is a realistic one.
        var notes = Notes(Progression, Voicing.Piano);
        var audio = Render(notes, Bpm);
        var detuned = new float[(int)(audio.Length / Math.Pow(2, -30 / 1200.0))];
        for (var i = 0; i < detuned.Length; i++) {
            var x = i * Math.Pow(2, -30 / 1200.0);
            var k = (int)x;
            detuned[i] = k + 1 < audio.Length ? (float)(audio[k] + (x - k) * (audio[k + 1] - audio[k])) : 0;
        }

        var beats = BeatFrames(8, Bpm * Math.Pow(2, -30 / 1200.0));
        var found = AudioChordRecognizer.Recognise(detuned, Rate, beats);
        Assert.True(Correct(Progression, found, beats[4], false) >= 7, Describe(found, beats[4]));
    }

    [Fact]
    public void Keys_are_found_in_notes_and_audio() {
        var c = KeyDetector.FromNotes(Notes(Progression, Voicing.Piano));
        Assert.Equal((0, false), (c.Tonic, c.Minor));
        Assert.InRange(c.Confidence, 0.5, 1);
        // A minor: Am, Dm, E7, Am.
        Truth[] minor = [new(9, Q("m"), -1), new(2, Q("m"), -1), new(4, Q("7"), -1), new(9, Q("m"), -1)];
        var a = KeyDetector.FromNotes(Notes(minor, Voicing.Piano));
        Assert.Equal((9, true), (a.Tonic, a.Minor));
        var audio = KeyDetector.FromAudio(Render(Notes(Progression, Voicing.Piano), Bpm), Rate);
        Assert.Equal((0, false), (audio.Tonic, audio.Minor));
        Assert.Equal(0, KeyDetector.FromNotes([]).Confidence);
    }

    [Fact]
    public void Recognition_is_deterministic() {
        var notes = Humanised(Notes(Progression, Voicing.Piano), 9);
        Assert.Equal(Midi(notes, 8), Midi(notes, 8));
        var audio = WithNoise(Render(Notes(Progression, Voicing.Close), Bpm), 15, 2);
        Assert.Equal(Audio(audio, 8), Audio(audio, 8));
    }

    static string Describe(IReadOnlyList<RecognisedChord> found, long unit) =>
        string.Join(
            ", ",
            found.Select(c => $"{c.Start / (double)unit:0.##}: {c.Root} {ChordTemplates.Qualities[c.Quality]}/{c.Bass}")
        );
}
