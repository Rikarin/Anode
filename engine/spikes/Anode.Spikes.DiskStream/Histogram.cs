using System.Diagnostics;

namespace Anode.Spikes.DiskStream;

/// <summary>Fixed-bin duration histogram, allocation-free to record into (used on the audio thread).</summary>
sealed class Histogram(double binMicroseconds, int bins) {
    static readonly double TicksPerMicrosecond = Stopwatch.Frequency / 1e6;
    readonly long[] counts = new long[bins + 1]; // the last bin collects everything beyond the range

    public long Count { get; private set; }

    public double MaxMicroseconds { get; private set; }

    public void RecordTicks(long ticks) {
        var us = ticks / TicksPerMicrosecond;
        counts[Math.Min(bins, (int)(us / binMicroseconds))]++;
        Count++;
        if (us > MaxMicroseconds) {
            MaxMicroseconds = us;
        }
    }

    public void Clear() {
        Array.Clear(counts);
        Count = 0;
        MaxMicroseconds = 0;
    }

    public void Add(Histogram other) {
        for (var i = 0; i < counts.Length; i++) {
            counts[i] += other.counts[i];
        }

        Count += other.Count;
        MaxMicroseconds = Math.Max(MaxMicroseconds, other.MaxMicroseconds);
    }

    /// <summary>Upper edge of the bin holding quantile <paramref name="q" />, in microseconds.</summary>
    public double Percentile(double q) {
        if (Count == 0) {
            return 0;
        }

        var target = (long)Math.Ceiling(q * Count);
        long seen = 0;
        for (var i = 0; i < counts.Length; i++) {
            seen += counts[i];
            if (seen >= target) {
                return Math.Min((i + 1) * binMicroseconds, MaxMicroseconds);
            }
        }

        return MaxMicroseconds;
    }
}
