using Anode.Contracts;
using Anode.Model.Time;

namespace Anode.Model.Tests;

/// <summary>The project tick ↔ movie time mapping (docs/11-phase-10.md, "Video"), which the editor mirrors.</summary>
public sealed class MovieTimeTests {
    static readonly Movie Picture = new(
        "Movies/Picture.mov",
        "Picture",
        true,
        3840,
        0,
        new(24000, 1001),
        3600,
        10,
        240,
        1920,
        1080,
        "h264",
        null
    );

    [Fact]
    public void Movie_time_is_project_seconds_from_the_start_position_plus_the_offset() {
        var map = TempoMap.Constant(120);
        Assert.Equal(-2, MovieTime.MovieSeconds(map, Picture, 0), 9);
        Assert.Equal(0, MovieTime.MovieSeconds(map, Picture, 3840), 9);
        Assert.Equal(1.5, MovieTime.MovieSeconds(map, Picture with { OffsetSeconds = 0.5 }, 5760), 9);
        Assert.Null(MovieTime.FrameAtTick(map, Picture, 0));
        Assert.Null(MovieTime.FrameAtTick(map, Picture, 3840 + 1920 * 10));
        // 23.976 fps: frame 24 starts at 1.001 s.
        Assert.Equal(23, MovieTime.FrameAt(Picture, 1.0));
        Assert.Equal(24, MovieTime.FrameAt(Picture, MovieTime.SecondsOfFrame(Picture, 24)));
    }

    [Fact]
    public void Frames_map_back_to_their_ticks_through_a_tempo_change() {
        var map = TempoMap.Of([new(0, 120, TempoCurve.Step), new(7680, 60, TempoCurve.Step)]);
        // The movie starts at 2 s; frame 96 (4.004 s in) plays at 6.004 s: 4 s at 120 BPM, then 2.004 s at 60.
        var tick = MovieTime.TickOfFrame(map, Picture, 96);
        Assert.Equal(7680 + (long)Math.Round(2.004 * 960), tick);
        Assert.Equal(96, MovieTime.FrameAtTick(map, Picture, tick));
        for (long frame = 0; frame < Picture.Frames; frame += 17) {
            Assert.Equal(
                frame,
                MovieTime.FrameAtTick(
                    map,
                    Picture,
                    MovieTime.TickAt(map, Picture, MovieTime.SecondsOfFrame(Picture, frame))
                )
            );
        }
    }

    [Fact]
    public void The_span_is_the_movie_on_the_project_cut_at_its_start() {
        var map = TempoMap.Constant(120);
        Assert.Equal((96_000L, 576_000L, 0L), MovieTime.Span(map, Picture, 48_000));
        Assert.Equal(
            (0L, 408_000L, 72_000L),
            MovieTime.Span(map, Picture with { StartTick = 0, OffsetSeconds = 1.5 }, 48_000)
        );
    }
}
