using System.Runtime.InteropServices;

namespace Anode.Engine.Streaming;

/// <summary>
///     A lock-free single-producer single-consumer ring of float samples in native, 64-byte aligned memory, so
///     multi-second rings for many tracks neither sit on the large object heap nor count toward a GC budget (spike S2).
///     The counters only grow. Producer and consumer each own one counter and read the other's with acquire.
/// </summary>
public sealed unsafe class SpscRing : IDisposable {
    float* data;
    long written; // producer-owned
    long read; // consumer-owned

    public SpscRing(int capacity) {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        data = (float*)NativeMemory.AlignedAlloc((nuint)capacity * sizeof(float), 64);
        NativeMemory.Clear(data, (nuint)capacity * sizeof(float));
    }

    public int Capacity { get; }

    /// <summary>Consumer side: samples readable now.</summary>
    public int Available => (int)(Volatile.Read(ref written) - read);

    /// <summary>Producer side: samples writable now.</summary>
    public int Free => Capacity - (int)(written - Volatile.Read(ref read));

    /// <summary>Producer side: the next <paramref name="count" /> writable samples, as two spans (wrapping).</summary>
    public void WriteRegions(int count, out Span<float> first, out Span<float> second) {
        var start = (int)(written % Capacity);
        var head = Math.Min(count, Capacity - start);
        first = new(data + start, head);
        second = new(data, count - head);
    }

    /// <summary>Producer side: copies <paramref name="samples" /> in and publishes them (must fit <see cref="Free" />).</summary>
    public void Write(ReadOnlySpan<float> samples) {
        WriteRegions(samples.Length, out var first, out var second);
        samples[..first.Length].CopyTo(first);
        samples[first.Length..].CopyTo(second);
        CommitWrite(samples.Length);
    }

    /// <summary>Producer side: publishes <paramref name="count" /> samples written through <see cref="WriteRegions" />.</summary>
    public void CommitWrite(int count) => Volatile.Write(ref written, written + count);

    /// <summary>Consumer side: the next <paramref name="count" /> readable samples, as two spans (wrapping).</summary>
    public void ReadRegions(int count, out ReadOnlySpan<float> first, out ReadOnlySpan<float> second) {
        var start = (int)(read % Capacity);
        var head = Math.Min(count, Capacity - start);
        first = new(data + start, head);
        second = new(data, count - head);
    }

    /// <summary>Consumer side: copies out the next <paramref name="destination" />.Length samples and consumes them.</summary>
    public void Read(Span<float> destination) {
        ReadRegions(destination.Length, out var first, out var second);
        first.CopyTo(destination);
        second.CopyTo(destination[first.Length..]);
        Skip(destination.Length);
    }

    /// <summary>Consumer side: consumes <paramref name="count" /> samples (no more than <see cref="Available" />).</summary>
    public void Skip(int count) => Volatile.Write(ref read, read + count);

    public void Dispose() {
        if (data is not null) {
            NativeMemory.AlignedFree(data);
            data = null;
        }
    }
}
