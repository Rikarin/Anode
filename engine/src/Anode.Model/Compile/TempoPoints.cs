using Anode.Contracts;
using Anode.Ipc.Segments;
using Anode.Model.Time;

namespace Anode.Model.Compile;

/// <summary>
///     The compiled tempo map of a segment (format 4, docs/07-phase-6.md): a point at the start, at every tempo and
///     signature change, and every 1/16 of a quarter note along a shaped ramp, in playback samples with Varispeed folded
///     in. A step or a linear ramp (linear in quarter notes, as the tempo track's) is exact between points; a shaped
///     ramp is sampled into linear pieces. Each point carries the signature and the bar line at or before it.
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

        var ordered = ticks.ToArray();
        var points = new List<TempoMapPoint>(ordered.Length);
        for (var k = 0; k < ordered.Length; k++) {
            var tick = ordered[k];
            var segment = SegmentAt(events, tick);
            double slope = 0;
            if (events[segment].Curve == TempoCurve.Ramp && segment + 1 < events.Count) {
                var (a, b) = (events[segment], events[segment + 1]);
                if (Shaped(events, segment)) {
                    var next = k + 1 < ordered.Length ? Math.Min(ordered[k + 1], b.Tick) : b.Tick;
                    slope = (map.BpmAt(next) - map.BpmAt(tick)) / ((double)(next - tick) / TempoMap.Ppq);
                } else {
                    slope = (b.Bpm - a.Bpm) / ((double)(b.Tick - a.Tick) / TempoMap.Ppq);
                }
            }

            var signature = signatures.At(tick);
            var point = new TempoMapPoint {
                Sample = map.SampleAt(tick, state.SampleRate),
                Quarter = (double)tick / TempoMap.Ppq,
                Bpm = map.BpmAt(tick) * speed,
                Slope = slope * speed,
                BarQuarter = (double)signatures.BarLineAtOrBefore(tick) / TempoMap.Ppq,
                Numerator = (short)signature.Numerator,
                Denominator = (short)signature.Denominator
            };
            // Samples strictly increase: of points that round to one sample, the later stands.
            if (points.Count > 0 && points[^1].Sample >= point.Sample) {
                points[^1] = point with { Sample = points[^1].Sample };
                continue;
            }

            points.Add(point);
        }

        return [..points];
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
