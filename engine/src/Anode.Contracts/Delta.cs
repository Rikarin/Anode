using System.Text.Json.Serialization;

namespace Anode.Contracts;

/// <summary>
///     What changed between two revisions of the open project (docs/adr/0004-state-ownership.md). Clients apply the
///     operations in order; a client that misses a revision refetches the project.
/// </summary>
public sealed record ProjectDelta(long Revision, IReadOnlyList<DeltaOp> Ops);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(TrackUpserted), "trackUpserted")]
[JsonDerivedType(typeof(TrackRemoved), "trackRemoved")]
[JsonDerivedType(typeof(RegionUpserted), "regionUpserted")]
[JsonDerivedType(typeof(RegionRemoved), "regionRemoved")]
[JsonDerivedType(typeof(AudioFileUpserted), "audioFileUpserted")]
[JsonDerivedType(typeof(SettingsChanged), "settingsChanged")]
[JsonDerivedType(typeof(MasterChanged), "masterChanged")]
[JsonDerivedType(typeof(ProjectRenamed), "projectRenamed")]
[JsonDerivedType(typeof(MidiRegionUpserted), "midiRegionUpserted")]
[JsonDerivedType(typeof(MidiRegionRemoved), "midiRegionRemoved")]
[JsonDerivedType(typeof(TakeFolderUpserted), "takeFolderUpserted")]
[JsonDerivedType(typeof(TakeFolderRemoved), "takeFolderRemoved")]
[JsonDerivedType(typeof(MarkerUpserted), "markerUpserted")]
[JsonDerivedType(typeof(MarkerRemoved), "markerRemoved")]
[JsonDerivedType(typeof(SectionUpserted), "sectionUpserted")]
[JsonDerivedType(typeof(SectionRemoved), "sectionRemoved")]
[JsonDerivedType(typeof(NotesChanged), "notesChanged")]
[JsonDerivedType(typeof(AutomationLaneUpserted), "automationLaneUpserted")]
[JsonDerivedType(typeof(AutomationLaneRemoved), "automationLaneRemoved")]
[JsonDerivedType(typeof(GroupUpserted), "groupUpserted")]
[JsonDerivedType(typeof(GroupRemoved), "groupRemoved")]
[JsonDerivedType(typeof(PatternRegionUpserted), "patternRegionUpserted")]
[JsonDerivedType(typeof(PatternRegionRemoved), "patternRegionRemoved")]
[JsonDerivedType(typeof(LiveLoopsChanged), "liveLoopsChanged")]
[JsonDerivedType(typeof(GrooveUpserted), "grooveUpserted")]
[JsonDerivedType(typeof(GrooveRemoved), "grooveRemoved")]
[JsonDerivedType(typeof(ArticulationSetUpserted), "articulationSetUpserted")]
[JsonDerivedType(typeof(ArticulationSetRemoved), "articulationSetRemoved")]
[JsonDerivedType(typeof(LaneSetUpserted), "laneSetUpserted")]
[JsonDerivedType(typeof(LaneSetRemoved), "laneSetRemoved")]
[JsonDerivedType(typeof(StaffStyleUpserted), "staffStyleUpserted")]
[JsonDerivedType(typeof(StaffStyleRemoved), "staffStyleRemoved")]
[JsonDerivedType(typeof(ScoreSymbolUpserted), "scoreSymbolUpserted")]
[JsonDerivedType(typeof(ScoreSymbolRemoved), "scoreSymbolRemoved")]
[JsonDerivedType(typeof(LyricUpserted), "lyricUpserted")]
[JsonDerivedType(typeof(LyricRemoved), "lyricRemoved")]
[JsonDerivedType(typeof(ChordSymbolUpserted), "chordSymbolUpserted")]
[JsonDerivedType(typeof(ChordSymbolRemoved), "chordSymbolRemoved")]
[JsonDerivedType(typeof(ScoreSetUpserted), "scoreSetUpserted")]
[JsonDerivedType(typeof(ScoreSetRemoved), "scoreSetRemoved")]
[JsonDerivedType(typeof(ScoreLayoutChanged), "scoreLayoutChanged")]
[JsonDerivedType(typeof(MovieChanged), "movieChanged")]
public abstract record DeltaOp;

/// <summary>A track was added or changed; <c>index</c> is its position in the track list.</summary>
public sealed record TrackUpserted(Track Track, int Index) : DeltaOp;

public sealed record TrackRemoved(string Id) : DeltaOp;

public sealed record RegionUpserted(AudioRegion Region) : DeltaOp;

public sealed record RegionRemoved(string Id) : DeltaOp;

public sealed record AudioFileUpserted(AudioFile File) : DeltaOp;

public sealed record SettingsChanged(ProjectSettings Settings) : DeltaOp;

public sealed record MasterChanged(ChannelStrip Strip) : DeltaOp;

public sealed record ProjectRenamed(string Name) : DeltaOp;

/// <summary>A MIDI region was added or changed (its notes included).</summary>
public sealed record MidiRegionUpserted(MidiRegion Region) : DeltaOp;

public sealed record MidiRegionRemoved(string Id) : DeltaOp;

public sealed record TakeFolderUpserted(TakeFolder Folder) : DeltaOp;

public sealed record TakeFolderRemoved(string Id) : DeltaOp;

public sealed record MarkerUpserted(Marker Marker) : DeltaOp;

public sealed record MarkerRemoved(string Id) : DeltaOp;

public sealed record SectionUpserted(ArrangementMarker Section) : DeltaOp;

public sealed record SectionRemoved(string Id) : DeltaOp;

/// <summary>The project notes changed.</summary>
public sealed record NotesChanged(string Text) : DeltaOp;

public sealed record AutomationLaneUpserted(AutomationLane Lane) : DeltaOp;

public sealed record AutomationLaneRemoved(string Id) : DeltaOp;

public sealed record GroupUpserted(MixerGroup Group) : DeltaOp;

public sealed record GroupRemoved(int Number) : DeltaOp;

/// <summary>A pattern region was added or changed (its pattern included).</summary>
public sealed record PatternRegionUpserted(PatternRegion Region) : DeltaOp;

public sealed record PatternRegionRemoved(string Id) : DeltaOp;

/// <summary>The Live Loops grid changed: its scenes, cells, quantise or visibility (the whole grid).</summary>
public sealed record LiveLoopsChanged(LiveLoopsGrid LiveLoops) : DeltaOp;

/// <summary>Phase 7: a groove template was added or changed.</summary>
public sealed record GrooveUpserted(GrooveTemplate Groove) : DeltaOp;

public sealed record GrooveRemoved(string Id) : DeltaOp;

/// <summary>Phase 7: an articulation set was added or changed.</summary>
public sealed record ArticulationSetUpserted(ArticulationSet Set) : DeltaOp;

public sealed record ArticulationSetRemoved(string Id) : DeltaOp;

/// <summary>Phase 7: a Step Editor lane set was added or changed.</summary>
public sealed record LaneSetUpserted(LaneSet Set) : DeltaOp;

public sealed record LaneSetRemoved(string Id) : DeltaOp;

/// <summary>
///     Phase 9 (docs/10-phase-9.md): a project staff style was added or changed. The score content's deltas change
///     <c>Project.score</c>; a track's staff style comes as <c>trackUpserted</c>, a region's notation as
///     <c>midiRegionUpserted</c>. Score views are refetched (<c>GET /project/score</c>) after any of them, or after
///     notes, meter, key, tempo or markers change.
/// </summary>
public sealed record StaffStyleUpserted(StaffStyle Style) : DeltaOp;

public sealed record StaffStyleRemoved(string Id) : DeltaOp;

/// <summary>Phase 9: a score symbol was added or changed.</summary>
public sealed record ScoreSymbolUpserted(ScoreSymbol Symbol) : DeltaOp;

public sealed record ScoreSymbolRemoved(string Id) : DeltaOp;

/// <summary>Phase 9: a lyric syllable was added or changed.</summary>
public sealed record LyricUpserted(LyricSyllable Lyric) : DeltaOp;

public sealed record LyricRemoved(string Id) : DeltaOp;

/// <summary>Phase 9: a chord symbol was added or changed.</summary>
public sealed record ChordSymbolUpserted(ChordSymbol Chord) : DeltaOp;

public sealed record ChordSymbolRemoved(string Id) : DeltaOp;

/// <summary>Phase 9: a score set was added or changed.</summary>
public sealed record ScoreSetUpserted(ScoreSet Set) : DeltaOp;

public sealed record ScoreSetRemoved(string Id) : DeltaOp;

/// <summary>Phase 9: the score layout changed.</summary>
public sealed record ScoreLayoutChanged(ScoreLayout Layout) : DeltaOp;

/// <summary>
///     Phase 10 (docs/11-phase-10.md): the project's movie was set, moved or removed (null). Formats and panners come as
///     <c>trackUpserted</c>, <c>masterChanged</c> and <c>settingsChanged</c>; scene markers as <c>markerUpserted</c>.
/// </summary>
public sealed record MovieChanged(Movie? Movie) : DeltaOp;
