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
public abstract record EditCommand;

/// <summary>Adds an audio track at <c>index</c> (the end when left out).</summary>
public sealed record AddTrackCommand(string? Name = null, int? Index = null, bool Stereo = false) : EditCommand;

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

/// <summary>Moves a region in time, and to another track when <c>trackId</c> is given.</summary>
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

/// <summary>Edits applied as one undo step; <c>label</c> names it in the undo history ("Move Region").</summary>
public sealed record CommandBatch(IReadOnlyList<EditCommand> Commands, string? Label = null);

/// <summary>What an edit, undo or redo did: the new revision and the delta every client receives too.</summary>
public sealed record CommandResult(long Revision, ProjectDelta Delta);

public sealed record HistoryEntry(string Label, long Revision);

/// <summary>The undo stack (most recent first) and the redo stack (next first).</summary>
public sealed record UndoHistory(IReadOnlyList<HistoryEntry> Undo, IReadOnlyList<HistoryEntry> Redo);
