using Anode.Contracts;
using Anode.Model;
using Anode.Model.Time;

namespace Anode.Notation;

/// <summary>
///     Display position ↔ tick (docs/10-phase-9.md, "Display quantise"; docs/notation/README.md): a region's grids are
///     its <see cref="RegionNotation" />'s, or its staff style's when its straight grid is 0; grids are counted from
///     each bar line. Each beat (or each three triplet units, when the beat does not divide) is drawn on the straight or
///     the triplet grid, whichever fits its notes' starts better in sum (the straight grid on a tie). Display quantise
///     moves where notes are drawn, never the notes.
/// </summary>
public static class DisplayGrid {
    static readonly int[] TripletGrids = [2560, 1280, 640, 320, 160, 80, 40, 20];

    /// <summary>
    ///     The straight and triplet grids (ticks; triplet 0: none) a region of a track of <paramref name="style" /> uses,
    ///     as drawn: the straight grid is a plain value from a 1/128 (30, also for 0) to a whole note (a grid between
    ///     values takes the value below), the triplet grid one whose triplet is a plain value (20…2560; 160 the 1/16
    ///     triplet), else 0.
    /// </summary>
    public static (int Straight, int Triplet) GridsOf(RegionNotation notation, StaffStyle style) {
        var (straight, triplet) = notation.DisplayQuantizeTicks == 0
            ? (style.DisplayQuantizeTicks, style.TripletQuantizeTicks)
            : (notation.DisplayQuantizeTicks, notation.TripletQuantizeTicks);
        var s = (int)Meter.Shortest;
        while (s * 2 <= Math.Min(straight, (int)Meter.Whole)) {
            s *= 2;
        }

        var t = 0;
        foreach (var grid in TripletGrids) {
            if (grid <= triplet) {
                t = grid;
                break;
            }
        }

        return (s, t);
    }

    /// <summary>
    ///     Where a note starting at project tick <paramref name="tick" /> in <paramref name="region" /> is drawn (the
    ///     region's notes decide each beat's grid, with the tick as one of them).
    /// </summary>
    public static long DisplayTick(ProjectState state, MidiRegion region, StaffStyle style, long tick) =>
        QuantizerOf(state, region, style, tick).Start(tick, GridsOf(region.Notation, style));

    /// <summary>
    ///     The MIDI start, a project tick, of a note at <paramref name="tick" /> moved <paramref name="steps" /> steps of the
    ///     region's display grid (a horizontal drag); a note off the grid keeps its offset from it. A step goes to the next
    ///     display position of the bar (the triplet grid's in a beat drawn as triplets), across bar lines; never before 0.
    /// </summary>
    public static long Move(ProjectState state, MidiRegion region, StaffStyle style, long tick, int steps) {
        var grids = GridsOf(region.Notation, style);
        var quantizer = QuantizerOf(state, region, style, tick);
        var display = quantizer.Start(tick, grids);
        var offset = tick - display;
        var at = display;
        for (var i = 0; i < Math.Abs(steps); i++) {
            at = quantizer.Step(at, Math.Sign(steps), grids);
        }

        return Math.Max(0, at + offset);
    }

    static Quantizer QuantizerOf(ProjectState state, MidiRegion region, StaffStyle style, long tick) {
        var grids = GridsOf(region.Notation, style);
        var onsets = region.Notes.Select(n => region.StartTick + n.StartTick).Append(tick).Distinct();
        var quantizer = new Quantizer(new(state.SignatureMap));
        foreach (var onset in onsets) {
            quantizer.Add(onset, grids);
        }

        return quantizer;
    }
}

/// <summary>A bar of the signature map with its metric tree.</summary>
sealed record Bar(long Index, long Start, long Length, TimeSignature Time, MetricNode Tree, List<MetricNode> Beats) {
    public long End => Start + Length;
}

/// <summary>The project's bars, built on demand.</summary>
sealed class Bars(SignatureMap map) {
    readonly Dictionary<long, Bar> cache = [];

    public SignatureMap Map => map;

    public Bar At(long tick) => Of(map.Position(tick).Bar);

    public Bar Of(long index) {
        if (!cache.TryGetValue(index, out var bar)) {
            var start = map.BarStart(index);
            var signature = map.At(start);
            var time = new TimeSignature(signature.Numerator, signature.Denominator);
            var tree = Meter.Tree(start, time);
            bar = new(index, start, SignatureMap.BarTicks(signature), time, tree, Meter.Beats(tree));
            cache[index] = bar;
        }

        return bar;
    }
}

/// <summary>
///     Display quantise for one track (docs/notation/README.md, "Display quantise"): the notes' starts are added with
///     their region's grids, then each triplet span (a beat, or three triplet units) is drawn on the triplet grid when
///     its starts are nearer it in sum than the straight grid.
/// </summary>
sealed class Quantizer(Bars bars) {
    readonly Dictionary<(long Start, int Triplet), (long Straight, long Triplet)> errors = [];

    public Bars Bars => bars;

    /// <summary>A note's start (raw) with its region's grids.</summary>
    public void Add(long tick, (int Straight, int Triplet) grids) {
        var bar = bars.At(tick);
        if (SpanAt(bar, tick, grids.Triplet) is not { } span) {
            return;
        }

        var straight = Math.Abs(tick - SnapStraight(bar, tick, grids.Straight));
        var triplet = Math.Abs(tick - SnapIn(span, tick, grids.Triplet));
        var key = (span.Start, grids.Triplet);
        errors.TryGetValue(key, out var sum);
        errors[key] = (sum.Straight + straight, sum.Triplet + triplet);
    }

    /// <summary>Where a start is drawn.</summary>
    public long Start(long tick, (int Straight, int Triplet) grids) {
        var bar = bars.At(tick);
        return IsTriplet(bar, tick, grids.Triplet) is { } span
            ? SnapIn(span, tick, grids.Triplet)
            : SnapStraight(bar, tick, grids.Straight);
    }

    /// <summary>Where an end is drawn: the nearest display position of its span (a bar line stays).</summary>
    public long End(long tick, (int Straight, int Triplet) grids) {
        var bar = bars.At(tick);
        return tick == bar.Start ? tick : Start(tick, grids);
    }

    /// <summary>The grid unit at a display position: the triplet unit in a triplet span, else the straight grid.</summary>
    public long UnitAt(long display, (int Straight, int Triplet) grids) =>
        IsTriplet(bars.At(display), display, grids.Triplet) is not null ? grids.Triplet : grids.Straight;

    /// <summary>The triplet span drawn as triplets that contains <paramref name="tick" />, if any.</summary>
    public (long Start, long End)? TripletSpanAt(long tick, int triplet) => IsTriplet(bars.At(tick), tick, triplet);

    /// <summary>The spans of <paramref name="bar" /> drawn as triplets, with their grid, in order (one grid per span).</summary>
    public List<(long Start, long End, int Triplet)> TripletSpans(Bar bar) {
        var spans = new List<(long Start, long End, int Triplet)>();
        foreach (var ((start, triplet), sum) in errors.OrderBy(e => e.Key.Start).ThenBy(e => e.Key.Triplet)) {
            if (start < bar.Start || start >= bar.End || sum.Triplet >= sum.Straight) {
                continue;
            }

            var span = SpanAt(bar, start, triplet)!.Value;
            if (spans.Count == 0 || spans[^1].End <= span.Start) {
                spans.Add((span.Start, span.End, triplet));
            }
        }

        return spans;
    }

    /// <summary>The next (or previous) display position from <paramref name="display" />.</summary>
    public long Step(long display, int direction, (int Straight, int Triplet) grids) {
        if (direction > 0) {
            var bar = bars.At(display);
            return Points(bar, grids).First(p => p > display);
        }

        if (display <= 0) {
            return 0;
        }

        var before = bars.At(display - 1);
        return Points(before, grids).Last(p => p < display);
    }

    /// <summary>The display positions of a bar, its end included.</summary>
    List<long> Points(Bar bar, (int Straight, int Triplet) grids) {
        var points = new SortedSet<long> { bar.End };
        var triplets = grids.Triplet > 0 ? Spans(bar, grids.Triplet).Where(s => IsTriplet(bar, s.Start, grids.Triplet) is not null).ToList() : [];
        for (var p = bar.Start; p < bar.End; p += grids.Straight) {
            if (!triplets.Any(s => p > s.Start && p < s.End)) {
                points.Add(p);
            }
        }

        foreach (var (start, end) in triplets) {
            for (var p = start; p <= end; p += grids.Triplet) {
                points.Add(p);
            }
        }

        return [..points];
    }

    (long Start, long End)? IsTriplet(Bar bar, long tick, int triplet) =>
        SpanAt(bar, tick, triplet) is { } span
        && errors.TryGetValue((span.Start, triplet), out var sum)
        && sum.Triplet < sum.Straight
            ? span
            : null;

    /// <summary>
    ///     A bar's triplet spans for a triplet grid: its beats when every beat holds whole triplets of the grid, else
    ///     three grid units at a time from the bar line (a partial one at the bar's end is not one).
    /// </summary>
    public static List<(long Start, long End)> Spans(Bar bar, int triplet) {
        if (triplet <= 0) {
            return [];
        }

        var span = 3L * triplet;
        if (bar.Beats.All(b => b.Length % span == 0)) {
            return [..bar.Beats.Select(b => (b.Start, b.End))];
        }

        var spans = new List<(long Start, long End)>();
        for (var at = bar.Start; at + span <= bar.End; at += span) {
            spans.Add((at, at + span));
        }

        return spans;
    }

    static (long Start, long End)? SpanAt(Bar bar, long tick, int triplet) {
        foreach (var span in Spans(bar, triplet)) {
            if (tick >= span.Start && tick < span.End) {
                return span;
            }
        }

        return null;
    }

    static long SnapStraight(Bar bar, long tick, int grid) =>
        Math.Min(bar.End, bar.Start + Math.DivRem(tick - bar.Start + grid / 2, grid).Quotient * grid);

    static long SnapIn((long Start, long End) span, long tick, int grid) =>
        Math.Min(span.End, span.Start + (tick - span.Start + grid / 2) / grid * grid);
}
