using System.Text.Json.Serialization;

namespace Anode.Contracts;

/// <summary>
///     An edit of the open project (docs/adr/0004-state-ownership.md). A batch of them is applied atomically as one
///     undo step. Optional properties left out are left unchanged.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AddTrackCommand), "addTrack")]
[JsonDerivedType(typeof(RemoveTrackCommand), "removeTrack")]
[JsonDerivedType(typeof(MoveTrackCommand), "moveTrack")]
[JsonDerivedType(typeof(SetTrackCommand), "setTrack")]
[JsonDerivedType(typeof(SetStripCommand), "setStrip")]
[JsonDerivedType(typeof(AddRegionCommand), "addRegion")]
[JsonDerivedType(typeof(MoveRegionCommand), "moveRegion")]
[JsonDerivedType(typeof(ResizeRegionCommand), "resizeRegion")]
[JsonDerivedType(typeof(SplitRegionCommand), "splitRegion")]
[JsonDerivedType(typeof(SetRegionCommand), "setRegion")]
[JsonDerivedType(typeof(DeleteRegionsCommand), "deleteRegions")]
[JsonDerivedType(typeof(SetTempoCommand), "setTempo")]
[JsonDerivedType(typeof(SetSignatureCommand), "setSignature")]
[JsonDerivedType(typeof(SetCycleCommand), "setCycle")]
[JsonDerivedType(typeof(SetMetronomeCommand), "setMetronome")]
[JsonDerivedType(typeof(RenameProjectCommand), "renameProject")]
[JsonDerivedType(typeof(SetInstrumentCommand), "setInstrument")]
[JsonDerivedType(typeof(SetParameterCommand), "setParameter")]
[JsonDerivedType(typeof(LoadInstrumentFileCommand), "loadInstrumentFile")]
[JsonDerivedType(typeof(SetMidiIoCommand), "setMidiIo")]
[JsonDerivedType(typeof(AddMidiRegionCommand), "addMidiRegion")]
[JsonDerivedType(typeof(ResizeMidiRegionCommand), "resizeMidiRegion")]
[JsonDerivedType(typeof(SetMidiRegionCommand), "setMidiRegion")]
[JsonDerivedType(typeof(AddNotesCommand), "addNotes")]
[JsonDerivedType(typeof(EditNotesCommand), "editNotes")]
[JsonDerivedType(typeof(DeleteNotesCommand), "deleteNotes")]
[JsonDerivedType(typeof(QuantizeNotesCommand), "quantizeNotes")]
[JsonDerivedType(typeof(AddControlsCommand), "addControls")]
[JsonDerivedType(typeof(DeleteControlsCommand), "deleteControls")]
[JsonDerivedType(typeof(SetMidiRecordingCommand), "setMidiRecording")]
public abstract record EditCommand;

/// <summary>
///     Adds a track at <c>index</c> (the end when left out): audio by default; an instrument track gets
///     <c>pluginId</c> (the default synth when left out).
/// </summary>
public sealed record AddTrackCommand(
    string? Name = null,
    int? Index = null,
    bool Stereo = false,
    TrackKind? Kind = null,
    string? PluginId = null
) : EditCommand;

public sealed record RemoveTrackCommand(string TrackId) : EditCommand;

public sealed record MoveTrackCommand(string TrackId, int Index) : EditCommand;

public sealed record SetTrackCommand(
    string TrackId,
    string? Name = null,
    string? Color = null,
    TrackInput? Input = null,
    bool? RecordArmed = null,
    bool? InputMonitoring = null
) : EditCommand;

/// <summary>Changes a track's channel strip, or the master's when <c>trackId</c> is left out.</summary>
public sealed record SetStripCommand(
    string? TrackId = null,
    double? VolumeDb = null,
    double? Pan = null,
    bool? Mute = null,
    bool? Solo = null
) : EditCommand;

/// <summary>Places a file on a track; the whole file unless <c>fileOffset</c> and <c>length</c> say otherwise.</summary>
public sealed record AddRegionCommand(
    string TrackId,
    string FileId,
    long StartTick,
    long? FileOffset = null,
    long? Length = null
) : EditCommand;

/// <summary>
///     Moves a region (audio or MIDI) in time, and to another track of a kind that can hold it when <c>trackId</c> is
///     given. <c>splitRegion</c> and <c>deleteRegions</c> also take either kind.
/// </summary>
public sealed record MoveRegionCommand(string RegionId, long StartTick, string? TrackId = null) : EditCommand;

/// <summary>Trims a region: its new start, and the part of the file it plays.</summary>
public sealed record ResizeRegionCommand(string RegionId, long StartTick, long FileOffset, long Length)
    : EditCommand;

/// <summary>Splits a region in two at <c>atTick</c>, which must fall inside it.</summary>
public sealed record SplitRegionCommand(string RegionId, long AtTick) : EditCommand;

public sealed record SetRegionCommand(
    string RegionId,
    string? Name = null,
    double? GainDb = null,
    long? FadeIn = null,
    long? FadeOut = null
) : EditCommand;

public sealed record DeleteRegionsCommand(IReadOnlyList<string> RegionIds) : EditCommand;

public sealed record SetTempoCommand(double Bpm) : EditCommand;

public sealed record SetSignatureCommand(int Numerator, int Denominator) : EditCommand;

public sealed record SetCycleCommand(bool? Enabled = null, long? StartTick = null, long? EndTick = null)
    : EditCommand;

public sealed record SetMetronomeCommand(bool? Enabled = null, bool? CountIn = null, double? LevelDb = null)
    : EditCommand;

public sealed record RenameProjectCommand(string Name) : EditCommand;

/// <summary>Replaces an instrument track's instrument; its parameters start at their defaults.</summary>
public sealed record SetInstrumentCommand(string TrackId, string PluginId) : EditCommand;

/// <summary>
///     Sets one parameter of a track's instrument. Consecutive batches on the same parameter within 1 s coalesce into
///     one undo step, as <c>setStrip</c> does.
/// </summary>
public sealed record SetParameterCommand(string TrackId, string ParameterId, double Value) : EditCommand;

/// <summary>Loads a file (a sampler instrument, a sample) into a track's instrument; a path on the server's disk.</summary>
public sealed record LoadInstrumentFileCommand(string TrackId, string Path) : EditCommand;

public sealed record SetMidiIoCommand(
    string TrackId,
    MidiInputSettings? Input = null,
    MidiOutputSettings? Output = null
) : EditCommand;

/// <summary>Adds an empty MIDI region to an instrument or external MIDI track.</summary>
public sealed record AddMidiRegionCommand(string TrackId, long StartTick, long LengthTicks, string? Name = null)
    : EditCommand;

/// <summary>
///     Moves a MIDI region's start and end. Its notes stay where they are in time: moving the start shifts their
///     relative positions, and notes that fall outside are kept but do not play.
/// </summary>
public sealed record ResizeMidiRegionCommand(string RegionId, long StartTick, long LengthTicks) : EditCommand;

public sealed record SetMidiRegionCommand(
    string RegionId,
    string? Name = null,
    int? Transpose = null,
    int? Velocity = null,
    int? QuantizeTicks = null,
    double? QuantizeStrength = null,
    double? Swing = null
) : EditCommand;

public sealed record AddNotesCommand(string RegionId, IReadOnlyList<NewNote> Notes) : EditCommand;

public sealed record EditNotesCommand(string RegionId, IReadOnlyList<NoteChange> Changes) : EditCommand;

public sealed record DeleteNotesCommand(string RegionId, IReadOnlyList<long> NoteIds) : EditCommand;

/// <summary>
///     Moves note starts towards the grid (destructive; <c>noteIds</c> left out: every note of the region), with the
///     same strength and swing meaning as <see cref="RegionPlayback" />.
/// </summary>
public sealed record QuantizeNotesCommand(
    string RegionId,
    int GridTicks,
    double Strength = 1,
    double Swing = 0,
    IReadOnlyList<long>? NoteIds = null
) : EditCommand;

public sealed record AddControlsCommand(string RegionId, IReadOnlyList<NewControlEvent> Events) : EditCommand;

public sealed record DeleteControlsCommand(string RegionId, IReadOnlyList<long> EventIds) : EditCommand;

public sealed record SetMidiRecordingCommand(bool Merge) : EditCommand;

/// <summary>Edits applied as one undo step; <c>label</c> names it in the undo history ("Move Region").</summary>
public sealed record CommandBatch(IReadOnlyList<EditCommand> Commands, string? Label = null);

/// <summary>What an edit, undo or redo did: the new revision and the delta every client receives too.</summary>
public sealed record CommandResult(long Revision, ProjectDelta Delta);

public sealed record HistoryEntry(string Label, long Revision);

/// <summary>The undo stack (most recent first) and the redo stack (next first).</summary>
public sealed record UndoHistory(IReadOnlyList<HistoryEntry> Undo, IReadOnlyList<HistoryEntry> Redo);
