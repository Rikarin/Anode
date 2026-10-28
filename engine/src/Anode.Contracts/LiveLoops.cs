using System.Text.Json.Serialization;

namespace Anode.Contracts;

/// <summary>
///     Where a Live Loops launch (or stop) takes effect: at once, or at the next boundary of a note value or of 1, 2 or
///     4 bars, computed from the tempo map (and, for bars, the signature).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<LaunchQuantize>))]
public enum LaunchQuantize {
    [JsonStringEnumMemberName("off")]
    Off,

    [JsonStringEnumMemberName("sixteenth")]
    Sixteenth,

    [JsonStringEnumMemberName("eighth")]
    Eighth,

    [JsonStringEnumMemberName("quarter")]
    Quarter,

    [JsonStringEnumMemberName("half")]
    Half,

    [JsonStringEnumMemberName("bar")]
    Bar,

    [JsonStringEnumMemberName("twoBars")]
    TwoBars,

    [JsonStringEnumMemberName("fourBars")]
    FourBars
}

[JsonConverter(typeof(JsonStringEnumConverter<CellKind>))]
public enum CellKind {
    [JsonStringEnumMemberName("audio")]
    Audio,

    [JsonStringEnumMemberName("midi")]
    Midi,

    [JsonStringEnumMemberName("pattern")]
    Pattern
}

[JsonConverter(typeof(JsonStringEnumConverter<CellPlayMode>))]
public enum CellPlayMode {
    /// <summary>Plays over and over until stopped; a launch while playing keeps it playing.</summary>
    [JsonStringEnumMemberName("loop")]
    Loop,

    /// <summary>Plays once and stops at its end.</summary>
    [JsonStringEnumMemberName("oneShot")]
    OneShot,

    /// <summary>Plays once; a launch while it plays restarts it (at the quantise boundary).</summary>
    [JsonStringEnumMemberName("retrigger")]
    Retrigger
}

/// <summary>A cell's state in the launcher, as the <c>cell</c> event reports it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CellState>))]
public enum CellState {
    /// <summary>Launched, waiting for its quantise boundary.</summary>
    [JsonStringEnumMemberName("queued")]
    Queued,

    [JsonStringEnumMemberName("playing")]
    Playing,

    /// <summary>Stopped (or replaced), waiting for its quantise boundary.</summary>
    [JsonStringEnumMemberName("stopping")]
    Stopping,

    [JsonStringEnumMemberName("stopped")]
    Stopped
}

/// <summary>A row of the Live Loops grid.</summary>
public sealed record Scene(string Id, string Name);

/// <summary>
///     What a cell plays: exactly one of a snapshot of an audio region, a MIDI region or a pattern region, the one of
///     its <c>kind</c>. The snapshot's <c>id</c> is the cell's, its <c>trackId</c> the cell's track and its
///     <c>startTick</c> 0; note and controller ids are unique project-wide, as in any MIDI region.
/// </summary>
public sealed record CellContent(AudioRegion? Audio = null, MidiRegion? Midi = null, PatternRegion? Pattern = null);

/// <summary>
///     A Live Loops cell: at most one per (track, scene). <c>lengthTicks</c> is its loop length; <c>quantize</c>
///     overrides the grid's for launching it (null: the grid's).
/// </summary>
/// <remarks>
///     Cells compile like regions, into per-cell slices of the segment (docs/07-phase-6.md). An audio cell is held in
///     memory whole (warped at model load, at most 60 s), so a launch never waits for the disk.
/// </remarks>
public sealed record Cell(
    string Id,
    string TrackId,
    string SceneId,
    CellKind Kind,
    string Name,
    long LengthTicks,
    CellPlayMode PlayMode,
    LaunchQuantize? Quantize,
    CellContent Content
);

/// <summary>
///     The Live Loops grid (docs/07-phase-6.md): its scenes in order, its cells, the launch quantise, and whether the
///     grid is shown beside the Tracks area. A track whose <c>liveLoopsActive</c> is on plays its playing cell instead
///     of its Tracks-area regions; launching a scene launches its cells and stops the cells of tracks with none in it.
/// </summary>
/// <remarks>
///     Remix FX is the master insert <c>anode.remix-fx</c>, added by default when the grid is first shown; its state is
///     the insert's parameters (two XY pads), which automate like any insert's. While a performance is recorded, the
///     server turns the launcher's cell states into regions on their tracks where they played and Remix FX moves into
///     automation, as one undo step at the end.
/// </remarks>
public sealed record LiveLoopsGrid(
    IReadOnlyList<Scene> Scenes,
    IReadOnlyList<Cell> Cells,
    LaunchQuantize Quantize,
    bool Shown
);
