using Anode.Contracts;
using Anode.Ipc.Segments;
using Anode.Model.Time;

namespace Anode.Model.Compile;

/// <summary>
///     A playlist entry as the compiler works on it: the entry, and for a warped one its own warp and pitch points
///     (offsets relative to the entry's start; the segment's indices are filled in last).
/// </summary>
public sealed record PlacedEntry(AudioPlaylistEntry Entry, WarpPoint[]? Warp = null, PitchPoint[]? Pitch = null) {
    public long Start => Entry.Start;
    public long End => Entry.Start + Entry.Length;
}

/// <summary>
///     Warp and pitch points of warped regions (docs/06-phase-5.md, "Compilation"): warp points at the region's ends,
///     at every flex marker and base-map breakpoint, every quarter note, every tempo change, and every
///     <see cref="RampStepSeconds" /> where the tempo ramps (linear between them); pitch points from the transpose, fine
///     tune and pitch notes, at the analysis hop.
/// </summary>
public static class Warps {
    /// <summary>The longest gap between warp points where the tempo ramps.</summary>
    public const double RampStepSeconds = 0.010;

    /// <summary>The pitch-point spacing when a file has no pitch analysis: 5 ms at 48 kHz.</summary>
    public const int DefaultHop = 240;

    /// <summary>The algorithm a warped region of <paramref name="track" /> plays through.</summary>
    public static FlexAlgorithm Algorithm(Track track) =>
        !track.FlexOn
            ? FlexAlgorithm.Polyphonic
            : track.FlexMode switch {
                FlexMode.Monophonic => FlexAlgorithm.Monophonic,
                FlexMode.Slicing => FlexAlgorithm.Slicing,
                FlexMode.Rhythmic => FlexAlgorithm.Rhythmic,
                FlexMode.Speed => FlexAlgorithm.Speed,
                FlexMode.Tempophone => FlexAlgorithm.Tempophone,
                FlexMode.Pitch => FlexAlgorithm.Pitch,
                _ => FlexAlgorithm.Polyphonic
            };

    /// <summary>A region's playlist entry: unwarped as before, warped with its points and its track's algorithm.</summary>
    public static PlacedEntry Entry(
        RegionTiming timing,
        Track track,
        TempoMap map,
        int sampleRate,
        int fileId,
        FileAnalysis? analysis
    ) {
        var r = timing.Region;
        var entry = new AudioPlaylistEntry {
            Start = timing.StartSample,
            FileOffset = r.FileOffset,
            Length = timing.Warped ? Math.Max(1, timing.EndSample - timing.StartSample) : r.Length,
            FileId = fileId,
            Gain = Units.DbToGain(r.GainDb),
            FadeIn = (int)Math.Min(r.FadeIn, int.MaxValue),
            FadeOut = (int)Math.Min(r.FadeOut, int.MaxValue),
            FadeInCurve = (float)r.FadeInCurve,
            FadeOutCurve = (float)r.FadeOutCurve
        };
        if (!timing.Warped) {
            return new(entry);
        }

        var fadeIn = (int)Math.Min(entry.FadeIn, entry.Length);
        entry = entry with {
            Algorithm = Algorithm(track),
            FadeIn = fadeIn,
            FadeOut = (int)Math.Min(entry.FadeOut, entry.Length - fadeIn)
        };
        return new(entry, Points(timing, map, sampleRate, entry.Length), Pitch(r, analysis));
    }

    /// <summary>A warped region's warp points over <paramref name="length" /> project samples.</summary>
    public static WarpPoint[] Points(RegionTiming timing, TempoMap map, int sampleRate, long length) {
        var r = timing.Region;
        var (first, last) = ((double)r.FileOffset, (double)(r.FileOffset + r.Length));
        var start = timing.StartSample;
        var offsets = new SortedSet<long>();
        var endTick = timing.EndTick;
        foreach (var tick in timing.Breakpoints()) {
            AddTick(tick);
        }

        for (var i = 0; i < map.Count; i++) {
            AddTick(map[i].Tick);
        }

        for (var tick = Math.Ceiling(timing.StartTick / (double)TempoMap.Ppq) * TempoMap.Ppq;
             tick < endTick;
             tick += TempoMap.Ppq) {
            AddTick(tick);
        }

        var step = Math.Max(1, (long)Math.Round(RampStepSeconds * sampleRate));
        for (var i = 0; i + 1 < map.Count; i++) {
            if (!map[i].Ramp) {
                continue;
            }

            var from = Math.Max(0, map.SampleAt(map[i].Tick, sampleRate) - start);
            var to = Math.Min(length, map.SampleAt(map[i + 1].Tick, sampleRate) - start);
            for (var o = from + step; o < to; o += step) {
                Add(o);
            }
        }

        var points = new List<WarpPoint>(offsets.Count + 2) { new() { Offset = 0, FileFrame = first } };
        foreach (var o in offsets) {
            var frame = Math.Clamp(timing.FrameAtSample(start + o), points[^1].FileFrame, last);
            points.Add(new() { Offset = o, FileFrame = frame });
        }

        points.Add(new() { Offset = length, FileFrame = last });
        return [..points];

        void AddTick(double tick) {
            if (tick > timing.StartTick && tick < endTick) {
                Add((long)Math.Round(map.SecondsAt(tick) * sampleRate) - start);
            }
        }

        void Add(long offset) {
            if (offset > 0 && offset < length) {
                offsets.Add(offset);
            }
        }
    }

    /// <summary>
    ///     A region's pitch points (none when it changes no pitch): the transpose and fine tune throughout; inside each
    ///     pitch note, at the analysis hop, cents = (target − detected) + (drift − 1)·(trend − detected) +
    ///     (vibrato − 1)·(curve − trend), the trend being the line fitted to the note's analysed curve, with its formant
    ///     shift and gain.
    /// </summary>
    public static PitchPoint[]? Pitch(AudioRegion region, FileAnalysis? analysis) {
        var transpose = region.Transpose * 100.0 + region.FineTune;
        if (region.PitchNotes.Count == 0) {
            return transpose == 0 ? null : [Point(region.FileOffset, transpose, 0, 0)];
        }

        var hop = analysis is { Hop: > 0 } a ? a.Hop : DefaultHop;
        var curve = analysis?.Pitch ?? [];
        var points = new List<PitchPoint> { Point(region.FileOffset, transpose, 0, 0) };
        foreach (var note in region.PitchNotes.OrderBy(static n => n.StartFrame)) {
            var (slope, intercept) = Trend(note, curve, hop);
            if (note.StartFrame - 1 > points[^1].FileFrame) {
                points.Add(Point(note.StartFrame - 1, transpose, 0, 0));
            }

            Add(note.StartFrame);
            for (var k = note.StartFrame / hop + 1; k * hop < note.EndFrame - 1; k++) {
                Add(k * hop);
            }

            Add(note.EndFrame - 1);
            if (note.EndFrame > points[^1].FileFrame) {
                points.Add(Point(note.EndFrame, transpose, 0, 0));
            }

            continue;

            void Add(long frame) {
                if (points.Count > 0 && frame <= points[^1].FileFrame) {
                    return;
                }

                var trend = intercept + slope * frame;
                var k = (int)Math.Round(frame / (double)hop);
                var vibrato = k >= 0 && k < curve.Count && curve[k] is { } c ? c - trend : 0;
                var cents = note.TargetCents
                    - note.DetectedCents
                    + (note.Drift - 1) * (trend - note.DetectedCents)
                    + (note.Vibrato - 1) * vibrato;
                points.Add(Point(frame, transpose + cents, note.FormantCents, note.GainDb));
            }
        }

        return [..points];
    }

    /// <summary>The line fitted to a note's voiced analysed pitch (cents against frame); flat at its pitch without.</summary>
    static (double Slope, double Intercept) Trend(PitchNote note, IReadOnlyList<float?> curve, int hop) {
        double n = 0, sx = 0, sy = 0, sxx = 0, sxy = 0;
        for (var k = (note.StartFrame + hop - 1) / hop; k * hop < note.EndFrame && k < curve.Count; k++) {
            if (curve[(int)k] is not { } y || !float.IsFinite(y)) {
                continue;
            }

            double x = k * hop;
            n++;
            sx += x;
            sy += y;
            sxx += x * x;
            sxy += x * y;
        }

        if (n < 2) {
            return (0, n == 1 ? sy : note.DetectedCents);
        }

        var d = n * sxx - sx * sx;
        if (Math.Abs(d) < 1e-9) {
            return (0, sy / n);
        }

        var slope = (n * sxy - sx * sy) / d;
        return (slope, (sy - slope * sx) / n);
    }

    static PitchPoint Point(double frame, double cents, double formant, double gain) =>
        new() { FileFrame = frame, Cents = (float)cents, FormantCents = (float)formant, GainDb = (float)gain };

    /// <summary>
    ///     A warped entry's part of <paramref name="length" /> samples from <paramref name="offset" />: its warp points
    ///     inside, with points interpolated at the new ends.
    /// </summary>
    public static WarpPoint[] Slice(WarpPoint[] warp, long offset, long length) {
        var points = new List<WarpPoint> { new() { Offset = 0, FileFrame = FrameAt(warp, offset) } };
        foreach (var p in warp) {
            if (p.Offset > offset && p.Offset < offset + length) {
                points.Add(new() { Offset = p.Offset - offset, FileFrame = p.FileFrame });
            }
        }

        points.Add(new() { Offset = length, FileFrame = FrameAt(warp, offset + length) });
        return [..points];
    }

    /// <summary>The file frame a warp map plays at <paramref name="offset" /> (linear between points, held beyond).</summary>
    public static double FrameAt(WarpPoint[] warp, long offset) {
        if (offset <= warp[0].Offset) {
            return warp[0].FileFrame;
        }

        for (var i = 1; i < warp.Length; i++) {
            if (offset <= warp[i].Offset) {
                var (a, b) = (warp[i - 1], warp[i]);
                return a.FileFrame + (b.FileFrame - a.FileFrame) * (offset - a.Offset) / (b.Offset - a.Offset);
            }
        }

        return warp[^1].FileFrame;
    }
}
