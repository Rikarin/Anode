using System.Runtime.InteropServices;

namespace Anode.Spikes.DiskStream;

/// <summary>
///     Lock-free single-producer (disk thread) single-consumer (audio thread) ring of float samples in native memory,
///     so 128 multi-second rings neither sit on the large object heap nor count toward a gen2 budget. The counters
///     only grow.
/// </summary>
sealed unsafe class SpscRing : IDisposable {
    readonly float* data;
    long written; // producer-owned, read by the consumer with acquire
    long read; // consumer-owned, read by the producer with acquire

    public SpscRing(int capacity) {
        Capacity = capacity;
        data = (float*)NativeMemory.AlignedAlloc((nuint)Capacity * sizeof(float), 64);
        NativeMemory.Clear(data, (nuint)Capacity * sizeof(float));
    }

    public int Capacity { get; }

    /// <summary>Samples the consumer can read now.</summary>
    public int Available => (int)(Volatile.Read(ref written) - read);

    /// <summary>Samples the producer can write now.</summary>
    public int Free => Capacity - (int)(written - Volatile.Read(ref read));

    /// <summary>Producer side: the writable region as at most two spans (the second is non-empty when it wraps).</summary>
    public void WriteRegions(int count, out Span<float> first, out Span<float> second) {
        var start = (int)(written % Capacity);
        var head = Math.Min(count, Capacity - start);
        first = new(data + start, head);
        second = new(data, count - head);
    }

    /// <summary>Producer side: publishes <paramref name="count" /> samples written through <see cref="WriteRegions" />.</summary>
    public void CommitWrite(int count) => Volatile.Write(ref written, written + count);

    /// <summary>Consumer side: adds the next samples, scaled by the two gains, into two outputs (a panned mono track).</summary>
    public void MixInto(Span<float> left, Span<float> right, float gainLeft, float gainRight) {
        var count = left.Length;
        var start = (int)(read % Capacity);
        var head = Math.Min(count, Capacity - start);
        var a = new ReadOnlySpan<float>(data + start, head);
        for (var i = 0; i < a.Length; i++) {
            left[i] += a[i] * gainLeft;
            right[i] += a[i] * gainRight;
        }

        var b = new ReadOnlySpan<float>(data, count - head);
        for (var i = 0; i < b.Length; i++) {
            left[head + i] += b[i] * gainLeft;
            right[head + i] += b[i] * gainRight;
        }

        Volatile.Write(ref read, read + count);
    }

    /// <summary>Consumer side: the next sample, without consuming it.</summary>
    public float Peek() => data[read % Capacity];

    /// <summary>Consumer side: drops <paramref name="count" /> samples (no more than <see cref="Available" />).</summary>
    public void Skip(int count) => Volatile.Write(ref read, read + count);

    public void Dispose() => NativeMemory.AlignedFree(data);
}
