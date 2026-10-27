namespace Anode.Model.Time;

/// <summary>
///     Converts between musical time (ticks at <see cref="Ppq" />) and real time (seconds, samples) through a list of
///     tempo changes (docs/adr/0005-time-base.md). Immutable; lookups are a binary search over the changes. Tempo ramps
///     are still to come: between two changes the tempo is constant.
/// </summary>
public sealed class TempoMap {
    /// <summary>Ticks per quarter note.</summary>
    public const int Ppq = 960;

    public const double MinBpm = 5;
    public const double MaxBpm = 990;

    readonly long[] ticks;
    readonly double[] bpms;

    /// <summary>The time, in seconds, at which each change starts.</summary>
    readonly double[] starts;

    public TempoMap(IEnumerable<TempoPoint> points) {
        var list = points.ToArray();
        if (list.Length == 0 || list[0].Tick != 0) {
            throw new ArgumentException("A tempo map starts with a change at tick 0.", nameof(points));
        }

        ticks = new long[list.Length];
        bpms = new double[list.Length];
        starts = new double[list.Length];
        for (var i = 0; i < list.Length; i++) {
            var (tick, bpm) = list[i];
            if (i > 0 && tick <= ticks[i - 1]) {
                throw new ArgumentException("Tempo changes must be in strictly increasing tick order.", nameof(points));
            }

            if (bpm is < MinBpm or > MaxBpm || double.IsNaN(bpm)) {
                throw new ArgumentOutOfRangeException(nameof(points), bpm, $"Tempo must be {MinBpm}–{MaxBpm} BPM.");
            }

            ticks[i] = tick;
            bpms[i] = bpm;
            starts[i] = i == 0 ? 0 : starts[i - 1] + SecondsPerTick(bpms[i - 1]) * (tick - ticks[i - 1]);
        }
    }

    /// <summary>A map with one tempo throughout.</summary>
    public static TempoMap Constant(double bpm) => new([new(0, bpm)]);

    public int Count => ticks.Length;

    public TempoPoint this[int index] => new(ticks[index], bpms[index]);

    public double BpmAt(long tick) => bpms[SegmentAtTick(tick)];

    /// <summary>Seconds from the project start to <paramref name="tick" /> (negative ticks extrapolate the first tempo).</summary>
    public double SecondsAt(long tick) {
        var i = SegmentAtTick(tick);
        return starts[i] + SecondsPerTick(bpms[i]) * (tick - ticks[i]);
    }

    /// <summary>The (fractional) tick at <paramref name="seconds" /> from the project start.</summary>
    public double TickAt(double seconds) {
        var i = SegmentAtSeconds(seconds);
        return ticks[i] + (seconds - starts[i]) / SecondsPerTick(bpms[i]);
    }

    /// <summary>The sample at <paramref name="tick" />, rounded to the nearest.</summary>
    public long SampleAt(long tick, double sampleRate) => (long)Math.Round(SecondsAt(tick) * sampleRate);

    /// <summary>The tick at <paramref name="sample" />, rounded to the nearest.</summary>
    public long TickAtSample(long sample, double sampleRate) => (long)Math.Round(TickAt(sample / sampleRate));

    static double SecondsPerTick(double bpm) => 60.0 / (bpm * Ppq);

    int SegmentAtTick(long tick) {
        var i = Array.BinarySearch(ticks, tick);
        return i >= 0 ? i : Math.Max(0, ~i - 1);
    }

    int SegmentAtSeconds(double seconds) {
        var i = Array.BinarySearch(starts, seconds);
        return i >= 0 ? i : Math.Max(0, ~i - 1);
    }
}
