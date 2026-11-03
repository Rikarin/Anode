using Anode.Contracts;

namespace Anode.Media.Ffmpeg;

/// <summary>
///     What FFprobe says of a movie (the server's <c>Movie</c> is made from it): the first video stream's codec, size,
///     frame rate and frame count, the timecode of its first frame (from a <c>tmcd</c> track or the <c>timecode</c> tag,
///     in seconds; 0 when none), the duration, and the first audio stream (null: none). A file without video throws
///     <see cref="FfmpegException" />.
/// </summary>
public sealed record MovieProbe(
    string VideoCodec,
    int Width,
    int Height,
    VideoFrameRate FrameRate,
    long Frames,
    double TimecodeStartSeconds,
    double DurationSeconds,
    MovieAudio? Audio
);

/// <summary>
///     The golden path's test movie (docs/11-phase-10.md): a frame counter and timecode burned in, a 1 kHz beep of
///     <c>BeepSeconds</c> at the start of every second, a cut to a new solid background at each of
///     <c>SceneCutFrames</c>, H.264 in MOV (or MP4 by the extension) with AAC audio at <c>AudioSampleRate</c>.
/// </summary>
public sealed record TestMovieSpec(
    double DurationSeconds,
    IReadOnlyList<long> SceneCutFrames,
    VideoFrameRate? FrameRate = null,
    int Width = 640,
    int Height = 360,
    double TimecodeStartSeconds = 0,
    double BeepHz = 1000,
    double BeepSeconds = 0.05,
    int AudioSampleRate = 48_000
);

/// <summary>
///     The media operations (docs/11-phase-10.md, workstream E), each one FFmpeg or FFprobe run through
///     <see cref="FfmpegRunner" />, with progress and cancellation.
/// </summary>
public static class MovieMedia {
    public static Task<MovieProbe> ProbeAsync(
        FfmpegRunner runner,
        string moviePath,
        CancellationToken cancellation = default
    ) =>
        throw Pending.WorkstreamE();

    /// <summary>
    ///     The first audio stream as a 32-bit float WAV at <paramref name="sampleRate" /> (resampled by FFmpeg's soxr),
    ///     from the movie's first frame (an audio stream that starts later is padded with silence, so sample 0 is
    ///     frame 0's time).
    /// </summary>
    public static Task ExtractAudioAsync(
        FfmpegRunner runner,
        string moviePath,
        string wavPath,
        int sampleRate,
        IProgress<double>? progress = null,
        CancellationToken cancellation = default
    ) =>
        throw Pending.WorkstreamE();

    /// <summary>
    ///     JPEGs of <paramref name="frames" /> (frame numbers), <paramref name="height" /> pixels high, as
    ///     <c>&lt;frame&gt;.jpg</c> in <paramref name="folder" />; returns the paths in the frames' order.
    /// </summary>
    public static Task<IReadOnlyList<string>> ThumbnailsAsync(
        FfmpegRunner runner,
        string moviePath,
        IReadOnlyList<long> frames,
        int height,
        string folder,
        CancellationToken cancellation = default
    ) =>
        throw Pending.WorkstreamE();

    /// <summary>
    ///     The frame numbers that start a new scene (FFmpeg's <c>scene</c> score above <paramref name="threshold" />),
    ///     ascending.
    /// </summary>
    public static Task<IReadOnlyList<long>> DetectScenesAsync(
        FfmpegRunner runner,
        string moviePath,
        double threshold,
        IProgress<double>? progress = null,
        CancellationToken cancellation = default
    ) =>
        throw Pending.WorkstreamE();

    /// <summary>
    ///     A new movie at <paramref name="outputPath" />: the video stream copied, and <paramref name="wavPath" /> (aligned to
    ///     the movie's first frame) replacing the audio streams (<paramref name="replace" />) or added after them, encoded
    ///     as <paramref name="codec" /> (PCM only in MOV).
    /// </summary>
    public static Task RemuxAsync(
        FfmpegRunner runner,
        string moviePath,
        string wavPath,
        string outputPath,
        bool replace,
        MovieAudioCodec codec,
        IProgress<double>? progress = null,
        CancellationToken cancellation = default
    ) =>
        throw Pending.WorkstreamE();

    /// <summary>Generates the test movie <paramref name="spec" /> describes at <paramref name="path" />.</summary>
    public static Task GenerateTestMovieAsync(
        FfmpegRunner runner,
        string path,
        TestMovieSpec spec,
        CancellationToken cancellation = default
    ) =>
        throw Pending.WorkstreamE();
}
