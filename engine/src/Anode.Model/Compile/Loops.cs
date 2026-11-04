using Anode.Contracts;
using Anode.Model.Editing;
using Anode.Model.Time;

namespace Anode.Model.Compile;

/// <summary>
///     Region loops (<c>loopTicks</c> on audio, MIDI and pattern regions): a region plays again and again, end to end,
///     for <c>loopTicks</c> after its end; the last repeat is cut there. Repeats stop where the next region of any kind
///     starts on the track, as in Logic, so a loop never plays over what follows it. Each repeat is the region again,
///     fades included, and at most <see cref="MaxRepeats" /> are played.
///     <para>
///         MIDI and pattern repeats are the region at later ticks. An unwarped audio region repeats sample-accurately
///         (each repeat starts where the one before ends, whatever the tempo does); a warped one is laid out in ticks,
///         each repeat starting a whole number of ticks after the one before and overlapping it by less than a tick so
///         no gap opens.
///     </para>
/// </summary>
public static class Loops {
    /// <summary>The most repeats a region plays: a tiny region looped over hours stops here rather than filling memory.</summary>
    public const int MaxRepeats = 10_000;

    /// <summary>The start of the first region of any kind on the track after <paramref name="tick" />, or null.</summary>
    public static long? NextStart(ProjectState state, string trackId, long tick) {
        var starts = state.RegionsOf(trackId)
            .Select(static r => r.StartTick)
            .Concat(state.MidiRegionsOf(trackId).Select(static r => r.StartTick))
            .Concat(state.PatternRegionsOf(trackId).Select(static r => r.StartTick))
            .Concat(state.SessionPlayerRegionsOf(trackId).Select(static r => r.StartTick))
            .Concat(state.TakeFoldersOf(trackId).Select(static f => f.StartTick))
            .Where(s => s > tick)
            .ToArray();
        return starts.Length == 0 ? null : starts.Min();
    }

    /// <summary>Where the repeats of a region from <paramref name="start" /> to <paramref name="end" /> stop.</summary>
    static long Until(ProjectState state, string trackId, long start, long end, long loopTicks) {
        var wanted = end + Math.Max(0, loopTicks);
        return NextStart(state, trackId, start) is { } next ? Math.Min(wanted, next) : wanted;
    }

    /// <summary>The tick a MIDI region's playing ends: its end, or where its repeats stop.</summary>
    public static long EndTick(ProjectState state, MidiRegion region) {
        var end = region.StartTick + region.LengthTicks;
        return region.LoopTicks <= 0
            ? end
            : Math.Max(end, Until(state, region.TrackId, region.StartTick, end, region.LoopTicks));
    }

    /// <summary>The tick a pattern region's playing ends: its end, or where its repeats stop.</summary>
    public static long EndTick(ProjectState state, PatternRegion region) {
        var end = region.StartTick + region.LengthTicks;
        return region.LoopTicks <= 0
            ? end
            : Math.Max(end, Until(state, region.TrackId, region.StartTick, end, region.LoopTicks));
    }

    /// <summary>The tick an audio region's playing ends: its last frame rounded up, or where its repeats stop.</summary>
    public static long EndTick(ProjectState state, AudioRegion region) {
        var timing = RegionTiming.Of(state, region);
        var end = BaseEndTick(timing);
        return region.LoopTicks <= 0
            ? end
            : Math.Max(end, Until(state, region.TrackId, region.StartTick, end, region.LoopTicks));
    }

    /// <summary>The project sample an audio region's playing ends at: its end, or where its repeats stop.</summary>
    public static long EndSample(ProjectState state, AudioRegion region) {
        var timing = RegionTiming.Of(state, region);
        if (region.LoopTicks <= 0) {
            return timing.EndSample;
        }

        var until = Until(state, region.TrackId, region.StartTick, BaseEndTick(timing), region.LoopTicks);
        return Math.Max(timing.EndSample, state.TempoMap.SampleAt(until, state.SampleRate));
    }

    static long BaseEndTick(RegionTiming timing) => (long)Math.Ceiling(timing.EndTick);

    /// <summary>A MIDI region as it plays: itself, then its repeats (each without a loop, cut as they play).</summary>
    public static IEnumerable<MidiRegion> Played(ProjectState state, MidiRegion region) {
        if (region.LoopTicks <= 0) {
            return [region];
        }

        var start = region.StartTick;
        var until = EndTick(state, region);
        return Starts(start, region.LengthTicks, until)
            .Select(s => region with {
                    StartTick = s, LengthTicks = Math.Min(region.LengthTicks, until - s), LoopTicks = 0
                }
            );
    }

    /// <summary>A pattern region as it plays: itself, then its repeats, each the same notes.</summary>
    public static IEnumerable<PatternRegion> Played(ProjectState state, PatternRegion region) {
        if (region.LoopTicks <= 0) {
            return [region];
        }

        var start = region.StartTick;
        var until = EndTick(state, region);
        return Starts(start, region.LengthTicks, until)
            .Select(s => region with {
                    StartTick = s, LengthTicks = Math.Min(region.LengthTicks, until - s), LoopTicks = 0
                }
            );
    }

    /// <summary>Where a region and each of its repeats start: every <paramref name="period" /> ticks before the end.</summary>
    static IEnumerable<long> Starts(long start, long period, long until) {
        for (var k = 0; k <= MaxRepeats; k++) {
            var at = start + k * period;
            if (at >= until && k > 0) {
                yield break;
            }

            yield return at;
        }
    }

    /// <summary>
    ///     An audio region's repeats as regions (without it, each without a loop; the last one cut where they stop), as a
    ///     warped region plays them and <c>convertLoops</c> makes them, a whole number of ticks apart.
    /// </summary>
    public static IEnumerable<AudioRegion> Repeats(ProjectState state, AudioRegion region) {
        var timing = RegionTiming.Of(state, region);
        var length = timing.EndTick - timing.StartTick;
        var period = Math.Max(1, (long)Math.Floor(length + 1e-6));
        var until = Until(state, region.TrackId, region.StartTick, BaseEndTick(timing), region.LoopTicks);
        for (var k = 1; k <= MaxRepeats; k++) {
            var start = region.StartTick + k * period;
            if (start >= until) {
                yield break;
            }

            var repeat = region with { StartTick = start, LoopTicks = 0 };
            var cut = RegionTiming.Of(state, repeat);
            if (cut.EndTick <= until) {
                yield return repeat;
                continue;
            }

            var frame = (long)Math.Round(cut.FrameAt(until));
            if (frame > region.FileOffset) {
                yield return Edits.Piece(repeat, region.FileOffset, frame, start) with { FadeOut = 0 };
            }

            yield break;
        }
    }

    /// <summary>The playlist entries of an audio region: its own, then its repeats'.</summary>
    public static IReadOnlyList<PlacedEntry> Entries(
        ProjectState state,
        AudioRegion region,
        Track track,
        TempoMap map,
        int fileId,
        FileAnalysis? analysis
    ) {
        var entries = PitchFollow.Entries(state, region, track, map, fileId, analysis);
        if (region.LoopTicks <= 0) {
            return entries;
        }

        var timing = RegionTiming.Of(state, region);
        if (!timing.Warped && entries is [var entry]) {
            return [entry, ..SampleRepeats(state, region, timing, entry)];
        }

        return [
            ..entries,
            ..Repeats(state, region)
                .SelectMany(r => PitchFollow.Entries(state, r, track, map, fileId, analysis))
        ];
    }

    /// <summary>An unwarped region's repeats, each starting on the sample after the one before ends.</summary>
    static IEnumerable<PlacedEntry> SampleRepeats(
        ProjectState state,
        AudioRegion region,
        RegionTiming timing,
        PlacedEntry first
    ) {
        var e = first.Entry;
        var until = state.TempoMap.SampleAt(
            Until(state, region.TrackId, region.StartTick, BaseEndTick(timing), region.LoopTicks),
            state.SampleRate
        );
        for (var k = 1; k <= MaxRepeats; k++) {
            var start = e.Start + k * e.Length;
            if (start >= until) {
                yield break;
            }

            var repeat = first with { Entry = e with { Start = start } };
            yield return start + e.Length <= until
                ? repeat
                : ModelCompiler.Narrowed(repeat, e.FileOffset, start, until);
        }
    }
}
