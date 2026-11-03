using Anode.Contracts;
using Anode.Model;
using Anode.Model.Audio;
using Anode.Model.Editing;
using Anode.Model.Time;

namespace Anode.Server.Projects;

/// <summary>
///     Phase 10's movie jobs (docs/11-phase-10.md, "Video"), each FFmpeg work through <see cref="IMovieMedia" />
///     (workstream E): import (probe, then copy into <c>Movies/</c> or reference in place, then <c>setMovie</c>),
///     thumbnails (rendered into the movie's cache, <see cref="Movies" />), Detect Scene Cuts (<c>addSceneMarkers</c> at
///     the ticks of the cut frames), Import Audio from Movie (extracted at the project's rate, then <c>importTracks</c>
///     with a track whose region starts at the movie's first frame) and Export Audio to Movie (the master bounced over the
///     movie's span, the stereo down-mix of a surround master, padded so its first sample is the movie's first frame, then
///     remuxed with the video stream copied). Ticks and movie time meet through <see cref="MovieTime" />. Without FFmpeg
///     they answer 503.
/// </summary>
public sealed partial class Jobs {
    /// <summary>Queues a movie import from the server's disk: 422 for a file that is not a movie Anode takes.</summary>
    public Job ImportMovie(MovieImportRequest request) {
        var view = session.View ?? throw ProjectException.NoProject();
        var path = ImportPath(request?.Path);
        if (!Movies.Extensions.Contains(Path.GetExtension(path).ToLowerInvariant())) {
            throw ProjectException.Unprocessable("A movie is a MOV, MP4 or M4V file.");
        }

        var startTick = request!.StartTick ?? 0;
        if (startTick is < 0 or > Edits.MaxTick) {
            throw ProjectException.Unprocessable($"A movie starts at tick 0…{Edits.MaxTick}.");
        }

        RequireMedia();
        return Enqueue(
            JobKind.MovieImport,
            async (entry, token) => {
                var probe = await media.ProbeAsync(path, token).ConfigureAwait(false);
                Progress(entry, 0.1);
                string? copied = null;
                try {
                    var stored = path;
                    if (request.CopyIntoProject) {
                        copied = await CopyMovieAsync(entry, path, view.Folder, token).ConfigureAwait(false);
                        stored = Path.GetRelativePath(view.Folder, copied).Replace('\\', '/');
                    }

                    var movie = new Movie(
                        stored,
                        Path.GetFileNameWithoutExtension(path),
                        request.CopyIntoProject,
                        startTick,
                        0,
                        probe.FrameRate,
                        probe.TimecodeStartSeconds,
                        probe.DurationSeconds,
                        probe.Frames,
                        probe.Width,
                        probe.Height,
                        probe.VideoCodec,
                        probe.Audio
                    );
                    session.ApplyJob(
                        view.Session,
                        "Import Movie",
                        [],
                        (job, _) => job.Apply(new SetMovieCommand(movie))
                    );
                    return [copied ?? path];
                } catch {
                    if (copied is not null) {
                        TryDelete(copied);
                    }

                    throw;
                }
            }
        );
    }

    /// <summary>Copies a movie into the project's <c>Movies</c> under a free name, with progress; its new path.</summary>
    async Task<string> CopyMovieAsync(Entry entry, string source, string projectFolder, CancellationToken token) {
        var folder = Path.Combine(projectFolder, Movies.Folder);
        Directory.CreateDirectory(folder);
        var name = Path.GetFileNameWithoutExtension(source);
        var extension = Path.GetExtension(source);
        for (var n = 1;; n++) {
            var target = Path.Combine(folder, n == 1 ? name + extension : $"{name} {n}{extension}");
            FileStream output;
            try {
                output = new(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            } catch (IOException) when (File.Exists(target)) {
                continue;
            }

            try {
                await using (output.ConfigureAwait(false)) {
                    var input = File.OpenRead(source);
                    await using (input.ConfigureAwait(false)) {
                        var buffer = new byte[1 << 20];
                        var length = Math.Max(1, input.Length);
                        long done = 0;
                        int read;
                        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0) {
                            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                            done += read;
                            Progress(entry, 0.1 + 0.85 * done / length);
                        }
                    }
                }
            } catch {
                TryDelete(target);
                throw;
            }

            return target;
        }
    }

    /// <summary>Queues rendering thumbnails of movie frames into the cache (frames already there are kept).</summary>
    public Job RenderMovieThumbnails(ThumbnailsRequest request) {
        var (view, movie, file) = MovieOf();
        var height = Movies.Height(request?.Height);
        if (request!.Frames is not { Count: > 0 and <= Movies.MaxThumbnailFrames } frames
            || frames.Any(f => f < 0 || (movie.Frames > 0 && f >= movie.Frames))) {
            throw ProjectException.Unprocessable(
                $"Ask for 1…{Movies.MaxThumbnailFrames} frames of the movie (0…{Math.Max(0, movie.Frames - 1)})."
            );
        }

        RequireMedia();
        var wanted = frames.Distinct().Order().ToArray();
        return Enqueue(
            JobKind.Thumbnails,
            async (_, token) => {
                var folder = Movies.ThumbnailFolder(view.Folder, movie, height);
                Directory.CreateDirectory(folder);
                var missing = wanted.Where(f => !File.Exists(Movies.ThumbnailOf(view.Folder, movie, height, f)))
                    .ToArray();
                if (missing.Length > 0) {
                    await media.ThumbnailsAsync(file, missing, height, folder, token).ConfigureAwait(false);
                }

                return [
                    ..wanted.Select(f => Movies.ThumbnailOf(view.Folder, movie, height, f)).Where(File.Exists)
                ];
            }
        );
    }

    /// <summary>Queues Detect Scene Cuts: scene markers at the cuts, one undo step.</summary>
    public Job DetectMovieScenes(SceneDetectRequest request) {
        var (view, _, file) = MovieOf();
        var threshold = request?.Threshold ?? 0.4;
        if (!(threshold is > 0 and <= 1)) {
            throw ProjectException.Unprocessable($"A scene threshold is more than 0 and at most 1, not {threshold}.");
        }

        RequireMedia();
        return Enqueue(
            JobKind.SceneDetect,
            async (entry, token) => {
                var cuts = await media.DetectScenesAsync(
                        file,
                        threshold,
                        new Progress<double>(f => Progress(entry, 0.95 * f)),
                        token
                    )
                    .ConfigureAwait(false);
                session.ApplyJob(
                    view.Session,
                    "Detect Scene Cuts",
                    [],
                    (job, _) => {
                        var movie = job.State.Movie ?? throw new EditException("The project no longer has a movie.");
                        var map = job.State.TempoMap;
                        job.Apply(
                            new AddSceneMarkersCommand(
                                [
                                    ..cuts.Select(f => MovieTime.TickOfFrame(map, movie, f))
                                        .Where(static t => t is >= 0 and <= Edits.MaxTick)
                                ],
                                request?.ReplaceExisting ?? true
                            )
                        );
                    }
                );
                return [];
            }
        );
    }

    /// <summary>Queues Import Audio from Movie: a new audio track at the movie's first frame, one undo step.</summary>
    public Job ImportMovieAudio(MovieAudioImportRequest request) {
        var (view, movie, file) = MovieOf();
        if (movie.Audio is null) {
            throw ProjectException.Unprocessable("The movie has no audio.");
        }

        var name = request?.TrackName is { } given ? given.Trim() : movie.Name;
        if (name.Length is 0 or > Edits.MaxNameLength) {
            throw ProjectException.Unprocessable($"A track name has 1…{Edits.MaxNameLength} characters.");
        }

        RequireMedia();
        return Enqueue(
            JobKind.MovieAudioImport,
            async (entry, token) => {
                var scratch = Path.Combine(view.Folder, $".movie-audio-{entry.Job.Id}");
                Directory.CreateDirectory(scratch);
                string? brought = null;
                try {
                    var wav = Path.Combine(scratch, Safe(movie.Name) + ".wav");
                    await media.ExtractAudioAsync(
                            file,
                            wav,
                            view.State.SampleRate,
                            new Progress<double>(f => Progress(entry, 0.9 * f)),
                            token
                        )
                        .ConfigureAwait(false);
                    var (_, path, info) = import.Bring(wav, Spatial.MaxChannels);
                    brought = path;
                    var format = Movies.FormatOf(info.Channels);
                    session.ApplyJob(
                        view.Session,
                        "Import Audio from Movie",
                        [(path, info)],
                        (job, added) => job.Apply(
                            new ImportTracksCommand(MovieAudioContent(job.State, name, added[0], format))
                        )
                    );
                    return [path];
                } catch {
                    if (brought is not null) {
                        TryDelete(brought);
                    }

                    throw;
                } finally {
                    RemoveScratch(scratch);
                }
            }
        );
    }

    /// <summary>
    ///     The movie's audio as a track: its region at the tick of the movie's first frame, or, when that is before the
    ///     project start, at 0 from the sample that plays there.
    /// </summary>
    public static ImportedContent MovieAudioContent(
        ProjectState state,
        string name,
        AudioFile file,
        ChannelFormat format
    ) {
        var movie = state.Movie ?? throw new EditException("The project no longer has a movie.");
        var map = state.TempoMap;
        var start = MovieTime.ProjectSeconds(map, movie, 0);
        long tick = 0, offset = 0;
        if (start >= 0) {
            tick = (long)Math.Round(map.TickAt(start));
        } else {
            offset = Math.Min(file.Frames - 1, (long)Math.Round(-start * state.SampleRate));
        }

        return new(
            [
                new(
                    "movie-audio",
                    name,
                    TrackKind.Audio,
                    null,
                    [],
                    [
                        new(
                            "movie-audio-region",
                            "ignored",
                            file.Id,
                            file.Name,
                            tick,
                            offset,
                            file.Frames - offset,
                            0,
                            0,
                            0,
                            0,
                            0,
                            false,
                            FlexFollow.Off,
                            [],
                            0,
                            0,
                            []
                        )
                    ],
                    format
                )
            ]
        );
    }

    /// <summary>Queues Export Audio to Movie: a new movie with the bounce as its audio, the video copied.</summary>
    public Job ExportMovieAudio(MovieAudioExportRequest request) {
        var (view, movie, file) = MovieOf();
        var state = view.State;
        var extension = Path.GetExtension(file);
        var output = ExportPath(view, request?.FileName, request?.Directory, extension);
        if (string.Equals(Path.GetFullPath(output), Path.GetFullPath(file), StringComparison.Ordinal)) {
            throw ProjectException.Unprocessable(
                "Export Audio to Movie writes a new movie, not over the movie itself."
            );
        }

        if (!Enum.IsDefined(request!.Codec)) {
            throw ProjectException.Unprocessable($"Unknown audio codec {request.Codec}.");
        }

        if (state.Settings.Varispeed.Enabled) {
            throw ProjectException.Unprocessable("Turn Varispeed off to export audio to the movie.");
        }

        var codec = request.Codec == MovieAudioCodec.Pcm
            && !extension.Equals(".mov", StringComparison.OrdinalIgnoreCase)
                ? MovieAudioCodec.Aac
                : request.Codec;
        var (start, end, skip) = MovieTime.Span(state.TempoMap, movie, state.SampleRate);
        if (end <= start) {
            throw ProjectException.Unprocessable("The movie ends before the project starts.");
        }

        RequireMedia();
        var format = state.Settings.SurroundFormat;
        return Enqueue(
            JobKind.MovieAudioExport,
            async (entry, token) => {
                var scratch = Path.Combine(view.Folder, $".movie-export-{entry.Job.Id}");
                Directory.CreateDirectory(scratch);
                try {
                    var mix = Path.Combine(scratch, "mix.wav");
                    var finished = await engine.RenderAsync(
                            new(entry.EngineId, start, end, mix),
                            f => Progress(entry, 0.6 * f),
                            token
                        )
                        .ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (finished.Error is { } error) {
                        throw ProjectException.Unprocessable($"The engine could not render: {error}");
                    }

                    var (channels, rate) = BounceProcessor.Read(finished.Files is [var written, ..] ? written : mix);
                    var stereo = Stereo(channels, format);
                    var aligned = Path.Combine(scratch, "aligned.wav");
                    WriteAligned(aligned, stereo, rate, skip);
                    Progress(entry, 0.7);
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    await media.RemuxAsync(
                            file,
                            aligned,
                            output,
                            request.Replace,
                            codec,
                            new Progress<double>(f => Progress(entry, 0.7 + 0.3 * f)),
                            token
                        )
                        .ConfigureAwait(false);
                    return [output];
                } finally {
                    RemoveScratch(scratch);
                }
            }
        );
    }

    /// <summary>A render as stereo: as it is, a mono one doubled, a surround one down-mixed (ITU-R BS.775, workstream D).</summary>
    float[][] Stereo(float[][] channels, ChannelFormat format) {
        switch (channels.Length) {
            case 2:
                return channels;
            case 1:
                return [channels[0], channels[0]];
        }

        var source = SpeakerLayouts.ChannelCount(format) == channels.Length ? format : Movies.FormatOf(channels.Length);
        var gains = spatial.StereoDownmix(source);
        var frames = channels[0].Length;
        float[][] stereo = [new float[frames], new float[frames]];
        for (var output = 0; output < 2; output++) {
            for (var input = 0; input < channels.Length; input++) {
                var gain = gains[output, input];
                if (gain == 0) {
                    continue;
                }

                for (var i = 0; i < frames; i++) {
                    stereo[output][i] += gain * channels[input][i];
                }
            }
        }

        return stereo;
    }

    /// <summary>
    ///     A 32-bit float stereo WAV whose first sample is the movie's first frame: <paramref name="skip" /> of silence
    ///     first.
    /// </summary>
    static void WriteAligned(string path, float[][] stereo, int rate, long skip) {
        using var writer = new WavWriter(path, 2, rate, WavEncoding.Float32);
        var block = new float[8192];
        for (long done = 0; done < skip;) {
            var n = (int)Math.Min(block.Length / 2, skip - done);
            writer.Write(block.AsSpan(0, n * 2));
            done += n;
        }

        var frames = stereo[0].Length;
        for (var at = 0; at < frames;) {
            var n = Math.Min(block.Length / 2, frames - at);
            for (var i = 0; i < n; i++) {
                block[i * 2] = stereo[0][at + i];
                block[i * 2 + 1] = stereo[1][at + i];
            }

            writer.Write(block.AsSpan(0, n * 2));
            at += n;
        }
    }

    /// <summary>The open project's movie and its file, which exists: 404 without either.</summary>
    (OpenProjectView View, Movie Movie, string File) MovieOf() {
        var view = session.View ?? throw ProjectException.NoProject();
        var movie = view.State.Movie ?? throw ProjectException.NotFound("The project has no movie.");
        var file = Movies.FileOf(view.Folder, movie);
        return File.Exists(file)
            ? (view, movie, file)
            : throw ProjectException.NotFound($"The movie '{file}' is missing.");
    }

    void RequireMedia() {
        if (!media.Available) {
            throw new ProjectException(
                StatusCodes.Status503ServiceUnavailable,
                "FFmpeg is not available (scripts/fetch-ffmpeg.mjs, or Anode:FfmpegDirectory)."
            );
        }
    }

    /// <summary>Phase 10 limits.</summary>
    static class Spatial {
        /// <summary>The widest file a surround import brings in (9.1.6).</summary>
        public const int MaxChannels = 16;
    }
}
