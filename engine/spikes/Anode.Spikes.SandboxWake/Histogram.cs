namespace Anode.Spikes.SandboxWake;

/// <summary>A preallocated latency histogram: 100 ns bins up to 20 ms, then one overflow bin. Allocation-free to add.</summary>
sealed class Histogram {
    const long BinNs = 100;
    const int Bins = 200_000;
    readonly long[] counts = new long[Bins + 1];

    public long Count { get; private set; }

    public long MaxNs { get; private set; }

    public void Add(long ns) {
        if (ns < 0) {
            ns = 0;
        }

        var bin = ns / BinNs;
        counts[bin >= Bins ? Bins : bin]++;
        Count++;
        if (ns > MaxNs) {
            MaxNs = ns;
        }
    }

    /// <summary>The upper edge of the bin holding quantile <paramref name="q" />, in microseconds.</summary>
    public double PercentileUs(double q) {
        if (Count == 0) {
            return 0;
        }

        var target = (long)Math.Ceiling(q * Count);
        long seen = 0;
        for (var i = 0; i <= Bins; i++) {
            seen += counts[i];
            if (seen >= target) {
                return i == Bins ? MaxNs / 1000.0 : Math.Min((i + 1) * BinNs, MaxNs) / 1000.0;
            }
        }

        return MaxNs / 1000.0;
    }

    /// <summary>How many samples exceeded <paramref name="ns" />.</summary>
    public long Above(long ns) {
        long n = 0;
        for (var i = (int)Math.Min(Bins, ns / BinNs); i <= Bins; i++) {
            n += counts[i];
        }

        return n;
    }
}
