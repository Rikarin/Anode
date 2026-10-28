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
[JsonDerivedType(typeof(SetTempoEventsCommand), "setTempoEvents")]
[JsonDerivedType(typeof(SetSignaturesCommand), "setSignatures")]
[JsonDerivedType(typeof(SetKeysCommand), "setKeys")]
[JsonDerivedType(typeof(AddMarkerCommand), "addMarker")]
[JsonDerivedType(typeof(SetMarkerCommand), "setMarker")]
[JsonDerivedType(typeof(DeleteMarkersCommand), "deleteMarkers")]
[JsonDerivedType(typeof(AddSectionCommand), "addSection")]
[JsonDerivedType(typeof(SetSectionCommand), "setSection")]
[JsonDerivedType(typeof(DeleteSectionsCommand), "deleteSections")]
[JsonDerivedType(typeof(MoveSectionCommand), "moveSection")]
[JsonDerivedType(typeof(SetCompCommand), "setComp")]
[JsonDerivedType(typeof(SelectTakeCommand), "selectTake")]
[JsonDerivedType(typeof(SetTakeFolderCommand), "setTakeFolder")]
[JsonDerivedType(typeof(DeleteTakeCommand), "deleteTake")]
[JsonDerivedType(typeof(FlattenTakeFolderCommand), "flattenTakeFolder")]
[JsonDerivedType(typeof(PackTakeFolderCommand), "packTakeFolder")]
[JsonDerivedType(typeof(SetRecordingCommand), "setRecording")]
[JsonDerivedType(typeof(CreateStackCommand), "createStack")]
[JsonDerivedType(typeof(RemoveStackCommand), "removeStack")]
[JsonDerivedType(typeof(SetTrackParentCommand), "setTrackParent")]
[JsonDerivedType(typeof(SetProjectNotesCommand), "setProjectNotes")]
[JsonDerivedType(typeof(JoinRegionsCommand), "joinRegions")]
[JsonDerivedType(typeof(CopyRegionsCommand), "copyRegions")]
[JsonDerivedType(typeof(AddInsertCommand), "addInsert")]
[JsonDerivedType(typeof(RemoveInsertCommand), "removeInsert")]
[JsonDerivedType(typeof(MoveInsertCommand), "moveInsert")]
[JsonDerivedType(typeof(SetInsertCommand), "setInsert")]
[JsonDerivedType(typeof(SetInsertParameterCommand), "setInsertParameter")]
[JsonDerivedType(typeof(AddSendCommand), "addSend")]
[JsonDerivedType(typeof(SetSendCommand), "setSend")]
[JsonDerivedType(typeof(RemoveSendCommand), "removeSend")]
[JsonDerivedType(typeof(SetOutputCommand), "setOutput")]
[JsonDerivedType(typeof(AddAuxCommand), "addAux")]
[JsonDerivedType(typeof(SetGroupCommand), "setGroup")]
[JsonDerivedType(typeof(DeleteGroupCommand), "deleteGroup")]
[JsonDerivedType(typeof(SetTrackGroupsCommand), "setTrackGroups")]
[JsonDerivedType(typeof(AddVcaCommand), "addVca")]
[JsonDerivedType(typeof(AssignVcaCommand), "assignVca")]
[JsonDerivedType(typeof(SetAutomationCommand), "setAutomation")]
[JsonDerivedType(typeof(SetAutomationModeCommand), "setAutomationMode")]
[JsonDerivedType(typeof(SetMixerSettingsCommand), "setMixerSettings")]
[JsonDerivedType(typeof(ApplyPatchCommand), "applyPatch")]
[JsonDerivedType(typeof(UnfreezeTrackCommand), "unfreezeTrack")]
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
    bool? InputMonitoring = null,
    bool? Collapsed = null,
    string? Notes = null
) : EditCommand;

/// <summary>
///     Changes a track's volume, pan, mute or solo, or the master's when <c>trackId</c> is left out. Members of the
///     track's enabled mixer groups that link the property follow it (volume keeps their offsets). During playback, a
///     track whose automation mode is touch, latch or write records the change into its lane (and the batch coalesces as
///     a drag does).
/// </summary>
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
///     Moves a region (audio, MIDI, or a take folder) in time, and to another track of a kind that can hold it when
///     <c>trackId</c> is given. <c>deleteRegions</c> also takes every kind; <c>splitRegion</c> takes audio and MIDI.
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
    long? FadeOut = null,
    double? FadeInCurve = null,
    double? FadeOutCurve = null,
    bool? Muted = null
) : EditCommand;

public sealed record DeleteRegionsCommand(IReadOnlyList<string> RegionIds) : EditCommand;

/// <summary>Sets the tempo at the project start (the first tempo event), keeping the rest of the tempo track.</summary>
public sealed record SetTempoCommand(double Bpm) : EditCommand;

/// <summary>Sets the signature at the project start (the first signature event), keeping later changes.</summary>
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
    double? Swing = null,
    bool? Muted = null
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

/// <summary>Replaces the tempo track; the first event must be at tick 0. Audio regions keep their musical positions.</summary>
public sealed record SetTempoEventsCommand(IReadOnlyList<TempoEvent> Events) : EditCommand;

/// <summary>Replaces the signature track; the first event at tick 0, each later one on a bar line.</summary>
public sealed record SetSignaturesCommand(IReadOnlyList<SignatureEvent> Events) : EditCommand;

/// <summary>Replaces the key track; the first event at tick 0.</summary>
public sealed record SetKeysCommand(IReadOnlyList<KeyEvent> Events) : EditCommand;

public sealed record AddMarkerCommand(string Name, long Tick, long LengthTicks = 0, string? Color = null) : EditCommand;

public sealed record SetMarkerCommand(
    string MarkerId,
    string? Name = null,
    long? Tick = null,
    long? LengthTicks = null,
    string? Color = null,
    string? Text = null
) : EditCommand;

public sealed record DeleteMarkersCommand(IReadOnlyList<string> MarkerIds) : EditCommand;

/// <summary>Adds an arrangement section; it must not overlap another.</summary>
public sealed record AddSectionCommand(string Name, long StartTick, long LengthTicks, string? Color = null)
    : EditCommand;

/// <summary>Renames, recolours or resizes a section (from its end); it must not come to overlap another.</summary>
public sealed record SetSectionCommand(
    string SectionId,
    string? Name = null,
    long? LengthTicks = null,
    string? Color = null
) : EditCommand;

/// <summary>
///     Deletes sections. With <c>removeContent</c>, also everything that starts in them, and later material closes the
///     gap (Logic's "delete the section and its content").
/// </summary>
public sealed record DeleteSectionsCommand(IReadOnlyList<string> SectionIds, bool RemoveContent = false)
    : EditCommand;

/// <summary>
///     Moves (or with <c>copy</c>, copies) a section and everything that starts in it to <c>toTick</c>, which must be a
///     section boundary or the end of the last section: the section is inserted there and the material after it moves
///     later by its length; a move then closes the gap it left. Regions, take folders and markers move with it.
/// </summary>
public sealed record MoveSectionCommand(string SectionId, long ToTick, bool Copy = false) : EditCommand;

/// <summary>Replaces a take folder's comp (Quick Swipe Comping sends the whole comp after each swipe).</summary>
public sealed record SetCompCommand(string FolderId, IReadOnlyList<CompSegment> Segments) : EditCommand;

/// <summary>Makes one take the whole comp.</summary>
public sealed record SelectTakeCommand(string FolderId, string TakeId) : EditCommand;

public sealed record SetTakeFolderCommand(
    string FolderId,
    string? Name = null,
    long? Crossfade = null,
    bool? Muted = null,
    string? TakeId = null,
    string? TakeName = null
) : EditCommand;

/// <summary>Removes a take (and its comp segments) from a folder; the folder's last take cannot be removed.</summary>
public sealed record DeleteTakeCommand(string FolderId, string TakeId) : EditCommand;

/// <summary>Replaces a take folder with regions playing its comp, crossfaded at the boundaries.</summary>
public sealed record FlattenTakeFolderCommand(string FolderId) : EditCommand;

/// <summary>Packs overlapping or adjacent audio regions of one track into a new take folder, one take each.</summary>
public sealed record PackTakeFolderCommand(IReadOnlyList<string> RegionIds, string? Name = null) : EditCommand;

public sealed record SetRecordingCommand(
    CycleRecordMode? CycleMode = null,
    bool? Replace = null,
    bool? Autopunch = null,
    long? PunchInTick = null,
    long? PunchOutTick = null
) : EditCommand;

/// <summary>Puts tracks into a new folder stack, inserted where the first of them was.</summary>
public sealed record CreateStackCommand(IReadOnlyList<string> TrackIds, string? Name = null) : EditCommand;

/// <summary>Removes a folder stack; its tracks stay, outside any stack.</summary>
public sealed record RemoveStackCommand(string StackId) : EditCommand;

/// <summary>Moves a track into a stack (<c>parentId</c>) or out of one (null). Stacks do not nest.</summary>
public sealed record SetTrackParentCommand(string TrackId, string? ParentId) : EditCommand;

public sealed record SetProjectNotesCommand(string Text) : EditCommand;

/// <summary>
///     Glues regions of one track into one: MIDI regions merge their notes; audio regions only when they play one
///     contiguous stretch of the same file (others need a bounce, Phase 4), else 422.
/// </summary>
public sealed record JoinRegionsCommand(IReadOnlyList<string> RegionIds) : EditCommand;

/// <summary>
///     Copies regions (of any kind) <c>offsetTicks</c> later (or earlier), onto <c>trackId</c> when given (⌥-drag,
///     repeat).
/// </summary>
public sealed record CopyRegionsCommand(IReadOnlyList<string> RegionIds, long OffsetTicks, string? TrackId = null)
    : EditCommand;

/// <summary>Adds an insert effect at <c>index</c> (the end when left out); the master's when <c>trackId</c> is left out.</summary>
public sealed record AddInsertCommand(string PluginId, string? TrackId = null, int? Index = null) : EditCommand;

public sealed record RemoveInsertCommand(string InsertId, string? TrackId = null) : EditCommand;

/// <summary>Moves an insert to another slot of the same strip.</summary>
public sealed record MoveInsertCommand(string InsertId, int Index, string? TrackId = null) : EditCommand;

/// <summary>Bypasses an insert, or sets its sidechain (a track id, <c>bus:N</c>, or an empty string for none).</summary>
public sealed record SetInsertCommand(
    string InsertId,
    string? TrackId = null,
    bool? Bypassed = null,
    string? Sidechain = null
) : EditCommand;

/// <summary>
///     Sets one insert parameter; coalesces like <c>setParameter</c>, and records automation like <c>setStrip</c>.
/// </summary>
public sealed record SetInsertParameterCommand(
    string InsertId,
    string ParameterId,
    double Value,
    string? TrackId = null)
    : EditCommand;

/// <summary>Adds a send to bus 1…64 (post-fader at −∞ dB unless told otherwise, as Logic does).</summary>
public sealed record AddSendCommand(string TrackId, int Bus, double? LevelDb = null, bool? PreFader = null)
    : EditCommand;

/// <summary>Changes a send; coalesces and records automation like <c>setStrip</c>.</summary>
public sealed record SetSendCommand(
    string TrackId,
    string SendId,
    double? LevelDb = null,
    double? Pan = null,
    bool? PreFader = null,
    int? Bus = null
) : EditCommand;

public sealed record RemoveSendCommand(string TrackId, string SendId) : EditCommand;

/// <summary>Routes a track to <c>master</c>, <c>bus:N</c> or <c>output:N</c>; a route that would make a loop is refused.</summary>
public sealed record SetOutputCommand(string TrackId, string Output) : EditCommand;

/// <summary>Adds an aux track listening to bus <c>bus</c> (the lowest unused bus when left out).</summary>
public sealed record AddAuxCommand(int? Bus = null, string? Name = null) : EditCommand;

/// <summary>Creates or changes mixer group 1…32.</summary>
public sealed record SetGroupCommand(
    int Number,
    string? Name = null,
    string? Color = null,
    bool? Enabled = null,
    GroupProperties? Properties = null
) : EditCommand;

/// <summary>Deletes a mixer group; its members leave it.</summary>
public sealed record DeleteGroupCommand(int Number) : EditCommand;

public sealed record SetTrackGroupsCommand(string TrackId, IReadOnlyList<int> Groups) : EditCommand;

public sealed record AddVcaCommand(string? Name = null) : EditCommand;

/// <summary>Assigns a track to a VCA track, or removes it from its VCA (null).</summary>
public sealed record AssignVcaCommand(string TrackId, string? VcaId) : EditCommand;

/// <summary>Replaces one automation lane's points (an empty list removes the lane); the master's when no track.</summary>
public sealed record SetAutomationCommand(string Target, IReadOnlyList<AutomationPoint> Points, string? TrackId = null)
    : EditCommand;

public sealed record SetAutomationModeCommand(AutomationMode Mode, string? TrackId = null) : EditCommand;

public sealed record SetMixerSettingsCommand(
    PdcMode? Pdc = null,
    bool? LowLatency = null,
    double? LowLatencyLimitMs = null)
    : EditCommand;

/// <summary>Replaces a track's instrument, inserts and sends with a saved patch's.</summary>
public sealed record ApplyPatchCommand(string TrackId, string PatchId) : EditCommand;

/// <summary>Plays a frozen track live again (its freeze file stays in the project's audio).</summary>
public sealed record UnfreezeTrackCommand(string TrackId) : EditCommand;
