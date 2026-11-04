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
[JsonDerivedType(typeof(ConvertLoopsCommand), "convertLoops")]
[JsonDerivedType(typeof(StretchRegionCommand), "stretchRegion")]
[JsonDerivedType(typeof(InsertTimeCommand), "insertTime")]
[JsonDerivedType(typeof(CutTimeCommand), "cutTime")]
[JsonDerivedType(typeof(RippleDeleteCommand), "rippleDelete")]
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
[JsonDerivedType(typeof(PasteRegionsCommand), "pasteRegions")]
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
[JsonDerivedType(typeof(SetTrackFlexCommand), "setTrackFlex")]
[JsonDerivedType(typeof(SetRegionFlexCommand), "setRegionFlex")]
[JsonDerivedType(typeof(SetFlexMarkersCommand), "setFlexMarkers")]
[JsonDerivedType(typeof(QuantizeAudioCommand), "quantizeAudio")]
[JsonDerivedType(typeof(SetPitchNotesCommand), "setPitchNotes")]
[JsonDerivedType(typeof(FlexPitchToMidiCommand), "flexPitchToMidi")]
[JsonDerivedType(typeof(SetFileTempoCommand), "setFileTempo")]
[JsonDerivedType(typeof(AdaptTempoCommand), "adaptTempo")]
[JsonDerivedType(typeof(MapBeatsCommand), "mapBeats")]
[JsonDerivedType(typeof(StripSilenceCommand), "stripSilence")]
[JsonDerivedType(typeof(NewTrackAlternativeCommand), "newTrackAlternative")]
[JsonDerivedType(typeof(SelectTrackAlternativeCommand), "selectTrackAlternative")]
[JsonDerivedType(typeof(SetTrackAlternativeCommand), "setTrackAlternative")]
[JsonDerivedType(typeof(DeleteTrackAlternativeCommand), "deleteTrackAlternative")]
[JsonDerivedType(typeof(SetVarispeedCommand), "setVarispeed")]
[JsonDerivedType(typeof(SetSmartTempoCommand), "setSmartTempo")]
[JsonDerivedType(typeof(AddPatternRegionCommand), "addPatternRegion")]
[JsonDerivedType(typeof(SetPatternRegionCommand), "setPatternRegion")]
[JsonDerivedType(typeof(SetPatternCommand), "setPattern")]
[JsonDerivedType(typeof(ConvertPatternToMidiCommand), "convertPatternToMidi")]
[JsonDerivedType(typeof(ReseedPatternCommand), "reseedPattern")]
[JsonDerivedType(typeof(CreateDrumMachineCommand), "createDrumMachine")]
[JsonDerivedType(typeof(AddDrumPadCommand), "addDrumPad")]
[JsonDerivedType(typeof(SetDrumPadCommand), "setDrumPad")]
[JsonDerivedType(typeof(RemoveDrumPadCommand), "removeDrumPad")]
[JsonDerivedType(typeof(SetSmartControlsCommand), "setSmartControls")]
[JsonDerivedType(typeof(SetSmartControlCommand), "setSmartControl")]
[JsonDerivedType(typeof(SetSmartMappingsCommand), "setSmartMappings")]
[JsonDerivedType(typeof(AutoMapSmartControlsCommand), "autoMapSmartControls")]
[JsonDerivedType(typeof(CompareSmartControlsCommand), "compareSmartControls")]
[JsonDerivedType(typeof(AddSceneCommand), "addScene")]
[JsonDerivedType(typeof(RemoveSceneCommand), "removeScene")]
[JsonDerivedType(typeof(SetSceneCommand), "setScene")]
[JsonDerivedType(typeof(SetCellCommand), "setCell")]
[JsonDerivedType(typeof(DeleteCellsCommand), "deleteCells")]
[JsonDerivedType(typeof(MoveCellCommand), "moveCell")]
[JsonDerivedType(typeof(SetLiveLoopsCommand), "setLiveLoops")]
[JsonDerivedType(typeof(SetTrackActivationCommand), "setTrackActivation")]
[JsonDerivedType(typeof(LoadInsertFileCommand), "loadInsertFile")]
[JsonDerivedType(typeof(SetControlsCommand), "setControls")]
[JsonDerivedType(typeof(TransformCommand), "transform")]
[JsonDerivedType(typeof(MakeGrooveCommand), "makeGroove")]
[JsonDerivedType(typeof(SetGrooveCommand), "setGroove")]
[JsonDerivedType(typeof(DeleteGrooveCommand), "deleteGroove")]
[JsonDerivedType(typeof(SetGrooveTrackCommand), "setGrooveTrack")]
[JsonDerivedType(typeof(SetMatchGrooveCommand), "setMatchGroove")]
[JsonDerivedType(typeof(SetArticulationSetCommand), "setArticulationSet")]
[JsonDerivedType(typeof(DeleteArticulationSetCommand), "deleteArticulationSet")]
[JsonDerivedType(typeof(AssignArticulationSetCommand), "assignArticulationSet")]
[JsonDerivedType(typeof(SetLaneSetCommand), "setLaneSet")]
[JsonDerivedType(typeof(DeleteLaneSetCommand), "deleteLaneSet")]
[JsonDerivedType(typeof(StepNotesCommand), "stepNotes")]
[JsonDerivedType(typeof(StepTieCommand), "stepTie")]
[JsonDerivedType(typeof(SetFileVersionCommand), "setFileVersion")]
[JsonDerivedType(typeof(SetScriptCommand), "setScript")]
[JsonDerivedType(typeof(SetSyncSettingsCommand), "setSyncSettings")]
[JsonDerivedType(typeof(AddStemsCommand), "addStems")]
[JsonDerivedType(typeof(SetStaffStyleCommand), "setStaffStyle")]
[JsonDerivedType(typeof(DeleteStaffStyleCommand), "deleteStaffStyle")]
[JsonDerivedType(typeof(AssignStaffStyleCommand), "assignStaffStyle")]
[JsonDerivedType(typeof(SetRegionNotationCommand), "setRegionNotation")]
[JsonDerivedType(typeof(AddScoreSymbolsCommand), "addScoreSymbols")]
[JsonDerivedType(typeof(EditScoreSymbolsCommand), "editScoreSymbols")]
[JsonDerivedType(typeof(DeleteScoreSymbolsCommand), "deleteScoreSymbols")]
[JsonDerivedType(typeof(SetLyricsCommand), "setLyrics")]
[JsonDerivedType(typeof(AddChordSymbolsCommand), "addChordSymbols")]
[JsonDerivedType(typeof(EditChordSymbolsCommand), "editChordSymbols")]
[JsonDerivedType(typeof(DeleteChordSymbolsCommand), "deleteChordSymbols")]
[JsonDerivedType(typeof(SetScoreSetCommand), "setScoreSet")]
[JsonDerivedType(typeof(DeleteScoreSetCommand), "deleteScoreSet")]
[JsonDerivedType(typeof(SetScoreLayoutCommand), "setScoreLayout")]
[JsonDerivedType(typeof(MoveScoreNotesCommand), "moveScoreNotes")]
[JsonDerivedType(typeof(AddScoreNoteCommand), "addScoreNote")]
[JsonDerivedType(typeof(ImportTracksCommand), "importTracks")]
[JsonDerivedType(typeof(SetSurroundFormatCommand), "setSurroundFormat")]
[JsonDerivedType(typeof(SetStripFormatCommand), "setStripFormat")]
[JsonDerivedType(typeof(SetPannerCommand), "setPanner")]
[JsonDerivedType(typeof(SetMovieCommand), "setMovie")]
[JsonDerivedType(typeof(SetMoviePositionCommand), "setMoviePosition")]
[JsonDerivedType(typeof(RemoveMovieCommand), "removeMovie")]
[JsonDerivedType(typeof(AddSceneMarkersCommand), "addSceneMarkers")]
[JsonDerivedType(typeof(AddChordsCommand), "addChords")]
[JsonDerivedType(typeof(EditChordsCommand), "editChords")]
[JsonDerivedType(typeof(DeleteChordsCommand), "deleteChords")]
[JsonDerivedType(typeof(SetScalesCommand), "setScales")]
[JsonDerivedType(typeof(ApplyChordIdCommand), "applyChordId")]
[JsonDerivedType(typeof(SetPitchSourceCommand), "setPitchSource")]
[JsonDerivedType(typeof(SetRegionKeyCommand), "setRegionKey")]
[JsonDerivedType(typeof(AddSessionPlayerTrackCommand), "addSessionPlayerTrack")]
[JsonDerivedType(typeof(AddSessionPlayerRegionCommand), "addSessionPlayerRegion")]
[JsonDerivedType(typeof(SetSessionPlayerRegionCommand), "setSessionPlayerRegion")]
[JsonDerivedType(typeof(RegenerateSessionPlayerCommand), "regenerateSessionPlayer")]
[JsonDerivedType(typeof(ConvertSessionPlayerRegionsCommand), "convertSessionPlayerRegions")]
[JsonDerivedType(typeof(SetMasteringCommand), "setMastering")]
[JsonDerivedType(typeof(SetMasteringAnalysisCommand), "setMasteringAnalysis")]
[JsonDerivedType(typeof(AddEnvironmentLayerCommand), "addEnvironmentLayer")]
[JsonDerivedType(typeof(SetEnvironmentLayerCommand), "setEnvironmentLayer")]
[JsonDerivedType(typeof(RemoveEnvironmentLayerCommand), "removeEnvironmentLayer")]
[JsonDerivedType(typeof(AddEnvironmentObjectCommand), "addEnvironmentObject")]
[JsonDerivedType(typeof(SetEnvironmentObjectCommand), "setEnvironmentObject")]
[JsonDerivedType(typeof(RemoveEnvironmentObjectsCommand), "removeEnvironmentObjects")]
[JsonDerivedType(typeof(ConnectEnvironmentCommand), "connectEnvironment")]
[JsonDerivedType(typeof(DisconnectEnvironmentCommand), "disconnectEnvironment")]
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
///     Moves a region (audio, MIDI, pattern, or a take folder) in time, and to another track of a kind that can hold it
///     when <c>trackId</c> is given. <c>deleteRegions</c> and <c>copyRegions</c> also take every kind;
///     <c>splitRegion</c> takes audio and MIDI.
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
    bool? Muted = null,
    long? LoopTicks = null
) : EditCommand;

public sealed record DeleteRegionsCommand(IReadOnlyList<string> RegionIds) : EditCommand;

/// <summary>
///     Time-stretches an audio region without touching its file: it lasts <c>stretch</c> times as long as recorded
///     (0.25…4, 1 back to as recorded), from <c>startTick</c> when given (a drag of its left edge moves its start). Its
///     flex markers scale with it; its fades stay in frames, cut to fit.
/// </summary>
public sealed record StretchRegionCommand(string RegionId, double Stretch, long? StartTick = null) : EditCommand;

/// <summary>
///     Inserts <c>lengthTicks</c> of empty time at <c>atTick</c>, on every track: regions across it are split (a looped
///     one's repeats become regions first; pattern and Session Player regions grow instead), and everything from there on
///     moves later, with the tempo, signature, key and chord tracks, markers, sections, automation, score symbols, the
///     cycle, the punch locators, the project end and the movie. A signature change that no longer falls on a bar line
///     moves to the nearest one.
/// </summary>
public sealed record InsertTimeCommand(long AtTick, long LengthTicks) : EditCommand;

/// <summary>
///     Removes the time from <c>startTick</c> to <c>endTick</c> on every track and closes it, as <c>insertTime</c> makes
///     room: what is inside goes, what crosses it is split, and what follows moves earlier. The tempo, signature, key and
///     chord in force at its end hold from its start.
/// </summary>
public sealed record CutTimeCommand(long StartTick, long EndTick) : EditCommand;

/// <summary>
///     Deletes regions and closes the time they took across every track: everything that starts after them moves earlier,
///     the global tracks, markers and automation as <c>cutTime</c> says; what other tracks hold across that time stays
///     where it is, uncut.
/// </summary>
public sealed record RippleDeleteCommand(IReadOnlyList<string> RegionIds) : EditCommand;

/// <summary>Turns the repeats of looped regions (audio, MIDI, pattern) into regions of their own; what plays stays.</summary>
public sealed record ConvertLoopsCommand(IReadOnlyList<string> RegionIds) : EditCommand;

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

/// <summary>
///     Changes a MIDI region's name, mute, loop or playback parameters. <c>grooveId</c> (Phase 7) makes its playback quantise
///     follow a groove template of the project; an empty string returns it to the grid.
/// </summary>
public sealed record SetMidiRegionCommand(
    string RegionId,
    string? Name = null,
    int? Transpose = null,
    int? Velocity = null,
    int? QuantizeTicks = null,
    double? QuantizeStrength = null,
    double? Swing = null,
    bool? Muted = null,
    string? GrooveId = null,
    long? LoopTicks = null
) : EditCommand;

public sealed record AddNotesCommand(string RegionId, IReadOnlyList<NewNote> Notes) : EditCommand;

public sealed record EditNotesCommand(string RegionId, IReadOnlyList<NoteChange> Changes) : EditCommand;

public sealed record DeleteNotesCommand(string RegionId, IReadOnlyList<long> NoteIds) : EditCommand;

/// <summary>
///     Moves note starts towards the grid (destructive; <c>noteIds</c> left out: every note of the region), with the
///     same strength and swing meaning as <see cref="RegionPlayback" />. Phase 7: with <c>grooveId</c> (a groove
///     template of the project), towards the template's positions instead (<c>gridTicks</c> and <c>swing</c> unused),
///     and with <c>grooveVelocity</c> true the velocities follow its scales by the same strength. On a track whose
///     <c>matchGroove</c> is on, a quantise without <c>grooveId</c> uses the groove track's groove, extracted from its
///     first region at the time of the quantise.
/// </summary>
public sealed record QuantizeNotesCommand(
    string RegionId,
    int GridTicks,
    double Strength = 1,
    double Swing = 0,
    IReadOnlyList<long>? NoteIds = null,
    string? GrooveId = null,
    bool? GrooveVelocity = null
) : EditCommand;

/// <summary>Adds controller events (Phase 7: of every <see cref="ControlKind" />, poly pressure included).</summary>
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

/// <summary>Adds a marker; <c>kind</c> (Phase 10) is <c>marker</c> unless given.</summary>
public sealed record AddMarkerCommand(
    string Name,
    long Tick,
    long LengthTicks = 0,
    string? Color = null,
    MarkerKind? Kind = null
) : EditCommand;

/// <summary>Changes a marker; left-out properties stay. <c>kind</c> (Phase 10) turns a scene marker into a marker or back.</summary>
public sealed record SetMarkerCommand(
    string MarkerId,
    string? Name = null,
    long? Tick = null,
    long? LengthTicks = null,
    string? Color = null,
    string? Text = null,
    MarkerKind? Kind = null
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

/// <summary>
///     Puts regions of any kind on the timeline from the editor's clipboard (⌘V): whole regions as the project returned
///     them, each with the <c>trackId</c> and <c>startTick</c> it lands on (a take folder's takes and comp moved with
///     it). A cut has deleted them, so they need name nothing in the project: everything is checked as new, and every
///     copy gets ids of its own. Files are the project's, so a region of another project is refused. Track automation
///     goes in the same batch, as <c>setAutomation</c>.
/// </summary>
public sealed record PasteRegionsCommand(
    IReadOnlyList<AudioRegion>? Regions = null,
    IReadOnlyList<MidiRegion>? MidiRegions = null,
    IReadOnlyList<TakeFolder>? TakeFolders = null,
    IReadOnlyList<PatternRegion>? PatternRegions = null,
    IReadOnlyList<SessionPlayerRegion>? SessionPlayerRegions = null
) : EditCommand;

/// <summary>
///     Which slots of a strip an insert command edits: its inserts (the default), or its MIDI FX slots (Phase 6; an
///     instrument or external MIDI track's only, and only plugins of category <c>MIDI Effect</c>).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<InsertChain>))]
public enum InsertChain {
    [JsonStringEnumMemberName("inserts")]
    Inserts,

    [JsonStringEnumMemberName("midiEffects")]
    MidiEffects
}

/// <summary>
///     Adds an insert effect at <c>index</c> (the end when left out); the master's when <c>trackId</c> is left out. With
///     <c>chain</c> <c>midiEffects</c>, a MIDI FX slot instead, as for every insert command.
/// </summary>
public sealed record AddInsertCommand(
    string PluginId,
    string? TrackId = null,
    int? Index = null,
    InsertChain? Chain = null
) : EditCommand;

public sealed record RemoveInsertCommand(string InsertId, string? TrackId = null, InsertChain? Chain = null)
    : EditCommand;

/// <summary>Moves an insert to another slot of the same strip (and chain).</summary>
public sealed record MoveInsertCommand(string InsertId, int Index, string? TrackId = null, InsertChain? Chain = null)
    : EditCommand;

/// <summary>
///     Bypasses an insert, or sets its sidechain (a track id, <c>bus:N</c>, or an empty string for none; never on a MIDI
///     FX slot).
/// </summary>
public sealed record SetInsertCommand(
    string InsertId,
    string? TrackId = null,
    bool? Bypassed = null,
    string? Sidechain = null,
    InsertChain? Chain = null
) : EditCommand;

/// <summary>
///     Sets one insert parameter; coalesces like <c>setParameter</c>, and records automation like <c>setStrip</c>.
/// </summary>
public sealed record SetInsertParameterCommand(
    string InsertId,
    string ParameterId,
    double Value,
    string? TrackId = null,
    InsertChain? Chain = null)
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

/// <summary>Turns a track's flex on or off, or changes its algorithm.</summary>
public sealed record SetTrackFlexCommand(string TrackId, bool? On = null, FlexMode? Mode = null) : EditCommand;

/// <summary>
///     Sets an audio region's Flex &amp; Follow, transpose (semitones) or fine tune (cents). A transpose or fine tune
///     needs Flex or Follow (the track's flex on, or a <c>follow</c> other than <c>off</c>), else 422.
/// </summary>
public sealed record SetRegionFlexCommand(
    string RegionId,
    FlexFollow? Follow = null,
    int? Transpose = null,
    int? FineTune = null
) : EditCommand;

/// <summary>
///     Replaces an audio region's flex markers: file frames inside the region, their ticks increasing with them and
///     inside the region. Needs the track's flex on.
/// </summary>
public sealed record SetFlexMarkersCommand(string RegionId, IReadOnlyList<FlexMarker> Markers) : EditCommand;

/// <summary>
///     Quantises audio regions: flex markers on their transients (analysed now if need be) move toward the grid of
///     <c>gridTicks</c> by <c>strength</c> (0…1), with <c>swing</c> (0…1) delaying every second grid step. Turns flex
///     on for their tracks when it is off. Phase 7: <c>grooveId</c> and a matching groove track as for
///     <see cref="QuantizeNotesCommand" /> (audio has no velocities, so no <c>grooveVelocity</c>).
/// </summary>
public sealed record QuantizeAudioCommand(
    IReadOnlyList<string> RegionIds,
    long GridTicks,
    double Strength = 1,
    double Swing = 0,
    string? GrooveId = null
) : EditCommand;

/// <summary>
///     Replaces an audio region's Flex Pitch notes (edits, splits and joins); notes need Flex or Follow, as a
///     transpose does.
/// </summary>
public sealed record SetPitchNotesCommand(string RegionId, IReadOnlyList<PitchNote> Notes) : EditCommand;

/// <summary>
///     Makes a MIDI region of an audio region's pitch notes (their target pitches, at the times they play) on
///     <c>trackId</c>, or on a new instrument track under the region's when left out.
/// </summary>
public sealed record FlexPitchToMidiCommand(string RegionId, string? TrackId = null) : EditCommand;

/// <summary>Sets a file's tempo, beats and downbeats (the Smart Tempo Editor), or clears them (null).</summary>
public sealed record SetFileTempoCommand(string FileId, FileTempo? Tempo) : EditCommand;

/// <summary>
///     Sets the project's tempo events from an audio region's file tempo (analysed now if need be) over the region's
///     range, and makes the region play unwarped there.
/// </summary>
public sealed record AdaptTempoCommand(string RegionId) : EditCommand;

/// <summary>
///     Beat mapping: for each mapping in order, sets tempo events so that its tick lands on its sample, keeping earlier
///     mappings and the positions before them.
/// </summary>
public sealed record MapBeatsCommand(IReadOnlyList<BeatMapping> Mappings) : EditCommand;

/// <summary>
///     Strip Silence: splits each audio region into one region per stretch above <c>thresholdDb</c> (RMS), each widened
///     by <c>preAttackMs</c> before and <c>postReleaseMs</c> after; the silence between them is removed. As in Logic,
///     <c>minTimeMs</c> is the shortest silence that splits: stretches separated by less stay in one region.
/// </summary>
public sealed record StripSilenceCommand(
    IReadOnlyList<string> RegionIds,
    double ThresholdDb = -40,
    double MinTimeMs = 100,
    double PreAttackMs = 20,
    double PostReleaseMs = 50
) : EditCommand;

/// <summary>
///     Makes a new track alternative and selects it: empty, or a copy of the active one when <c>duplicate</c> (with new
///     ids). The track's first alternative also names the current content.
/// </summary>
public sealed record NewTrackAlternativeCommand(string TrackId, string? Name = null, bool Duplicate = false)
    : EditCommand;

/// <summary>Swaps a track's live regions, MIDI regions and take folders for those of alternative <c>alternativeId</c>.</summary>
public sealed record SelectTrackAlternativeCommand(string TrackId, string AlternativeId) : EditCommand;

/// <summary>Renames a track alternative.</summary>
public sealed record SetTrackAlternativeCommand(string TrackId, string AlternativeId, string Name) : EditCommand;

/// <summary>
///     Deletes a track alternative with its content; deleting the active one selects another first.
/// </summary>
public sealed record DeleteTrackAlternativeCommand(string TrackId, string AlternativeId) : EditCommand;

/// <summary>Turns Varispeed on or off, or sets its mode or speed (0.5…1.5).</summary>
public sealed record SetVarispeedCommand(bool? Enabled = null, VarispeedMode? Mode = null, double? Speed = null)
    : EditCommand;

/// <summary>Sets the project tempo mode, the follow new audio regions get, or whether imports are analysed.</summary>
public sealed record SetSmartTempoCommand(
    ProjectTempoMode? Mode = null,
    FlexFollow? DefaultFollow = null,
    bool? AnalyzeImports = null
) : EditCommand;

/// <summary>
///     Adds a pattern region to an instrument track: <c>lengthTicks</c> (left out: one bar at <c>startTick</c>'s
///     signature) and <c>pattern</c> (left out: the default pattern: on a Drum Machine Designer track one pad row per
///     pad, else one note row on C3 (60); each 16 steps of a 16th, all off, no swing).
/// </summary>
public sealed record AddPatternRegionCommand(
    string TrackId,
    long StartTick,
    long? LengthTicks = null,
    Pattern? Pattern = null,
    string? Name = null
) : EditCommand;

/// <summary>
///     Moves (in time), resizes, renames, loops or mutes a pattern region; <c>moveRegion</c>, <c>copyRegions</c> and
///     <c>deleteRegions</c> take pattern regions too.
/// </summary>
public sealed record SetPatternRegionCommand(
    string RegionId,
    long? StartTick = null,
    long? LengthTicks = null,
    string? Name = null,
    bool? Muted = null,
    long? LoopTicks = null
) : EditCommand;

/// <summary>
///     Replaces a pattern region's whole pattern (every Step Sequencer edit sends it). Rows: unique ids, length 1…64
///     with as many steps, step values in their ranges (<see cref="PatternStep" />); swing 0…1; else 422.
/// </summary>
public sealed record SetPatternCommand(string RegionId, Pattern Pattern) : EditCommand;

/// <summary>
///     Replaces a pattern region with a MIDI region of exactly the notes it plays over its length (one pass of the
///     region), at the same place, with its name and mute.
/// </summary>
public sealed record ConvertPatternToMidiCommand(string RegionId) : EditCommand;

/// <summary>Sets a pattern's seed (left out: a new one the server draws), so chance and random steps draw anew.</summary>
public sealed record ReseedPatternCommand(string RegionId, long? Seed = null) : EditCommand;

/// <summary>
///     Makes a Drum Machine Designer: a new DMD stack at the end, or, with <c>trackId</c>, an instrument track turned
///     into one in place (its regions and pattern regions stay on the DMD track, its instrument goes). The pads come
///     from kit patch <c>kitPatchId</c> (left out: no pads).
/// </summary>
public sealed record CreateDrumMachineCommand(string? TrackId = null, string? KitPatchId = null, string? Name = null)
    : EditCommand;

/// <summary>
///     Adds a pad on note <c>note</c> (0…127, free on this DMD) to a DMD track: a new member instrument track playing
///     <c>pluginId</c> (left out: the Drum Synth, <c>anode.drum-synth</c>), named <c>name</c>.
/// </summary>
public sealed record AddDrumPadCommand(string TrackId, int Note, string? Name = null, string? PluginId = null)
    : EditCommand;

/// <summary>
///     Changes the pad on note <c>note</c> of a DMD track: renames it (and its track), gives its track another
///     instrument (<c>pluginId</c>, parameters at their defaults), or moves it to free note <c>toNote</c>. Its channel
///     strip is edited through the pad track's own strip and insert commands.
/// </summary>
public sealed record SetDrumPadCommand(
    string TrackId,
    int Note,
    string? Name = null,
    string? PluginId = null,
    int? ToNote = null
) : EditCommand;

/// <summary>Removes the pad on note <c>note</c> of a DMD track, with its track.</summary>
public sealed record RemoveDrumPadCommand(string TrackId, int Note) : EditCommand;

/// <summary>
///     Sets a track's Smart Controls layout (controls a smaller layout drops lose their mappings) or turns its
///     arpeggiator button on or off (adding, bypassing or unbypassing <c>anode.arpeggiator</c> in its first MIDI FX
///     slot). Sets the Smart Controls up when they are null.
/// </summary>
public sealed record SetSmartControlsCommand(
    string TrackId,
    SmartControlsLayout? Layout = null,
    bool? Arpeggiator = null
) : EditCommand;

/// <summary>
///     Sets a Smart Control's value (0…1; applying every mapping's parameter, coalesced as <c>setParameter</c>, and
///     recording automation of <c>smartControl:{controlId}</c> as <c>setStrip</c> records volume) or renames it.
/// </summary>
public sealed record SetSmartControlCommand(
    string TrackId,
    string ControlId,
    double? Value = null,
    string? Name = null
) : EditCommand;

/// <summary>Replaces a Smart Control's mappings (targets that exist on the track; graphs as <see cref="SmartMapping" />).</summary>
public sealed record SetSmartMappingsCommand(string TrackId, string ControlId, IReadOnlyList<SmartMapping> Mappings)
    : EditCommand;

/// <summary>
///     Automatic mapping: replaces the knobs' names and mappings with the plugins' macro hints in order (the
///     instrument's, then each insert's), up to the layout's knobs; each knob takes its parameter's current value.
/// </summary>
public sealed record AutoMapSmartControlsCommand(string TrackId) : EditCommand;

/// <summary>
///     Compare: on, the controls show (and their parameters take) their saved values; off, the edited ones again. The
///     server applies it without an undo step (its delta still reaches every client).
/// </summary>
public sealed record CompareSmartControlsCommand(string TrackId, bool On) : EditCommand;

/// <summary>Adds an empty scene to the Live Loops grid at <c>index</c> (the end when left out).</summary>
public sealed record AddSceneCommand(string? Name = null, int? Index = null) : EditCommand;

/// <summary>Removes a scene and its cells.</summary>
public sealed record RemoveSceneCommand(string SceneId) : EditCommand;

/// <summary>Renames a scene, or moves it to <c>index</c>.</summary>
public sealed record SetSceneCommand(string SceneId, string? Name = null, int? Index = null) : EditCommand;

/// <summary>
///     Creates or changes the cell of <c>trackId</c> and <c>sceneId</c>. <c>content</c> creates it or replaces what it
///     plays (its kind follows; it must suit the track: audio on an audio track, MIDI or pattern on an instrument
///     track; ids as <see cref="CellContent" /> says, the server renumbering what clashes); a cell that does not exist
///     needs it. <c>quantize</c> sets its own launch quantise; <c>gridQuantize</c> true clears it, so it follows the
///     grid's.
/// </summary>
public sealed record SetCellCommand(
    string TrackId,
    string SceneId,
    CellContent? Content = null,
    string? Name = null,
    long? LengthTicks = null,
    CellPlayMode? PlayMode = null,
    LaunchQuantize? Quantize = null,
    bool? GridQuantize = null
) : EditCommand;

public sealed record DeleteCellsCommand(IReadOnlyList<string> CellIds) : EditCommand;

/// <summary>
///     Moves (or with <c>copy</c>, copies) a cell to the slot of <c>trackId</c> and <c>sceneId</c>, replacing the cell
///     there; the track must suit its content.
/// </summary>
public sealed record MoveCellCommand(string CellId, string TrackId, string SceneId, bool Copy = false) : EditCommand;

/// <summary>Sets the Live Loops grid's launch quantise, or shows or hides it.</summary>
public sealed record SetLiveLoopsCommand(LaunchQuantize? Quantize = null, bool? Shown = null) : EditCommand;

/// <summary>
///     Loads a file into an insert (the master's when <c>trackId</c> is left out): an impulse response into the
///     convolution reverb (<c>anode.space</c>), a path on the server's disk of one of the plugin's <c>fileTypes</c>. A
///     file outside the project is copied into its <c>Impulse Responses</c> folder; the insert keeps it relative.
/// </summary>
public sealed record LoadInsertFileCommand(string InsertId, string Path, string? TrackId = null) : EditCommand;

/// <summary>Track activation: the tracks play their Live Loops cells (on) or their Tracks-area regions (off).</summary>
public sealed record SetTrackActivationCommand(IReadOnlyList<string> TrackIds, bool LiveLoopsActive) : EditCommand;

/// <summary>
///     Phase 7, the Event List: changes controller events of a region (tick, channel, number, value; checked as
///     <c>addControls</c> checks them).
/// </summary>
public sealed record SetControlsCommand(string RegionId, IReadOnlyList<ControlEventChange> Changes) : EditCommand;

/// <summary>
///     Phase 7, MIDI Transform: applies <c>set</c> (mode <c>operateOnly</c> or <c>selectAndOperate</c>;
///     <c>selectOnly</c> is the editor's own, and refused) to the events of MIDI regions <c>regionIds</c>: when
///     <c>noteIds</c> or <c>controlIds</c> is given, to those events only (the editor's selection), else to every
///     event of the regions. Positions are project ticks while it runs, so position conditions and crescendos span
///     the regions. One undo step, through <c>TransformEngine.Apply</c>.
/// </summary>
public sealed record TransformCommand(
    IReadOnlyList<string> RegionIds,
    TransformSet Set,
    IReadOnlyList<long>? NoteIds = null,
    IReadOnlyList<long>? ControlIds = null
) : EditCommand;

/// <summary>
///     Phase 7: Make Groove Template: adds to the project's grooves a template made from a MIDI region (its notes) or an
///     audio region (its file's transients), over its first <c>lengthBars</c> bars (1…4; left out: 1), with grid
///     steps of <c>stepTicks</c> (left out: a 1/16, 240), named <c>name</c> (left out: the region's).
/// </summary>
public sealed record MakeGrooveCommand(
    string RegionId,
    string? Name = null,
    int? LengthBars = null,
    long? StepTicks = null)
    : EditCommand;

/// <summary>
///     Phase 7: adds a groove template to the project (one from the library) or replaces the one with its id (a
///     rename); <c>points</c> must hold lengthTicks ÷ stepTicks points.
/// </summary>
public sealed record SetGrooveCommand(GrooveTemplate Groove) : EditCommand;

/// <summary>Phase 7: removes a groove template; regions whose playback quantise used it return to the grid.</summary>
public sealed record DeleteGrooveCommand(string GrooveId) : EditCommand;

/// <summary>Phase 7: makes a track (with regions: MIDI or audio) the groove track, or none when null.</summary>
public sealed record SetGrooveTrackCommand(string? TrackId) : EditCommand;

/// <summary>Phase 7: a track's "match groove track": its quantise follows the groove track.</summary>
public sealed record SetMatchGrooveCommand(string TrackId, bool MatchGroove) : EditCommand;

/// <summary>
///     Phase 7: adds an articulation set to the project (one from the library) or replaces the one with its id.
///     Articulation ids 1…254 and unique; switches and outputs name articulations of the set, with values in their
///     ranges (<see cref="ArticulationOutputKind" />).
/// </summary>
public sealed record SetArticulationSetCommand(ArticulationSet Set) : EditCommand;

/// <summary>Phase 7: removes an articulation set; tracks that used it have none (their notes keep their ids).</summary>
public sealed record DeleteArticulationSetCommand(string SetId) : EditCommand;

/// <summary>Phase 7: gives an instrument or external MIDI track an articulation set of the project (null: none).</summary>
public sealed record AssignArticulationSetCommand(string TrackId, string? SetId) : EditCommand;

/// <summary>Phase 7: adds a Step Editor lane set to the project or replaces the one with its id.</summary>
public sealed record SetLaneSetCommand(LaneSet Set) : EditCommand;

public sealed record DeleteLaneSetCommand(string SetId) : EditCommand;

/// <summary>
///     Phase 7, step input: adds a step of notes (one per pitch, a chord when several) at the step position. Clients
///     (and MIDI step input) send only <c>pitches</c>, and <c>velocity</c> to override the state's; the server fills
///     every left-out property from the step input state (<see cref="StepInputState" />) before it applies the command
///     (a new region on <c>trackId</c> when <c>regionId</c> is left out there too), then advances the position unless
///     chord mode is on. <c>positionTick</c> is a project tick; <c>lengthTicks</c> the step's length; <c>channel</c>
///     1 unless given.
/// </summary>
public sealed record StepNotesCommand(
    IReadOnlyList<int> Pitches,
    int? Velocity = null,
    string? RegionId = null,
    string? TrackId = null,
    long? PositionTick = null,
    long? LengthTicks = null,
    int? Channel = null
) : EditCommand;

/// <summary>
///     Phase 7, step input: a tie lengthens the last step's notes (<c>noteIds</c>; the server fills them in) by one step
///     (<c>lengthTicks</c>; filled in from the state), and advances the position. A rest is not an edit:
///     <c>POST /project/step-input/rest</c>.
/// </summary>
public sealed record StepTieCommand(
    string? RegionId = null,
    IReadOnlyList<long>? NoteIds = null,
    long? LengthTicks = null
) : EditCommand;

/// <summary>
///     Phase 7, the Audio File Editor: points a file at another version of itself: <c>path</c> (relative to the
///     project, the <c>&lt;name&gt;.vN.wav</c> an <c>audioEdit</c> job wrote) as <c>version</c>, with its frame count
///     and rate when an operation changed them. The job applies it when it finishes (with the region shifts a length
///     change needs, in the same undo step); undo applies it back. Clients do not send it.
/// </summary>
public sealed record SetFileVersionCommand(
    string FileId,
    int Version,
    string Path,
    long? Frames = null,
    int? SampleRate = null,
    int? Channels = null,
    int? BitDepth = null
) : EditCommand;

/// <summary>
///     Phase 7, Scripter: sets the script of MIDI FX slot <c>slotId</c> (an <c>anode.scripter</c> slot) of a track.
///     The host compiles it and reports its parameters (<c>Insert.runtimeParameters</c>); a script that fails to
///     compile is bypassed with a <c>pluginProblem</c>, and the slot keeps the source.
/// </summary>
public sealed record SetScriptCommand(string TrackId, string SlotId, string Source) : EditCommand;

/// <summary>
///     Phase 8: changes the project's synchronisation (<c>ProjectSettings.sync</c>); left-out properties stay.
///     <c>destinations</c> replaces the list (each port at most once). An empty <c>clockInPort</c> or <c>mtcInPort</c>
///     sets it to any input (null).
/// </summary>
public sealed record SetSyncSettingsCommand(
    SyncSource? Source = null,
    IReadOnlyList<MidiSyncDestination>? Destinations = null,
    MtcFrameRate? FrameRate = null,
    double? StartOffsetSeconds = null,
    bool? LinkStartStop = null,
    string? ClockInPort = null,
    string? MtcInPort = null
) : EditCommand;

/// <summary>A stem file the Stem Splitter wrote and added to the project (an <c>AudioFile</c> id).</summary>
public sealed record StemFile(StemKind Kind, string FileId);

/// <summary>
///     Phase 8, the Stem Splitter's result: for each stem, in the order given (the order of <see cref="StemKind" />), a
///     new audio track below audio region <c>regionId</c>'s track (named after the region and the stem, with the source
///     track's output) holding a region of the whole file at the source region's position (each file covers the
///     region's span); the source region is muted unless <c>keepSource</c>. The <c>stemSplit</c> job applies it when it
///     finishes, after adding the files; undo removes the tracks and unmutes the region (the files stay). Clients do not
///     send it.
/// </summary>
public sealed record AddStemsCommand(string RegionId, IReadOnlyList<StemFile> Stems, bool KeepSource) : EditCommand;

/// <summary>
///     Phase 9 (docs/10-phase-9.md): adds a staff style to the project or replaces the one with its id (an id the client
///     chooses, not starting <c>factory.</c>; 1…4 staves, splits descending with the last null, clefs, transpositions
///     −48…48, voices 1…4, grids 0 or a divisor of a whole note, a drum map only with a percussion staff).
/// </summary>
public sealed record SetStaffStyleCommand(StaffStyle Style) : EditCommand;

/// <summary>Phase 9: removes a project staff style; tracks that used it return to the default (null).</summary>
public sealed record DeleteStaffStyleCommand(string StyleId) : EditCommand;

/// <summary>
///     Phase 9: gives instrument and external MIDI tracks a staff style (a factory id or one of the project's; null:
///     the default, <c>StaffStyleIds.Default</c>).
/// </summary>
public sealed record AssignStaffStyleCommand(IReadOnlyList<string> TrackIds, string? StyleId) : EditCommand;

/// <summary>
///     Phase 9: changes the score settings (<see cref="RegionNotation" />) of MIDI regions; left-out properties stay.
///     Grids are 0 or divide a whole note (3840 ticks); <c>maxDots</c> 0…3.
/// </summary>
public sealed record SetRegionNotationCommand(
    IReadOnlyList<string> RegionIds,
    int? DisplayQuantizeTicks = null,
    int? TripletQuantizeTicks = null,
    bool? Interpretation = null,
    bool? NoOverlap = null,
    bool? Syncopation = null,
    int? MaxDots = null
) : EditCommand;

/// <summary>
///     A score symbol to add (<see cref="ScoreSymbol" /> without its id, which the server gives): its anchors as
///     <c>ScoreSymbols</c> says for its kind; notes of MIDI regions of <c>trackId</c>; ticks of 0 or more.
/// </summary>
public sealed record NewScoreSymbol(
    ScoreSymbolKind Kind,
    string TrackId,
    long? NoteId = null,
    long? Tick = null,
    long? EndNoteId = null,
    long? EndTick = null,
    string? Text = null,
    ScorePlacement? Placement = null,
    int? Staff = null
);

/// <summary>Phase 9: adds score symbols (the new ones come back as <c>scoreSymbolUpserted</c>, with their ids).</summary>
public sealed record AddScoreSymbolsCommand(IReadOnlyList<NewScoreSymbol> Symbols) : EditCommand;

/// <summary>Phase 9: replaces score symbols by id (each checked as when added; the track stays).</summary>
public sealed record EditScoreSymbolsCommand(IReadOnlyList<ScoreSymbol> Symbols) : EditCommand;

public sealed record DeleteScoreSymbolsCommand(IReadOnlyList<string> SymbolIds) : EditCommand;

/// <summary>A note's syllable in a verse; an empty <c>text</c> removes the note's syllable there.</summary>
public sealed record LyricChange(long NoteId, string Text, Syllabic Syllabic = Syllabic.Single, bool Extender = false);

/// <summary>
///     Phase 9, lyrics entry: sets the syllables of notes in verse <c>verse</c> (1…8), each note once; a note's existing
///     syllable in the verse keeps its id, a new one gets one.
/// </summary>
public sealed record SetLyricsCommand(int Verse, IReadOnlyList<LyricChange> Syllables) : EditCommand;

/// <summary>A chord symbol to add (<see cref="ChordSymbol" /> without its id).</summary>
public sealed record NewChordSymbol(
    string TrackId,
    long Tick,
    PitchName Root,
    string Quality,
    PitchName? Bass = null,
    bool ShowGrid = false
);

/// <summary>Phase 9: adds chord symbols to tracks that hold MIDI (alterations −2…2, ticks of 0 or more).</summary>
public sealed record AddChordSymbolsCommand(IReadOnlyList<NewChordSymbol> Chords) : EditCommand;

/// <summary>Phase 9: replaces chord symbols by id.</summary>
public sealed record EditChordSymbolsCommand(IReadOnlyList<ChordSymbol> Chords) : EditCommand;

public sealed record DeleteChordSymbolsCommand(IReadOnlyList<string> ChordIds) : EditCommand;

/// <summary>
///     Phase 9: adds a score set (an id the client chooses) or replaces the one with its id; its tracks exist, each
///     once. Removing a track removes it from the sets.
/// </summary>
public sealed record SetScoreSetCommand(ScoreSet Set) : EditCommand;

public sealed record DeleteScoreSetCommand(string SetId) : EditCommand;

/// <summary>
///     Phase 9: replaces the score layout (page 50…2000 mm a side, margins leaving at least half of it, staff height
///     2…20 mm, instrument names of existing tracks, each track once).
/// </summary>
public sealed record SetScoreLayoutCommand(ScoreLayout Layout) : EditCommand;

/// <summary>
///     Phase 9, a score edit: moves notes of region <c>regionId</c> by <c>steps</c> staff steps (each note's written
///     pitch to the note of the key at its position that many diatonic steps up, or down when negative; MIDI stays at
///     concert pitch, so the staff's transposition is undone) and its MIDI start by <c>ticks</c> (the editor sends a
///     multiple of the region's display grid). <c>accidental</c> forces the written alteration of the result
///     (modifier keys; null: the key's). Converted with the notation engine's staff positions
///     (<c>Anode.Notation.StaffPositions</c>) into the notes' pitch and start changes, as <c>editNotes</c> makes them.
/// </summary>
public sealed record MoveScoreNotesCommand(
    string RegionId,
    IReadOnlyList<long> NoteIds,
    int Steps,
    long Ticks = 0,
    Accidental? Accidental = null
) : EditCommand;

/// <summary>
///     Phase 9, a score edit: inserts a note from the Part box at project tick <c>tick</c> (a display position) lasting
///     <c>lengthTicks</c>, on staff <c>staff</c> (0 the top of the track's style) at staff position
///     <c>staffPosition</c> (diatonic steps above the bottom line). Its pitch is the note of the key at that position
///     under the staff's clef, with <c>accidental</c> forcing the alteration, less the staff's transposition. It goes
///     into region <c>regionId</c>, else the track's region at the tick, else a new region of whole bars on the track;
///     velocity 80 unless given, channel 1.
/// </summary>
public sealed record AddScoreNoteCommand(
    string TrackId,
    long Tick,
    long LengthTicks,
    int StaffPosition,
    int Staff = 0,
    Accidental? Accidental = null,
    int? Velocity = null,
    string? RegionId = null
) : EditCommand;

/// <summary>
///     Phase 9, an interchange import's result (<see cref="ImportedContent" />): adds its tracks after the project's
///     with their regions, staff styles and score content, gives every provisional id a real one (rewriting the
///     references), replaces the tempo map, signatures and keys it carries, and adds its markers, in one undo step. The
///     <c>musicXmlImport</c>, <c>aafImport</c> and <c>midiImport</c> jobs apply it after copying the media into the
///     project (undo leaves the files); clients do not send it.
/// </summary>
public sealed record ImportTracksCommand(ImportedContent Content) : EditCommand;

/// <summary>
///     Phase 10 (docs/11-phase-10.md): sets the project's channel format (<c>settings.surroundFormat</c>). The master's
///     input and output formats follow, and the output format of every strip that feeds the master; strips in object
///     mode return to the Surround Panner when the format is stereo or narrower (objects need a surround master).
/// </summary>
public sealed record SetSurroundFormatCommand(ChannelFormat Format) : EditCommand;

/// <summary>
///     Phase 10: sets a track strip's input format (mono or stereo for instrument tracks; any for audio and aux
///     tracks). An aux's input format is its bus's format: the output formats of the strips that output or send to that
///     bus follow (every aux on one bus has the same input format). The output format is never set directly: it is
///     the destination's.
/// </summary>
public sealed record SetStripFormatCommand(string TrackId, ChannelFormat InputFormat) : EditCommand;

/// <summary>
///     Phase 10: changes a track strip's panner (<c>ChannelStrip.panner</c>); left-out properties stay. <c>mode</c>
///     <c>surround</c> needs a surround output, <c>object</c> a strip that outputs to the master of a surround project
///     (a project has at most 118 objects). The Surround Panner's <c>angle</c>, <c>diversity</c>,
///     <c>surroundElevation</c>, <c>centreLevelDb</c>, <c>lfeLevelDb</c> and <c>spread</c>, and the Object Panner's
///     <c>azimuth</c>, <c>objectElevation</c>, <c>distance</c> and <c>size</c>, in <see cref="SurroundPanner" />'s and
///     <see cref="ObjectPanner" />'s units and ranges (angles −180…180, levels −144…0 dB). During playback a track in
///     touch, latch or write records the change into its <c>surround:…</c> or <c>object:…</c> lanes, as
///     <c>setStrip</c> records pan (and a batch coalesces as a drag does).
/// </summary>
public sealed record SetPannerCommand(
    string TrackId,
    PannerMode? Mode = null,
    double? Angle = null,
    double? Diversity = null,
    double? SurroundElevation = null,
    double? CentreLevelDb = null,
    double? LfeLevelDb = null,
    double? Spread = null,
    double? Azimuth = null,
    double? ObjectElevation = null,
    double? Distance = null,
    double? Size = null
) : EditCommand;

/// <summary>
///     Phase 10, a movie import's result: sets the project's movie (replacing one it has). The <c>movieImport</c> job
///     applies it after probing and copying the file; clients do not send it. Undo restores the previous movie (or none);
///     the file stays.
/// </summary>
public sealed record SetMovieCommand(Movie Movie) : EditCommand;

/// <summary>
///     Phase 10: moves the movie (Logic's movie start and offset): its frame at <c>offsetSeconds</c> (0 or more, less
///     than its duration) plays at <c>startTick</c> (0 or more); left-out properties stay. 422 without a movie.
/// </summary>
public sealed record SetMoviePositionCommand(long? StartTick = null, double? OffsetSeconds = null) : EditCommand;

/// <summary>Phase 10, Remove Movie: the project no longer has a movie (a copied file stays in <c>Movies/</c>).</summary>
public sealed record RemoveMovieCommand : EditCommand;

/// <summary>
///     Phase 10, Detect Scene Cuts' result: adds a <c>scene</c> marker at each tick (named <c>Scene 1</c>, <c>Scene 2</c>,
///     … in time order, no length, no colour), first removing the scene markers when <c>replaceExisting</c>. The
///     <c>sceneDetect</c> job applies it; clients do not send it.
/// </summary>
public sealed record AddSceneMarkersCommand(IReadOnlyList<long> Ticks, bool ReplaceExisting = true) : EditCommand;

/// <summary>A chord to add to the chord track (<see cref="Chord" /> without its id).</summary>
public sealed record NewChord(long Tick, PitchName Root, string Quality, PitchName? Bass = null);

/// <summary>
///     Phase 11 (docs/12-phase-11.md): adds chords to the chord track (ticks of 0 or more, alterations −2…2, a quality of
///     <see cref="ChordQualities" />); a chord at the tick of an existing one replaces it.
/// </summary>
public sealed record AddChordsCommand(IReadOnlyList<NewChord> Chords) : EditCommand;

/// <summary>Phase 11: replaces chord track chords by id (moving, re-rooting, a new quality or bass, or none).</summary>
public sealed record EditChordsCommand(IReadOnlyList<Chord> Chords) : EditCommand;

public sealed record DeleteChordsCommand(IReadOnlyList<string> ChordIds) : EditCommand;

/// <summary>Phase 11: replaces the chord track's key and scale changes (ticks of 0 or more, each once).</summary>
public sealed record SetScalesCommand(IReadOnlyList<ScaleChange> Scales) : EditCommand;

/// <summary>
///     Phase 11, Chord ID's result: writes the detected chords into the chord track over the result's range by its
///     <c>write</c> and sets each detected region key, in one undo step. The <c>chordId</c> job applies it; clients do
///     not send it.
/// </summary>
public sealed record ApplyChordIdCommand(ChordIdResult Result) : EditCommand;

/// <summary>
///     Phase 11: sets how MIDI and audio regions follow the chord track; <c>chords</c> only on MIDI regions (an audio
///     region among them: 422). <c>root</c> on an audio region needs nothing more: its pitch shift follows.
/// </summary>
public sealed record SetPitchSourceCommand(IReadOnlyList<string> RegionIds, PitchSourceMode Mode) : EditCommand;

/// <summary>Phase 11: sets (or with null clears) MIDI and audio regions' own key.</summary>
public sealed record SetRegionKeyCommand(IReadOnlyList<string> RegionIds, RegionKey? Key) : EditCommand;

/// <summary>
///     Phase 11, New Tracks' Session Player: adds a <c>drummer</c>, <c>bassPlayer</c> or <c>keyboardPlayer</c> track at
///     <c>index</c> (the end when left out) playing style <c>styleId</c> (left out: the player's first) with preset
///     <c>presetId</c> (left out: the style's first). A Drummer gets kit patch <c>kitPatchId</c> (left out: the style's);
///     a Bass or Keyboard Player instrument <c>pluginId</c> (left out: the style's patch, else the default synth). With
///     <c>regions</c>, it gets a Session Player region per arrangement section (each with its section kind's defaults
///     and a seed of its own), or, without sections, one of 8 bars from the project start.
/// </summary>
public sealed record AddSessionPlayerTrackCommand(
    SessionPlayerKind Player,
    string? StyleId = null,
    string? PresetId = null,
    string? KitPatchId = null,
    string? PluginId = null,
    string? Name = null,
    int? Index = null,
    bool Regions = true
) : EditCommand;

/// <summary>
///     Phase 11: adds a Session Player region to a Session Player track at <c>startTick</c>, <c>lengthTicks</c> long
///     (left out: to the end of the section it starts in, else 8 bars), with <c>parameters</c> (left out: the track's
///     style's defaults for the section kind there) and <c>seed</c> (left out: drawn).
/// </summary>
public sealed record AddSessionPlayerRegionCommand(
    string TrackId,
    long StartTick,
    long? LengthTicks = null,
    SessionPlayerParameters? Parameters = null,
    string? Name = null,
    long? Seed = null
) : EditCommand;

/// <summary>
///     Phase 11, the Session Player Editor: changes a Session Player region; left-out properties stay. <c>presetId</c>
///     applies that preset of the region's style first (then the other properties given); <c>styleId</c> a style of
///     the track's player (its first preset's parameters unless <c>presetId</c> names another); any other parameter
///     change clears the region's <c>presetId</c>. The XY pad is <c>complexity</c> and <c>intensity</c> (0…1);
///     <c>feel</c> is −1…1, the others 0…1; the switches (<c>drums</c>, <c>bass</c> or <c>keys</c>, the track's
///     player's) replace the region's. The region keeps its seed: the same inputs give the same notes.
/// </summary>
public sealed record SetSessionPlayerRegionCommand(
    string RegionId,
    long? StartTick = null,
    long? LengthTicks = null,
    string? Name = null,
    bool? Muted = null,
    string? StyleId = null,
    string? PresetId = null,
    double? Complexity = null,
    double? Intensity = null,
    double? FillAmount = null,
    bool? FillAtSectionEnds = null,
    double? Swing = null,
    double? Feel = null,
    bool? FollowChords = null,
    DrummerSwitches? Drums = null,
    BassSwitches? Bass = null,
    KeysSwitches? Keys = null
) : EditCommand;

/// <summary>Phase 11, Regenerate: gives Session Player regions a new seed (left out: each one drawn).</summary>
public sealed record RegenerateSessionPlayerCommand(IReadOnlyList<string> RegionIds, long? Seed = null) : EditCommand;

/// <summary>
///     Phase 11, Convert's result: replaces each Session Player region by a MIDI region of its notes (or, from a
///     Drummer, a pattern region of its pattern) at the same place, with its name and mute, in one undo step. The
///     <c>sessionPlayerConvert</c> job applies it; clients do not send it.
/// </summary>
public sealed record ConvertSessionPlayerRegionsCommand(IReadOnlyList<ConvertedSessionPlayerRegion> Regions)
    : EditCommand;

/// <summary>
///     Phase 11: changes the master's Mastering Assistant's settings (its parameters, <see cref="MasteringParameters" />,
///     in their ranges); left-out properties stay. <c>insertId</c> left out: the master's (last) assistant; 422 when it
///     has none. During playback a master in touch, latch or write records the change as <c>setInsertParameter</c> does.
/// </summary>
public sealed record SetMasteringCommand(
    string? InsertId = null,
    MasteringCharacter? Character = null,
    double? TargetLufs = null,
    double? CeilingDbtp = null,
    double? Eq = null,
    double? Dynamics = null,
    double? Width = null,
    bool? MatchedBypass = null
) : EditCommand;

/// <summary>
///     Phase 11, a Mastering analysis' result: stores it in the master's assistant <c>insertId</c>
///     (<c>Insert.mastering</c>). The <c>masteringAnalysis</c> job applies it; clients do not send it.
/// </summary>
public sealed record SetMasteringAnalysisCommand(string InsertId, MasteringAnalysis Analysis) : EditCommand;

/// <summary>Phase 11, the Environment: adds a layer at the end (named <c>Layer N</c> when left out).</summary>
public sealed record AddEnvironmentLayerCommand(string? Name = null) : EditCommand;

/// <summary>Phase 11: renames a layer or moves it to <c>index</c> among the layers.</summary>
public sealed record SetEnvironmentLayerCommand(string LayerId, string? Name = null, int? Index = null) : EditCommand;

/// <summary>Phase 11: removes a layer, its objects and every cable to or from them.</summary>
public sealed record RemoveEnvironmentLayerCommand(string LayerId) : EditCommand;

/// <summary>
///     Phase 11: adds an object of <c>kind</c> to layer <c>layerId</c> (left out: the first layer, made as
///     <c>Layer 1</c> when there is none) at <c>x</c>, <c>y</c>, named <c>name</c> (left out: the kind's), with
///     <c>parameters</c> (missing ones at their defaults) and the kind's other settings
///     (<see cref="EnvironmentObject" />).
/// </summary>
public sealed record AddEnvironmentObjectCommand(
    EnvironmentObjectKind Kind,
    double X,
    double Y,
    string? LayerId = null,
    string? Name = null,
    IReadOnlyDictionary<string, double>? Parameters = null,
    string? Port = null,
    string? TrackId = null,
    string? Address = null,
    IReadOnlyList<TransformCondition>? Conditions = null,
    IReadOnlyList<TransformOperation>? Operations = null,
    IReadOnlyList<DelayTap>? Taps = null,
    IReadOnlyList<int>? Chord = null
) : EditCommand;

/// <summary>
///     Phase 11: changes an Environment object (moving it on its canvas or to another layer, renaming it, its settings);
///     left-out properties stay. <c>parameters</c> are merged into its own; an empty <c>port</c>, <c>trackId</c> or
///     <c>address</c> clears it; <c>conditions</c>, <c>operations</c>, <c>taps</c> and <c>chord</c> replace its own (an
///     empty <c>chord</c> clears it).
/// </summary>
public sealed record SetEnvironmentObjectCommand(
    string ObjectId,
    string? Name = null,
    string? LayerId = null,
    double? X = null,
    double? Y = null,
    IReadOnlyDictionary<string, double>? Parameters = null,
    string? Port = null,
    string? TrackId = null,
    string? Address = null,
    IReadOnlyList<TransformCondition>? Conditions = null,
    IReadOnlyList<TransformOperation>? Operations = null,
    IReadOnlyList<DelayTap>? Taps = null,
    IReadOnlyList<int>? Chord = null
) : EditCommand;

/// <summary>Phase 11: removes Environment objects and every cable to or from them.</summary>
public sealed record RemoveEnvironmentObjectsCommand(IReadOnlyList<string> ObjectIds) : EditCommand;

/// <summary>
///     Phase 11: a cable from outlet <c>outlet</c> of one object to inlet <c>inlet</c> of another (both existing on their
///     kinds; 422 for a cable that exists, or one that would close a cycle).
/// </summary>
public sealed record ConnectEnvironmentCommand(string FromObjectId, int Outlet, string ToObjectId, int Inlet)
    : EditCommand;

public sealed record DisconnectEnvironmentCommand(IReadOnlyList<string> CableIds) : EditCommand;
