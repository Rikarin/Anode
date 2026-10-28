namespace Anode.Engine;

/// <summary>A transport command from the control thread, applied by the audio thread at the start of a callback.</summary>
public enum EngineCommandKind : byte {
    Play,
    Stop,

    /// <summary>
    ///     Record from the current position; <see cref="EngineCommand.A" /> count-in frames (0: none),
    ///     <see cref="EngineCommand.B" /> beats.
    /// </summary>
    Record,

    /// <summary>Move the playhead to <see cref="EngineCommand.A" />.</summary>
    Locate,

    /// <summary>
    ///     The cycle: <see cref="EngineCommand.Flag" /> enabled, <see cref="EngineCommand.A" /> start,
    ///     <see cref="EngineCommand.B" /> end.
    /// </summary>
    SetCycle,

    /// <summary>
    ///     Autopunch: <see cref="EngineCommand.Flag" /> enabled, <see cref="EngineCommand.A" /> punch-in,
    ///     <see cref="EngineCommand.B" /> punch-out.
    /// </summary>
    SetPunch
}

public readonly record struct EngineCommand(EngineCommandKind Kind, long A = 0, long B = 0, bool Flag = false);

/// <summary>
///     Lock-free single-producer (control thread) single-consumer (audio thread) queue of commands, preallocated.
/// </summary>
public sealed class CommandRing {
    const int Capacity = 256; // power of two
    readonly EngineCommand[] items = new EngineCommand[Capacity];
    long head; // consumer-owned
    long tail; // producer-owned

    /// <summary>Producer: false when the queue is full (the audio thread has stopped calling back).</summary>
    public bool TryEnqueue(in EngineCommand command) {
        if (tail - Volatile.Read(ref head) >= Capacity) {
            return false;
        }

        items[tail & (Capacity - 1)] = command;
        Volatile.Write(ref tail, tail + 1);
        return true;
    }

    /// <summary>Consumer.</summary>
    public bool TryDequeue(out EngineCommand command) {
        if (head == Volatile.Read(ref tail)) {
            command = default;
            return false;
        }

        command = items[head & (Capacity - 1)];
        Volatile.Write(ref head, head + 1);
        return true;
    }

    /// <summary>Commands enqueued so far (producer side).</summary>
    public long Enqueued => Volatile.Read(ref tail);
}
