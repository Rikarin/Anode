using Anode.Contracts;

namespace Anode.Model.Editing;

/// <summary>
///     Phase 10 (docs/11-phase-10.md, "Video"): the project's movie (<c>setMovie</c> from the import job,
///     <c>setMoviePosition</c>, <c>removeMovie</c>) and Detect Scene Cuts' markers (<c>addSceneMarkers</c>).
/// </summary>
public static partial class Edits {
    /// <summary>The longest movie a project takes: 24 hours.</summary>
    public const double MaxMovieSeconds = 86_400;

    /// <summary>A movie as a project keeps it: a path and name, a position, and the probe's facts in their ranges.</summary>
    public static Movie CheckMovie(Movie? movie) {
        if (movie is null) {
            throw new EditException("Name a movie.");
        }

        if (movie.Path is not { Length: > 0 and <= MaxPathLength }
            || movie.Codec is not { Length: > 0 and <= MaxNameLength }) {
            throw new EditException($"A movie has a path of 1…{MaxPathLength} characters and a codec.");
        }

        var name = Name(movie.Name ?? string.Empty, "movie name");
        CheckMovieTiming(movie);
        CheckMoviePicture(movie);
        CheckMoviePosition(movie.StartTick, movie.OffsetSeconds, movie.DurationSeconds);
        return movie with { Name = name };
    }

    /// <summary>A movie's frame rate, duration and timecode start in their ranges.</summary>
    static void CheckMovieTiming(Movie movie) {
        if (movie.FrameRate is not { Numerator: > 0 and <= 1_000_000, Denominator: > 0 and <= 1_000_000 } rate
            || rate.FramesPerSecond > 1000) {
            throw new EditException("A movie's frame rate is a positive fraction, at most 1000 frames per second.");
        }

        if (!double.IsFinite(movie.DurationSeconds) || movie.DurationSeconds is <= 0 or > MaxMovieSeconds) {
            throw new EditException($"A movie lasts more than 0 and at most {MaxMovieSeconds} seconds.");
        }

        if (!double.IsFinite(movie.TimecodeStartSeconds)
            || movie.TimecodeStartSeconds is < 0 or > MaxStartOffsetSeconds) {
            throw new EditException($"A movie's timecode start is 0…{MaxStartOffsetSeconds} seconds.");
        }
    }

    /// <summary>A movie's frame count and size, and its audio's, in their ranges.</summary>
    static void CheckMoviePicture(Movie movie) {
        if (movie.Frames < 0 || movie.Width is <= 0 or > 16_384 || movie.Height is <= 0 or > 16_384) {
            throw new EditException("A movie has a frame count and a size of 1…16384 pixels.");
        }

        if (movie.Audio is { } audio
            && (audio.Codec is not { Length: > 0 }
                || audio.Channels is <= 0 or > 64
                || audio.SampleRate is <= 0 or > 768_000)) {
            throw new EditException("A movie's audio has a codec, 1…64 channels and a sample rate.");
        }
    }

    static void CheckMoviePosition(long startTick, double offsetSeconds, double duration) {
        CheckTick(startTick, "movie start");
        if (!double.IsFinite(offsetSeconds) || offsetSeconds < 0 || offsetSeconds >= duration) {
            throw new EditException($"A movie's offset is 0 or more and less than its duration ({duration} s).");
        }
    }

    static DeltaOp[] SetMoviePosition(ProjectState state, SetMoviePositionCommand c) {
        var movie = state.Movie ?? throw new EditException("The project has no movie.");
        var moved = movie with {
            StartTick = c.StartTick ?? movie.StartTick, OffsetSeconds = c.OffsetSeconds ?? movie.OffsetSeconds
        };
        CheckMoviePosition(moved.StartTick, moved.OffsetSeconds, moved.DurationSeconds);
        return [new MovieChanged(moved)];
    }

    /// <summary>
    ///     Scene markers at the ticks (<c>Scene 1</c>, … in time order; duplicates once), after removing the scene markers
    ///     there are when <c>replaceExisting</c>.
    /// </summary>
    static List<DeltaOp> AddSceneMarkers(ProjectState state, AddSceneMarkersCommand c, IIdSource ids) {
        if (c.Ticks is null) {
            throw new EditException("Name the scene cuts' ticks.");
        }

        var ticks = c.Ticks.Distinct().Order().ToArray();
        foreach (var tick in ticks) {
            CheckTick(tick, "scene marker");
        }

        var ops = new List<DeltaOp>();
        var kept = state.Markers.Count;
        if (c.ReplaceExisting) {
            foreach (var marker in state.Markers.Values.Where(static m => m.Kind == MarkerKind.Scene)) {
                ops.Add(new MarkerRemoved(marker.Id));
                kept--;
            }
        }

        if (kept + ticks.Length > MaxMarkers) {
            throw new EditException($"A project holds at most {MaxMarkers} markers.");
        }

        for (var i = 0; i < ticks.Length; i++) {
            ops.Add(
                new MarkerUpserted(
                    new(ids.Next(IdPrefix.Marker), $"Scene {i + 1}", ticks[i], 0, null, string.Empty, MarkerKind.Scene)
                )
            );
        }

        return ops;
    }

    static void CheckMarkerKind(MarkerKind? kind) {
        if (kind is { } k && !Enum.IsDefined(k)) {
            throw new EditException($"Unknown marker kind {k}.");
        }
    }
}
