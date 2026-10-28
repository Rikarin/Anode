using Anode.Contracts;

namespace Anode.Model.Time;

/// <summary>A position in bars and beats: 0-based <c>Bar</c> and <c>Beat</c>, and the ticks into that beat.</summary>
public readonly record struct BarBeat(long Bar, int Beat, long Tick);

/// <summary>
///     The signature track as bars and beats (docs/04-phase-3.md, "Tempo map"): each change starts on a bar line of the
///     signature before it, and bars and beats count from tick 0 through every change. A beat is the signature's
///     denominator note. Immutable; lookups are a binary search over the changes.
/// </summary>
public sealed class SignatureMap {
    readonly SignatureEvent[] changes;

    /// <summary>The bar each change starts at.</summary>
    readonly long[] firstBars;

    public SignatureMap(IEnumerable<SignatureEvent> events) {
        changes = [..events];
        if (changes.Length == 0 || changes[0].Tick != 0) {
            throw new ArgumentException("A signature track starts with a change at tick 0.", nameof(events));
        }

        firstBars = new long[changes.Length];
        for (var i = 1; i < changes.Length; i++) {
            var previous = changes[i - 1];
            var span = changes[i].Tick - previous.Tick;
            var bar = BarTicks(previous);
            if (span <= 0 || span % bar != 0) {
                throw new ArgumentException(
                    $"The signature change at tick {changes[i].Tick} is not on a bar line of the one before it.",
                    nameof(events)
                );
            }

            firstBars[i] = firstBars[i - 1] + span / bar;
        }
    }

    /// <summary>One signature throughout.</summary>
    public static SignatureMap Constant(int numerator, int denominator) => new([new(0, numerator, denominator)]);

    public int Count => changes.Length;

    public SignatureEvent this[int index] => changes[index];

    /// <summary>Ticks in one beat (the denominator's note) of a signature.</summary>
    public static long BeatTicks(int denominator) => TempoMap.Ppq * 4L / denominator;

    public static long BarTicks(SignatureEvent signature) => BeatTicks(signature.Denominator) * signature.Numerator;

    /// <summary>The signature in force at <paramref name="tick" />.</summary>
    public SignatureEvent At(long tick) => changes[IndexAt(tick)];

    /// <summary>Where <paramref name="tick" /> falls in bars and beats (ticks before 0 count in the first signature).</summary>
    public BarBeat Position(long tick) {
        var i = IndexAt(tick);
        var s = changes[i];
        var bar = BarTicks(s);
        var beat = BeatTicks(s.Denominator);
        var offset = tick - s.Tick;
        var bars = Math.DivRem(offset, bar, out var inBar);
        if (inBar < 0) {
            bars--;
            inBar += bar;
        }

        return new(firstBars[i] + bars, (int)(inBar / beat), inBar % beat);
    }

    /// <summary>The tick at which 0-based bar <paramref name="bar" /> starts.</summary>
    public long BarStart(long bar) {
        var i = changes.Length - 1;
        while (i > 0 && firstBars[i] > bar) {
            i--;
        }

        return changes[i].Tick + (bar - firstBars[i]) * BarTicks(changes[i]);
    }

    /// <summary>The tick of a position in bars and beats.</summary>
    public long TickOf(BarBeat position) =>
        BarStart(position.Bar) + position.Beat * BeatTicks(At(BarStart(position.Bar)).Denominator) + position.Tick;

    public bool IsBarLine(long tick) => Position(tick) is { Beat: 0, Tick: 0 };

    /// <summary>The bar line at or before <paramref name="tick" />.</summary>
    public long BarLineAtOrBefore(long tick) => BarStart(Position(tick).Bar);

    /// <summary>The bar line at or after <paramref name="tick" />.</summary>
    public long BarLineAtOrAfter(long tick) {
        var before = BarLineAtOrBefore(tick);
        return before == tick ? tick : BarStart(Position(tick).Bar + 1);
    }

    /// <summary>Every beat from tick 0 up to (not including) <paramref name="endTick" />, and whether it starts a bar.</summary>
    public IEnumerable<(long Tick, bool Downbeat)> Beats(long endTick) {
        for (var i = 0; i < changes.Length; i++) {
            var s = changes[i];
            var to = i + 1 < changes.Length ? Math.Min(changes[i + 1].Tick, endTick) : endTick;
            var beat = BeatTicks(s.Denominator);
            var n = 0L;
            for (var tick = s.Tick; tick < to; tick += beat, n++) {
                yield return (tick, n % s.Numerator == 0);
            }

            if (to >= endTick) {
                yield break;
            }
        }
    }

    int IndexAt(long tick) {
        int lo = 0, hi = changes.Length - 1;
        while (lo < hi) {
            var mid = (lo + hi + 1) / 2;
            if (changes[mid].Tick <= tick) {
                lo = mid;
            } else {
                hi = mid - 1;
            }
        }

        return lo;
    }
}
