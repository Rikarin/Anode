using System.Text.Json.Serialization;

namespace Anode.Contracts;

// Video (docs/11-phase-10.md, docs/adr/0013-video-and-listen-in.md): one movie per project, played by the editor's
// <video> element locked to the transport; FFmpeg (a separate process) does the media work. The project tick ↔ movie
// time mapping: at project position p, the movie shows movie time m = seconds(p) − seconds(startTick) + offsetSeconds
// (seconds through the tempo map; m < 0 or past the duration shows no frame), and the frame is floor(m × rate).

/// <summary>A video frame rate as a fraction: 24000/1001 (23.976), 25/1, 30000/1001 (29.97), …</summary>
public sealed record VideoFrameRate(int Numerator, int Denominator) {
    [JsonIgnore]
    public double FramesPerSecond => (double)Numerator / Denominator;
}

/// <summary>A movie's first audio stream, as FFprobe reports it.</summary>
public sealed record MovieAudio(string Codec, int Channels, int SampleRate);

/// <summary>
///     The project's movie (<c>Project.movie</c>; null: none). <c>path</c> is relative to the project folder when the
///     file was copied into it (<c>inProject</c>, under <c>Movies/</c>), else absolute (a reference). <c>startTick</c>
///     is where its frame at <c>offsetSeconds</c> plays (offset 0: its first frame there); <c>frameRate</c>,
///     <c>timecodeStartSeconds</c> (the timecode of its first frame, in seconds; 0 when it has none), duration, frame
///     count, size, and video codec (FFprobe's name: <c>h264</c>, <c>hevc</c>, <c>prores</c>) come from the probe, as
///     does <c>audio</c> (null: no audio stream).
/// </summary>
public sealed record Movie(
    string Path,
    string Name,
    bool InProject,
    long StartTick,
    double OffsetSeconds,
    VideoFrameRate FrameRate,
    double TimecodeStartSeconds,
    double DurationSeconds,
    long Frames,
    int Width,
    int Height,
    string Codec,
    MovieAudio? Audio
);

/// <summary>What a marker is: one the user made, or one Detect Scene Cuts made (an ordinary, editable marker).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MarkerKind>))]
public enum MarkerKind {
    [JsonStringEnumMemberName("marker")]
    Marker,

    /// <summary>A scene cut of the movie (<c>addSceneMarkers</c>), drawn in the movie track's colour.</summary>
    [JsonStringEnumMemberName("scene")]
    Scene
}

/// <summary>
///     Movie import (<c>POST /project/movie</c>, job <c>movieImport</c>): a MOV, MP4 or M4V on the server's disk, probed
///     by FFprobe, copied into the project's <c>Movies/</c> when <c>copyIntoProject</c> (else referenced where it is),
///     placed at <c>startTick</c> (null: the project start) with offset 0; then <c>setMovie</c> (one undo step; undo
///     leaves a copied file). It replaces a movie the project has.
/// </summary>
public sealed record MovieImportRequest(string Path, bool CopyIntoProject = true, long? StartTick = null);

/// <summary>
///     Thumbnails (<c>POST /project/movie/thumbnails</c>, job <c>thumbnails</c>): renders <c>frames</c> (movie frame
///     numbers) as JPEGs <c>height</c> pixels high (the width keeps the aspect), cached per movie; then each is
///     <c>GET /project/movie/frames/{frame}?height=</c>.
/// </summary>
public sealed record ThumbnailsRequest(IReadOnlyList<long> Frames, int Height = 64);

/// <summary>
///     The movie frames cached as thumbnails at <c>height</c> (<c>GET /project/movie/thumbnails?height=</c>): their
///     size and the frame numbers available, ascending.
/// </summary>
public sealed record MovieThumbnails(int Width, int Height, IReadOnlyList<long> Frames);

/// <summary>
///     Detect Scene Cuts (<c>POST /project/movie/scenes</c>, job <c>sceneDetect</c>): FFmpeg's scene score above
///     <c>threshold</c> (0…1) marks a cut; the job applies <c>addSceneMarkers</c> with the cuts' ticks (the first frame
///     of each new scene, through the mapping above), replacing the existing scene markers when
///     <c>replaceExisting</c>.
/// </summary>
public sealed record SceneDetectRequest(double Threshold = 0.4, bool ReplaceExisting = true);

/// <summary>
///     Import Audio from Movie (<c>POST /project/movie/audio/import</c>, job <c>movieAudioImport</c>): the movie's
///     first audio stream extracted at the project's sample rate into <c>Audio Files</c>, then, through
///     <c>importTracks</c> (one undo step), a new audio track (<c>trackName</c>, null: the movie's name) with a region
///     at the movie's first frame.
/// </summary>
public sealed record MovieAudioImportRequest(string? TrackName = null);

/// <summary>The audio codec Export Audio to Movie writes.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MovieAudioCodec>))]
public enum MovieAudioCodec {
    /// <summary>AAC-LC at 256 kbit/s (MP4, M4V and MOV).</summary>
    [JsonStringEnumMemberName("aac")]
    Aac,

    /// <summary>24-bit PCM (MOV only; an MP4 or M4V gets AAC).</summary>
    [JsonStringEnumMemberName("pcm")]
    Pcm
}

/// <summary>
///     Export Audio to Movie (<c>POST /project/movie/audio/export</c>, job <c>movieAudioExport</c>): bounces the master
///     (stereo, or the stereo down-mix of a surround master) over the movie's span, aligned to its frames, and writes a
///     new movie <c>fileName</c> in <c>directory</c> (default <c>&lt;project&gt;/Exports</c>; the movie's extension
///     added when missing) with the video stream copied and the bounce replacing the audio (<c>replace</c>) or added as
///     another audio track. The movie itself is not changed.
/// </summary>
public sealed record MovieAudioExportRequest(
    string FileName,
    string? Directory = null,
    bool Replace = true,
    MovieAudioCodec Codec = MovieAudioCodec.Aac
);
