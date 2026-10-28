using Anode.Contracts;

namespace Anode.Model.Time;

/// <summary>
///     Converts between musical time (ticks at <see cref="Ppq" />) and real time (seconds, samples) through a list of
///     tempo changes (docs/adr/0005-time-base.md). Between a change and the next the tempo holds (a step) or, after a
///     ramp, changes linearly in BPM over ticks, and seconds are its exact integral: over a ramp from
///     <c>b₀</c> to <c>b₁</c> across <c>Δ</c> ticks, with slope <c>k = (b₁ − b₀) / Δ</c>, the time to tick offset
///     <c>t</c> is <c>60 / (PPQ·k) · ln(1 + k·t / b₀)</c>. A ramp on the last change holds its tempo. Immutable; lookups
///     are a binary search over the changes.
/// </summary>
public sealed class TempoMap {
    /// <summary>Ticks per quarter note.</summary>
    public const int Ppq = 960;

    public const double MinBpm = 5;
    public const double MaxBpm = 990;

    /// <summary>Below this slope (BPM per tick) a ramp is computed as a constant tempo.</summary>
    const double FlatSlope = 1e-12;

    readonly long[] ticks;
    readonly double[] bpms;

    /// <summary>The slope of each segment, in BPM per tick (0 for a step or the last change).</summary>
    readonly double[] slopes;

    /// <summary>The time, in seconds, at which each change starts.</summary>
    readonly double[] starts;

    readonly bool[] ramps;

    public TempoMap(IEnumerable<TempoPoint> points) {
        var list = points.ToArray();
        if (list.Length == 0 || list[0].Tick != 0) {
            throw new ArgumentException("A tempo map starts with a change at tick 0.", nameof(points));
        }

        ticks = new long[list.Length];
        bpms = new double[list.Length];
        slopes = new double[list.Length];
        starts = new double[list.Length];
        ramps = new bool[list.Length];
        for (var i = 0; i < list.Length; i++) {
            var (tick, bpm, ramp) = list[i];
            if (i > 0 && tick <= ticks[i - 1]) {
                throw new ArgumentException("Tempo changes must be in strictly increasing tick order.", nameof(points));
            }

            if (bpm is < MinBpm or > MaxBpm || double.IsNaN(bpm)) {
                throw new ArgumentOutOfRangeException(nameof(points), bpm, $"Tempo must be {MinBpm}–{MaxBpm} BPM.");
            }

            ticks[i] = tick;
            bpms[i] = bpm;
            ramps[i] = ramp;
        }

        for (var i = 0; i < list.Length; i++) {
            if (ramps[i] && i + 1 < list.Length) {
                slopes[i] = (bpms[i + 1] - bpms[i]) / (ticks[i + 1] - ticks[i]);
            }

            if (i > 0) {
                starts[i] = starts[i - 1] + Seconds(i - 1, ticks[i] - ticks[i - 1]);
            }
        }
    }

    /// <summary>The map of a project's tempo track.</summary>
    public static TempoMap Of(IEnumerable<TempoEvent> events) =>
        new(events.Select(static e => new TempoPoint(e.Tick, e.Bpm, e.Curve == TempoCurve.Ramp)));

    /// <summary>A map with one tempo throughout.</summary>
    public static TempoMap Constant(double bpm) => new([new TempoPoint(0, bpm)]);

    public int Count => ticks.Length;

    public TempoPoint this[int index] => new(ticks[index], bpms[index], ramps[index]);

    /// <summary>The tempo at <paramref name="tick" /> (on a ramp, part of the way to the next change).</summary>
    public double BpmAt(double tick) {
        var i = SegmentAtTick(tick);
        return tick <= ticks[i] ? bpms[i] : bpms[i] + slopes[i] * (tick - ticks[i]);
    }

    /// <summary>Seconds from the project start to <paramref name="tick" /> (negative ticks extrapolate the first tempo).</summary>
    public double SecondsAt(double tick) {
        var i = SegmentAtTick(tick);
        var offset = tick - ticks[i];
        return offset < 0 ? starts[i] + offset * SecondsPerTick(bpms[i]) : starts[i] + Seconds(i, offset);
    }

    /// <summary>The (fractional) tick at <paramref name="seconds" /> from the project start.</summary>
    public double TickAt(double seconds) {
        var i = SegmentAtSeconds(seconds);
        var elapsed = seconds - starts[i];
        if (elapsed <= 0 || Math.Abs(slopes[i]) < FlatSlope) {
            return ticks[i] + elapsed / SecondsPerTick(bpms[i]);
        }

        // The inverse of the integral: t = b₀/k · (e^(s·PPQ·k/60) − 1).
        var k = slopes[i];
        return ticks[i] + bpms[i] / k * ExpM1(elapsed * Ppq * k / 60);
    }

    /// <summary>The sample at <paramref name="tick" />, rounded to the nearest.</summary>
    public long SampleAt(long tick, double sampleRate) => (long)Math.Round(SecondsAt(tick) * sampleRate);

    /// <summary>The tick at <paramref name="sample" />, rounded to the nearest.</summary>
    public long TickAtSample(long sample, double sampleRate) => (long)Math.Round(TickAt(sample / sampleRate));

    static double SecondsPerTick(double bpm) => 60.0 / (bpm * Ppq);

    /// <summary>Seconds over the first <paramref name="offset" /> ticks of segment <paramref name="i" />.</summary>
    double Seconds(int i, double offset) {
        var k = slopes[i];
        if (Math.Abs(k) < FlatSlope) {
            return offset * SecondsPerTick(bpms[i]);
        }

        // ∫ 60 / (PPQ · (b₀ + k·t)) dt from 0 to offset.
        return 60.0 / (Ppq * k) * LogP1(k * offset / bpms[i]);
    }

    /// <summary>ln(1 + x), accurate for small x (Goldberg's trick).</summary>
    static double LogP1(double x) {
        var u = 1 + x;
        return u == 1 ? x : Math.Log(u) * x / (u - 1);
    }

    /// <summary>eˣ − 1, accurate for small x (Kahan's trick).</summary>
    static double ExpM1(double x) {
        var u = Math.Exp(x);
        if (u == 1) {
            return x;
        }

        var um1 = u - 1;
        return um1 == -1 ? -1 : um1 * x / Math.Log(u);
    }

    int SegmentAtTick(double tick) {
        int lo = 0, hi = ticks.Length - 1;
        while (lo < hi) {
            var mid = (lo + hi + 1) / 2;
            if (ticks[mid] <= tick) {
                lo = mid;
            } else {
                hi = mid - 1;
            }
        }

        return lo;
    }

    int SegmentAtSeconds(double seconds) {
        var i = Array.BinarySearch(starts, seconds);
        return i >= 0 ? i : Math.Max(0, ~i - 1);
    }
}
