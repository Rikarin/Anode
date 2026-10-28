namespace Anode.Ipc.Segments;

/// <summary>
///     One track's compiled playback data, as the server's compiler produces it: both arrays sorted. Its warped
///     entries' <see cref="AudioPlaylistEntry.WarpStart" /> and <see cref="AudioPlaylistEntry.PitchStart" /> index
///     this track's own <see cref="Warp" /> and <see cref="Pitch" /> (none: no warped entries); the writer rebases them.
/// </summary>
public sealed record TrackModel(
    int TrackId,
    PlaybackEvent[] Events,
    AudioPlaylistEntry[] Playlist,
    WarpPoint[]? Warp = null,
    PitchPoint[]? Pitch = null
);

/// <summary>
///     One Live Loops cell's compiled content (format 4, docs/07-phase-6.md): <see cref="Index" /> in the cell table, on
///     engine track <see cref="TrackId" />, <see cref="Length" /> samples, played as <see cref="Mode" />; its arrays as
///     a track's, positions relative to the cell's start (its own <see cref="Warp" /> and <see cref="Pitch" />, which
///     the writer rebases).
/// </summary>
public sealed record CellModel(
    int Index,
    int TrackId,
    CellMode Mode,
    long Length,
    PlaybackEvent[] Events,
    AudioPlaylistEntry[] Playlist,
    WarpPoint[]? Warp = null,
    PitchPoint[]? Pitch = null
);
