using Anode.Contracts;

namespace Anode.Model.Time;

/// <summary>
///     The project ↔ movie time mapping (docs/11-phase-10.md, "Video"), which the editor mirrors: at project position
///     <c>p</c> the movie shows movie time <c>m = seconds(p) − seconds(startTick) + offsetSeconds</c>, seconds through
///     the tempo map at its nominal speed (Varispeed plays the movie faster or slower, as it does the project); movie time
///     <c>m</c> is frame <c>floor(m × rate)</c> (with a 1e-9 frame tolerance, so a frame's own start time maps back to
///     it); before 0 or from the duration on there is no frame. Frame <c>f</c> starts at <c>f / rate</c> seconds, so it
///     plays from project time <c>seconds(startTick) + f / rate − offsetSeconds</c>.
/// </summary>
public static class MovieTime {
    /// <summary>The movie time (seconds) at project tick <paramref name="tick" />; negative before the movie starts.</summary>
    public static double MovieSeconds(TempoMap map, Movie movie, double tick) =>
        map.SecondsAt(tick) - map.SecondsAt(movie.StartTick) + movie.OffsetSeconds;

    /// <summary>The project time (seconds) at which movie time <paramref name="seconds" /> plays.</summary>
    public static double ProjectSeconds(TempoMap map, Movie movie, double seconds) =>
        map.SecondsAt(movie.StartTick) + seconds - movie.OffsetSeconds;

    /// <summary>The project tick (fractional) at which movie time <paramref name="seconds" /> plays.</summary>
    public static double TickAt(TempoMap map, Movie movie, double seconds) =>
        map.TickAt(ProjectSeconds(map, movie, seconds));

    /// <summary>The frame shown at movie time <paramref name="seconds" />, or null when there is none.</summary>
    public static long? FrameAt(Movie movie, double seconds) {
        if (seconds < 0 || seconds >= movie.DurationSeconds) {
            return null;
        }

        var frame = (long)Math.Floor(seconds * movie.FrameRate.FramesPerSecond + 1e-9);
        return movie.Frames > 0 ? Math.Min(frame, movie.Frames - 1) : frame;
    }

    /// <summary>The frame shown at project tick <paramref name="tick" />, or null when there is none.</summary>
    public static long? FrameAtTick(TempoMap map, Movie movie, double tick) =>
        FrameAt(movie, MovieSeconds(map, movie, tick));

    /// <summary>The movie time (seconds) frame <paramref name="frame" /> starts at.</summary>
    public static double SecondsOfFrame(Movie movie, long frame) =>
        frame * (double)movie.FrameRate.Denominator / movie.FrameRate.Numerator;

    /// <summary>The project tick (rounded) at which frame <paramref name="frame" /> starts playing.</summary>
    public static long TickOfFrame(TempoMap map, Movie movie, long frame) =>
        (long)Math.Round(TickAt(map, movie, SecondsOfFrame(movie, frame)));

    /// <summary>
    ///     The project's sample range the movie spans at <paramref name="sampleRate" />: from where its first frame plays
    ///     (time 0; before the project start it is cut off, so <c>Skip</c> samples of the movie are not covered) to where
    ///     its duration ends.
    /// </summary>
    public static (long Start, long End, long Skip) Span(TempoMap map, Movie movie, int sampleRate) {
        var start = ProjectSeconds(map, movie, 0);
        var end = ProjectSeconds(map, movie, movie.DurationSeconds);
        var skip = start < 0 ? (long)Math.Round(-start * sampleRate) : 0;
        return ((long)Math.Round(Math.Max(0, start) * sampleRate), (long)Math.Round(Math.Max(0, end) * sampleRate),
            skip);
    }
}
