using Anode.Contracts;
using Anode.Model.Time;

namespace Anode.Notation;

/// <summary>
///     A node of a measure's metric tree (docs/notation/README.md, "Durations"): the bar, its halves (in a meter of
///     four beats), its beats, and their subdivisions down to the 1/64. <c>IsBeat</c> marks the beat level (a dotted
///     beat in compound meters); <c>InBeat</c> is the beat or anything below it.
/// </summary>
sealed class MetricNode(long start, long length, bool isBeat, bool inBeat, MetricNode[] children) {
    public long Start { get; } = start;
    public long Length { get; } = length;
    public long End => Start + Length;
    public bool IsBeat { get; } = isBeat;
    public bool InBeat { get; } = inBeat;
    public MetricNode[] Children { get; } = children;
}

/// <summary>A notated duration: a value and its dots.</summary>
readonly record struct Written(NoteValue Value, int Dots);

/// <summary>Note values and meters: what a duration can be written as, and how a measure divides.</summary>
static class Meter {
    public const long Whole = TempoMap.Ppq * 4;

    /// <summary>The shortest value written (a 1/128).</summary>
    public const long Shortest = Whole / 128;

    static readonly (NoteValue Value, long Ticks)[] Values = [
        (NoteValue.Breve, Whole * 2),
        (NoteValue.Whole, Whole),
        (NoteValue.Half, Whole / 2),
        (NoteValue.Quarter, Whole / 4),
        (NoteValue.Eighth, Whole / 8),
        (NoteValue.Sixteenth, Whole / 16),
        (NoteValue.ThirtySecond, Whole / 32),
        (NoteValue.SixtyFourth, Whole / 64),
        (NoteValue.HundredTwentyEighth, Whole / 128)
    ];

    public static long TicksOf(NoteValue value) {
        foreach (var (v, ticks) in Values) {
            if (v == value) {
                return ticks;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(value));
    }

    /// <summary>Whether <paramref name="ticks" /> is an undotted value (a power-of-two fraction of a breve).</summary>
    public static bool IsPlainValue(long ticks) {
        foreach (var (_, t) in Values) {
            if (t == ticks) {
                return true;
            }
        }

        return false;
    }

    /// <summary>The value with at most <paramref name="maxDots" /> dots lasting exactly <paramref name="ticks" />.</summary>
    public static bool TryWrite(long ticks, int maxDots, out Written written) {
        foreach (var (value, t) in Values) {
            var total = t;
            var add = t;
            for (var dots = 0; dots <= maxDots; dots++) {
                if (total == ticks) {
                    written = new(value, dots);
                    return true;
                }

                if (add % 2 != 0) {
                    break;
                }

                add /= 2;
                total += add;
            }
        }

        written = default;
        return false;
    }

    /// <summary>
    ///     A meter is compound when its numerator is a multiple of three, six or more (6/8, 9/8, 12/8, 6/4), or three
    ///     in eighths or shorter (3/8): its beat is a dotted value of three units.
    /// </summary>
    public static bool IsCompound(int numerator, int denominator) =>
        numerator % 3 == 0 && (numerator >= 6 || denominator >= 8);

    /// <summary>
    ///     The beat groups of a meter, in units of the denominator: threes in compound meters; ones up to four units;
    ///     3 + 2 + 2… for an odd numerator (5/8 is 3 + 2, 7/8 3 + 2 + 2); twos otherwise.
    /// </summary>
    public static int[] Groups(int numerator, int denominator) {
        if (IsCompound(numerator, denominator)) {
            return [..Enumerable.Repeat(3, numerator / 3)];
        }

        if (numerator <= 4) {
            return [..Enumerable.Repeat(1, numerator)];
        }

        return numerator % 2 == 1 ? [3, ..Enumerable.Repeat(2, (numerator - 3) / 2)] : [..Enumerable.Repeat(2, numerator / 2)];
    }

    /// <summary>
    ///     The metric tree of a measure of <paramref name="time" /> starting at <paramref name="start" />. Beats are the
    ///     groups in compound meters and in eighths or shorter, else the units; a meter of four groups is first halved.
    /// </summary>
    public static MetricNode Tree(long start, TimeSignature time) {
        var unit = Whole / time.Denominator;
        var groups = Groups(time.Numerator, time.Denominator);
        var beatIsGroup = IsCompound(time.Numerator, time.Denominator) || time.Denominator >= 8;
        var nodes = new MetricNode[groups.Length];
        var at = start;
        for (var i = 0; i < groups.Length; i++) {
            nodes[i] = Group(at, groups[i], unit, beatIsGroup);
            at += groups[i] * unit;
        }

        var length = at - start;
        if (nodes.Length == 1) {
            return nodes[0];
        }

        if (nodes.Length == 4) {
            var half = nodes[0].Length + nodes[1].Length;
            return new(
                start,
                length,
                false,
                false,
                [
                    new(start, half, false, false, [nodes[0], nodes[1]]),
                    new(start + half, length - half, false, false, [nodes[2], nodes[3]])
                ]
            );
        }

        return new(start, length, false, false, nodes);
    }

    /// <summary>The beat nodes of a tree, in order.</summary>
    public static List<MetricNode> Beats(MetricNode tree) {
        var beats = new List<MetricNode>();
        Collect(tree, beats);
        return beats;

        static void Collect(MetricNode node, List<MetricNode> into) {
            if (node.IsBeat) {
                into.Add(node);
                return;
            }

            foreach (var child in node.Children) {
                Collect(child, into);
            }
        }
    }

    /// <summary>The beat node containing <paramref name="tick" /> (the last one at the measure's end).</summary>
    public static MetricNode BeatAt(List<MetricNode> beats, long tick) {
        foreach (var beat in beats) {
            if (tick < beat.End) {
                return beat;
            }
        }

        return beats[^1];
    }

    static MetricNode Group(long start, int units, long unit, bool beatIsGroup) {
        if (units == 1) {
            return Binary(start, unit, true, true);
        }

        var children = new MetricNode[units];
        for (var i = 0; i < units; i++) {
            children[i] = Binary(start + i * unit, unit, !beatIsGroup, beatIsGroup);
        }

        return new(start, units * unit, beatIsGroup, beatIsGroup, children);
    }

    /// <summary>A node halved down to the 1/64 (three parts when it is a dotted length).</summary>
    static MetricNode Binary(long start, long length, bool isBeat, bool inBeat) {
        inBeat |= isBeat;
        if (length % 3 == 0 && IsPlainValue(length / 3) && length / 3 >= Shortest * 2) {
            var third = length / 3;
            return new(
                start,
                length,
                isBeat,
                inBeat,
                [
                    Binary(start, third, false, inBeat),
                    Binary(start + third, third, false, inBeat),
                    Binary(start + 2 * third, third, false, inBeat)
                ]
            );
        }

        if (length % 2 != 0 || length / 2 < Shortest * 2) {
            return new(start, length, isBeat, inBeat, []);
        }

        var half = length / 2;
        return new(
            start,
            length,
            isBeat,
            inBeat,
            [Binary(start, half, false, inBeat), Binary(start + half, half, false, inBeat)]
        );
    }

    /// <summary>
    ///     How a span of a measure [<paramref name="a" />, <paramref name="b" />) is written as tied values
    ///     (docs/notation/README.md, "Durations"): one value where the meter allows it, else split at the metric tree's
    ///     boundaries. Rests are stricter than notes (no dots except a compound beat, no syncopation).
    /// </summary>
    public static void Split(SplitContext context, long a, long b, bool rest, List<(long Start, long End, Written Written)> into) =>
        Split(context, context.Tree, a, b, rest, into);

    static void Split(
        SplitContext context,
        MetricNode node,
        long a,
        long b,
        bool rest,
        List<(long Start, long End, Written Written)> into
    ) {
        var length = b - a;
        if (a == node.Start && b == node.End) {
            var dots = rest ? node.IsBeat && context.Compound ? 1 : 0 : context.MaxDots;
            if (TryWrite(length, dots, out var whole)) {
                into.Add((a, b, whole));
                return;
            }
        }

        foreach (var child in node.Children) {
            if (a >= child.Start && b <= child.End) {
                Split(context, child, a, b, rest, into);
                return;
            }
        }

        if (node.Children.Length == 0) {
            Greedy(a, b, into);
            return;
        }

        if (Single(context, node, a, b, rest, out var single)) {
            into.Add((a, b, single));
            return;
        }

        foreach (var child in node.Children) {
            var from = Math.Max(a, child.Start);
            var to = Math.Min(b, child.End);
            if (from < to) {
                Split(context, child, from, to, rest, into);
            }
        }
    }

    /// <summary>Whether a span crossing <paramref name="node" />'s children is written as one value.</summary>
    static bool Single(SplitContext context, MetricNode node, long a, long b, bool rest, out Written written) {
        var length = b - a;
        if (rest) {
            return TryWrite(length, 0, out written) && a == node.Start && IsChildBoundary(node, b);
        }

        if (!TryWrite(length, context.MaxDots, out written)) {
            return false;
        }

        // Inside a beat anything goes; from a node's start (in compound meters only up to a beat line); the last two
        // parts of a triple node (a half note on beat 2 of 3/4, a quarter after the first eighth of a 6/8 beat).
        if (node.InBeat
            || (a == node.Start && (context.Simple || IsChildBoundary(node, b)))
            || (node.Children.Length == 3 && a == node.Children[1].Start && b == node.End)) {
            return true;
        }

        // Syncopation shown: a value on a multiple of half its undotted length.
        return context.Syncopation && (a - context.Tree.Start) % (TicksOf(written.Value) / 2) == 0;
    }

    static bool IsChildBoundary(MetricNode node, long tick) {
        foreach (var child in node.Children) {
            if (child.End == tick) {
                return true;
            }
        }

        return false;
    }

    /// <summary>The fallback for a span off every grid: the longest values first, the remainder on the last.</summary>
    static void Greedy(long a, long b, List<(long Start, long End, Written Written)> into) {
        var at = a;
        while (at < b) {
            var left = b - at;
            var (value, ticks) = Values.FirstOrDefault(v => v.Ticks <= left);
            if (ticks == 0) {
                // Less than a 1/128 left: drawn as one, lasting what is left.
                into.Add((at, b, new(NoteValue.HundredTwentyEighth, 0)));
                return;
            }

            var end = b - (at + ticks) < Shortest ? b : at + ticks;
            into.Add((at, end, new(value, 0)));
            at = end;
        }
    }
}

/// <summary>What splitting a measure's spans needs: its tree, meter kind and the region's settings.</summary>
sealed record SplitContext(MetricNode Tree, bool Compound, bool Simple, int MaxDots, bool Syncopation) {
    public static SplitContext Of(MetricNode tree, TimeSignature time, RegionNotation notation) =>
        new(
            tree,
            Meter.IsCompound(time.Numerator, time.Denominator),
            time.Denominator <= 4 && !Meter.IsCompound(time.Numerator, time.Denominator),
            Math.Clamp(notation.MaxDots, 0, 3),
            notation.Syncopation
        );
}
