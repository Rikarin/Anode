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
