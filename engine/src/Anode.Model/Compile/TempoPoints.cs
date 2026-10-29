using Anode.Contracts;
using Anode.Ipc.Segments;
using Anode.Model.Time;

namespace Anode.Model.Compile;

/// <summary>
///     The compiled tempo map of a segment (format 4, docs/07-phase-6.md): a point at the start, at every tempo and
///     signature change, and every 1/16 of a quarter note along a shaped ramp, in playback samples with Varispeed folded
///     in. A step or a linear ramp (linear in quarter notes, as the tempo track's) is exact between points; a shaped
///     ramp is sampled into linear pieces. Each point sits where the previous piece analytically reaches it (rounded to
///     a sample) and its quarter note is that piece's at that sample, so the engine takes the points at face value;
///     each carries the signature and the bar line at or before it.
/// </summary>
public static class TempoPoints {
    /// <summary>Ticks between the points a shaped ramp is sampled at: 1/16 of a quarter note.</summary>
    public const int ShapedStepTicks = TempoMap.Ppq / 16;

    public static TempoMapPoint[] Of(ProjectState state) {
        var map = state.PlaybackMap;
        var speed = state.PlaybackSpeed;
        var signatures = state.SignatureMap;
        var events = state.Settings.TempoEvents is { Count: > 0 } tempo
            ? tempo
            : [new TempoEvent(0, state.Settings.Tempo, TempoCurve.Step)];
        var ordered = Ticks(events, signatures);
        var points = new List<TempoMapPoint>(ordered.Length);
        for (var k = 0; k < ordered.Length; k++) {
            var tick = ordered[k];
            var slope = Slope(map, events, tick, k + 1 < ordered.Length ? ordered[k + 1] : null);

            var signature = signatures.At(tick);
            var point = new TempoMapPoint {
                Sample = 0,
                Quarter = 0,
                Bpm = map.BpmAt(tick) * speed,
                Slope = slope * speed,
                BarQuarter = (double)signatures.BarLineAtOrBefore(tick) / TempoMap.Ppq,
                Numerator = (short)signature.Numerator,
                Denominator = (short)signature.Denominator
            };
            if (points.Count == 0) {
                points.Add(point);
                continue;
            }

            // The engine takes points at face value: a point sits where the previous piece, analytically, reaches its
            // quarter note (rounded to a sample), and its quarter note is that piece's at that very sample.
            var previous = points[^1];
            var sample = previous.Sample
                + (long)Math.Round(Seconds(previous, (double)tick / TempoMap.Ppq) * state.SampleRate);
            if (sample <= previous.Sample) {
                // Samples strictly increase: of points that land on one sample, the later's tempo stands.
                points[^1] = point with { Sample = previous.Sample, Quarter = previous.Quarter };
                continue;
            }

            points.Add(point with { Sample = sample, Quarter = Quarter(previous, sample, state.SampleRate) });
        }

        return [..points];
    }

    /// <summary>The ticks with a point: the start, every change, and every 1/16 along a shaped ramp.</summary>
    static long[] Ticks(IReadOnlyList<TempoEvent> events, SignatureMap signatures) {
        var ticks = new SortedSet<long> { 0 };
        for (var i = 0; i < events.Count; i++) {
            ticks.Add(events[i].Tick);
            if (Shaped(events, i)) {
                for (var t = events[i].Tick + ShapedStepTicks; t < events[i + 1].Tick; t += ShapedStepTicks) {
                    ticks.Add(t);
                }
            }
        }

        for (var i = 0; i < signatures.Count; i++) {
            ticks.Add(signatures[i].Tick);
        }

        return [..ticks];
    }

    /// <summary>
    ///     The slope (bpm per quarter note, before Varispeed) of the piece from <paramref name="tick" />: a linear ramp's,
    ///     a shaped ramp's linear piece to <paramref name="next" />, or 0.
    /// </summary>
    static double Slope(TempoMap map, IReadOnlyList<TempoEvent> events, long tick, long? next) {
        var segment = SegmentAt(events, tick);
        if (events[segment].Curve != TempoCurve.Ramp || segment + 1 >= events.Count) {
            return 0;
        }

        var (a, b) = (events[segment], events[segment + 1]);
        if (!Shaped(events, segment)) {
            return (b.Bpm - a.Bpm) / ((double)(b.Tick - a.Tick) / TempoMap.Ppq);
        }

        var end = Math.Min(next ?? b.Tick, b.Tick);
        return (map.BpmAt(end) - map.BpmAt(tick)) / ((double)(end - tick) / TempoMap.Ppq);
    }

    /// <summary>Seconds from a point until its piece reaches quarter note <paramref name="quarter" />.</summary>
    static double Seconds(TempoMapPoint p, double quarter) {
        var dq = quarter - p.Quarter;
        return p.Slope == 0 ? dq * 60 / p.Bpm : 60 / p.Slope * Math.Log(1 + p.Slope * dq / p.Bpm);
    }

    /// <summary>The quarter note a point's piece reaches at <paramref name="sample" />.</summary>
    public static double Quarter(TempoMapPoint p, long sample, int rate) {
        var e = (double)(sample - p.Sample) / rate;
        return p.Slope == 0
            ? p.Quarter + p.Bpm * e / 60
            : p.Quarter + p.Bpm / p.Slope * (Math.Exp(p.Slope * e / 60) - 1);
    }

    static bool Shaped(IReadOnlyList<TempoEvent> events, int i) =>
        events[i].Curve == TempoCurve.Ramp
        && i + 1 < events.Count
        && Math.Abs(events[i].Shape) >= 1e-9
        && events[i].Bpm != events[i + 1].Bpm;

    static int SegmentAt(IReadOnlyList<TempoEvent> events, long tick) {
        var found = 0;
        for (var i = 0; i < events.Count; i++) {
            if (events[i].Tick <= tick) {
                found = i;
            }
        }

        return found;
    }
}
