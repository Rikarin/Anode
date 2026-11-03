using Anode.Dsp.Analysis.Chords;
using Anode.Dsp.Instruments;

namespace Anode.Dsp.Tests.Analysis.Chords;

/// <summary>
///     Synthesized material for Chord ID, with its truth: chord progressions (the golden path's, and random ones) as MIDI
///     notes in several voicings, rendered through Anode's Electric Piano, with noise or drums added. Seeded.
/// </summary>
public static class ChordMaterial {
    public const int Rate = 48_000;
    public const int Beat = 960;
    public const int Bar = 4 * Beat;

    /// <summary>A chord of the truth: root and slash bass pitch classes (−1: none), quality index.</summary>
    public sealed record Truth(int Root, int Quality, int Bass);

    /// <summary>The golden path's progression (docs/12-phase-11.md, step 1): C, Am7, Fmaj7, G7, Em, A7, Dm7, G/B.</summary>
    public static Truth[] Progression { get; } = [
        new(0, Q(string.Empty), -1), new(9, Q("m7"), -1), new(5, Q("maj7"), -1), new(7, Q("7"), -1), new(4, Q("m"), -1),
        new(9, Q("7"), -1), new(2, Q("m7"), -1), new(7, Q(string.Empty), 11)
    ];

    public static int Q(string quality) => ChordTemplates.Qualities.ToList().IndexOf(quality);

    /// <summary>How a chord is played.</summary>
    public enum Voicing {
        /// <summary>The bass (root or slash bass) in octave 3, the chord's tones close above C4.</summary>
        Piano,

        /// <summary>The chord's tones stacked from its bass (or root) up from C4, no separate bass.</summary>
        Close,

        /// <summary>Piano, with the right hand inverted (its lowest tone the third or the fifth, by chord).</summary>
        UpperInversions,

        /// <summary>Close, rotated so the lowest note is the third (first inversion; the truth's bass becomes it).</summary>
        FirstInversion
    }

    /// <summary>The chord's tones as pitch classes, root first.</summary>
    static int[] Tones(Truth chord) =>
        [..ChordTemplates.Intervals(chord.Quality).ToArray().Select(i => (chord.Root + i) % 12)];

    /// <summary>The notes of a chord a bar, voiced, at velocity <paramref name="velocity" />.</summary>
    public static ChordNote[] Notes(IReadOnlyList<Truth> chords, Voicing voicing, int velocity = 90) {
        var notes = new List<ChordNote>();
        for (var bar = 0; bar < chords.Count; bar++) {
            var start = bar * (long)Bar;
            foreach (var pitch in Pitches(chords[bar], voicing, bar)) {
                notes.Add(new(start, Bar - 60, pitch, velocity));
            }
        }

        return [..notes];
    }

    /// <summary>The truth as played: first inversions take their third as the bass.</summary>
    public static Truth[] Heard(IReadOnlyList<Truth> chords, Voicing voicing) =>
        [..chords.Select(c => voicing == Voicing.FirstInversion ? c with { Bass = Tones(c)[1] } : c)];

    static int[] Pitches(Truth chord, Voicing voicing, int bar) {
        var tones = Tones(chord);
        var bass = chord.Bass >= 0 ? chord.Bass : chord.Root;
        switch (voicing) {
            case Voicing.Piano:
                return [48 + bass, ..Stack(tones, 60)];
            case Voicing.UpperInversions:
                var rotation = 1 + bar % 2;
                return [48 + bass, ..Stack([..tones.Skip(rotation), ..tones.Take(rotation)], 60)];
            case Voicing.FirstInversion:
                return Stack([..tones.Skip(1), tones[0]], 55);
            default:
                return chord.Bass >= 0 ? Stack([bass, ..tones.Where(t => t != bass)], 55) : Stack(tones, 55);
        }
    }

    /// <summary>Pitch classes stacked upward, the first at or above <paramref name="from" />.</summary>
    static int[] Stack(IReadOnlyList<int> classes, int from) {
        var pitches = new int[classes.Count];
        var at = from + ((classes[0] - from) % 12 + 12) % 12;
        for (var k = 0; k < classes.Count; k++) {
            if (k > 0) {
                at += 1 + ((classes[k] - at - 1) % 12 + 12) % 12;
            }

            pitches[k] = at;
        }

        return pitches;
    }

    /// <summary>
    ///     MIDI made untidy: every note's start and end moved by up to a 32nd, velocities 50–120, and a passing note (a
    ///     scale step of C major, an eighth long) on the last eighth of every other bar.
    /// </summary>
    public static ChordNote[] Humanised(ChordNote[] notes, int seed) {
        var random = new Random(seed);
        var result = notes.Select(n => {
                    var start = Math.Max(0, n.StartTick + random.Next(-120, 121));
                    return n with {
                        StartTick = start,
                        LengthTicks = n.StartTick + n.LengthTicks + random.Next(-120, 121) - start,
                        Velocity = random.Next(50, 121)
                    };
                }
            )
            .ToList();
        int[] scale = [0, 2, 4, 5, 7, 9, 11];
        var bars = (int)(notes.Max(static n => n.StartTick) / Bar) + 1;
        for (var bar = 0; bar < bars; bar += 2) {
            result.Add(new(bar * (long)Bar + Bar - Beat / 2, Beat / 2, 72 + scale[random.Next(7)], 70));
        }

        return [..result];
    }

    /// <summary>Random chords (triads and sevenths, any root), some with a slash bass that is one of their tones.</summary>
    public static Truth[] RandomProgression(Random random, int count) {
        string[] qualities = [string.Empty, "m", "7", "maj7", "m7", "dim", "m7b5", "sus4", "6", "m6"];
        return [
            ..Enumerable.Range(0, count)
                .Select(_ => {
                        var root = random.Next(12);
                        var quality = Q(qualities[random.Next(qualities.Length)]);
                        var tones = ChordTemplates.Intervals(quality).ToArray();
                        var bass = random.Next(4) == 0 ? (root + tones[1 + random.Next(tones.Length - 1)]) % 12 : -1;
                        return new Truth(root, quality, bass);
                    }
                )
        ];
    }

    /// <summary>Beat ticks over <paramref name="bars" /> bars, the last the end.</summary>
    public static long[] BeatTicks(int bars) =>
        [..Enumerable.Range(0, bars * 4 + 1).Select(static b => (long)b * Beat)];

    /// <summary>Beat frames over <paramref name="bars" /> bars at <paramref name="bpm" />, the last the end.</summary>
    public static long[] BeatFrames(int bars, double bpm) =>
        [..Enumerable.Range(0, bars * 4 + 1).Select(b => (long)Math.Round(b * 60.0 / bpm * Rate))];

    /// <summary><paramref name="notes" /> (ticks at <paramref name="bpm" />) played by the Electric Piano, mono.</summary>
    public static float[] Render(IReadOnlyList<ChordNote> notes, double bpm) {
        const int block = 256;
        var framesPerTick = 60.0 / bpm / Beat * Rate;
        var events = new List<(long Frame, int Rank, MidiEvent Event)>();
        foreach (var n in notes) {
            events.Add(
                ((long)Math.Round(n.StartTick * framesPerTick), 1, new(0, 0x90, (byte)n.Pitch, (byte)n.Velocity))
            );
            events.Add(
                ((long)Math.Round((n.StartTick + n.LengthTicks) * framesPerTick), 0, new(0, 0x80, (byte)n.Pitch, 0))
            );
        }

        events.Sort(static (a, b) => a.Frame != b.Frame ? a.Frame.CompareTo(b.Frame) : a.Rank.CompareTo(b.Rank));
        var frames = events[^1].Frame + Rate;
        using var piano = BuiltinInstruments.Create("anode.electric-piano")!;
        piano.Prepare(Rate, block);
        var mono = new float[frames];
        var left = new float[block];
        var right = new float[block];
        var pending = new List<MidiEvent>();
        var next = 0;
        for (long at = 0; at < frames; at += block) {
            var length = (int)Math.Min(block, frames - at);
            pending.Clear();
            while (next < events.Count && events[next].Frame < at + length) {
                pending.Add(events[next].Event with { Frame = (int)Math.Max(0, events[next].Frame - at) });
                next++;
            }

            piano.Process(pending.ToArray(), left.AsSpan(0, length), right.AsSpan(0, length));
            for (var i = 0; i < length; i++) {
                mono[at + i] = 0.5f * (left[i] + right[i]);
            }
        }

        return mono;
    }

    /// <summary>White noise added at <paramref name="snrDb" /> below the signal's RMS.</summary>
    public static float[] WithNoise(float[] audio, double snrDb, int seed) {
        var random = new Random(seed);
        var rms = Math.Sqrt(audio.Average(static s => (double)s * s));
        var level = rms * Math.Pow(10, -snrDb / 20) * Math.Sqrt(3);
        return [..audio.Select(s => (float)(s + level * (random.NextDouble() * 2 - 1)))];
    }

    /// <summary>A kick and hat loop (the analyses' drum material) mixed in at <paramref name="gain" />.</summary>
    public static float[] WithDrums(float[] audio, double bpm, int bars, float gain) {
        var drums = Material.DrumLoop(bpm, bars).Audio;
        var mixed = (float[])audio.Clone();
        for (var i = 0; i < Math.Min(mixed.Length, drums.Length); i++) {
            mixed[i] += gain * drums[i];
        }

        return mixed;
    }

    /// <summary>
    ///     How many truths <paramref name="found" /> gets right: the chord found at each bar's start ± an eighth (or
    ///     holding over it), its root and quality (and its bass, when <paramref name="bass" />); <paramref name="unit" />
    ///     ticks or frames per bar. With <paramref name="equivalent" />, a chord of the same tones over the same bass is
    ///     right too (C6 for Am7/C: the same notes).
    /// </summary>
    public static int Correct(
        IReadOnlyList<Truth> truth,
        IReadOnlyList<RecognisedChord> found,
        long unit,
        bool bass,
        bool equivalent = false
    ) {
        var right = 0;
        for (var bar = 0; bar < truth.Count; bar++) {
            var at = bar * unit;
            var chord = found.FirstOrDefault(c =>
                Math.Abs(c.Start - at) <= unit / 8 || c.Start < at && c.End > at + unit / 2
            );
            if (chord is null) {
                continue;
            }

            var t = truth[bar];
            var lowest = t.Bass >= 0 ? t.Bass : t.Root;
            var same = chord.Root == t.Root
                && chord.Quality == t.Quality
                && (!bass || chord.Bass == (lowest == t.Root ? -1 : lowest));
            var alike = equivalent
                && Mask(chord.Root, chord.Quality) == Mask(t.Root, t.Quality)
                && (chord.Bass >= 0 ? chord.Bass : chord.Root) == lowest;
            right += same || alike ? 1 : 0;
        }

        return right;
    }

    static int Mask(int root, int quality) =>
        ChordTemplates.Intervals(quality).ToArray().Aggregate(0, (mask, i) => mask | 1 << ((root + i) % 12));
}
