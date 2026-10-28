using System.Text.Json.Serialization;

namespace Anode.Contracts;

/// <summary>What a tagged loop holds, and what adding it to a project makes.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LoopKind>))]
public enum LoopKind {
    /// <summary>A WAV file: an audio region that follows the tempo (<c>follow: on</c>) and the key (<c>transpose</c>).</summary>
    [JsonStringEnumMemberName("audio")]
    Audio,

    /// <summary>A Standard MIDI File: a MIDI region, its notes transposed to the project's key.</summary>
    [JsonStringEnumMemberName("midi")]
    Midi,

    /// <summary>A pattern (in the sidecar): a pattern region.</summary>
    [JsonStringEnumMemberName("pattern")]
    Pattern
}

/// <summary>The scale a loop suits, for the Loop Browser's scale filter.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LoopScale>))]
public enum LoopScale {
    [JsonStringEnumMemberName("major")]
    Major,

    [JsonStringEnumMemberName("minor")]
    Minor,

    /// <summary>Neither major nor minor (drums, effects).</summary>
    [JsonStringEnumMemberName("neither")]
    Neither,

    /// <summary>Good for both.</summary>
    [JsonStringEnumMemberName("both")]
    Both
}

/// <summary>
///     A loop of the loop index (<c>&lt;DataDirectory&gt;/Library/Loops/index.json</c>, rebuilt from the installed
///     packs and the user's loops): its pack (null: a user loop), its file on the server's disk, its tempo (bpm) and
///     length in beats (null for a one-shot), its key (a tonic 0 (C) … 11 (B); null: none, as drums), scale and tags
///     (the keyword buttons: Drums, Bass …), whether it is a one-shot, and whether the user marked it a favourite.
/// </summary>
/// <remarks>
///     Adding a loop copies its file into the project. An audio loop with a tempo becomes a region with
///     <c>
///         follow:
///         on
///     </c>
///     at the sidecar's tempo (not analysis) and <c>transpose</c> the interval from its key to the project's
///     (−6…+5 semitones); a one-shot is added unwarped. A MIDI loop's notes are transposed by the same interval.
/// </remarks>
public sealed record LoopInfo(
    string Id,
    string Name,
    LoopKind Kind,
    string? PackId,
    string Path,
    double? Tempo,
    double? Beats,
    int? Key,
    LoopScale Scale,
    IReadOnlyList<string> Tags,
    bool OneShot,
    bool Favourite
);

/// <summary>
///     A Loop Browser search, as query parameters: <c>text</c> in the name or tags; every one of <c>tags</c>; a kind;
///     a key (tonic 0…11); a scale (a major or minor filter also matches <c>both</c>); favourites only; and a page
///     (<c>offset</c>, default 0; <c>limit</c>, default 100, at most 500). Results are sorted by name.
/// </summary>
public sealed record LoopQuery(
    string? Text = null,
    string[]? Tags = null,
    LoopKind? Kind = null,
    int? Key = null,
    LoopScale? Scale = null,
    bool? FavouritesOnly = null,
    int? Offset = null,
    int? Limit = null
);

/// <summary>A page of a loop search, and how many loops match in all.</summary>
public sealed record LoopPage(IReadOnlyList<LoopInfo> Items, int Total);

/// <summary>Marks a loop a favourite, or not.</summary>
public sealed record LoopFavouriteRequest(bool Favourite);

/// <summary>
///     A content pack (<c>.anodepack</c>: a zip of loops, patches, kits and IRs with a <c>pack.json</c> manifest),
///     installed under <c>&lt;DataDirectory&gt;/Library/Packs/&lt;id&gt;/</c>. <c>size</c> is in bytes.
/// </summary>
public sealed record Pack(
    string Id,
    string Name,
    string Version,
    string Author,
    int LoopCount,
    long Size,
    bool Installed
);

/// <summary>Installs a <c>.anodepack</c> from the server's disk (replacing an installed version of the same pack).</summary>
public sealed record InstallPackRequest(string Path);

/// <summary>
///     Adds a loop to the open project, as one undoable step: at <c>tick</c> on <c>trackId</c> (left out: a new track
///     of the loop's kind), or, with <c>cellSceneId</c>, into the Live Loops cell of <c>trackId</c> and that scene.
/// </summary>
public sealed record AddLoopRequest(string LoopId, string? TrackId = null, long Tick = 0, string? CellSceneId = null);

/// <summary>
///     "Add to Loop Library": saves an audio, MIDI or pattern region as a user loop (its file copied into the library,
///     with a sidecar), at the project's tempo, with the given key (a tonic 0…11; null: none), scale (null: neither)
///     and tags; as a one-shot (no tempo) when <c>oneShot</c>.
/// </summary>
public sealed record AddToLoopLibraryRequest(
    string RegionId,
    string Name,
    IReadOnlyList<string> Tags,
    int? Key = null,
    LoopScale? Scale = null,
    bool OneShot = false
);

/// <summary>
///     Launches Live Loops cells, or a scene (its cells; tracks with no cell in it stop), each at its quantise boundary.
/// </summary>
public sealed record LaunchRequest(IReadOnlyList<string>? CellIds = null, string? SceneId = null);

/// <summary>Stops the cells playing on <c>trackIds</c> (left out: every track) at the grid's quantise boundary.</summary>
public sealed record StopRequest(IReadOnlyList<string>? TrackIds = null);

/// <summary>
///     Starts or stops recording in Live Loops: into the cell of <c>trackId</c> and <c>sceneId</c> (the track's input,
///     from the next quantise boundary, for the cell's length or until stopped); without them, a performance recording
///     (cells and Remix FX into the Tracks area, one undo step when it stops).
/// </summary>
public sealed record LiveRecordRequest(bool On, string? TrackId = null, string? SceneId = null);

/// <summary>
///     Anode's loop sidecar, the JSON in <c>&lt;file&gt;.anodeloop.json</c> next to a WAV or MIDI file (a pattern loop
///     has no media file: its sidecar is <c>&lt;name&gt;.anodeloop.json</c> and holds the pattern). Version 1. The
///     fields are <see cref="LoopInfo" />'s; <c>transients</c> are file frames (audio only; null: analyse on add).
/// </summary>
public sealed record LoopSidecar(
    int Version,
    LoopKind Kind,
    string? Name,
    double? Tempo,
    double? Beats,
    int? Key,
    LoopScale Scale,
    IReadOnlyList<string> Tags,
    bool OneShot,
    IReadOnlyList<long>? Transients = null,
    Pattern? Pattern = null
);
