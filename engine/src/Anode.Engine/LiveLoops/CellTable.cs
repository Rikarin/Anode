using Anode.Ipc;
using Anode.Ipc.Segments;

namespace Anode.Engine.LiveLoops;

/// <summary>
///     One Live Loops cell as the engine plays it (docs/07-phase-6.md, "Live Loops"): its table entry (index, track,
///     length, play mode), its compiled events (positions relative to its start) in the segment that holds them, and,
///     for an audio cell, its audio rendered whole into memory when the model loaded (warped through the flex
///     renderers then), so a launch never waits for the disk. Immutable once published.
/// </summary>
public sealed class CellContent {
    /// <summary>The most audio a cell holds in memory: 60 s (longer cells are cut there).</summary>
    public const int MaxSeconds = 60;

    public CellContent(CellEntry entry, TrackView view, MappedSegment owner, float[]? audio, int channels, ulong hash) {
        Entry = entry;
        View = view;
        Owner = owner;
        Audio = audio;
        Channels = channels;
        Frames = audio is null ? 0 : audio.Length / Math.Max(1, channels);
        Hash = hash;
    }

    public CellEntry Entry { get; }

    public int Index => Entry.Index;

    public int TrackId => Entry.TrackId;

    /// <summary>The cell's length in samples: a looping cell wraps here, a one-shot ends here.</summary>
    public long Length => Entry.Length;

    public CellMode Mode => Entry.Mode;

    /// <summary>Its events and playlist, positions relative to its start.</summary>
    public TrackView View { get; }

    /// <summary>The segment <see cref="View" /> points into (reference-counted by the models that hold the cell).</summary>
    public MappedSegment Owner { get; }

    /// <summary>An audio cell's audio from its start, <see cref="Channels" /> interleaved; null for a MIDI cell.</summary>
    public float[]? Audio { get; }

    public int Channels { get; }

    /// <summary>Frames of <see cref="Audio" /> (at most <see cref="MaxSeconds" />; silence after).</summary>
    public long Frames { get; }

    /// <summary>What the audio was rendered from (its playlist, maps, files, channels and length), to reuse it.</summary>
    public ulong Hash { get; }
}

/// <summary>The model's cells by index (null where none). Immutable once published.</summary>
public sealed class CellTable {
    public static readonly CellTable Empty = new([]);

    public CellTable(CellContent?[] byIndex) {
        ByIndex = byIndex;
    }

    public CellContent?[] ByIndex { get; }

    /// <summary>Cell <paramref name="index" />, or null.</summary>
    public CellContent? this[int index] => (uint)index < (uint)ByIndex.Length ? ByIndex[index] : null;

    public IEnumerable<CellContent> All => ByIndex.OfType<CellContent>();
}

/// <summary>
///     A cell's state change, from the audio thread to the control thread, which pushes it as a
///     <see cref="CellStateMessage" />.
/// </summary>
public readonly record struct CellStateEvent(int Cell, EngineCellState State, long Sample);

/// <summary>What a launcher command asks.</summary>
public enum LaunchKind : byte {
    /// <summary>Launch cell <see cref="LaunchCommand.Value" />.</summary>
    Launch,

    /// <summary>Stop the cell on engine track <see cref="LaunchCommand.Value" />.</summary>
    Stop
}

/// <summary>One launch or stop of a <see cref="LaunchCellsMessage" /> or <see cref="StopCellsMessage" />.</summary>
public readonly record struct LaunchCommand(LaunchKind Kind, int Value, EngineQuantize Quantize);

/// <summary>
///     The launcher's queue from the control thread to the audio thread: lock-free, preallocated, single producer
///     (callers serialise on <see cref="ProducerGate" />). A message's commands are published together
///     (<see cref="TryEnqueue" /> writes them all, then moves the tail once), so the audio thread applies one message in
///     one block: one decision.
/// </summary>
public sealed class LaunchRing {
    readonly LaunchCommand[] items;
    readonly int mask;
    long head; // consumer-owned
    long tail; // producer-owned

    public LaunchRing(int capacity = 4096) {
        var size = 1;
        while (size < capacity) {
            size <<= 1;
        }

        items = new LaunchCommand[size];
        mask = size - 1;
    }

    public Lock ProducerGate { get; } = new();

    /// <summary>Producer: queues every command or none (false: the queue is full).</summary>
    public bool TryEnqueue(ReadOnlySpan<LaunchCommand> commands) {
        if (tail + commands.Length - Volatile.Read(ref head) > items.Length) {
            return false;
        }

        for (var i = 0; i < commands.Length; i++) {
            items[(tail + i) & mask] = commands[i];
        }

        Volatile.Write(ref tail, tail + commands.Length);
        return true;
    }

    /// <summary>Consumer.</summary>
    public bool TryDequeue(out LaunchCommand command) {
        if (head == Volatile.Read(ref tail)) {
            command = default;
            return false;
        }

        command = items[head & mask];
        Volatile.Write(ref head, head + 1);
        return true;
    }
}
