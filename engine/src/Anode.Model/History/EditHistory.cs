using Anode.Contracts;

namespace Anode.Model.History;

/// <summary>One undo step: what it is called, the revision it last produced, and the operations either way.</summary>
public sealed record UndoStep(
    string Label,
    long Revision,
    IReadOnlyList<DeltaOp> Forward,
    IReadOnlyList<DeltaOp> Inverse,
    string? CoalesceKey,
    DateTimeOffset Time
);

/// <summary>
///     The project's undo history: one stack (docs/02-phase-1.md). Pushing an edit clears the redo stack. Consecutive
///     <c>setStrip</c> batches on the same control (and <c>setParameter</c> batches on the same parameter) within
///     <see cref="CoalesceWindow" /> merge into one step, so a fader or knob drag undoes in one go.
/// </summary>
public sealed class EditHistory(TimeProvider time, int capacity = EditHistory.DefaultCapacity) {
    public const int DefaultCapacity = 500;

    public static readonly TimeSpan CoalesceWindow = TimeSpan.FromSeconds(1);

    readonly LinkedList<UndoStep> undo = new();
    readonly Stack<UndoStep> redo = new();

    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    /// <summary>Records an applied batch.</summary>
    public void Push(string label, long revision, EditBatchOps ops, string? coalesceKey) {
        var now = time.GetUtcNow();
        redo.Clear();
        if (coalesceKey is not null
            && undo.Last?.Value is { } last
            && last.CoalesceKey == coalesceKey
            && now - last.Time <= CoalesceWindow) {
            // Strip and parameter edits upsert the whole strip or track: the newest edit's operations lead to the end
            // of the drag and the first one's inverse back to before it, with nothing in between to keep.
            undo.RemoveLast();
            undo.AddLast(last with { Revision = revision, Forward = ops.Forward, Time = now });
            return;
        }

        undo.AddLast(new UndoStep(label, revision, ops.Forward, ops.Inverse, coalesceKey, now));
        if (undo.Count > capacity) {
            undo.RemoveFirst();
        }
    }

    /// <summary>The step undo would undo; the caller applies its inverse, then calls <see cref="Undone" />.</summary>
    public UndoStep? PeekUndo() => undo.Last?.Value;

    public UndoStep? PeekRedo() => redo.Count > 0 ? redo.Peek() : null;

    /// <summary>The step <see cref="PeekUndo" /> gave was undone, producing <paramref name="revision" />.</summary>
    public void Undone(long revision) {
        var step = undo.Last!.Value;
        undo.RemoveLast();
        redo.Push(step with { Revision = revision, CoalesceKey = null });
    }

    /// <summary>The step <see cref="PeekRedo" /> gave was redone, producing <paramref name="revision" />.</summary>
    public void Redone(long revision) {
        var step = redo.Pop();
        undo.AddLast(step with { Revision = revision, CoalesceKey = null });
    }

    public UndoHistory History() =>
        new(
            [..undo.Reverse().Select(static s => new HistoryEntry(s.Label, s.Revision))],
            [..redo.Select(static s => new HistoryEntry(s.Label, s.Revision))]
        );

    public void Clear() {
        undo.Clear();
        redo.Clear();
    }
}

/// <summary>The operations of an applied batch, forward and back.</summary>
public sealed record EditBatchOps(IReadOnlyList<DeltaOp> Forward, IReadOnlyList<DeltaOp> Inverse);

/// <summary>Names undo steps and decides which batches coalesce.</summary>
public static class UndoLabels {
    /// <summary>The batch's label, or one made from its first command ("Move Region").</summary>
    public static string For(CommandBatch batch) =>
        batch.Label is { Length: > 0 } label
            ? label
            : batch.Commands.Count == 0
                ? "Edit"
                : batch.Commands[0] switch {
                    AddTrackCommand => "Add Track",
                    RemoveTrackCommand => "Remove Track",
                    MoveTrackCommand => "Move Track",
                    SetTrackCommand => "Change Track",
                    SetStripCommand => "Change Channel Strip",
                    AddRegionCommand => "Add Region",
                    MoveRegionCommand => "Move Region",
                    ResizeRegionCommand => "Resize Region",
                    SplitRegionCommand => "Split Region",
                    SetRegionCommand => "Change Region",
                    DeleteRegionsCommand => "Delete Regions",
                    SetTempoCommand => "Change Tempo",
                    SetSignatureCommand => "Change Time Signature",
                    SetCycleCommand => "Change Cycle",
                    SetMetronomeCommand => "Change Metronome",
                    RenameProjectCommand => "Rename Project",
                    SetInstrumentCommand => "Change Instrument",
                    SetParameterCommand => "Change Parameter",
                    LoadInstrumentFileCommand => "Load Instrument File",
                    SetMidiIoCommand => "Change MIDI Settings",
                    AddMidiRegionCommand => "Add MIDI Region",
                    ResizeMidiRegionCommand => "Resize Region",
                    SetMidiRegionCommand => "Change Region",
                    AddNotesCommand => "Add Notes",
                    EditNotesCommand => "Edit Notes",
                    DeleteNotesCommand => "Delete Notes",
                    QuantizeNotesCommand => "Quantize Notes",
                    AddControlsCommand => "Add Controller Events",
                    DeleteControlsCommand => "Delete Controller Events",
                    SetMidiRecordingCommand => "Change MIDI Recording",
                    _ => "Edit"
                };

    /// <summary>
    ///     The control a batch of only <c>setStrip</c> commands moves (the strip and the fields it sets), or of only
    ///     <c>setParameter</c> commands on one parameter, or null for any other batch: two batches with the same key
    ///     within the window are one undo step.
    /// </summary>
    public static string? CoalesceKey(CommandBatch batch) {
        string? key = null;
        foreach (var command in batch.Commands) {
            var mine = command switch {
                SetStripCommand strip => $"strip|{strip.TrackId ?? "master"}|{strip.VolumeDb.HasValue}"
                    + $"{strip.Pan.HasValue}{strip.Mute.HasValue}{strip.Solo.HasValue}",
                SetParameterCommand parameter => $"parameter|{parameter.TrackId}|{parameter.ParameterId}",
                _ => null
            };
            if (mine is null) {
                return null;
            }
            if (key is not null && key != mine) {
                return null;
            }

            key = mine;
        }

        return key;
    }
}
