using Anode.Contracts;
using Anode.Model.Time;

namespace Anode.Model.Transform;

/// <summary>
///     What applying a Transform set did: every event given, in the order given (the matching ones changed, unless the
///     mode is <c>selectOnly</c>), and the ids of the events that matched its conditions.
/// </summary>
public sealed record TransformResult(
    IReadOnlyList<Note> Notes,
    IReadOnlyList<ControlEvent> Controls,
    IReadOnlyList<long> SelectedNoteIds,
    IReadOnlyList<long> SelectedControlIds
);

/// <summary>
///     The MIDI Transform engine (docs/08-phase-7.md, "Rules the contracts imply"): conditions ANDed; operations in the
///     order position, length, channel, pitch, velocity; random operations seeded by the set's seed and the event's id;
///     results clamped to each field's range (a note's length at least 1 tick, its position at least 0). Pure and
///     deterministic; the editor's TypeScript port matches it on the shared <c>TransformVectors.json</c>.
/// </summary>
/// <remarks>
///     <para>
///         The rules, beyond the contract's (<see cref="TransformOperationKind" />), that the port reproduces:
///     </para>
///     <list type="bullet">
///         <item>
///             An event's fields are doubles: a note's position, length, channel, pitch, velocity, status 144; a
///             controller event's position, length 0, channel, pitch (a control change's controller, a poly pressure's
///             key, else 0), velocity (its value) and status (176, 224, 208, 192, 160); subposition is the position less
///             the bar line at or before it. A condition compares a field with <c>a</c> (and <c>b</c>) as written.
///         </item>
///         <item>
///             The selection's first and last positions P0 and P1 are the least and greatest position of the matching
///             events, before any operation. Crescendos ramp over them by each event's original position:
///             t = (p − P0) ÷ (P1 − P0), 0 when P0 = P1. On position, <c>multiply</c>, <c>divide</c> and <c>scale</c>
///             measure from P0 (x' = P0 + op(x − P0)) and <c>flip</c> mirrors around the selection's middle shifted by
///             a (x' = P0 + P1 + 2a − x), so Double Speed and Reverse Position keep the selection in place.
///         </item>
///         <item>
///             <c>exponent</c> normalises over the field's range: channel 1…16, pitch 0…127, velocity 1…127 for notes,
///             −8192…8191 for pitch bend, 0…127 for the other controller events; position and length over the matching
///             events' least…greatest value of the field. a ≤ 0 leaves x.
///         </item>
///         <item>
///             <c>divide</c> by 0 and <c>quantize</c> by a ≤ 0 leave x; <c>range</c> clamps to min(a, b)…max(a, b);
///             <c>quantize</c> is round(x ÷ a) × a; <c>useMap</c> maps round(x) when it is 0…127, else leaves x.
///         </item>
///         <item>
///             Every result is rounded half away from zero, then clamped: position ≥ 0, a note's length ≥ 1, channel
///             1…16, pitch 0…127, velocity as for <c>exponent</c>. A controller event has no length, and only control
///             changes and poly pressure have a pitch: operations on a field an event lacks leave it.
///         </item>
///         <item>
///             Randomness: each matching event has its own SplitMix64 generator, state = seed XOR (id ×
///             0xD1B54A32D192ED03) (64-bit unsigned, wrapping); next(): state += 0x9E3779B97F4A7C15; z = state;
///             z = (z XOR z >> 30) × 0xBF58476D1CE4E5B9; z = (z XOR z >> 27) × 0x94D049BB133111EB; z XOR z >> 31.
///             Every <c>random</c> and <c>plusMinusRandom</c> operation draws once, in field order, even on a field the
///             event lacks. An integer in lo…hi is lo + next() mod (hi − lo + 1): <c>random</c> takes lo = round(min(a,
///             b)), hi = round(max(a, b)); <c>plusMinusRandom</c> adds one in −round(|a|)…round(|a|).
///         </item>
///     </list>
///     A set is refused (<see cref="ArgumentException" />) when an operation is on <c>status</c> or
///     <c>subposition</c>, two are on one field, a value is not finite, or its map is not 128 values 0…127.
/// </remarks>
public static class TransformEngine {
    public const int NoteStatus = 144;
    public const int PolyPressureStatus = 160;
    public const int ControlChangeStatus = 176;
    public const int ProgramChangeStatus = 192;
    public const int ChannelPressureStatus = 208;
    public const int PitchBendStatus = 224;

    const ulong IdMultiplier = 0xD1B54A32D192ED03UL;
    const ulong Gamma = 0x9E3779B97F4A7C15UL;

    /// <summary>
    ///     Applies <paramref name="set" /> to <paramref name="notes" /> and <paramref name="controls" />, whose positions
    ///     (<c>startTick</c>, <c>tick</c>) are project ticks here: the caller adds each region's start before and
    ///     subtracts it after, so position conditions, subposition and crescendos span regions.
    ///     <paramref name="signatures" /> places bar lines for <c>subposition</c>.
    /// </summary>
    public static TransformResult Apply(
        IReadOnlyList<Note> notes,
        IReadOnlyList<ControlEvent> controls,
        TransformSet set,
        SignatureMap signatures
    ) {
        var operations = Check(set);
        Event[] events = [..notes.Select(Event.Of), ..controls.Select(Event.Of)];
        var (selected, selection) = Select(events, set.Conditions, signatures);
        if (set.Mode != TransformMode.SelectOnly) {
            foreach (var i in selected) {
                events[i] = Operate(events[i], operations, set, selection);
            }
        }

        var resultNotes = new Note[notes.Count];
        for (var i = 0; i < notes.Count; i++) {
            resultNotes[i] = events[i].ToNote(notes[i]);
        }

        var resultControls = new ControlEvent[controls.Count];
        for (var i = 0; i < controls.Count; i++) {
            resultControls[i] = events[notes.Count + i].ToControl(controls[i]);
        }

        return new(
            resultNotes,
            resultControls,
            [..selected.Where(i => i < notes.Count).Select(i => notes[i].Id)],
            [..selected.Where(i => i >= notes.Count).Select(i => controls[i - notes.Count].Id)]
        );
    }

    /// <summary>The indices of the events that match, ascending, and their extent.</summary>
    static (List<int> Indices, Selection Extent) Select(
        Event[] events,
        IReadOnlyList<TransformCondition> conditions,
        SignatureMap signatures
    ) {
        var indices = new List<int>();
        var extent = new Selection();
        for (var i = 0; i < events.Length; i++) {
            if (Matches(events[i], conditions, signatures)) {
                indices.Add(i);
                extent.Add(events[i]);
            }
        }

        return (indices, extent);
    }

    /// <summary>
    ///     The factory presets, as data, in menu order; their ids are <see cref="TransformPresetIds" />, with seed 0
    ///     (the editor's Transform window draws a new seed when it applies a random one).
    /// </summary>
    public static IReadOnlyList<TransformSet> Presets() => FactoryPresets;

    /// <summary>
    ///     The first <paramref name="count" /> numbers of the generator of event <paramref name="id" /> under
    ///     <paramref name="seed" /> (for the shared vectors, which pin them for the editor's port).
    /// </summary>
    public static ulong[] Draws(long seed, long id, int count) {
        var random = new SplitMix64(seed, id);
        var draws = new ulong[count];
        for (var i = 0; i < count; i++) {
            draws[i] = random.Next();
        }

        return draws;
    }


    static readonly IReadOnlyList<TransformSet> FactoryPresets = [
        Preset(
            TransformPresetIds.Humanize,
            "Humanize",
            Op(TransformField.Position, TransformOperationKind.PlusMinusRandom, 10),
            Op(TransformField.Length, TransformOperationKind.PlusMinusRandom, 10),
            Op(TransformField.Velocity, TransformOperationKind.PlusMinusRandom, 10)
        ),
        Preset(
            TransformPresetIds.Quantize,
            "Quantize Notes (1/16)",
            Op(TransformField.Position, TransformOperationKind.Quantize, TempoMap.Ppq / 4.0)
        ),
        Preset(
            TransformPresetIds.Crescendo,
            "Crescendo",
            Op(TransformField.Velocity, TransformOperationKind.Crescendo, 40, 120)
        ),
        Preset(
            TransformPresetIds.FixedVelocity,
            "Fixed Velocity",
            Op(TransformField.Velocity, TransformOperationKind.Fix, 100)
        ),
        Preset(
            TransformPresetIds.Transpose,
            "Transpose (+12)",
            Op(TransformField.Pitch, TransformOperationKind.Add, 12)
        ),
        Preset(
            TransformPresetIds.DoubleSpeed,
            "Double Speed",
            Op(TransformField.Position, TransformOperationKind.Divide, 2),
            Op(TransformField.Length, TransformOperationKind.Divide, 2)
        ),
        Preset(
            TransformPresetIds.HalfSpeed,
            "Half Speed",
            Op(TransformField.Position, TransformOperationKind.Multiply, 2),
            Op(TransformField.Length, TransformOperationKind.Multiply, 2)
        ),
        Preset(
            TransformPresetIds.ReversePosition,
            "Reverse Position",
            Op(TransformField.Position, TransformOperationKind.Flip)
        ),
        Preset(
            TransformPresetIds.ReversePitch,
            "Reverse Pitch (around C3)",
            Op(TransformField.Pitch, TransformOperationKind.Flip, 60)
        ),
        Preset(
            TransformPresetIds.LengthLimits,
            "Length Limits",
            Op(TransformField.Length, TransformOperationKind.Range, TempoMap.Ppq / 8.0, TempoMap.Ppq * 4)
        ),
        Preset(
            TransformPresetIds.RandomVelocity,
            "Random Velocity",
            Op(TransformField.Velocity, TransformOperationKind.Random, 60, 120)
        ),
        Preset(
            TransformPresetIds.VelocityCompress,
            "Velocity Compress",
            Op(TransformField.Velocity, TransformOperationKind.Scale, 0.5, 48)
        )
    ];

    static TransformOperation Op(TransformField field, TransformOperationKind op, double a = 0, double b = 0) =>
        new(field, op, a, b);

    static TransformSet Preset(string id, string name, params TransformOperation[] operations) =>
        new(
            id,
            name,
            [new(TransformField.Status, TransformConditionOp.Equal, NoteStatus)],
            operations,
            null,
            TransformMode.SelectAndOperate,
            0
        );

    /// <summary>The set's operations by field (position … velocity), after checking the set.</summary>
    static TransformOperation?[] Check(TransformSet set) {
        ArgumentNullException.ThrowIfNull(set);
        if (set.Conditions.Any(static c => !double.IsFinite(c.A) || !double.IsFinite(c.B))
            || set.Operations.Any(static o => !double.IsFinite(o.A) || !double.IsFinite(o.B))) {
            throw new ArgumentException("A condition's and an operation's values must be finite.", nameof(set));
        }

        var byField = new TransformOperation?[5];
        foreach (var o in set.Operations) {
            if (o.Field is TransformField.Status or TransformField.Subposition) {
                throw new ArgumentException($"There is no operation on {o.Field}.", nameof(set));
            }

            if (byField[(int)o.Field] is not null) {
                throw new ArgumentException($"At most one operation on {o.Field}.", nameof(set));
            }

            byField[(int)o.Field] = o;
        }

        if (set.Map is { } map && (map.Count != 128 || map.Any(static v => v is < 0 or > 127))) {
            throw new ArgumentException("A map is 128 values 0…127.", nameof(set));
        }

        return byField;
    }

    static bool Matches(in Event e, IReadOnlyList<TransformCondition> conditions, SignatureMap signatures) {
        foreach (var c in conditions) {
            if (c.Op == TransformConditionOp.All) {
                continue;
            }

            var x = c.Field switch {
                TransformField.Position => e.Position,
                TransformField.Length => e.Length,
                TransformField.Channel => e.Channel,
                TransformField.Pitch => e.Pitch,
                TransformField.Velocity => e.Velocity,
                TransformField.Status => e.Status,
                TransformField.Subposition => e.Position - signatures.BarLineAtOrBefore((long)e.Position),
                _ => throw new ArgumentOutOfRangeException(nameof(conditions))
            };
            var match = c.Op switch {
                TransformConditionOp.Equal => x == c.A,
                TransformConditionOp.Unequal => x != c.A,
                TransformConditionOp.LessEqual => x <= c.A,
                TransformConditionOp.GreaterEqual => x >= c.A,
                TransformConditionOp.Inside => x >= c.A && x <= c.B,
                TransformConditionOp.Outside => x < c.A || x > c.B,
                _ => throw new ArgumentOutOfRangeException(nameof(conditions))
            };
            if (!match) {
                return false;
            }
        }

        return true;
    }

    static Event Operate(Event e, TransformOperation?[] operations, TransformSet set, Selection selection) {
        var random = new SplitMix64(set.Seed, e.Id);
        var originalPosition = e.Position;
        for (var f = 0; f < operations.Length; f++) {
            if (operations[f] is not { } o) {
                continue;
            }

            var field = (TransformField)f;
            var draw = Draw(o, ref random);
            if (e.Has(field)) {
                var value = Compute(o, field, draw, originalPosition, selection, set.Map, e);
                var (lowest, highest) = e.Bounds(field);
                e = e.With(field, Math.Clamp(Round(value), lowest, highest));
            }
        }

        return e;
    }

    /// <summary>A random operation's number (one draw each); 0 for the others, which draw nothing.</summary>
    static long Draw(TransformOperation o, ref SplitMix64 random) =>
        o.Op switch {
            TransformOperationKind.Random => random.Between(Round(Math.Min(o.A, o.B)), Round(Math.Max(o.A, o.B))),
            TransformOperationKind.PlusMinusRandom => random.Between(-Round(Math.Abs(o.A)), Round(Math.Abs(o.A))),
            _ => 0
        };

    /// <summary>The operation's result on <paramref name="e" />'s <paramref name="field" />, before rounding.</summary>
    static double Compute(
        TransformOperation o,
        TransformField field,
        long draw,
        double position,
        Selection selection,
        IReadOnlyList<int>? map,
        in Event e
    ) {
        var x = e.Get(field);
        if (field == TransformField.Position
            && o.Op is TransformOperationKind.Flip
                or TransformOperationKind.Multiply
                or TransformOperationKind.Divide
                or TransformOperationKind.Scale) {
            return ComputePosition(o, x, selection);
        }

        return o.Op switch {
            TransformOperationKind.Thru => x,
            TransformOperationKind.Fix => o.A,
            TransformOperationKind.Add => x + o.A,
            TransformOperationKind.Subtract => x - o.A,
            TransformOperationKind.Min => Math.Max(x, o.A),
            TransformOperationKind.Max => Math.Min(x, o.A),
            TransformOperationKind.Flip => 2 * o.A - x,
            TransformOperationKind.Multiply => x * o.A,
            TransformOperationKind.Divide => o.A == 0 ? x : x / o.A,
            TransformOperationKind.Scale => x * o.A + o.B,
            TransformOperationKind.Range => Math.Clamp(x, Math.Min(o.A, o.B), Math.Max(o.A, o.B)),
            TransformOperationKind.Random => draw,
            TransformOperationKind.PlusMinusRandom => x + draw,
            TransformOperationKind.Quantize => o.A <= 0 ? x : Round(x / o.A) * o.A,
            TransformOperationKind.Exponent => Exponent(o.A, x, Extent(field, selection, e)),
            TransformOperationKind.Crescendo => Ramp(o, position, selection),
            TransformOperationKind.RelativeCrescendo => x + Ramp(o, position, selection),
            TransformOperationKind.UseMap => Map(x, map),
            _ => throw new ArgumentOutOfRangeException(nameof(o))
        };
    }

    /// <summary>
    ///     Flip, multiply, divide and scale on position, measured from the selection: multiply, divide and scale from
    ///     its first position P0, flip around its middle shifted by a.
    /// </summary>
    static double ComputePosition(TransformOperation o, double x, Selection selection) {
        var p0 = selection.FirstPosition;
        return o.Op switch {
            TransformOperationKind.Flip => p0 + selection.LastPosition + 2 * o.A - x,
            TransformOperationKind.Multiply => p0 + (x - p0) * o.A,
            TransformOperationKind.Divide => o.A == 0 ? x : p0 + (x - p0) / o.A,
            _ => p0 + (x - p0) * o.A + o.B
        };
    }

    /// <summary>The range <c>exponent</c> normalises over: the selection's for position and length, else the field's.</summary>
    static (double Lowest, double Highest) Extent(TransformField field, Selection selection, in Event e) =>
        field switch {
            TransformField.Position => (selection.FirstPosition, selection.LastPosition),
            TransformField.Length => (selection.ShortestLength, selection.LongestLength),
            _ => e.Bounds(field)
        };

    /// <summary>x curved over <paramref name="range" /> by <paramref name="exponent" />; x itself when either is empty.</summary>
    static double Exponent(double exponent, double x, (double Lowest, double Highest) range) {
        var (lo, hi) = range;
        if (exponent <= 0 || hi <= lo) {
            return x;
        }

        var n = Math.Clamp((x - lo) / (hi - lo), 0, 1);
        return lo + Math.Pow(n, exponent) * (hi - lo);
    }

    /// <summary>round(x) looked up in <paramref name="map" /> (null: the identity) when it is 0…127, else x.</summary>
    static double Map(double x, IReadOnlyList<int>? map) {
        var v = Round(x);
        if (v is < 0 or > 127) {
            return x;
        }

        return map is null ? v : map[(int)v];
    }

    /// <summary>a at the selection's first position to b at its last, linearly by <paramref name="position" />.</summary>
    static double Ramp(TransformOperation o, double position, Selection selection) {
        var span = selection.LastPosition - selection.FirstPosition;
        var t = span <= 0 ? 0 : (position - selection.FirstPosition) / span;
        return o.A + (o.B - o.A) * t;
    }

    /// <summary>Rounds half away from zero.</summary>
    static long Round(double x) => (long)Math.Round(x, MidpointRounding.AwayFromZero);

    /// <summary>The matching events' extent: their least and greatest position and (of those with one) length.</summary>
    sealed class Selection {
        public double FirstPosition { get; private set; } = double.PositiveInfinity;
        public double LastPosition { get; private set; } = double.NegativeInfinity;
        public double ShortestLength { get; private set; } = double.PositiveInfinity;
        public double LongestLength { get; private set; } = double.NegativeInfinity;

        public void Add(in Event e) {
            FirstPosition = Math.Min(FirstPosition, e.Position);
            LastPosition = Math.Max(LastPosition, e.Position);
            if (e.IsNote) {
                ShortestLength = Math.Min(ShortestLength, e.Length);
                LongestLength = Math.Max(LongestLength, e.Length);
            }
        }
    }

    /// <summary>SplitMix64 over (seed, event id): the generator the editor's port reproduces bit for bit.</summary>
    struct SplitMix64(long seed, long id) {
        ulong state = (ulong)seed ^ unchecked((ulong)id * IdMultiplier);

        public ulong Next() {
            unchecked {
                state += Gamma;
                var z = state;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        /// <summary>An integer in <paramref name="lo" />…<paramref name="hi" />: lo + next() mod (hi − lo + 1).</summary>
        public long Between(long lo, long hi) {
            var next = Next();
            return lo + (long)(next % (ulong)(hi - lo + 1));
        }
    }

    /// <summary>An event's fields as the Transform window sees them.</summary>
    readonly record struct Event(
        long Id,
        bool IsNote,
        ControlKind Kind,
        double Position,
        double Length,
        double Channel,
        double Pitch,
        double Velocity,
        double Status
    ) {
        public static Event Of(Note n) =>
            new(n.Id, true, default, n.StartTick, n.LengthTicks, n.Channel, n.Pitch, n.Velocity, NoteStatus);

        public static Event Of(ControlEvent c) =>
            new(
                c.Id,
                false,
                c.Kind,
                c.Tick,
                0,
                c.Channel,
                HasNumber(c.Kind) ? c.Number : 0,
                c.Value,
                c.Kind switch {
                    ControlKind.ControlChange => ControlChangeStatus,
                    ControlKind.PitchBend => PitchBendStatus,
                    ControlKind.ChannelPressure => ChannelPressureStatus,
                    ControlKind.ProgramChange => ProgramChangeStatus,
                    ControlKind.PolyPressure => PolyPressureStatus,
                    _ => throw new ArgumentOutOfRangeException(nameof(c))
                }
            );

        static bool HasNumber(ControlKind kind) => kind is ControlKind.ControlChange or ControlKind.PolyPressure;

        public bool Has(TransformField field) =>
            field switch {
                TransformField.Length => IsNote,
                TransformField.Pitch => IsNote || HasNumber(Kind),
                _ => true
            };

        public double Get(TransformField field) =>
            field switch {
                TransformField.Position => Position,
                TransformField.Length => Length,
                TransformField.Channel => Channel,
                TransformField.Pitch => Pitch,
                TransformField.Velocity => Velocity,
                _ => throw new ArgumentOutOfRangeException(nameof(field))
            };

        public (long Lowest, long Highest) Bounds(TransformField field) =>
            field switch {
                TransformField.Position => (0, long.MaxValue),
                TransformField.Length => (1, long.MaxValue),
                TransformField.Channel => (1, 16),
                TransformField.Pitch => (0, 127),
                TransformField.Velocity when IsNote => (1, 127),
                TransformField.Velocity when Kind == ControlKind.PitchBend => (-8192, 8191),
                TransformField.Velocity => (0, 127),
                _ => throw new ArgumentOutOfRangeException(nameof(field))
            };

        public Event With(TransformField field, long value) =>
            field switch {
                TransformField.Position => this with { Position = value },
                TransformField.Length => this with { Length = value },
                TransformField.Channel => this with { Channel = value },
                TransformField.Pitch => this with { Pitch = value },
                TransformField.Velocity => this with { Velocity = value },
                _ => throw new ArgumentOutOfRangeException(nameof(field))
            };

        public Note ToNote(Note n) =>
            n with {
                StartTick = (long)Position,
                LengthTicks = (long)Length,
                Channel = (int)Channel,
                Pitch = (int)Pitch,
                Velocity = (int)Velocity
            };

        public ControlEvent ToControl(ControlEvent c) =>
            c with {
                Tick = (long)Position,
                Channel = (int)Channel,
                Number = HasNumber(Kind) ? (int)Pitch : c.Number,
                Value = (int)Velocity
            };
    }
}

/// <summary>The factory Transform presets' ids (<c>factory.</c> plus the preset); workstream E may add more.</summary>
public static class TransformPresetIds {
    public const string Humanize = "factory.humanize";
    public const string Quantize = "factory.quantize";
    public const string Crescendo = "factory.crescendo";
    public const string FixedVelocity = "factory.fixedVelocity";
    public const string Transpose = "factory.transpose";
    public const string DoubleSpeed = "factory.doubleSpeed";
    public const string HalfSpeed = "factory.halfSpeed";
    public const string ReversePosition = "factory.reversePosition";
    public const string ReversePitch = "factory.reversePitch";
    public const string LengthLimits = "factory.lengthLimits";

    /// <summary>Velocities drawn anew, 60…120.</summary>
    public const string RandomVelocity = "factory.randomVelocity";

    /// <summary>Velocities halved around 96: x × 0.5 + 48.</summary>
    public const string VelocityCompress = "factory.velocityCompress";
}
