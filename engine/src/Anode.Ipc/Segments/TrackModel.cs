namespace Anode.Ipc.Segments;

/// <summary>One track's compiled playback data, as the server's compiler produces it: both arrays sorted.</summary>
public sealed record TrackModel(int TrackId, PlaybackEvent[] Events, AudioPlaylistEntry[] Playlist);
