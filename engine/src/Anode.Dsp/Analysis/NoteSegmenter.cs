namespace Anode.Dsp.Analysis;

/// <summary>Note segmentation: stable voiced runs of a pitch curve. Deterministic.</summary>
/// <remarks>
///     <list type="number">
///         <item>Voiced runs (between unvoiced hops) are note candidates.</item>
///         <item>
///             Within a run, a note changes where the mean pitch of the next 50 ms differs from that of the previous
///             50 ms by more than 70 cents, at the local maximum of that difference (vibrato of ±40 cents stays below it).
///         </item>
///         <item>
///             Each change is then placed at the hop that best splits the curve between the two notes' levels (least
///             absolute deviation), so glides and steps split where the pitch crosses over.
///         </item>
///         <item>
///             Notes shorter than 60 ms are dropped; neighbours within 50 cents are joined. A note's pitch is the median
///             of its curve without its first and last 20 ms.
///         </item>
///     </list>
/// </remarks>
public static class NoteSegmenter {
    const double CompareSeconds = 0.05;
    const double ChangeCents = 70;
    const double MinNoteSeconds = 0.06;
    const double JoinCents = 50;
    const double TrimSeconds = 0.02;

    public static NoteSegment[] Segment(PitchTrack track, int sampleRate) {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        var cents = track.Cents;
        var hop = track.Hop;
        var hopSeconds = (double)hop / sampleRate;
        var compare = Math.Max(1, (int)Math.Round(CompareSeconds / hopSeconds));
        var minNote = Math.Max(1, (int)Math.Round(MinNoteSeconds / hopSeconds));
        var trim = (int)Math.Round(TrimSeconds / hopSeconds);

        // Notes as hop ranges [Start, End).
        var notes = new List<(int Start, int End)>();
        var k = 0;
        while (k < cents.Length) {
            if (float.IsNaN(cents[k])) {
                k++;
                continue;
            }

            var start = k;
            while (k < cents.Length && !float.IsNaN(cents[k])) {
                k++;
            }

            SplitRun(cents, start, k, compare, notes);
        }

        // Drop short notes, then join neighbours (touching, within 50 cents).
        notes.RemoveAll(n => n.End - n.Start < minNote);
        var joined = new List<(int Start, int End)>();
        foreach (var note in notes) {
            if (joined.Count > 0
                && joined[^1].End == note.Start
                && Math.Abs(
                    Level(cents, joined[^1].Start, joined[^1].End, trim) - Level(cents, note.Start, note.End, trim)
                )
                < JoinCents) {
                joined[^1] = (joined[^1].Start, note.End);
            } else {
                joined.Add(note);
            }
        }

        var result = new NoteSegment[joined.Count];
        for (var i = 0; i < joined.Count; i++) {
            var (start, end) = joined[i];
            double confidence = 0;
            for (var j = start; j < end; j++) {
                confidence += track.Voicing[j];
            }

            // A boundary lies halfway between hops.
            var startFrame = Math.Max(0, (long)Math.Round((start - 0.5) * hop));
            var endFrame = (long)Math.Round((end - 0.5) * hop);
            result[i] = new(startFrame, endFrame, Level(cents, start, end, trim), confidence / (end - start));
        }

        return result;
    }

    /// <summary>Splits the voiced run <c>start…end</c> into notes where its pitch changes.</summary>
    static void SplitRun(float[] cents, int start, int end, int compare, List<(int Start, int End)> notes) {
        var length = end - start;
        var prefix = new double[length + 1];
        for (var i = 0; i < length; i++) {
            prefix[i + 1] = prefix[i] + cents[start + i];
        }

        // D(t): the mean of the next `compare` hops less that of the previous ones (fewer at the run's ends, at least half).
        var change = new double[length];
        var half = Math.Max(1, compare / 2);
        for (var t = 0; t < length; t++) {
            var before = Math.Min(compare, t);
            var after = Math.Min(compare, length - t);
            if (before < half || after < half) {
                continue;
            }

            change[t] = (prefix[t + after] - prefix[t]) / after - (prefix[t] - prefix[t - before]) / before;
        }

        var cuts = new List<int>();
        for (var t = 0; t < length; t++) {
            var size = Math.Abs(change[t]);
            if (size <= ChangeCents) {
                continue;
            }

            var isPeak = true;
            for (var j = Math.Max(0, t - compare); j <= Math.Min(length - 1, t + compare) && isPeak; j++) {
                var other = Math.Abs(change[j]);
                isPeak = other < size || (other == size && j >= t);
            }

            if (isPeak) {
                cuts.Add(t);
            }
        }

        // Place each cut where the curve best splits between the levels either side.
        var bounds = new List<int> { 0 };
        for (var c = 0; c < cuts.Count; c++) {
            var low = Math.Max(bounds[^1] + 1, cuts[c] - compare);
            var high = Math.Min(c + 1 < cuts.Count ? cuts[c + 1] - 1 : length - 1, cuts[c] + compare);
            if (high <= low) {
                continue;
            }

            var left = Median(cents, start + Math.Max(bounds[^1], cuts[c] - 2 * compare), start + cuts[c]);
            var right = Median(
                cents,
                start + cuts[c],
                start + Math.Min(c + 1 < cuts.Count ? cuts[c + 1] : length, cuts[c] + 2 * compare)
            );
            var best = low;
            var bestCost = double.PositiveInfinity;
            for (var s = low; s <= high; s++) {
                double cost = 0;
                for (var i = low; i < high + 1; i++) {
                    cost += Math.Abs(cents[start + i] - (i < s ? left : right));
                }

                if (cost < bestCost) {
                    bestCost = cost;
                    best = s;
                }
            }

            bounds.Add(best);
        }

        bounds.Add(length);
        for (var b = 0; b + 1 < bounds.Count; b++) {
            if (bounds[b + 1] > bounds[b]) {
                notes.Add((start + bounds[b], start + bounds[b + 1]));
            }
        }
    }

    /// <summary>The median of <c>cents[from…to)</c>, without <paramref name="trim" /> hops at each end when long enough.</summary>
    static double Level(float[] cents, int from, int to, int trim) =>
        to - from > 4 * trim ? Median(cents, from + trim, to - trim) : Median(cents, from, to);

    static double Median(float[] cents, int from, int to) {
        if (to <= from) {
            return cents[Math.Min(from, cents.Length - 1)];
        }

        var values = cents[from..to];
        Array.Sort(values);
        var middle = values.Length / 2;
        return values.Length % 2 == 1 ? values[middle] : 0.5 * (values[middle - 1] + values[middle]);
    }
}
