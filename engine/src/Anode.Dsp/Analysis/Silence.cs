namespace Anode.Dsp.Analysis;

/// <summary>
///     Silence detection (Strip Silence): an RMS gate. Deterministic, and about 50× real time or better on one core.
/// </summary>
/// <remarks>
///     The level is the RMS of a 10 ms window centred on each frame. Runs above the threshold shorter than the minimum
///     time are dropped; the rest are widened by the pre-attack and post-release times and merged where they meet.
/// </remarks>
public static class Silence {
    const double WindowSeconds = 0.01;

    /// <summary>
    ///     The non-silent ranges of <paramref name="mono" /> (start inclusive, end exclusive, in frames, ascending): above
    ///     <paramref name="thresholdDb" /> for at least <paramref name="minTimeMs" />, widened by
    ///     <paramref name="preAttackMs" /> before and <paramref name="postReleaseMs" /> after, merged where they meet.
    /// </summary>
    public static (long Start, long End)[] Detect(
        ReadOnlySpan<float> mono,
        int sampleRate,
        double thresholdDb,
        double minTimeMs,
        double preAttackMs,
        double postReleaseMs
    ) {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        var length = mono.Length;
        if (length == 0) {
            return [];
        }

        var window = Math.Max(1, (int)Math.Round(WindowSeconds * sampleRate));
        var lead = window / 2;
        var threshold = Math.Pow(10, thresholdDb / 10) * window; // the window's energy at the threshold
        var minFrames = (long)Math.Round(Math.Max(0, minTimeMs) * sampleRate / 1000);
        var pre = (long)Math.Round(Math.Max(0, preAttackMs) * sampleRate / 1000);
        var post = (long)Math.Round(Math.Max(0, postReleaseMs) * sampleRate / 1000);

        var ranges = new List<(long Start, long End)>();
        long runStart = -1;

        // The window of frame n is n − lead … n − lead + window − 1. Its energy is kept exactly by summing each block of
        // 4,096 frames afresh, so long files do not drift.
        double sum = 0;
        for (var n = -lead; n < -lead + window; n++) {
            sum += Square(mono, n);
        }

        for (var n = 0; n < length; n++) {
            if (n > 0) {
                if ((n & 4095) == 0) {
                    sum = 0;
                    for (var m = n - lead; m < n - lead + window; m++) {
                        sum += Square(mono, m);
                    }
                } else {
                    sum += Square(mono, n - lead + window - 1) - Square(mono, n - lead - 1);
                }
            }

            var above = sum > threshold;
            if (above && runStart < 0) {
                runStart = n;
            } else if (!above && runStart >= 0) {
                Add(ranges, runStart, n, minFrames, pre, post, length);
                runStart = -1;
            }
        }

        if (runStart >= 0) {
            Add(ranges, runStart, length, minFrames, pre, post, length);
        }

        return [..ranges];
    }

    static double Square(ReadOnlySpan<float> mono, long n) =>
        n >= 0 && n < mono.Length ? (double)mono[(int)n] * mono[(int)n] : 0;

    static void Add(
        List<(long Start, long End)> ranges,
        long start,
        long end,
        long minFrames,
        long pre,
        long post,
        long length
    ) {
        if (end - start < minFrames) {
            return;
        }

        start = Math.Max(0, start - pre);
        end = Math.Min(length, end + post);
        if (ranges.Count > 0 && start <= ranges[^1].End) {
            ranges[^1] = (ranges[^1].Start, Math.Max(ranges[^1].End, end));
            return;
        }

        ranges.Add((start, end));
    }
}
