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
///     <see cref="CoalesceWindow" /> merge into one step, so a fader or knob drag undoes in one go. A key starting
///     <see cref="PassPrefix" /> is an automation recording pass (docs/05-phase-4.md, "Automation"): its batches merge
///     whatever they touch and however far apart they come, so a pass undoes in one go.
/// </summary>
public sealed class EditHistory(TimeProvider time, int capacity = EditHistory.DefaultCapacity) {
    public const int DefaultCapacity = 500;

    public static readonly TimeSpan CoalesceWindow = TimeSpan.FromSeconds(1);

    /// <summary>The coalescing key prefix of an automation recording pass.</summary>
    public const string PassPrefix = "pass|";

    readonly LinkedList<UndoStep> undo = new();
    readonly Stack<UndoStep> redo = new();

    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    /// <summary>Records an applied batch.</summary>
    public void Push(string label, long revision, EditBatchOps ops, string? coalesceKey) {
        var now = time.GetUtcNow();
        redo.Clear();
        if (coalesceKey is not null
            && coalesceKey.StartsWith(PassPrefix, StringComparison.Ordinal)
            && undo.Last?.Value is { } pass
            && pass.CoalesceKey == coalesceKey) {
            // A recording pass: each batch may touch other objects, so the operations add up, kept to the last one
            // of each object either way (every operation sets its object whole).
            undo.RemoveLast();
            undo.AddLast(
                pass with {
                    Revision = revision,
                    Forward = Compact([..pass.Forward, ..ops.Forward]),
                    Inverse = Compact([..ops.Inverse, ..pass.Inverse]),
                    Time = now
                }
            );
            return;
        }

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

    /// <summary>
    ///     Operations applied in order, without those a later one on the same object overrides (an upsert or removal
    ///     of one track, region, lane… sets it whole; a track's index comes with its last upsert).
    /// </summary>
    public static IReadOnlyList<DeltaOp> Compact(IReadOnlyList<DeltaOp> ops) {
        var last = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < ops.Count; i++) {
            if (Key(ops[i]) is { } key) {
                last[key] = i;
            }
        }

        var kept = new List<DeltaOp>(last.Count);
        for (var i = 0; i < ops.Count; i++) {
            if (Key(ops[i]) is not { } key || last[key] == i) {
                kept.Add(ops[i]);
            }
        }

        return kept;
    }

    /// <summary>The object an operation sets whole, or null when it must be kept (anything else).</summary>
    static string? Key(DeltaOp op) =>
        op switch {
            TrackUpserted(var track, _) => "track|" + track.Id,
            AutomationLaneUpserted(var lane) => "lane|" + lane.Id,
            AutomationLaneRemoved(var id) => "lane|" + id,
            MasterChanged => "master",
            _ => null
        };

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
                    SetTempoEventsCommand => "Change Tempo",
                    SetSignaturesCommand => "Change Time Signature",
                    SetKeysCommand => "Change Key",
                    AddMarkerCommand => "Create Marker",
                    SetMarkerCommand => "Change Marker",
                    DeleteMarkersCommand => "Delete Markers",
                    AddSectionCommand => "Create Arrangement Marker",
                    SetSectionCommand => "Change Arrangement Marker",
                    DeleteSectionsCommand => "Delete Arrangement Markers",
                    MoveSectionCommand { Copy: true } => "Copy Arrangement Section",
                    MoveSectionCommand => "Move Arrangement Section",
                    SetCompCommand => "Quick Swipe Comping",
                    SelectTakeCommand => "Select Take",
                    SetTakeFolderCommand => "Change Take Folder",
                    DeleteTakeCommand => "Delete Take",
                    FlattenTakeFolderCommand => "Flatten Take Folder",
                    PackTakeFolderCommand => "Pack Take Folder",
                    SetRecordingCommand => "Change Recording Settings",
                    CreateStackCommand => "Create Track Stack",
                    RemoveStackCommand => "Remove Track Stack",
                    SetTrackParentCommand => "Change Track Stack",
                    SetProjectNotesCommand => "Change Project Notes",
                    JoinRegionsCommand => "Join Regions",
                    CopyRegionsCommand => "Copy Regions",
                    AddInsertCommand { Chain: InsertChain.MidiEffects } => "Add MIDI FX",
                    RemoveInsertCommand { Chain: InsertChain.MidiEffects } => "Remove MIDI FX",
                    MoveInsertCommand { Chain: InsertChain.MidiEffects } => "Move MIDI FX",
                    SetInsertCommand { Chain: InsertChain.MidiEffects } => "Change MIDI FX",
                    SetInsertParameterCommand { Chain: InsertChain.MidiEffects } => "Change MIDI FX Parameter",
                    AddInsertCommand => "Add Plug-in",
                    RemoveInsertCommand => "Remove Plug-in",
                    MoveInsertCommand => "Move Plug-in",
                    SetInsertCommand => "Change Plug-in",
                    SetInsertParameterCommand => "Change Plug-in Parameter",
                    AddSendCommand => "Add Send",
                    SetSendCommand => "Change Send",
                    RemoveSendCommand => "Remove Send",
                    SetOutputCommand => "Change Output",
                    AddAuxCommand => "Create Aux",
                    SetGroupCommand => "Change Group",
                    DeleteGroupCommand => "Delete Group",
                    SetTrackGroupsCommand => "Change Group Assignment",
                    AddVcaCommand => "Create VCA",
                    AssignVcaCommand => "Change VCA Assignment",
                    SetAutomationCommand => "Change Automation",
                    SetAutomationModeCommand => "Change Automation Mode",
                    SetMixerSettingsCommand => "Change Mixer Settings",
                    ApplyPatchCommand => "Load Patch",
                    UnfreezeTrackCommand => "Unfreeze Track",
                    _ => Phase6(batch.Commands[0])
                };

    static string Phase6(EditCommand command) =>
        command switch {
            AddPatternRegionCommand => "Create Pattern Region",
            SetPatternRegionCommand => "Change Pattern Region",
            SetPatternCommand => "Edit Pattern",
            ConvertPatternToMidiCommand => "Convert Pattern to MIDI Region",
            ReseedPatternCommand => "Randomize Pattern",
            CreateDrumMachineCommand => "Create Drum Machine Designer",
            AddDrumPadCommand => "Add Pad",
            SetDrumPadCommand => "Change Pad",
            RemoveDrumPadCommand => "Remove Pad",
            SetSmartControlsCommand => "Change Smart Controls",
            SetSmartControlCommand => "Change Smart Control",
            SetSmartMappingsCommand => "Change Smart Control Mapping",
            AutoMapSmartControlsCommand => "Map Smart Controls",
            CompareSmartControlsCommand => "Compare Smart Controls",
            AddSceneCommand => "Create Scene",
            RemoveSceneCommand => "Delete Scene",
            SetSceneCommand => "Change Scene",
            SetCellCommand => "Change Cell",
            DeleteCellsCommand => "Delete Cells",
            MoveCellCommand { Copy: true } => "Copy Cell",
            MoveCellCommand => "Move Cell",
            SetLiveLoopsCommand => "Change Live Loops",
            SetTrackActivationCommand => "Change Track Activation",
            _ => "Edit"
        };

    /// <summary>
    ///     The control a batch of only <c>setStrip</c> commands moves (the strip and the fields it sets), or of only
    ///     <c>setParameter</c>, <c>setInsertParameter</c>, send level or pan, or <c>setAutomation</c> commands on one
    ///     target, or null for any other batch: two batches with the same key within the window are one undo step.
    /// </summary>
    public static string? CoalesceKey(CommandBatch batch) {
        string? key = null;
        foreach (var command in batch.Commands) {
            var mine = command switch {
                SetStripCommand strip => $"strip|{strip.TrackId ?? "master"}|{strip.VolumeDb.HasValue}"
                    + $"{strip.Pan.HasValue}{strip.Mute.HasValue}{strip.Solo.HasValue}",
                SetParameterCommand parameter => $"parameter|{parameter.TrackId}|{parameter.ParameterId}",
                SetInsertParameterCommand insert =>
                    $"insert|{insert.TrackId ?? "master"}|{insert.InsertId}|{insert.ParameterId}",
                SetSmartControlCommand { Name: null, Value: not null } smart =>
                    $"smart|{smart.TrackId}|{smart.ControlId}",
                SetSendCommand { Bus: null, PreFader: null } send =>
                    $"send|{send.TrackId}|{send.SendId}|{send.LevelDb.HasValue}{send.Pan.HasValue}",
                SetAutomationCommand lane => $"automation|{lane.TrackId ?? "master"}|{lane.Target}",
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
