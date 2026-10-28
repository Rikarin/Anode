using Anode.Contracts;

namespace Anode.Model.Time;

/// <summary>
///     Where an audio region's file frames play (docs/06-phase-5.md, "Region timing"); the editor mirrors it, and
///     <c>engine/tests/Anode.Model.Tests/RegionTimingVectors.json</c> holds the cases both check.
///     <list type="bullet">
///         <item>
///             An <b>unwarped</b> region (<see cref="IsWarped" /> false) plays file frame <c>fileOffset + n</c> at project
///             sample <c>SampleAt(startTick) + n</c>.
///         </item>
///         <item>
///             A <b>warped</b> one maps file frames to ticks relative to <c>startTick</c> through its <b>base map</b>:
///             <c>follow: off</c> as time (as unwarped); <c>on</c> linearly, a file beat (<c>60·rate / tempo.bpm</c>
///             frames) to a quarter note; <c>bars</c> (<c>barsAndBeats</c>) piecewise linearly through the file's
///             downbeats (beats) after <c>fileOffset</c>: the first lands on the project bar line (beat) nearest to where
///             the <c>on</c> map puts it, among those after <c>startTick</c>, and each next one on the next bar line
///             (beat); before the first and after the last, the <c>on</c> slope. Without a file tempo every follow
///             is time.
///         </item>
///         <item>
///             <b>Flex markers</b> pin file frames to ticks: between neighbouring knots (the region's start at tick 0,
///             the markers, its end where the base map puts it) the base map is scaled linearly. Markers that are not
///             strictly inside the region, or not strictly increasing in both frame and tick after the ones kept
///             before them, are ignored.
///         </item>
///         <item>The end tick is where the region's last frame lands; its length in ticks is derived.</item>
///     </list>
///     Immutable; built per region and project.
/// </summary>
public sealed class RegionTiming {
    readonly TempoMap map;
    readonly double rate;
    readonly long fileOffset;
    readonly long endFrame;

    /// <summary>The base map's breakpoints (file frame, tick relative to the start), ascending; empty for time.</summary>
    readonly double[] baseFrames;

    readonly double[] baseTicks;

    /// <summary>The base map's slope outside its breakpoints, in ticks per frame.</summary>
    readonly double slope;

    /// <summary>The knots: the start, the markers kept and the end (file frame, relative tick).</summary>
    readonly double[] knotFrames;

    readonly double[] knotTicks;

    /// <summary>The base map's relative tick at each knot.</summary>
    readonly double[] knotBase;

    RegionTiming(AudioRegion region, FileTempo? tempo, bool warped, TempoMap map, SignatureMap signatures, int rate) {
        this.map = map;
        this.rate = rate;
        Region = region;
        Warped = warped;
        fileOffset = region.FileOffset;
        endFrame = region.FileOffset + region.Length;
        StartTick = region.StartTick;
        StartSample = map.SampleAt(region.StartTick, rate);
        if (!warped) {
            (baseFrames, baseTicks, slope) = ([], [], 0);
            (knotFrames, knotTicks, knotBase) = ([], [], []);
            EndSample = StartSample + region.Length;
            EndTick = map.TickAt(EndSample / this.rate);
            return;
        }

        Follow = tempo is { Bpm: > 0 } ? region.Follow : FlexFollow.Off;
        (baseFrames, baseTicks, slope) = BaseMap(Follow, tempo, signatures);
        var end = Base(endFrame);
        var frames = new List<double> { fileOffset };
        var ticks = new List<double> { 0 };
        foreach (var marker in region.FlexMarkers.Where(static m => m is not null).OrderBy(static m => m.FileFrame)) {
            if (marker.FileFrame > frames[^1]
                && marker.FileFrame < endFrame
                && marker.Tick > ticks[^1]
                && marker.Tick < end) {
                frames.Add(marker.FileFrame);
                ticks.Add(marker.Tick);
            }
        }

        frames.Add(endFrame);
        ticks.Add(end);
        knotFrames = [..frames];
        knotTicks = [..ticks];
        knotBase = [..frames.Select(Base)];
        EndTick = StartTick + end;
        EndSample = (long)Math.Round(map.SecondsAt(EndTick) * rate);
    }

    public AudioRegion Region { get; }

    /// <summary>Whether the region is warped (plays through a flex algorithm).</summary>
    public bool Warped { get; }

    /// <summary>The follow mode in effect (off when the file has no tempo); off for an unwarped region.</summary>
    public FlexFollow Follow { get; }

    public long StartTick { get; }

    /// <summary>The (fractional) tick at which the region ends: where its last frame lands.</summary>
    public double EndTick { get; }

    public long StartSample { get; }

    /// <summary>The project sample at which the region ends (exclusive).</summary>
    public long EndSample { get; }

    /// <summary>The flex markers in effect (a marker not strictly inside, or out of order, is ignored).</summary>
    public IEnumerable<FlexMarker> Markers {
        get {
            for (var k = 1; k < knotFrames.Length - 1; k++) {
                yield return new((long)knotFrames[k], (long)knotTicks[k]);
            }
        }
    }

    /// <summary>
    ///     Whether a region is warped: its track's flex is on, it follows the tempo, or it has a transpose, a fine tune or
    ///     pitch notes.
    /// </summary>
    public static bool IsWarped(AudioRegion region, bool flexOn) =>
        flexOn
        || region.Follow != FlexFollow.Off
        || region.Transpose != 0
        || region.FineTune != 0
        || region.PitchNotes is { Count: > 0 };

    /// <summary>The timing of <paramref name="region" /> in <paramref name="state" />.</summary>
    public static RegionTiming Of(ProjectState state, AudioRegion region) =>
        new(
            region,
            state.Files.GetValueOrDefault(region.FileId)?.Tempo,
            IsWarped(region, state.FindTrack(region.TrackId)?.FlexOn == true),
            state.TempoMap,
            state.SignatureMap,
            state.SampleRate
        );

    /// <summary>The timing of <paramref name="region" /> with everything it depends on given.</summary>
    public static RegionTiming Of(
        AudioRegion region,
        FileTempo? tempo,
        bool flexOn,
        TempoMap map,
        SignatureMap signatures,
        int sampleRate
    ) =>
        new(region, tempo, IsWarped(region, flexOn), map, signatures, sampleRate);

    /// <summary>The base map's tick (relative to the start) at file frame <paramref name="frame" />.</summary>
    public double Base(double frame) {
        if (baseFrames.Length == 0) {
            return map.TickAt((StartSample + frame - fileOffset) / rate) - StartTick;
        }

        if (frame <= baseFrames[0]) {
            return baseTicks[0] + (frame - baseFrames[0]) * slope;
        }

        var last = baseFrames.Length - 1;
        if (frame >= baseFrames[last]) {
            return baseTicks[last] + (frame - baseFrames[last]) * slope;
        }

        var i = Index(baseFrames, frame);
        return baseTicks[i]
            + (frame - baseFrames[i]) * (baseTicks[i + 1] - baseTicks[i]) / (baseFrames[i + 1] - baseFrames[i]);
    }

    /// <summary>The file frame the base map puts at relative tick <paramref name="tick" />.</summary>
    public double BaseFrame(double tick) {
        if (baseFrames.Length == 0) {
            return fileOffset + map.SecondsAt(StartTick + tick) * rate - StartSample;
        }

        if (tick <= baseTicks[0]) {
            return baseFrames[0] + (tick - baseTicks[0]) / slope;
        }

        var last = baseTicks.Length - 1;
        if (tick >= baseTicks[last]) {
            return baseFrames[last] + (tick - baseTicks[last]) / slope;
        }

        var i = Index(baseTicks, tick);
        return baseFrames[i]
            + (tick - baseTicks[i]) * (baseFrames[i + 1] - baseFrames[i]) / (baseTicks[i + 1] - baseTicks[i]);
    }

    /// <summary>The (fractional) project tick at which file frame <paramref name="frame" /> plays.</summary>
    public double TickAt(double frame) {
        if (!Warped) {
            return map.TickAt((StartSample + frame - fileOffset) / rate);
        }

        if (frame <= fileOffset || frame >= endFrame) {
            return StartTick + Base(frame);
        }

        var k = Index(knotFrames, frame);
        var scale = (knotTicks[k + 1] - knotTicks[k]) / (knotBase[k + 1] - knotBase[k]);
        return StartTick + knotTicks[k] + (Base(frame) - knotBase[k]) * scale;
    }

    /// <summary>The (fractional) file frame that plays at project tick <paramref name="tick" />.</summary>
    public double FrameAt(double tick) {
        if (!Warped) {
            return fileOffset + map.SecondsAt(tick) * rate - StartSample;
        }

        var relative = tick - StartTick;
        if (relative <= 0 || relative >= knotTicks[^1]) {
            return BaseFrame(relative);
        }

        var k = Index(knotTicks, relative);
        var scale = (knotTicks[k + 1] - knotTicks[k]) / (knotBase[k + 1] - knotBase[k]);
        return BaseFrame(knotBase[k] + (relative - knotTicks[k]) / scale);
    }

    /// <summary>The (fractional) project sample at which file frame <paramref name="frame" /> plays.</summary>
    public double SampleAt(double frame) =>
        Warped ? map.SecondsAt(TickAt(frame)) * rate : StartSample + frame - fileOffset;

    /// <summary>The (fractional) file frame that plays at project sample <paramref name="sample" />.</summary>
    public double FrameAtSample(double sample) =>
        Warped ? FrameAt(map.TickAt(sample / rate)) : fileOffset + sample - StartSample;

    /// <summary>The ticks (absolute) of the knots and base-map breakpoints inside the region: where warp points go.</summary>
    public IEnumerable<double> Breakpoints() {
        for (var k = 1; k < knotTicks.Length - 1; k++) {
            yield return StartTick + knotTicks[k];
        }

        for (var i = 0; i < baseFrames.Length; i++) {
            if (baseFrames[i] > fileOffset && baseFrames[i] < endFrame) {
                yield return TickAt(baseFrames[i]);
            }
        }
    }

    /// <summary>The base map of a warped region: breakpoints and the slope beyond them (none: time).</summary>
    (double[] Frames, double[] Ticks, double Slope) BaseMap(
        FlexFollow follow,
        FileTempo? tempo,
        SignatureMap signatures
    ) {
        if (follow == FlexFollow.Off || tempo is null) {
            return ([], [], 0);
        }

        // On: a file beat is a quarter note.
        var on = tempo.Bpm * TempoMap.Ppq / (60 * rate);
        var frames = new List<double> { fileOffset };
        var ticks = new List<double> { 0 };
        if (follow == FlexFollow.On) {
            return ([..frames], [..ticks], on);
        }

        var bars = follow == FlexFollow.Bars;
        var anchors = (bars ? tempo.Downbeats : tempo.Beats) ?? [];
        long? line = null;
        foreach (var anchor in anchors) {
            if (anchor <= fileOffset) {
                continue;
            }

            if (line is not { } previous) {
                var provisional = StartTick + (anchor - fileOffset) * on;
                line = Nearest(signatures, bars, provisional);
            } else {
                line = Next(signatures, bars, previous);
            }

            frames.Add(anchor);
            ticks.Add(line.Value - StartTick);
            if (anchor >= endFrame) {
                break; // the anchor after the end fixes the last stretch; later ones change nothing here
            }
        }

        return ([..frames], [..ticks], on);
    }

    /// <summary>The bar line (beat) after <see cref="StartTick" /> nearest to <paramref name="tick" />.</summary>
    long Nearest(SignatureMap signatures, bool bars, double tick) {
        var first = Next(signatures, bars, StartTick);
        if (tick <= first) {
            return first;
        }

        var at = (long)Math.Floor(tick);
        var before = bars ? signatures.BarLineAtOrBefore(at) : BeatAtOrBefore(signatures, at);
        var after = Next(signatures, bars, before);
        var nearest = tick - before <= after - tick ? before : after;
        return Math.Max(nearest, first);
    }

    /// <summary>The first bar line (beat) strictly after <paramref name="tick" />.</summary>
    static long Next(SignatureMap signatures, bool bars, long tick) {
        var position = signatures.Position(tick);
        var nextBar = signatures.BarStart(position.Bar + 1);
        if (bars) {
            return nextBar;
        }

        var signature = signatures.At(signatures.BarStart(position.Bar));
        if (position.Beat + 1 >= signature.Numerator) {
            return nextBar;
        }

        return signatures.TickOf(position with { Beat = position.Beat + 1, Tick = 0 });
    }

    static long BeatAtOrBefore(SignatureMap signatures, long tick) {
        var position = signatures.Position(tick);
        return signatures.TickOf(position with { Tick = 0 });
    }

    /// <summary>The index i of the interval [values[i], values[i+1]] holding <paramref name="value" /> (inside).</summary>
    static int Index(double[] values, double value) {
        var i = Array.BinarySearch(values, value);
        i = i >= 0 ? i : ~i - 1;
        return Math.Clamp(i, 0, values.Length - 2);
    }
}
