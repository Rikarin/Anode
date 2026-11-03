using Anode.Contracts;

namespace Anode.Generative;

/// <summary>
///     The Keyboard Player's voicings: a chord's tones stacked close (four voices, a triad's root doubled, a ninth
///     without its fifth), spread (close with its second voice from the top dropped an octave) or as a shell (the bass,
///     third and seventh), every placement inside the range, the one chosen that moves least from the voicing before
///     (no voice by more than a fifth when any placement allows), near the range's middle for the first.
/// </summary>
static class Voicer {
    const int MaxMove = 7;

    public static int[] Voice(Harmony harmony, KeysVoicing voicing, int low, int high, int[]? previous) {
        var candidates = Candidates(harmony, voicing, low, high);
        if (candidates.Count == 0 && voicing != KeysVoicing.Close) {
            candidates = Candidates(harmony, KeysVoicing.Close, low, high);
        }

        if (candidates.Count == 0) {
            candidates = Fallback(harmony, low);
        }

        var middle = (low + high) / 2.0;
        int[]? best = null;
        var bestCost = double.MaxValue;
        foreach (var candidate in candidates) {
            var cost = Cost(previous, candidate, middle);
            if (cost < bestCost) {
                (best, bestCost) = (candidate, cost);
            }
        }

        return best!;
    }

    /// <summary>
    ///     A placement's cost: from no voicing, its distance from the range's middle; else how far its voices move, much
    ///     more when one moves by more than a fifth, and a quarter of its distance from the middle.
    /// </summary>
    static double Cost(int[]? previous, int[] candidate, double middle) {
        var off = Math.Abs(candidate.Average() - middle);
        if (previous is null) {
            return off;
        }

        var movement = Movement(previous, candidate, out var largest);
        return movement + (largest > MaxMove ? 1_000 : 0) + 0.25 * off;
    }

    /// <summary>
    ///     How far <paramref name="to" />'s voices move from <paramref name="from" />'s: voice by voice when they have as
    ///     many, else each voice to the nearest of the other's (both ways); <paramref name="largest" /> the largest move.
    /// </summary>
    public static int Movement(int[] from, int[] to, out int largest) {
        largest = 0;
        var total = 0;
        if (from.Length == to.Length) {
            for (var k = 0; k < from.Length; k++) {
                var move = Math.Abs(from[k] - to[k]);
                total += move;
                largest = Math.Max(largest, move);
            }

            return total;
        }

        foreach (var (a, b) in (ReadOnlySpan<(int[], int[])>)[(from, to), (to, from)]) {
            foreach (var p in a) {
                var move = b.Min(q => Math.Abs(p - q));
                total += move;
                largest = Math.Max(largest, move);
            }
        }

        return total;
    }

    /// <summary>The chord's tones (pitch classes) for a voicing, bottom first in the order they stack.</summary>
    static int[] Tones(Harmony harmony, KeysVoicing voicing) {
        var i = harmony.Intervals;
        int Pc(int interval) => Harmony.Mod12(harmony.Root + interval);

        if (voicing == KeysVoicing.Shell) {
            var (third, seventh) = (Pc(i[1]), Pc(i.Count >= 4 ? i[3] : i[2]));
            // A slash bass that is the third or seventh already gives way to the root: three tones, all different.
            return [harmony.Bass == third || harmony.Bass == seventh ? harmony.Root : harmony.Bass, third, seventh];
        }

        return i.Count switch {
            3 => [Pc(i[0]), Pc(i[1]), Pc(i[2])],
            4 => [Pc(i[0]), Pc(i[1]), Pc(i[2]), Pc(i[3])],
            _ => [Pc(i[0]), Pc(i[1]), Pc(i[3]), Pc(i[4])]
        };
    }

    static List<int[]> Candidates(Harmony harmony, KeysVoicing voicing, int low, int high) {
        var placed = new List<int[]>();
        foreach (var shape in Shapes(Tones(harmony, voicing), voicing)) {
            // Every octave placement inside the range, lowest first.
            var shift = low - shape[0] + Harmony.Mod12(shape[0] - low);
            for (; shape[^1] + shift <= high; shift += 12) {
                placed.Add([..shape.Select(p => p + shift)]);
            }
        }

        return placed;
    }

    /// <summary>A voicing's shapes (pitches from its bottom tone's class, before placing them in an octave).</summary>
    static List<int[]> Shapes(int[] tones, KeysVoicing voicing) {
        var shapes = new List<int[]>();
        if (voicing == KeysVoicing.Shell) {
            // The bass, third and seventh stacked in any order (the left hand plays the bass below, when it plays).
            foreach (var (a, b, c) in (ReadOnlySpan<(int, int, int)>)
                     [(0, 1, 2), (0, 2, 1), (1, 0, 2), (1, 2, 0), (2, 0, 1), (2, 1, 0)]) {
                shapes.Add(Stack([tones[a], tones[b], tones[c]]));
            }

            return shapes;
        }

        // Four voices cycling through the chord's classes from each in turn (a triad doubles its bottom tone).
        var sorted = tones.Distinct().Order().ToArray();
        for (var first = 0; first < sorted.Length; first++) {
            var close = Stack([..Enumerable.Range(0, 4).Select(k => sorted[(first + k) % sorted.Length])]);
            shapes.Add(voicing == KeysVoicing.Spread ? DropTwo(close) : close);
        }

        return shapes;
    }

    /// <summary>Pitch classes stacked upwards from the first, each the next above the one before.</summary>
    static int[] Stack(int[] order) {
        var pitches = new int[order.Length];
        pitches[0] = order[0];
        for (var k = 1; k < order.Length; k++) {
            pitches[k] = pitches[k - 1] + 1 + Harmony.Mod12(order[k] - pitches[k - 1] - 1);
        }

        return pitches;
    }

    /// <summary>A close voicing with its second voice from the top an octave down (open, over more than an octave).</summary>
    static int[] DropTwo(int[] close) {
        var spread = (int[])close.Clone();
        spread[^2] -= 12;
        Array.Sort(spread);
        return spread;
    }

    /// <summary>
    ///     For a range too narrow for any stack: each of the chord's tones once, in the range's lowest octave (a range is
    ///     at least an octave, so every tone fits).
    /// </summary>
    static List<int[]> Fallback(Harmony harmony, int low) =>
        [[..Tones(harmony, KeysVoicing.Close).Distinct().Select(pc => low + Harmony.Mod12(pc - low)).Order()]];
}
