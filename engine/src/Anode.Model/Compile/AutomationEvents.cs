using Anode.Contracts;
using Anode.Ipc;
using Anode.Ipc.Segments;
using Anode.Model.Time;

namespace Anode.Model.Compile;

/// <summary>
///     Compiles automation lanes into segment events (docs/05-phase-4.md, "Automation"). Each automated control of an
///     engine track is a target (<see cref="EngineAutomationTarget" />) whose index <see cref="EngineIds" /> keeps
///     stable; its events carry the value in engine units — linear gain for volume and sends, pan, 0/1 for mute, a
///     parameter's own unit — and the host ramps linearly from one to the next. A segment between two points shapes as
///     a fade does, <c>v0 + (v1 − v0)·x^(2^(−2c))</c> for its curve <c>c</c>; the server samples what is not a straight
///     line in engine units (a curve, a dB ramp in gain, a tempo ramp) at most every
///     <see cref="MaxStepMs" /> ms, finer where linear interpolation would stray more than a small tolerance. Mute steps
///     (<c>Data1</c> = 1) at its points, as do two points on one tick (a jump). Before its first point a lane holds its
///     first value, after its last its last value.
///     <para>
///         A track's volume and mute targets also carry its folder stack's and its VCA's lanes (volumes add in dB,
///         mutes combine), as their strips fold into its own (<see cref="Stacks" />). A track in <c>off</c> mode sends
///         no events (nor do a stack's or VCA's lanes in <c>off</c>); a frozen track keeps only volume, pan, mute and
///         sends. The master's lanes always read; its events live under engine track −1.
///     </para>
/// </summary>
public static class AutomationEvents {
    public const double MaxStepMs = 10;
    const double MinStepMs = 0.5;

    /// <summary>The engine track id the master's automation events are listed under.</summary>
    public const int MasterTrack = -1;

    /// <summary>The automated targets of a strip (null: the master), as lane targets.</summary>
    public static IReadOnlyList<string> Targets(ProjectState state, Track? track) {
        if (track is null) {
            return [..state.LanesOf(null).Select(static l => l.Target).Order(StringComparer.Ordinal)];
        }

        if (track.AutomationMode == AutomationMode.Off) {
            return [];
        }

        var targets = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var lane in state.LanesOf(track.Id)) {
            if (track.FrozenFileId is null || !IsPluginTarget(lane.Target)) {
                targets.Add(lane.Target);
            }
        }

        foreach (var outer in Outers(state, track)) {
            foreach (var lane in state.LanesOf(outer.Id)) {
                if (lane.Target is "volume" or "mute") {
                    targets.Add(lane.Target);
                }
            }
        }

        return [..targets];
    }

    static bool IsPluginTarget(string target) =>
        target.StartsWith("insert:", StringComparison.Ordinal)
        || target.StartsWith("instrument:", StringComparison.Ordinal);

    /// <summary>A track's stack and VCA, when their lanes play (not off).</summary>
    static IEnumerable<Track> Outers(ProjectState state, Track track) {
        if (track.ParentId is not null
            && state.FindTrack(track.ParentId) is { Kind: TrackKind.Stack } stack
            && stack.AutomationMode != AutomationMode.Off) {
            yield return stack;
        }

        if (track.Strip.VcaId is not null
            && state.FindTrack(track.Strip.VcaId) is { Kind: TrackKind.Vca } vca
            && vca.AutomationMode != AutomationMode.Off) {
            yield return vca;
        }
    }

    /// <summary>What target <paramref name="target" /> of engine track <paramref name="engineTrack" /> drives.</summary>
    public static EngineAutomationTarget Describe(int index, int engineTrack, string target) {
        var parts = target.Split(':', 3);
        return parts switch {
            ["send", var id] => new(index, engineTrack, "send", id),
            ["insert", var id, var parameter] => new(index, engineTrack, "insert", id, parameter),
            ["instrument", var parameter] => new(index, engineTrack, "instrument", null, parameter),
            _ => new(index, engineTrack, target)
        };
    }

    /// <summary>The automation events of a strip (null: the master) under engine track <paramref name="engineTrack" />.</summary>
    public static PlaybackEvent[] Compile(
        ProjectState state,
        TempoMap map,
        EngineIds ids,
        Track? track,
        int engineTrack
    ) {
        var targets = Targets(state, track);
        if (targets.Count == 0) {
            return [];
        }

        var events = new List<PlaybackEvent>();
        foreach (var target in targets) {
            var index = ids.Target(track?.Id, target);
            var curves = Components(state, track, target);
            if (target == "mute") {
                Steps(events, state, map, curves, engineTrack, index);
            } else {
                Ramps(events, state, map, curves, engineTrack, index, target);
            }
        }

        return [
            ..events.Select(static (e, i) => (e, i))
                .OrderBy(static x => x.e.SamplePos)
                .ThenBy(static x => x.i)
                .Select(static x => x.e)
        ];
    }

    /// <summary>The curves whose sum (or, for mute, whose maximum) is a target's value: the track's, its stack's, its VCA's.</summary>
    static List<Curve> Components(ProjectState state, Track? track, string target) {
        var own = state.LaneOf(track?.Id, target);
        var strip = track?.Strip ?? state.Master;
        var curves = new List<Curve> { own is null ? Curve.Constant(Value(strip, target)) : new(own.Points) };
        if (track is not null && target is "volume" or "mute") {
            foreach (var outer in OuterTracks(state, track)) {
                var lane = outer.AutomationMode == AutomationMode.Off ? null : state.LaneOf(outer.Id, target);
                curves.Add(lane is null ? Curve.Constant(Value(outer.Strip, target)) : new(lane.Points));
            }
        }

        return curves;
    }

    static IEnumerable<Track> OuterTracks(ProjectState state, Track track) {
        if (track.ParentId is not null && state.FindTrack(track.ParentId) is { Kind: TrackKind.Stack } stack) {
            yield return stack;
        }

        if (track.Strip.VcaId is not null && state.FindTrack(track.Strip.VcaId) is { Kind: TrackKind.Vca } vca) {
            yield return vca;
        }
    }

    /// <summary>A control's static value on a strip, in the lane's unit.</summary>
    static double Value(ChannelStrip strip, string target) =>
        target switch {
            "volume" => strip.VolumeDb,
            "pan" => strip.Pan,
            "mute" => strip.Mute ? 1 : 0,
            _ => 0
        };

    /// <summary>Mute: a step wherever the combined value changes (and at the start).</summary>
    static void Steps(
        List<PlaybackEvent> events,
        ProjectState state,
        TempoMap map,
        List<Curve> curves,
        int track,
        int index
    ) {
        var ticks = curves.SelectMany(static c => c.Points.Select(static p => p.Tick)).Append(0).Distinct().Order();
        double? last = null;
        foreach (var tick in ticks) {
            var value = curves.Max(c => c.Right(tick, true));
            if (last != value) {
                events.Add(Event(map.SampleAt(tick, state.SampleRate), track, index, value, true));
                last = value;
            }
        }
    }

    /// <summary>A continuous target: events at every point, and where the line between bends, sampled.</summary>
    static void Ramps(
        List<PlaybackEvent> events,
        ProjectState state,
        TempoMap map,
        List<Curve> curves,
        int track,
        int index,
        string target
    ) {
        var ticks = curves.SelectMany(static c => c.Points.Select(static p => p.Tick)).Distinct().Order().ToArray();
        if (ticks.Length == 0) {
            return;
        }

        var sampler = new RampSampler(events, map, state.SampleRate, curves, track, index, target);
        // The value before the first point, from the start.
        events.Add(Event(0, track, index, sampler.Engine(sampler.Combined(ticks[0], false)), true));
        for (var i = 0; i < ticks.Length; i++) {
            sampler.Point(ticks[i], i > 0 || ticks[i] > 0);
            if (i + 1 < ticks.Length) {
                sampler.Between(ticks[i], ticks[i + 1]);
            }
        }
    }

    /// <summary>
    ///     Samples one continuous target into events: its value in engine units at each point, and inside a segment
    ///     that is no straight line in engine units, at most every <see cref="MaxStepMs" /> ms and finer where the line
    ///     between two samples strays from the curve by more than the tolerance (0.01 dB for gains).
    /// </summary>
    sealed class RampSampler(
        List<PlaybackEvent> events,
        TempoMap map,
        int rate,
        List<Curve> curves,
        int track,
        int index,
        string target
    ) {
        readonly bool gainLike = target == "volume" || target.StartsWith("send:", StringComparison.Ordinal);

        readonly double tolerance = target == "volume" || target.StartsWith("send:", StringComparison.Ordinal)
            ? 0.01
            : 1e-4 * Math.Max(1, curves.Max(static c => c.Magnitude));

        long from;
        long to;

        public double Combined(long tick, bool right) => Sum(curves, c => right ? c.Right(tick, false) : c.Left(tick));

        public float Engine(double v) => gainLike ? Units.DbToGain(v) : (float)v;

        /// <summary>The events at a point: the value arriving (unless it is the start), then a step when it jumps.</summary>
        public void Point(long tick, bool arriving) {
            var sample = map.SampleAt(tick, rate);
            var left = Combined(tick, false);
            var right = Combined(tick, true);
            if (arriving) {
                events.Add(Event(sample, track, index, Engine(left), false));
            }

            if (!right.Equals(left)) {
                events.Add(Event(sample, track, index, Engine(right), true));
            }
        }

        /// <summary>The events inside a segment, when it bends.</summary>
        public void Between(long start, long end) {
            (from, to) = (start, end);
            var s0 = map.SampleAt(from, rate);
            var s1 = map.SampleAt(to, rate);
            var v0 = Engine(Combined(from, true));
            var v1 = Engine(Combined(to, false));
            if (s1 - s0 < 2 || IsLine(s0, s1, v0, v1)) {
                return;
            }

            var pieces = (int)Math.Ceiling((s1 - s0) / (rate * MaxStepMs / 1000));
            var previous = (Sample: s0, Value: v0);
            for (var k = 1; k <= pieces; k++) {
                var s = k == pieces ? s1 : s0 + (long)Math.Round(k * (double)(s1 - s0) / pieces);
                var v = k == pieces ? v1 : At(s);
                Refine(previous.Sample, previous.Value, s, v, 0);
                if (k < pieces) {
                    events.Add(Event(s, track, index, v, false));
                }

                previous = (s, v);
            }
        }

        float At(long sample) => Engine(Combined(Math.Clamp(map.TickAtSample(sample, rate), from, to), true));

        bool IsLine(long a, long b, float va, float vb) {
            foreach (var f in (ReadOnlySpan<double>)[0.25, 0.5, 0.75]) {
                var s = a + (long)((b - a) * f);
                if (Math.Abs(Error(At(s), va + (vb - va) * (float)((double)(s - a) / (b - a)))) > 1e-6) {
                    return false;
                }
            }

            return true;
        }

        void Refine(long a, float va, long b, float vb, int depth) {
            if (depth > 8 || b - a < rate * MinStepMs / 1000) {
                return;
            }

            var m = (a + b) / 2;
            var vm = At(m);
            if (Math.Abs(Error(vm, (va + vb) / 2)) <= tolerance) {
                return;
            }

            Refine(a, va, m, vm, depth + 1);
            events.Add(Event(m, track, index, vm, false));
            Refine(m, vm, b, vb, depth + 1);
        }

        /// <summary>How far a value strays from the line: in dB for gains, in their own units for the rest.</summary>
        double Error(float actual, float line) =>
            gainLike ? 20 * Math.Log10(Math.Max(actual, 1e-7f) / Math.Max(line, 1e-7f)) : actual - line;
    }

    static double Sum(List<Curve> curves, Func<Curve, double> value) {
        if (curves.Count == 1) {
            return value(curves[0]);
        }

        // Volumes add in dB, −∞ absorbing.
        var total = value(curves[0]);
        for (var i = 1; i < curves.Count; i++) {
            total = Stacks.Add(total, value(curves[i]));
        }

        return total;
    }

    static PlaybackEvent Event(long sample, int track, int index, double value, bool step) =>
        new() {
            SamplePos = sample,
            Track = track,
            Kind = PlaybackEventKind.Automation,
            Data1 = (byte)(step ? 1 : 0),
            Value = (float)value,
            Target = index
        };

    /// <summary>A lane's value over time (or a constant one).</summary>
    sealed class Curve(IReadOnlyList<AutomationPoint> points) {
        public IReadOnlyList<AutomationPoint> Points { get; } = points;

        public double Magnitude => Points.Count == 0 ? 0 : Points.Max(static p => Math.Abs(p.Value));

        double constant;

        public static Curve Constant(double value) => new([]) { constant = value };

        /// <summary>The value just after <paramref name="tick" /> (the second of two points on it).</summary>
        public double Right(long tick, bool step) {
            if (Points.Count == 0) {
                return constant;
            }

            var i = LastAtOrBefore(tick);
            if (i < 0) {
                return Points[0].Value;
            }

            if (step || i == Points.Count - 1 || Points[i + 1].Tick == Points[i].Tick) {
                return Points[i].Value;
            }

            return Shape(Points[i], Points[i + 1], tick);
        }

        /// <summary>The value just before <paramref name="tick" /> (the first of two points on it).</summary>
        public double Left(long tick) {
            if (Points.Count == 0) {
                return constant;
            }

            var i = FirstAtOrAfter(tick);
            if (i < Points.Count && Points[i].Tick == tick) {
                return Points[i].Value;
            }

            return Right(tick, false);
        }

        int LastAtOrBefore(long tick) {
            int lo = 0, hi = Points.Count - 1, found = -1;
            while (lo <= hi) {
                var mid = (lo + hi) / 2;
                if (Points[mid].Tick <= tick) {
                    found = mid;
                    lo = mid + 1;
                } else {
                    hi = mid - 1;
                }
            }

            return found;
        }

        int FirstAtOrAfter(long tick) {
            int lo = 0, hi = Points.Count;
            while (lo < hi) {
                var mid = (lo + hi) / 2;
                if (Points[mid].Tick < tick) {
                    lo = mid + 1;
                } else {
                    hi = mid;
                }
            }

            return lo;
        }

        static double Shape(AutomationPoint a, AutomationPoint b, long tick) {
            var x = Math.Clamp((double)(tick - a.Tick) / (b.Tick - a.Tick), 0, 1);
            return a.Value + (b.Value - a.Value) * Shaped(x, a.Curve);
        }
    }

    /// <summary>
    ///     A segment's shape at position <paramref name="x" /> (0…1) for curve <paramref name="curve" /> (−1…1, 0
    ///     linear).
    /// </summary>
    public static double Shaped(double x, double curve) => curve == 0 ? x : Math.Pow(x, Math.Pow(2, -2 * curve));
}
