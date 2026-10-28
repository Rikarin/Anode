using Anode.Contracts;

namespace Anode.Model.Time;

/// <summary>
///     Converts between musical time (ticks at <see cref="Ppq" />) and real time (seconds, samples) through a list of
///     tempo changes (docs/adr/0005-time-base.md). Between a change and the next the tempo holds (a step) or, after a
///     ramp, changes along the ramp's shape (docs/06-phase-5.md): at fraction x of the way the tempo is
///     <c>b₀ + (b₁ − b₀)·x^(2^(−2·shape))</c>, and seconds are its integral. A linear ramp (shape 0) is integrated
///     exactly: across <c>Δ</c> ticks, with slope <c>k = (b₁ − b₀) / Δ</c>, the time to tick offset <c>t</c> is
///     <c>60 / (PPQ·k) · ln(1 + k·t / b₀)</c>; a curved one numerically (Gauss–Legendre over
///     <see cref="ShapeSteps" /> equal steps, about 1e-9 s), and inverted by Newton's method. A ramp on the last change
///     holds its tempo. Immutable; lookups are a binary search over the changes.
///     <para>
///         <see cref="Speed" /> (Varispeed) scales real time: seconds at speed <c>s</c> are the map's seconds divided by
///         <c>s</c>, as if every tempo were multiplied by <c>s</c>; <see cref="BpmAt" /> stays the musical tempo.
///     </para>
/// </summary>
public sealed class TempoMap {
    /// <summary>Ticks per quarter note.</summary>
    public const int Ppq = 960;

    public const double MinBpm = 5;
    public const double MaxBpm = 990;

    /// <summary>The equal steps a curved ramp is integrated over.</summary>
    public const int ShapeSteps = 256;

    /// <summary>Below this slope (BPM per tick) a ramp is computed as a constant tempo.</summary>
    const double FlatSlope = 1e-12;

    /// <summary>Below this (absolute) shape a ramp is linear.</summary>
    const double FlatShape = 1e-9;

    // Five-point Gauss–Legendre nodes and weights on [−1, 1].
    static readonly double[] Nodes = [
        -0.9061798459386640, -0.5384693101056831, 0, 0.5384693101056831, 0.9061798459386640
    ];

    static readonly double[] Weights = [
        0.2369268850561891, 0.4786286704993665, 0.5688888888888889, 0.4786286704993665, 0.2369268850561891
    ];

    readonly long[] ticks;
    readonly double[] bpms;

    /// <summary>The slope of each segment, in BPM per tick (0 for a step or the last change).</summary>
    readonly double[] slopes;

    /// <summary>The time, in seconds, at which each change starts (at speed 1).</summary>
    readonly double[] starts;

    readonly bool[] ramps;
    readonly double[] shapes;

    /// <summary>A curved ramp's exponent <c>2^(−2·shape)</c> (0: linear or not a ramp).</summary>
    readonly double[] powers;

    /// <summary>A curved ramp's cumulative seconds at each of its <see cref="ShapeSteps" /> + 1 step boundaries.</summary>
    readonly double[]?[] curves;

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
        shapes = new double[list.Length];
        powers = new double[list.Length];
        curves = new double[list.Length][];
        for (var i = 0; i < list.Length; i++) {
            var (tick, bpm, ramp, shape) = list[i];
            Check(tick, bpm, i > 0 ? ticks[i - 1] : null);
            ticks[i] = tick;
            bpms[i] = bpm;
            ramps[i] = ramp;
            shapes[i] = double.IsFinite(shape) ? Math.Clamp(shape, -1, 1) : 0;
        }

        for (var i = 0; i < list.Length; i++) {
            if (ramps[i] && i + 1 < list.Length) {
                slopes[i] = (bpms[i + 1] - bpms[i]) / (ticks[i + 1] - ticks[i]);
                if (Math.Abs(shapes[i]) >= FlatShape && Math.Abs(slopes[i]) >= FlatSlope) {
                    powers[i] = Math.Pow(2, -2 * shapes[i]);
                    curves[i] = Curve(i);
                }
            }

            if (i > 0) {
                starts[i] = starts[i - 1] + Seconds(i - 1, ticks[i] - ticks[i - 1]);
            }
        }
    }

    TempoMap(TempoMap map, double speed) {
        ticks = map.ticks;
        bpms = map.bpms;
        slopes = map.slopes;
        starts = map.starts;
        ramps = map.ramps;
        shapes = map.shapes;
        powers = map.powers;
        curves = map.curves;
        Speed = speed;
    }

    static void Check(long tick, double bpm, long? previous) {
        if (tick <= previous) {
            throw new ArgumentException("Tempo changes must be in strictly increasing tick order.", nameof(tick));
        }

        if (bpm is < MinBpm or > MaxBpm || double.IsNaN(bpm)) {
            throw new ArgumentOutOfRangeException(nameof(bpm), bpm, $"Tempo must be {MinBpm}–{MaxBpm} BPM.");
        }
    }

    /// <summary>The map of a project's tempo track.</summary>
    public static TempoMap Of(IEnumerable<TempoEvent> events) =>
        new(events.Select(static e => new TempoPoint(e.Tick, e.Bpm, e.Curve == TempoCurve.Ramp, e.Shape)));

    /// <summary>A map with one tempo throughout.</summary>
    public static TempoMap Constant(double bpm) => new([new TempoPoint(0, bpm)]);

    /// <summary>The Varispeed factor real time is scaled by (1: none).</summary>
    public double Speed { get; } = 1;

    /// <summary>This map at <paramref name="speed" /> (Varispeed): the same ticks, real time divided by it.</summary>
    public TempoMap WithSpeed(double speed) {
        if (!(speed > 0) || !double.IsFinite(speed)) {
            throw new ArgumentOutOfRangeException(nameof(speed), speed, "A speed is positive.");
        }

        return speed == Speed ? this : new(this, speed);
    }

    public int Count => ticks.Length;

    public TempoPoint this[int index] => new(ticks[index], bpms[index], ramps[index], shapes[index]);

    /// <summary>The tempo at <paramref name="tick" /> (on a ramp, part of the way to the next change).</summary>
    public double BpmAt(double tick) {
        var i = SegmentAtTick(tick);
        return tick <= ticks[i] ? bpms[i] : Bpm(i, tick - ticks[i]);
    }

    /// <summary>The tempo <paramref name="offset" /> ticks into segment <paramref name="i" />.</summary>
    double Bpm(int i, double offset) {
        if (powers[i] == 0) {
            return bpms[i] + slopes[i] * offset;
        }

        var span = ticks[i + 1] - ticks[i];
        var x = Math.Clamp(offset / span, 0, 1);
        return bpms[i] + (bpms[i + 1] - bpms[i]) * Math.Pow(x, powers[i]);
    }

    /// <summary>Seconds from the project start to <paramref name="tick" /> (negative ticks extrapolate the first tempo).</summary>
    public double SecondsAt(double tick) {
        var i = SegmentAtTick(tick);
        var offset = tick - ticks[i];
        var seconds = offset < 0 ? starts[i] + offset * SecondsPerTick(bpms[i]) : starts[i] + Seconds(i, offset);
        return seconds / Speed;
    }

    /// <summary>The (fractional) tick at <paramref name="seconds" /> from the project start.</summary>
    public double TickAt(double seconds) {
        seconds *= Speed;
        var i = SegmentAtSeconds(seconds);
        var elapsed = seconds - starts[i];
        if (elapsed <= 0 || Math.Abs(slopes[i]) < FlatSlope) {
            return ticks[i] + elapsed / SecondsPerTick(bpms[i]);
        }

        if (curves[i] is { } curve) {
            return ticks[i] + CurveTick(i, curve, elapsed);
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

        if (curves[i] is { } curve) {
            var span = ticks[i + 1] - ticks[i];
            if (offset >= span) {
                // Past the end of a ramp that is not the last change cannot happen (the next segment starts there).
                return curve[ShapeSteps] + (offset - span) * SecondsPerTick(bpms[i + 1]);
            }

            var step = span / (double)ShapeSteps;
            var j = Math.Min((int)(offset / step), ShapeSteps - 1);
            return curve[j] + Integral(i, j * step, offset);
        }

        // ∫ 60 / (PPQ · (b₀ + k·t)) dt from 0 to offset.
        return 60.0 / (Ppq * k) * LogP1(k * offset / bpms[i]);
    }

    /// <summary>A curved ramp's cumulative seconds at its step boundaries.</summary>
    double[] Curve(int i) {
        var span = ticks[i + 1] - ticks[i];
        var step = span / (double)ShapeSteps;
        var curve = new double[ShapeSteps + 1];
        for (var j = 0; j < ShapeSteps; j++) {
            curve[j + 1] = curve[j] + Integral(i, j * step, (j + 1) * step);
        }

        return curve;
    }

    /// <summary>∫ 60 / (PPQ · bpm(t)) dt over ticks <paramref name="from" />…<paramref name="to" /> of segment i.</summary>
    double Integral(int i, double from, double to) {
        if (to <= from) {
            return 0;
        }

        var half = (to - from) / 2;
        var mid = (to + from) / 2;
        double sum = 0;
        for (var n = 0; n < Nodes.Length; n++) {
            sum += Weights[n] / Bpm(i, mid + half * Nodes[n]);
        }

        return 60.0 / Ppq * half * sum;
    }

    /// <summary>The tick offset into curved segment i at which <paramref name="elapsed" /> seconds have passed.</summary>
    double CurveTick(int i, double[] curve, double elapsed) {
        var span = ticks[i + 1] - ticks[i];
        if (elapsed >= curve[ShapeSteps]) {
            return span + (elapsed - curve[ShapeSteps]) / SecondsPerTick(bpms[i + 1]);
        }

        var j = Array.BinarySearch(curve, elapsed);
        j = j >= 0 ? Math.Min(j, ShapeSteps - 1) : Math.Max(0, ~j - 1);
        var step = span / (double)ShapeSteps;
        var (lo, hi) = (j * step, (j + 1) * step);
        // Newton's method from the linear guess, kept inside the step.
        var t = lo + (elapsed - curve[j]) / (curve[j + 1] - curve[j]) * step;
        for (var n = 0; n < 20; n++) {
            var error = curve[j] + Integral(i, lo, t) - elapsed;
            var next = Math.Clamp(t - error / SecondsPerTick(Bpm(i, t)), lo, hi);
            if (Math.Abs(next - t) < 1e-9) {
                return next;
            }

            t = next;
        }

        return t;
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
