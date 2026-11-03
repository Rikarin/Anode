namespace Anode.Media.Ffmpeg;

/// <summary>
///     Where <c>ffmpeg</c> and <c>ffprobe</c> are: the pinned LGPL build fetched by <c>scripts/fetch-ffmpeg.mjs</c> for
///     development, bundled beside the server in a release (docs/adr/0013-video-and-listen-in.md).
/// </summary>
public sealed record FfmpegTools(string FfmpegPath, string FfprobePath) {
    /// <summary>
    ///     The tools in <paramref name="directory" /> (null: beside the server, then the development fetch folder), or null
    ///     when either is missing; the Server answers the media endpoints 503 without them.
    /// </summary>
    public static FfmpegTools? Locate(string? directory = null) => throw Pending.WorkstreamE();
}

/// <summary>A finished FFmpeg or FFprobe run: its exit code and what it wrote to standard output and error.</summary>
public sealed record FfmpegResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
///     Runs <c>ffmpeg</c> and <c>ffprobe</c> as child processes (never linked): arguments as a list (no shell), a timeout,
///     progress from <c>-progress pipe:1</c> as 0…1 of <c>durationSeconds</c>, and cancellation (the process is killed
///     and its partial output deleted by the caller).
/// </summary>
public sealed class FfmpegRunner(FfmpegTools tools) {
    public FfmpegTools Tools { get; } = tools;

    /// <summary>
    ///     Runs ffmpeg with <paramref name="arguments" />; a non-zero exit throws <see cref="FfmpegException" /> with its
    ///     last error lines, a timeout <see cref="TimeoutException" />.
    /// </summary>
    public Task<FfmpegResult> RunFfmpegAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        double? durationSeconds = null,
        IProgress<double>? progress = null,
        CancellationToken cancellation = default
    ) =>
        throw Pending.WorkstreamE();

    /// <summary>Runs ffprobe with <paramref name="arguments" />, as <see cref="RunFfmpegAsync" />.</summary>
    public Task<FfmpegResult> RunFfprobeAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellation = default
    ) =>
        throw Pending.WorkstreamE();
}

/// <summary>FFmpeg or FFprobe failed: its exit code and the end of its error output.</summary>
public sealed class FfmpegException : Exception {
    public FfmpegException() { }

    public FfmpegException(string message) : base(message) { }

    public FfmpegException(string message, Exception innerException) : base(message, innerException) { }

    public int ExitCode { get; init; }
}
