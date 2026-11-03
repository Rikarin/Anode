namespace Anode.Dsp.Analysis.Chords;

/// <summary>
///     One stretch Chord ID decodes (a beat, for MIDI and audio alike): its 12 pitch-class weights, its bass pitch class
///     (−1: none heard) and whether it holds any sound at all.
/// </summary>
readonly record struct ChordFrame(double[] Chroma, int Bass, bool Silent);

/// <summary>
///     What MIDI and audio recognition share (docs/12-phase-11.md, ADR-0014): every root and quality of
///     <see cref="ChordTemplates" /> (and no chord) scored against each frame's chroma, then a Viterbi pass over the
///     frames with a change penalty, a bass bonus and the key as a prior.
/// </summary>
/// <remarks>
///     <list type="bullet">
///         <item>
///             A chord's score is the cosine between the frame's chroma and its template (every tone weighted 1), so a
///             tone the chroma lacks and a tone the template lacks both cost; plus <see cref="RootBonus" /> when the bass
///             is its root (Am7 over C6, Dm7 over F6), <see cref="ToneBonus" /> when the bass is another of its tones (an
///             inversion), less <see cref="ForeignBass" /> when the bass is none of them; plus <see cref="KeyBonus" />
///             when every tone is in the key (its major scale, or its minor with the raised seventh).
///         </item>
///         <item>A silent frame scores 1 for no chord and 0 for any chord; a sounding one −1 for no chord.</item>
///         <item>The path maximises the scores' sum less <see cref="ChangePenalty" /> per change.</item>
///     </list>
/// </remarks>
static class ChordDecoder {
    public const int Qualities = 13;
    public const int Chords = 12 * Qualities;
    public const int NoChord = Chords;

    const double RootBonus = 0.08;
    const double ToneBonus = 0.02;
    const double ForeignBass = 0.04;
    const double KeyBonus = 0.03;
    const double ChangePenalty = 0.12;

    static readonly int[] MajorScale = [0, 2, 4, 5, 7, 9, 11];
    static readonly int[] MinorScale = [0, 2, 3, 5, 7, 8, 10, 11];

    /// <summary>Each chord state's tones as a 12-bit pitch-class mask (root r, quality q at r × 13 + q).</summary>
    static readonly int[] Masks = MakeMasks();

    static int[] MakeMasks() {
        var masks = new int[Chords];
        for (var r = 0; r < 12; r++) {
            for (var q = 0; q < Qualities; q++) {
                var mask = 0;
                foreach (var interval in ChordTemplates.Intervals(q)) {
                    mask |= 1 << ((r + interval) % 12);
                }

                masks[r * Qualities + q] = mask;
            }
        }

        return masks;
    }

    public static int Root(int state) => state / Qualities;

    public static int Quality(int state) => state % Qualities;

    /// <summary>The template's fit to a chroma alone (the cosine, 0…1): a decoded chord's confidence.</summary>
    public static double Fit(int state, double[] chroma) {
        var mask = Masks[state];
        double inside = 0, squares = 0;
        var tones = 0;
        for (var pc = 0; pc < 12; pc++) {
            squares += chroma[pc] * chroma[pc];
            if ((mask >> pc & 1) != 0) {
                inside += chroma[pc];
                tones++;
            }
        }

        return squares <= 0 ? 0 : inside / (Math.Sqrt(squares) * Math.Sqrt(tones));
    }

    static double Score(int state, ChordFrame frame, int keyMask) {
        if (state == NoChord) {
            return frame.Silent ? 1 : -1;
        }

        if (frame.Silent) {
            return 0;
        }

        var mask = Masks[state];
        var score = Fit(state, frame.Chroma);
        if (frame.Bass >= 0) {
            score += frame.Bass == Root(state) ? RootBonus : (mask >> frame.Bass & 1) != 0 ? ToneBonus : -ForeignBass;
        }

        if (keyMask != 0 && (mask & ~keyMask) == 0) {
            score += KeyBonus;
        }

        return score;
    }

    /// <summary>The key's tones as a mask (none without a key).</summary>
    static int KeyMask(DetectedMusicalKey? key) {
        if (key is null) {
            return 0;
        }

        var mask = 0;
        foreach (var step in key.Minor ? MinorScale : MajorScale) {
            mask |= 1 << ((key.Tonic + step) % 12);
        }

        return mask;
    }

    /// <summary>The most likely chord state of every frame (Viterbi), <see cref="NoChord" /> where nothing sounds.</summary>
    public static int[] Decode(IReadOnlyList<ChordFrame> frames, DetectedMusicalKey? key) {
        var count = frames.Count;
        var path = new int[count];
        if (count == 0) {
            return path;
        }

        const int states = Chords + 1;
        var keyMask = KeyMask(key);
        var score = new double[states];
        var next = new double[states];
        var back = new int[count * states];
        for (var s = 0; s < states; s++) {
            score[s] = Score(s, frames[0], keyMask);
        }

        for (var t = 1; t < count; t++) {
            // The best state to change from, once per frame: staying costs nothing, changing the penalty.
            var best = 0;
            for (var s = 1; s < states; s++) {
                if (score[s] > score[best]) {
                    best = s;
                }
            }

            for (var s = 0; s < states; s++) {
                var stay = score[s];
                var change = score[best] - ChangePenalty;
                var from = stay >= change ? s : best;
                next[s] = Math.Max(stay, change) + Score(s, frames[t], keyMask);
                back[t * states + s] = from;
            }

            (score, next) = (next, score);
        }

        var end = 0;
        for (var s = 1; s < states; s++) {
            if (score[s] > score[end]) {
                end = s;
            }
        }

        path[count - 1] = end;
        for (var t = count - 1; t > 0; t--) {
            path[t - 1] = back[t * states + path[t]];
        }

        return path;
    }

    /// <summary>
    ///     The decoded path as chords: neighbouring frames of one state merged, chords shorter than
    ///     <paramref name="minimum" /> folded into the one before (or after, for the first), no-chord stretches dropped;
    ///     a chord's bass is its first frame's (−1 when that is the root, or none), its confidence its frames' mean fit.
    /// </summary>
    public static List<RecognisedChord> Merge(
        IReadOnlyList<ChordFrame> frames,
        IReadOnlyList<long> bounds,
        int[] path,
        long minimum
    ) {
        var runs = new List<(int State, int First, int Last)>();
        for (var t = 0; t < path.Length; t++) {
            if (runs.Count > 0 && runs[^1].State == path[t]) {
                runs[^1] = runs[^1] with { Last = t };
            } else {
                runs.Add((path[t], t, t));
            }
        }

        // Too short: merged into a neighbour (the one before, else the one after).
        for (var k = 0; k < runs.Count && runs.Count > 1;) {
            var (state, first, last) = runs[k];
            if (state == NoChord || bounds[last + 1] - bounds[first] >= minimum) {
                k++;
                continue;
            }

            if (k > 0 && runs[k - 1].State != NoChord) {
                runs[k - 1] = runs[k - 1] with { Last = last };
                runs.RemoveAt(k);
            } else if (k + 1 < runs.Count && runs[k + 1].State != NoChord) {
                runs[k + 1] = runs[k + 1] with { First = first };
                runs.RemoveAt(k);
            } else {
                k++;
            }
        }

        var chords = new List<RecognisedChord>();
        foreach (var (state, first, last) in runs) {
            if (state == NoChord) {
                continue;
            }

            double fit = 0;
            for (var t = first; t <= last; t++) {
                fit += Fit(state, frames[t].Chroma);
            }

            var bass = frames[first].Bass;
            chords.Add(
                new(
                    bounds[first],
                    bounds[last + 1],
                    Root(state),
                    Quality(state),
                    bass == Root(state) ? -1 : bass,
                    Math.Clamp(fit / (last - first + 1), 0, 1)
                )
            );
        }

        return chords;
    }
}
