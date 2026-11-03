using Anode.Contracts;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Anode.Media.Ffmpeg;

/// <summary>
///     What FFprobe says of a movie (the server's <c>Movie</c> is made from it): the first video stream's codec, size,
///     frame rate (its <c>r_frame_rate</c>, as a fraction) and frame count, the timecode of its first frame (from the
///     <c>timecode</c> tag of the video stream, a <c>tmcd</c> track or the file, in seconds: the label's frame count over
///     the frame rate, drop-frame when the label says so; 0 when none), the video stream's duration, and the first audio
///     stream (null: none). A file without video throws <see cref="FfmpegException" />.
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
///     The golden path's test movie (docs/11-phase-10.md): the frame number and the timecode burned in (with
///     <c>drawtext</c> and a system font when the build and machine have them) over FFmpeg's <c>testsrc</c> pattern (whose
///     own counter shows the time in hundredths, so every frame differs), on a solid background that changes colour at
///     each of <c>SceneCutFrames</c> (the first frame of each new scene); a stereo 1 kHz beep of <c>BeepSeconds</c> at
///     half scale at the start of every second (sample <c>k × AudioSampleRate</c>); H.264 in MOV (or MP4 by the
///     extension) with AAC audio at <c>AudioSampleRate</c>, or VP9 with Opus for <c>.webm</c>. <c>FrameRate</c> null is
///     25 fps. <c>VideoEncoder</c> null picks <c>h264_videotoolbox</c>, then <c>libopenh264</c>, then <c>libx264</c>
///     (a GPL build's, for local development only), whichever the build has.
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
    int AudioSampleRate = 48_000,
    string? VideoEncoder = null
) {
    /// <summary>The frame rate the movie has: <see cref="FrameRate" />, or 25 fps.</summary>
    public VideoFrameRate Rate => FrameRate ?? new(25, 1);

    /// <summary>The movie's frame count: the duration in whole frames, rounded.</summary>
    public long Frames => (long)Math.Round(DurationSeconds * Rate.FramesPerSecond);
}

/// <summary>
///     The media operations (docs/11-phase-10.md, workstream E), each one FFmpeg or FFprobe run through
///     <see cref="FfmpegRunner" />, with progress and cancellation. Times are frame-exact: "the movie's start" is its
///     first video frame (a movie whose streams start at different times is aligned to it); frame numbers count
///     decoded frames from 0.
/// </summary>
public static partial class MovieMedia {
    /// <summary>How long a probe may take.</summary>
    static readonly TimeSpan ProbeTimeout = TimeSpan.FromMinutes(2);

    /// <summary>How long a whole-movie operation may take (a feature-length movie decoded once, or remuxed).</summary>
    static readonly TimeSpan WorkTimeout = TimeSpan.FromHours(2);

    /// <summary>The movie's facts (see <see cref="MovieProbe" />).</summary>
    public static async Task<MovieProbe> ProbeAsync(
        FfmpegRunner runner,
        string moviePath,
        CancellationToken cancellation = default
    ) =>
        (await DetailAsync(runner, moviePath, cancellation).ConfigureAwait(false)).Movie;

    /// <summary>
    ///     The first audio stream as a 32-bit float WAV (RF64 past 4 GB) at <paramref name="sampleRate" /> (resampled by
    ///     FFmpeg's <c>aresample</c>), from the movie's first frame: sample 0 is frame 0's time (an audio stream that starts
    ///     later is padded with silence, one that starts earlier is trimmed). A movie without audio throws
    ///     <see cref="FfmpegException" />.
    /// </summary>
    public static async Task ExtractAudioAsync(
        FfmpegRunner runner,
        string moviePath,
        string wavPath,
        int sampleRate,
        IProgress<double>? progress = null,
        CancellationToken cancellation = default
    ) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        var detail = await DetailAsync(runner, moviePath, cancellation).ConfigureAwait(false);
        if (detail.Movie.Audio is null) {
            throw new FfmpegException($"{Path.GetFileName(moviePath)} has no audio stream.") { ExitCode = -1 };
        }

        // With -copyts the audio keeps the file's timestamps; first_pts pads or trims it to start at the first frame.
        var firstSample = (long)Math.Round(detail.VideoStartSeconds * sampleRate);
        await runner.RunFfmpegAsync(
                [
                    "-y", "-copyts", "-i", moviePath, "-map", "0:a:0", "-vn", "-sn", "-dn",
                    "-af", $"aresample={Text(sampleRate)}:first_pts={Text(firstSample)}",
                    "-c:a", "pcm_f32le", "-rf64", "auto", "-f", "wav", wavPath
                ],
                WorkTimeout,
                detail.Movie.DurationSeconds,
                progress,
                cancellation
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     JPEGs of <paramref name="frames" /> (frame numbers, decoded in one pass), <paramref name="height" /> pixels high
    ///     (the width keeps the aspect, rounded to even), as <c>&lt;frame&gt;.jpg</c> in <paramref name="folder" />
    ///     (created when missing; existing files replaced); returns the paths in the frames' order. A frame past the
    ///     movie's end throws <see cref="ArgumentOutOfRangeException" /> (nothing is written).
    /// </summary>
    public static async Task<IReadOnlyList<string>> ThumbnailsAsync(
        FfmpegRunner runner,
        string moviePath,
        IReadOnlyList<long> frames,
        int height,
        string folder,
        CancellationToken cancellation = default
    ) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (frames.Count == 0) {
            return [];
        }

        var wanted = frames.Distinct().Order().ToList();
        if (wanted[0] < 0) {
            throw new ArgumentOutOfRangeException(nameof(frames), "Frame numbers start at 0.");
        }

        var detail = await DetailAsync(runner, moviePath, cancellation).ConfigureAwait(false);
        if (wanted[^1] >= detail.Movie.Frames) {
            throw new ArgumentOutOfRangeException(
                nameof(frames),
                $"Frame {wanted[^1]} is past the movie's {detail.Movie.Frames} frames."
            );
        }

        Directory.CreateDirectory(folder);
        var work = Path.Combine(folder, $".thumbnails-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        try {
            var select = string.Join('+', wanted.Select(static f => $"eq(n\\,{Text(f)})"));
            await runner.RunFfmpegAsync(
                    [
                        "-y", "-i", moviePath, "-map", "0:v:0", "-an", "-sn", "-dn",
                        "-vf",
                        $"select='{select}',scale=-2:{Text(height)}:flags=bicubic:out_range=full,format=yuvj420p",
                        "-fps_mode", "passthrough", "-frames:v", Text(wanted.Count), "-q:v", "3",
                        "-f", "image2", "-start_number", "0", Path.Combine(work, "%08d.jpg")
                    ],
                    WorkTimeout,
                    cancellation: cancellation
                )
                .ConfigureAwait(false);
            for (var i = 0; i < wanted.Count; i++) {
                var written = Path.Combine(work, $"{i:00000000}.jpg");
                if (!File.Exists(written)) {
                    throw new FfmpegException($"FFmpeg wrote {i} of {wanted.Count} thumbnails.") { ExitCode = 0 };
                }

                File.Move(written, ThumbnailPath(folder, wanted[i]), true);
            }
        } finally {
            Directory.Delete(work, true);
        }

        return frames.Select(f => ThumbnailPath(folder, f)).ToList();
    }

    /// <summary>
    ///     The frame numbers that start a new scene (FFmpeg's <c>scene</c> score above <paramref name="threshold" />,
    ///     0…1), ascending, from each frame's timestamp relative to the first frame.
    /// </summary>
    public static async Task<IReadOnlyList<long>> DetectScenesAsync(
        FfmpegRunner runner,
        string moviePath,
        double threshold,
        IProgress<double>? progress = null,
        CancellationToken cancellation = default
    ) {
        ArgumentOutOfRangeException.ThrowIfNegative(threshold);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(threshold, 1);
        var detail = await DetailAsync(runner, moviePath, cancellation).ConfigureAwait(false);
        var result = await runner.RunFfmpegAsync(
                [
                    "-copyts", "-i", moviePath, "-map", "0:v:0", "-an", "-sn", "-dn",
                    "-vf", $"select='gt(scene\\,{Text(threshold)})',showinfo", "-fps_mode", "passthrough",
                    "-f", "null", "-"
                ],
                WorkTimeout,
                detail.Movie.DurationSeconds,
                progress,
                cancellation
            )
            .ConfigureAwait(false);
        var rate = detail.Movie.FrameRate.FramesPerSecond;
        var cuts = new SortedSet<long>();
        foreach (Match match in ShowinfoTime().Matches(result.StandardError)) {
            var time = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            cuts.Add((long)Math.Round((time - detail.VideoStartSeconds) * rate));
        }

        return [..cuts];
    }

    /// <summary>
    ///     A new movie at <paramref name="outputPath" /> (its extension picks the container): every video stream copied
    ///     (packets unchanged), the file's metadata (the timecode among it) kept, and <paramref name="wavPath" /> (its
    ///     sample 0 at the movie's first frame) replacing the audio streams (<paramref name="replace" />) or added after
    ///     them (copied), encoded as <paramref name="codec" /> (AAC-LC at 256 kbit/s, or 24-bit PCM in MOV only: an MP4 or
    ///     M4V gets AAC); <c>faststart</c>, so a browser plays it while it loads.
    /// </summary>
    public static async Task RemuxAsync(
        FfmpegRunner runner,
        string moviePath,
        string wavPath,
        string outputPath,
        bool replace,
        MovieAudioCodec codec,
        IProgress<double>? progress = null,
        CancellationToken cancellation = default
    ) {
        var detail = await DetailAsync(runner, moviePath, cancellation).ConfigureAwait(false);
        var pcm = codec == MovieAudioCodec.Pcm
            && HasExtension(outputPath, ".mov");
        var kept = replace ? 0 : detail.AudioStreams;

        // FFmpeg starts each input at its first timestamp; the WAV is moved to where the first frame lands.
        var offset = detail.VideoStartSeconds - detail.FormatStartSeconds;
        List<string> arguments = ["-y", "-i", moviePath, "-itsoffset", Text(offset), "-i", wavPath, "-map", "0:v"];
        if (!replace) {
            arguments.AddRange(["-map", "0:a?"]);
        }

        arguments.AddRange(["-map", "1:a:0", "-c", "copy"]);
        arguments.AddRange(
            pcm ? [$"-c:a:{Text(kept)}", "pcm_s24le"] : [$"-c:a:{Text(kept)}", "aac", $"-b:a:{Text(kept)}", "256k"]
        );
        arguments.AddRange(["-map_metadata", "0", "-movflags", "+faststart", outputPath]);
        await runner.RunFfmpegAsync(arguments, WorkTimeout, detail.Movie.DurationSeconds, progress, cancellation)
            .ConfigureAwait(false);
    }

    /// <summary>Generates the test movie <paramref name="spec" /> describes at <paramref name="path" />.</summary>
    public static async Task GenerateTestMovieAsync(
        FfmpegRunner runner,
        string path,
        TestMovieSpec spec,
        CancellationToken cancellation = default
    ) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(spec.DurationSeconds);
        var rate = spec.Rate;
        var frames = spec.Frames;
        var cuts = spec.SceneCutFrames.Order().ToList();
        if (cuts.Count > 0 && (cuts[0] <= 0 || cuts[^1] >= frames || cuts.Distinct().Count() != cuts.Count)) {
            throw new ArgumentOutOfRangeException(nameof(spec), "Scene cuts must be distinct frames inside the movie.");
        }

        var webm = HasExtension(path, ".webm");
        var encoders = await ListAsync(runner, "-encoders", cancellation).ConfigureAwait(false);
        var encoder = spec.VideoEncoder
            ?? (webm ? WebmEncoders : H264Encoders).FirstOrDefault(encoders.Contains)
            ?? throw new FfmpegException(
                $"This FFmpeg has no {(webm ? "VP9" : "H.264")} encoder for {Path.GetFileName(path)}."
            ) { ExitCode = -1 };
        var filters = await ListAsync(runner, "-filters", cancellation).ConfigureAwait(false);
        var font = filters.Contains("drawtext") ? Font() : null;

        var graph = new StringBuilder();
        var size = $"{Text(spec.Width)}x{Text(spec.Height)}";
        var rateText = $"{Text(rate.Numerator)}/{Text(rate.Denominator)}";
        var bounds = new List<long> { 0 };
        bounds.AddRange(cuts);
        bounds.Add(frames);
        for (var i = 0; i + 1 < bounds.Count; i++) {
            graph.Append(
                CultureInfo.InvariantCulture,
                $"color=c={Colours[i % Colours.Length]}:s={size}:r={rateText},trim=end_frame={bounds[i + 1] - bounds[i]}[b{i}];"
            );
        }

        for (var i = 0; i + 1 < bounds.Count; i++) {
            graph.Append(CultureInfo.InvariantCulture, $"[b{i}]");
        }

        var patternWidth = spec.Width / 2 / 2 * 2;
        var patternHeight = spec.Height / 2 / 2 * 2;
        graph.Append(CultureInfo.InvariantCulture, $"concat=n={bounds.Count - 1}:v=1:a=0[bg];");
        graph.Append(
            CultureInfo.InvariantCulture,
            $"testsrc=s={Text(patternWidth)}x{Text(patternHeight)}:r={rateText}:decimals=2[fg];"
        );
        graph.Append(
            CultureInfo.InvariantCulture,
            $"[bg][fg]overlay=x={Text((spec.Width - patternWidth) / 2)}:y={Text((spec.Height - patternHeight) / 2)}:shortest=1"
        );
        if (font is not null) {
            var fontSize = Math.Max(12, spec.Height / 9);
            var style = $"fontfile='{Escape(font)}':fontsize={Text(fontSize)}:fontcolor=white:box=1:boxcolor=black";
            var label = Escape(TimecodeLabel(spec.TimecodeStartSeconds, rate));
            graph.Append(CultureInfo.InvariantCulture, $",drawtext={style}:text='%{{frame_num}}':x=8:y=8");
            graph.Append(
                CultureInfo.InvariantCulture,
                $",drawtext={style}:timecode='{label}':rate={rateText}:x=8:y=h-th-8"
            );
        }

        graph.Append(",format=yuv420p[v];");
        var beep = (long)Math.Round(spec.BeepSeconds * spec.AudioSampleRate);
        var sampleRate = Text(spec.AudioSampleRate);
        var tone =
            $"if(lt(mod(n\\,{sampleRate})\\,{Text(beep)})\\,0.5*sin(2*PI*{Text(spec.BeepHz)}*n/{sampleRate})\\,0)";
        var samples = (long)Math.Round(frames / rate.FramesPerSecond * spec.AudioSampleRate);
        graph.Append(
            CultureInfo.InvariantCulture,
            $"aevalsrc='{tone}|{tone}':s={sampleRate},atrim=end_sample={samples}[a]"
        );

        var keyframes = Text((int)Math.Round(rate.FramesPerSecond));
        List<string> arguments = [
            "-y", "-filter_complex", graph.ToString(), "-map", "[v]", "-map", "[a]", "-frames:v", Text(frames),
            "-c:v", encoder, "-g", keyframes
        ];
        arguments.AddRange(
            encoder switch {
                "h264_videotoolbox" => ["-allow_sw", "1", "-realtime", "0", "-b:v", "4M"],
                "libopenh264" => ["-b:v", "4M"],
                "libx264" => ["-crf", "18", "-bf", "0"],
                "libvpx-vp9" => ["-b:v", "2M", "-deadline", "realtime", "-cpu-used", "8"],
                _ => []
            }
        );
        arguments.AddRange(webm ? ["-c:a", "libopus", "-b:a", "128k"] : ["-c:a", "aac", "-b:a", "192k"]);
        if (spec.TimecodeStartSeconds > 0 && !webm) {
            arguments.AddRange(["-timecode", TimecodeLabel(spec.TimecodeStartSeconds, rate)]);
        }

        if (!webm) {
            arguments.AddRange(["-movflags", "+faststart"]);
        }

        arguments.Add(path);
        await runner.RunFfmpegAsync(arguments, WorkTimeout, cancellation: cancellation).ConfigureAwait(false);
    }

    /// <summary>The thumbnail of <paramref name="frame" /> in <paramref name="folder" />.</summary>
    public static string ThumbnailPath(string folder, long frame) =>
        Path.Combine(folder, $"{frame.ToString(CultureInfo.InvariantCulture)}.jpg");

    /// <summary>The video encoders the test movie may use, in order of preference.</summary>
    static readonly string[] H264Encoders = ["h264_videotoolbox", "libopenh264", "libx264"];

    static readonly string[] WebmEncoders = ["libvpx-vp9"];

    /// <summary>
    ///     Solid backgrounds, one per scene, dark and bright in turn (luma about 50 and 180, hues far apart), so every cut
    ///     scores well above Detect Scene Cuts' 0.4 (FFmpeg's scene score follows the luma change: green to magenta, two
    ///     neighbours of the first palette, scored 0.24).
    /// </summary>
    static readonly string[] Colours = ["0x203080", "0xE0D020", "0x802010", "0x30E0E0", "0x403040", "0xF0A0F0"];

    /// <summary>The probe and the start times the operations align by.</summary>
    internal sealed record ProbeDetail(
        MovieProbe Movie,
        double VideoStartSeconds,
        double FormatStartSeconds,
        int AudioStreams
    );

    internal static async Task<ProbeDetail> DetailAsync(
        FfmpegRunner runner,
        string moviePath,
        CancellationToken cancellation
    ) {
        if (!File.Exists(moviePath)) {
            throw new FileNotFoundException("No such movie.", moviePath);
        }

        var result = await runner.RunFfprobeAsync(
                ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", moviePath],
                ProbeTimeout,
                cancellation
            )
            .ConfigureAwait(false);
        using var json = JsonDocument.Parse(result.StandardOutput);
        var root = json.RootElement;
        var streams = root.TryGetProperty("streams", out var list) ? list.EnumerateArray().ToList() : [];
        var video = streams.FirstOrDefault(static s => Str(s, "codec_type") == "video"
            && !(s.TryGetProperty("disposition", out var d)
                && d.TryGetProperty("attached_pic", out var a)
                && a.GetInt32() == 1)
        );
        if (video.ValueKind != JsonValueKind.Object) {
            throw new FfmpegException($"{Path.GetFileName(moviePath)} has no video stream.") { ExitCode = -1 };
        }

        var format = root.GetProperty("format");
        var rate = Rational(Str(video, "r_frame_rate"))
            ?? Rational(Str(video, "avg_frame_rate"))
            ?? throw new FfmpegException($"{Path.GetFileName(moviePath)}: no frame rate.") { ExitCode = -1 };
        var frames = Long(video, "nb_frames") ?? 0;
        if (frames <= 0) {
            frames = await CountFramesAsync(runner, moviePath, cancellation).ConfigureAwait(false);
        }

        var duration = Double(video, "duration") ?? Double(format, "duration") ?? frames / rate.FramesPerSecond;
        var audios = streams.Where(static s => Str(s, "codec_type") == "audio").ToList();
        var audio = audios.Count == 0 ? null : AudioOf(audios[0]);
        var timecode = Tag(video, "timecode")
            ?? streams.Select(static s => Tag(s, "timecode")).FirstOrDefault(static t => t is not null)
            ?? Tag(format, "timecode");
        var movie = new MovieProbe(
            Str(video, "codec_name") ?? "unknown",
            video.GetProperty("width").GetInt32(),
            video.GetProperty("height").GetInt32(),
            rate,
            frames,
            timecode is null ? 0 : TimecodeSeconds(timecode, rate),
            duration,
            audio
        );
        var videoStart = Double(video, "start_time") ?? 0;
        return new(movie, videoStart, Double(format, "start_time") ?? videoStart, audios.Count);
    }

    static MovieAudio AudioOf(JsonElement stream) {
        var channels = stream.TryGetProperty("channels", out var count) ? count.GetInt32() : 0;
        return new(Str(stream, "codec_name") ?? "unknown", channels, (int)(Long(stream, "sample_rate") ?? 0));
    }

    static async Task<long> CountFramesAsync(FfmpegRunner runner, string moviePath, CancellationToken cancellation) {
        var result = await runner.RunFfprobeAsync(
                [
                    "-v", "error", "-select_streams", "v:0", "-count_packets", "-show_entries",
                    "stream=nb_read_packets",
                    "-of", "csv=p=0", moviePath
                ],
                WorkTimeout,
                cancellation
            )
            .ConfigureAwait(false);
        return long.TryParse(result.StandardOutput.Trim().TrimEnd(','), CultureInfo.InvariantCulture, out var count)
            ? count
            : 0;
    }

    /// <summary>
    ///     A timecode label (<c>HH:MM:SS:FF</c>, or <c>HH:MM:SS;FF</c> drop-frame) in seconds: its frame count at the
    ///     nominal rate (with the drop-frame rule at 29.97 and 59.94) over the exact rate, as the engine's MTC counts.
    /// </summary>
    internal static double TimecodeSeconds(string label, VideoFrameRate rate) {
        var parts = label.Split(':', ';', '.');
        if (parts.Length != 4 || !parts.All(static p => int.TryParse(p, CultureInfo.InvariantCulture, out _))) {
            return 0;
        }

        var (h, m, s, f) = (Int(parts[0]), Int(parts[1]), Int(parts[2]), Int(parts[3]));
        var nominal = (int)Math.Round(rate.FramesPerSecond);
        var count = ((h * 60L + m) * 60 + s) * nominal + f;
        if (label.Contains(';', StringComparison.Ordinal) && nominal is 30 or 60) {
            var dropped = nominal / 15;
            var minutes = h * 60L + m;
            count -= dropped * (minutes - minutes / 10);
        }

        return count / rate.FramesPerSecond;

        static int Int(string text) => int.Parse(text, CultureInfo.InvariantCulture);
    }

    /// <summary>A non-drop timecode label for <paramref name="seconds" /> at <paramref name="rate" />.</summary>
    internal static string TimecodeLabel(double seconds, VideoFrameRate rate) {
        var nominal = (int)Math.Round(rate.FramesPerSecond);
        var count = (long)Math.Round(seconds * rate.FramesPerSecond);
        var f = count % nominal;
        var total = count / nominal;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{total / 3600 % 24:00}:{total / 60 % 60:00}:{total % 60:00}:{f:00}"
        );
    }

    /// <summary>The names in <c>ffmpeg -encoders</c> or <c>-filters</c> (the second column).</summary>
    static async Task<HashSet<string>> ListAsync(FfmpegRunner runner, string what, CancellationToken cancellation) {
        var result = await runner.RunFfmpegAsync([what], ProbeTimeout, cancellation: cancellation)
            .ConfigureAwait(false);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in result.StandardOutput.Split('\n')) {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].Length is >= 2 and <= 6 && parts[0] != "------") {
                names.Add(parts[1]);
            }
        }

        return names;
    }

    /// <summary>A font file the system has for <c>drawtext</c>, or null.</summary>
    static string? Font() {
        string[] candidates = OperatingSystem.IsMacOS()
            ? [
                "/System/Library/Fonts/Supplemental/Arial.ttf", "/System/Library/Fonts/Helvetica.ttc",
                "/Library/Fonts/Arial.ttf"
            ]
            : OperatingSystem.IsWindows()
                ? [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf")]
                : [
                    "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", "/usr/share/fonts/dejavu/DejaVuSans.ttf",
                    "/usr/share/fonts/TTF/DejaVuSans.ttf",
                    "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf"
                ];
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    ///     Whether <paramref name="path" />'s extension is <paramref name="extension" /> in any case: it names the
    ///     container format (not the file's identity, so the comparison is the same on every platform).
    /// </summary>
    static bool HasExtension(string path, string extension) => FormatNames.Equals(Path.GetExtension(path), extension);

    /// <summary>Extensions as format names: <c>.MOV</c> is a MOV.</summary>
    static readonly StringComparer FormatNames = StringComparer.OrdinalIgnoreCase;

    /// <summary>A value inside a filter graph's quoted option: forward slashes, and ':' and '\' escaped.</summary>
    static string Escape(string text) =>
        text.Replace('\\', '/')
            .Replace(":", """\:""", StringComparison.Ordinal)
            .Replace("'", string.Empty, StringComparison.Ordinal);

    static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A number as FFmpeg reads it (no exponent; microseconds are enough for times).</summary>
    static string Text(double value) => value.ToString("0.0#####", CultureInfo.InvariantCulture);

    static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    static string? Tag(JsonElement element, string name) =>
        element.TryGetProperty("tags", out var tags) ? Str(tags, name) : null;

    static long? Long(JsonElement element, string name) =>
        long.TryParse(Str(element, name), CultureInfo.InvariantCulture, out var value) ? value : null;

    static double? Double(JsonElement element, string name) =>
        double.TryParse(Str(element, name), CultureInfo.InvariantCulture, out var value) ? value : null;

    static VideoFrameRate? Rational(string? text) {
        var parts = text?.Split('/');
        return parts is { Length: 2 }
            && int.TryParse(parts[0], CultureInfo.InvariantCulture, out var numerator)
            && int.TryParse(parts[1], CultureInfo.InvariantCulture, out var denominator)
            && numerator > 0
            && denominator > 0
                ? new(numerator, denominator)
                : null;
    }

    [GeneratedRegex(@"\bpts_time:\s*(-?[0-9.]+)")]
    private static partial Regex ShowinfoTime();
}
