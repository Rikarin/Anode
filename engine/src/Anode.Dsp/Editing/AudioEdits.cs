using Anode.Dsp.Analysis;
using Anode.Dsp.Resampling;

namespace Anode.Dsp.Editing;

/// <summary>
///     The Audio File Editor's operations (docs/08-phase-7.md, "Audio File Editor"), over a file's channels as
///     <c>WavData.Channels</c> holds them (<c>channels[c][frame]</c>, all the same length). Ranges are frames
///     <c>start</c> (inclusive) … <c>end</c> (exclusive), applied to every channel; a range
///     outside the file is an <see cref="ArgumentOutOfRangeException" />. The in-place operations change the arrays;
///     <see cref="Trim" /> and <see cref="ConvertRate" /> return new ones. Deterministic.
/// </summary>
public static class AudioEdits {
    /// <summary>Scales the range so its peak (over every channel) is <paramref name="peakDb" /> dBFS; silence stays.</summary>
    public static void Normalize(float[][] channels, long start, long end, double peakDb) {
        var (from, count) = Range(channels, start, end);
        float peak = 0;
        foreach (var channel in channels) {
            foreach (var x in channel.AsSpan(from, count)) {
                peak = Math.Max(peak, Math.Abs(x));
            }
        }

        if (peak > 0) {
            Scale(channels, from, count, Math.Pow(10, peakDb / 20) / peak);
        }
    }

    /// <summary>Changes the range's level by <paramref name="gainDb" /> dB (no clipping: floats may exceed 1).</summary>
    public static void Gain(float[][] channels, long start, long end, double gainDb) {
        var (from, count) = Range(channels, start, end);
        Scale(channels, from, count, Math.Pow(10, gainDb / 20));
    }

    /// <summary>
    ///     Fades the range in from silence: the gain at position x (0…1 across the range) is x^(2^(−2c)) for curve
    ///     <paramref name="curve" /> c (−1…1), as a region's fade-in.
    /// </summary>
    /// <remarks>
    ///     As the playlist renders a region's fade: frame i of an n-frame range has x = i ÷ n (so the first frame is
    ///     silent and the last just under unity); a linear fade (c = 0) is x in float, a curved one is evaluated in double.
    /// </remarks>
    public static void FadeIn(float[][] channels, long start, long end, double curve) {
        var (from, count) = Range(channels, start, end);
        foreach (var channel in channels) {
            var span = channel.AsSpan(from, count);
            for (var i = 0; i < count; i++) {
                span[i] *= FadeShape(i, count, curve);
            }
        }
    }

    /// <summary>Fades the range out to silence, the mirror of <see cref="FadeIn" />.</summary>
    /// <remarks>Frame i of an n-frame range has x = (n − 1 − i) ÷ n: the last frame is silent.</remarks>
    public static void FadeOut(float[][] channels, long start, long end, double curve) {
        var (from, count) = Range(channels, start, end);
        foreach (var channel in channels) {
            var span = channel.AsSpan(from, count);
            for (var i = 0; i < count; i++) {
                span[i] *= FadeShape(count - 1 - i, count, curve);
            }
        }
    }

    public static void Reverse(float[][] channels, long start, long end) {
        var (from, count) = Range(channels, start, end);
        foreach (var channel in channels) {
            channel.AsSpan(from, count).Reverse();
        }
    }

    /// <summary>Inverts the range's polarity.</summary>
    public static void Invert(float[][] channels, long start, long end) {
        var (from, count) = Range(channels, start, end);
        foreach (var channel in channels) {
            var span = channel.AsSpan(from, count);
            for (var i = 0; i < span.Length; i++) {
                span[i] = -span[i];
            }
        }
    }

    public static void Silence(float[][] channels, long start, long end) {
        var (from, count) = Range(channels, start, end);
        foreach (var channel in channels) {
            channel.AsSpan(from, count).Clear();
        }
    }

    /// <summary>Subtracts each channel's mean over the range from the range.</summary>
    public static void RemoveDc(float[][] channels, long start, long end) {
        var (from, count) = Range(channels, start, end);
        if (count == 0) {
            return;
        }

        foreach (var channel in channels) {
            var span = channel.AsSpan(from, count);
            double sum = 0;
            foreach (var x in span) {
                sum += x;
            }

            var mean = sum / count;
            for (var i = 0; i < span.Length; i++) {
                span[i] = (float)(span[i] - mean);
            }
        }
    }

    /// <summary>The range alone, as new channel arrays.</summary>
    public static float[][] Trim(float[][] channels, long start, long end) {
        var (from, count) = Range(channels, start, end);
        var result = new float[channels.Length][];
        for (var c = 0; c < channels.Length; c++) {
            result[c] = channels[c].AsSpan(from, count).ToArray();
        }

        return result;
    }

    /// <summary>
    ///     The whole file converted from <paramref name="fromRate" /> to <paramref name="toRate" /> Hz with the bounce
    ///     resampler, as new channel arrays (round(frames × toRate ÷ fromRate) frames).
    /// </summary>
    public static float[][] ConvertRate(float[][] channels, int fromRate, int toRate) =>
        SampleRateConverter.Convert(channels, fromRate, toRate);

    /// <summary>
    ///     A fade's gain <paramref name="step" /> frames from its silent end, of <paramref name="length" /> frames: the
    ///     playlist's <c>FadeShape</c>, x^(2^(−2c)) with x = step ÷ length.
    /// </summary>
    internal static float FadeShape(long step, long length, double curve) {
        if (curve == 0 || !double.IsFinite(curve)) {
            return (float)step / length;
        }

        var exponent = Math.Pow(2, -2.0 * Math.Clamp((float)curve, -1f, 1f));
        return (float)Math.Pow((double)step / length, exponent);
    }

    static void Scale(float[][] channels, int from, int count, double gain) {
        foreach (var channel in channels) {
            var span = channel.AsSpan(from, count);
            for (var i = 0; i < span.Length; i++) {
                span[i] = (float)(span[i] * gain);
            }
        }
    }

    /// <summary>The range as an index and a count, checked against every channel.</summary>
    internal static (int From, int Count) Range(float[][] channels, long start, long end) {
        ArgumentNullException.ThrowIfNull(channels);
        var frames = channels.Length == 0 ? 0 : channels[0].Length;
        foreach (var channel in channels) {
            if (channel.Length != frames) {
                throw new ArgumentException("Every channel must have the same length.", nameof(channels));
            }
        }

        if (start < 0 || end < start || end > frames) {
            throw new ArgumentOutOfRangeException(
                nameof(end),
                $"Frames {start}…{end} are outside the file's 0…{frames}."
            );
        }

        return ((int)start, (int)(end - start));
    }
}

/// <summary>Zero crossings, for the Audio File Editor's snap.</summary>
public static class ZeroCrossings {
    /// <summary>
    ///     The frame nearest <paramref name="frame" /> (within <paramref name="maxDistance" /> frames either way) where
    ///     <paramref name="mono" /> crosses zero (the frame after a sign change, or a frame that is exactly 0); −1 when
    ///     there is none.
    /// </summary>
    /// <remarks>Of two crossings equally near, the earlier.</remarks>
    public static long Nearest(ReadOnlySpan<float> mono, long frame, long maxDistance) {
        if (mono.Length == 0 || maxDistance < 0) {
            return -1;
        }

        for (long d = 0; d <= maxDistance; d++) {
            var before = frame - d;
            var after = frame + d;
            if (before < 0 && after >= mono.Length) {
                break;
            }

            if (before >= 0 && before < mono.Length && IsCrossing(mono, before)) {
                return before;
            }

            if (d > 0 && after >= 0 && after < mono.Length && IsCrossing(mono, after)) {
                return after;
            }
        }

        return -1;
    }

    /// <summary>Every zero crossing in frames <paramref name="start" />…<paramref name="end" />, ascending.</summary>
    /// <remarks><paramref name="end" /> is exclusive, as every range of the editor.</remarks>
    public static long[] Find(ReadOnlySpan<float> mono, long start, long end) {
        if (start < 0 || end < start || end > mono.Length) {
            throw new ArgumentOutOfRangeException(
                nameof(end),
                $"Frames {start}…{end} are outside the file's 0…{mono.Length}."
            );
        }

        var crossings = new List<long>();
        for (var i = start; i < end; i++) {
            if (IsCrossing(mono, i)) {
                crossings.Add(i);
            }
        }

        return [..crossings];
    }

    /// <summary>Whether frame <paramref name="i" /> is exactly 0 or has the opposite sign of the frame before it.</summary>
    internal static bool IsCrossing(ReadOnlySpan<float> mono, long i) {
        var x = mono[(int)i];
        if (x == 0) {
            return true;
        }

        if (i == 0) {
            return false;
        }

        var previous = mono[(int)i - 1];
        return (previous < 0 && x > 0) || (previous > 0 && x < 0);
    }
}

/// <summary>A loop candidate: frames <c>StartFrame</c>…<c>EndFrame</c> (exclusive), scored 0…1 (1: seamless).</summary>
public sealed record LoopCandidate(long StartFrame, long EndFrame, double Score);

/// <summary>
///     Loop search: candidate loop ends correlated with the loop start, scored for continuity (waveform and slope at
///     the seam). Deterministic.
/// </summary>
/// <remarks>
///     The loop starts at the range's start s and plays s … e − 1, then s again, so a seamless end e is one whose
///     neighbourhood looks like the start's: x[e] ≈ x[s] and x[e] − x[e − 1] ≈ x[s] − x[s − 1]. Every end from
///     s + minFrames to the range's end is scored for that continuity (value and slope errors over the start's RMS
///     level and RMS slope, c = exp(−4 (dv² + ds²))); the best <see cref="Shortlist" /> local maxima are then scored by
///     the normalised correlation r of the 20 ms around s with the 20 ms around e. The score is c × max(0, r), times
///     0.98 + 0.02 × (e − s) ÷ (end − s): among equally seamless ends (every period of a steady tone) the longest loop
///     wins, so the best is within a period of the range's end.
/// </remarks>
public static class LoopSearch {
    /// <summary>How many of the most continuous ends are correlated.</summary>
    const int Shortlist = 4096;

    const double WindowSeconds = 0.02;

    /// <summary>
    ///     The <paramref name="count" /> best loops inside frames <paramref name="start" />…<paramref name="end" /> of
    ///     <paramref name="mono" />, at least <paramref name="minFrames" /> long, best first (no two within 1 ms of each
    ///     other at both ends).
    /// </summary>
    public static LoopCandidate[] Find(
        ReadOnlySpan<float> mono,
        int sampleRate,
        long start,
        long end,
        int count,
        long minFrames
    ) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        if (start < 0 || end < start || end > mono.Length) {
            throw new ArgumentOutOfRangeException(
                nameof(end),
                $"Frames {start}…{end} are outside the file's 0…{mono.Length}."
            );
        }

        if (count <= 0) {
            return [];
        }

        var s = (int)start;
        var first = s + Math.Max(2, minFrames);

        // x[e] must exist: an end at the file's end has nothing to compare with the start.
        var last = Math.Min(end, mono.Length - 1);
        if (first > last) {
            return [];
        }

        var half = Math.Max(1, (int)Math.Round(WindowSeconds * sampleRate / 2));
        var (level, slopeLevel) = StartLevels(mono, s, half);
        var startValue = mono[s];
        var startSlope = s > 0 ? mono[s] - mono[s - 1] : 0f;

        // Continuity of every end, then the local maxima.
        var ends = (int)(last - first + 1);
        var continuity = new double[ends];
        for (var k = 0; k < ends; k++) {
            var e = (int)first + k;
            var dv = (mono[e] - startValue) / level;
            var ds = (mono[e] - mono[e - 1] - startSlope) / slopeLevel;
            continuity[k] = Math.Exp(-4 * (dv * dv + ds * ds));
        }

        var peaks = new List<int>();
        for (var k = 0; k < ends; k++) {
            if ((k == 0 || continuity[k] >= continuity[k - 1])
                && (k == ends - 1 || continuity[k] > continuity[k + 1])) {
                peaks.Add(k);
            }
        }

        peaks.Sort((a, b) => {
                var order = continuity[b].CompareTo(continuity[a]);
                return order != 0 ? order : a.CompareTo(b);
            }
        );
        if (peaks.Count > Shortlist) {
            peaks.RemoveRange(Shortlist, peaks.Count - Shortlist);
        }

        double span = end - start;
        var scored = new List<LoopCandidate>(peaks.Count);
        foreach (var k in peaks) {
            var e = first + k;
            var r = Correlation(mono, s, (int)e, half);
            var preference = 0.98 + 0.02 * (e - start) / span;
            var score = Math.Clamp(continuity[k] * Math.Max(0, r) * preference, 0, 1);
            scored.Add(new(start, e, score));
        }

        scored.Sort(static (a, b) => {
                var order = b.Score.CompareTo(a.Score);
                return order != 0 ? order : a.EndFrame.CompareTo(b.EndFrame);
            }
        );
        var apart = Math.Max(1, sampleRate / 1000);
        var chosen = new List<LoopCandidate>(count);
        foreach (var candidate in scored) {
            var near = false;
            foreach (var other in chosen) {
                if (Math.Abs(other.StartFrame - candidate.StartFrame) < apart
                    && Math.Abs(other.EndFrame - candidate.EndFrame) < apart) {
                    near = true;
                    break;
                }
            }

            if (!near) {
                chosen.Add(candidate);
                if (chosen.Count == count) {
                    break;
                }
            }
        }

        return [..chosen];
    }

    /// <summary>The RMS level and RMS slope of the window around the start (floored, so silence divides safely).</summary>
    static (double Level, double Slope) StartLevels(ReadOnlySpan<float> mono, int s, int half) {
        var from = Math.Max(1, s - half);
        var to = Math.Min(mono.Length, s + half);
        double power = 0, slope = 0;
        for (var i = from; i < to; i++) {
            power += (double)mono[i] * mono[i];
            var d = (double)mono[i] - mono[i - 1];
            slope += d * d;
        }

        var n = Math.Max(1, to - from);
        return (Math.Sqrt(power / n) + 1e-9, Math.Sqrt(slope / n) + 1e-9);
    }

    /// <summary>
    ///     The normalised correlation of the windows around <paramref name="s" /> and <paramref name="e" /> (the same
    ///     offsets, inside the file); two silent windows correlate fully.
    /// </summary>
    static double Correlation(ReadOnlySpan<float> mono, int s, int e, int half) {
        var low = -Math.Min(half, s);
        var high = Math.Min(half, mono.Length - e);
        double xy = 0, xx = 0, yy = 0;
        for (var o = low; o < high; o++) {
            double x = mono[s + o], y = mono[e + o];
            xy += x * y;
            xx += x * x;
            yy += y * y;
        }

        const double Silent = 1e-12;
        if (xx < Silent || yy < Silent) {
            return xx < Silent && yy < Silent ? 1 : 0;
        }

        return xy / Math.Sqrt(xx * yy);
    }
}

/// <summary>An onset found for a groove template: its frame and its strength 0…1 (1: the strongest of the file).</summary>
public sealed record GrooveOnset(long Frame, double Strength);

/// <summary>
///     Groove extraction from audio: the onsets of a file (Phase 5's <c>Transients</c>) with their strengths (peak level
///     over the first 20 ms after each). The model turns them into a template with the project's tempo map
///     (<c>Grooves.FromOnsets</c>).
/// </summary>
public static class GrooveFromAudio {
    const double PeakSeconds = 0.02;

    public static GrooveOnset[] Onsets(ReadOnlySpan<float> mono, int sampleRate) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        var frames = Transients.Detect(mono, sampleRate);
        if (frames.Length == 0) {
            return [];
        }

        var span = Math.Max(1, (int)Math.Round(PeakSeconds * sampleRate));
        var peaks = new double[frames.Length];
        double loudest = 0;
        for (var i = 0; i < frames.Length; i++) {
            var from = (int)frames[i];
            var to = Math.Min(mono.Length, from + span);
            float peak = 0;
            for (var n = from; n < to; n++) {
                peak = Math.Max(peak, Math.Abs(mono[n]));
            }

            peaks[i] = peak;
            loudest = Math.Max(loudest, peak);
        }

        var onsets = new GrooveOnset[frames.Length];
        for (var i = 0; i < frames.Length; i++) {
            onsets[i] = new(frames[i], loudest > 0 ? peaks[i] / loudest : 0);
        }

        return onsets;
    }
}
