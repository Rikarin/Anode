namespace Anode.Engine.Midi;

/// <summary>
///     A lock-free, preallocated single-producer single-consumer queue of small structs, for MIDI between the audio
///     thread and the threads beside it. Where several threads produce (live MIDI from devices and from the server),
///     they serialise among themselves with <see cref="ProducerGate" />; the audio thread never takes it.
/// </summary>
public sealed class SpscChannel<T> where T : struct {
    readonly T[] items;
    readonly int mask;
    long head; // consumer-owned
    long tail; // producer-owned

    /// <param name="capacity">Rounded up to a power of two.</param>
    public SpscChannel(int capacity) {
        var size = 1;
        while (size < capacity) {
            size <<= 1;
        }

        items = new T[size];
        mask = size - 1;
    }

    public int Capacity => items.Length;

    /// <summary>For producers that are not alone (never the audio thread).</summary>
    public Lock ProducerGate { get; } = new();

    /// <summary>Items queued now (approximate from either side).</summary>
    public int Count => (int)(Volatile.Read(ref tail) - Volatile.Read(ref head));

    /// <summary>Producer: false (and the item is dropped) when the queue is full.</summary>
    public bool TryEnqueue(in T item) {
        if (tail - Volatile.Read(ref head) >= items.Length) {
            return false;
        }

        items[tail & mask] = item;
        Volatile.Write(ref tail, tail + 1);
        return true;
    }

    /// <summary>Consumer.</summary>
    public bool TryPeek(out T item) {
        if (head == Volatile.Read(ref tail)) {
            item = default;
            return false;
        }

        item = items[head & mask];
        return true;
    }

    /// <summary>Consumer.</summary>
    public bool TryDequeue(out T item) {
        if (!TryPeek(out item)) {
            return false;
        }

        Volatile.Write(ref head, head + 1);
        return true;
    }

    /// <summary>Consumer: drops everything queued.</summary>
    public void Clear() => Volatile.Write(ref head, Volatile.Read(ref tail));
}
