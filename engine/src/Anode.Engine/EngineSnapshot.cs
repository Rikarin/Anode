namespace Anode.Engine;

/// <summary>What the audio thread publishes after every callback, for the disk, control and telemetry threads.</summary>
public readonly record struct EngineSnapshot {
    /// <summary>Frames rolled since the engine started (the transport's clock), at the start of the last callback.</summary>
    public long Clock { get; init; }

    /// <summary>The playhead at the start of the last callback.</summary>
    public long Position { get; init; }

    /// <summary>The transport state; a count-in reports <see cref="TransportState.Recording" />.</summary>
    public TransportState State { get; init; }

    /// <summary>A count-in is running (the playhead is held).</summary>
    public bool CountIn { get; init; }

    /// <summary>The play order's current anchor.</summary>
    public Anchor Anchor { get; init; }

    /// <summary>The id of the latest hard anchor: audio queued under older anchors never plays.</summary>
    public long HardFloor { get; init; }

    /// <summary>When the last callback started, on the monotonic clock, in nanoseconds.</summary>
    public long HostTimeNs { get; init; }

    public long ModelVersion { get; init; }

    public long Callbacks { get; init; }

    /// <summary>Track-callbacks that rolled with less audio than they needed.</summary>
    public long Underruns { get; init; }

    /// <summary>Commands the audio thread has applied (compare with <see cref="CommandRing.Enqueued" />).</summary>
    public long CommandsApplied { get; init; }
}

/// <summary>A seqlock around one <see cref="EngineSnapshot" />: one writer that never waits, readers that retry.</summary>
public sealed class SnapshotSeqlock {
    long sequence;
    EngineSnapshot data;

    /// <summary>Writer (the audio thread).</summary>
    public void Write(in EngineSnapshot snapshot) {
        Interlocked.Increment(ref sequence); // odd: a write is in progress (a full fence before the data)
        data = snapshot;
        Volatile.Write(ref sequence, sequence + 1); // even again; release orders the data before it
    }

    /// <summary>Any reader: a consistent copy.</summary>
    public EngineSnapshot Read() {
        var spin = new SpinWait();
        while (true) {
            var before = Volatile.Read(ref sequence);
            if ((before & 1) == 0) {
                var copy = data;
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref sequence) == before) {
                    return copy;
                }
            }

            spin.SpinOnce(-1);
        }
    }
}
