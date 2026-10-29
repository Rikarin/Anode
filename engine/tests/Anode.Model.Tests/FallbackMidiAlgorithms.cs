using Anode.Contracts;
using Anode.Model;
using Anode.Model.Groove;
using Anode.Model.Time;
using Anode.Model.Transform;
using System.Runtime.CompilerServices;

namespace Anode.Testing;

/// <summary>
///     The MIDI algorithms for tests (linked into the model's and the server's test projects): workstream E's
///     <see cref="TransformEngine" /> and <see cref="Grooves" /> where they are implemented, and small fakes of the
///     documented rules where they are still stubs (docs/08-phase-7.md). Put in place once per test assembly.
/// </summary>
public sealed class FallbackMidiAlgorithms : IMidiAlgorithms {
    public static FallbackMidiAlgorithms Instance { get; } = new();

    [ModuleInitializer]
    internal static void Install() => MidiAlgorithms.Current = Instance;

    static T Either<T>(Func<T> real, Func<T> fake) {
        try {
            return real();
        } catch (NotImplementedException) {
            return fake();
        }
    }

    public TransformResult Transform(
        IReadOnlyList<Note> notes,
        IReadOnlyList<ControlEvent> controls,
        TransformSet set,
        SignatureMap signatures
    ) =>
        Either(
            () => TransformEngine.Apply(notes, controls, set, signatures),
            () => FakeTransform.Apply(notes, controls, set)
        );

    public IReadOnlyList<TransformSet> TransformPresets() =>
        Either(
            TransformEngine.Presets,
            static () => [
                new(
                    TransformPresetIds.FixedVelocity,
                    "Fixed Velocity",
                    [new(TransformField.Status, TransformConditionOp.Equal, 144)],
                    [new(TransformField.Velocity, TransformOperationKind.Fix, 100)],
                    null,
                    TransformMode.SelectAndOperate,
                    0
                ),
                new(
                    TransformPresetIds.Transpose,
                    "Transpose",
                    [new(TransformField.Status, TransformConditionOp.Equal, 144)],
                    [new(TransformField.Pitch, TransformOperationKind.Add, 12)],
                    null,
                    TransformMode.SelectAndOperate,
                    0
                )
            ]
        );

    public GrooveTemplate GrooveFromOnsets(
        string id,
        string name,
        IReadOnlyList<GrooveOnsetTick> onsets,
        long startTick,
        long lengthTicks,
        long stepTicks
    ) =>
        Either(
            () => Grooves.FromOnsets(id, name, onsets, startTick, lengthTicks, stepTicks),
            () => FakeGrooves.FromOnsets(id, name, onsets, startTick, lengthTicks, stepTicks)
        );

    public GrooveTemplate GrooveFromNotes(
        string id,
        string name,
        IReadOnlyList<Note> notes,
        long startTick,
        long lengthTicks,
        long stepTicks
    ) =>
        Either(
            () => Grooves.FromNotes(id, name, notes, startTick, lengthTicks, stepTicks),
            () => FakeGrooves.FromOnsets(
                id,
                name,
                [..notes.Select(n => new GrooveOnsetTick(startTick + n.StartTick, n.Velocity / 127.0))],
                startTick,
                lengthTicks,
                stepTicks
            )
        );

    public (long Tick, double VelocityScale) GrooveTarget(GrooveTemplate groove, long tick, double strength) =>
        Either(() => Grooves.Target(groove, tick, strength), () => FakeGrooves.Target(groove, tick, strength));

    public IReadOnlyList<Note> GrooveQuantize(
        IReadOnlyList<Note> notes,
        long regionStartTick,
        GrooveTemplate groove,
        double strength,
        bool velocity
    ) =>
        Either(
            () => Grooves.Quantize(notes, regionStartTick, groove, strength, velocity),
            () => [
                ..notes.Select(n => {
                        var (tick, scale) = FakeGrooves.Target(groove, regionStartTick + n.StartTick, strength);
                        return n with {
                            StartTick = tick - regionStartTick,
                            Velocity = velocity
                                ? Math.Clamp((int)Math.Round(n.Velocity * (1 + (scale - 1) * strength)), 1, 127)
                                : n.Velocity
                        };
                    }
                )
            ]
        );
}

/// <summary>Groove templates by the documented rules: the strongest onset per step, a step's position by strength.</summary>
static class FakeGrooves {
    public static GrooveTemplate FromOnsets(
        string id,
        string name,
        IReadOnlyList<GrooveOnsetTick> onsets,
        long startTick,
        long lengthTicks,
        long stepTicks
    ) {
        var count = (int)(lengthTicks / stepTicks);
        var points = new GroovePoint[count];
        var strongest = 0.0;
        var found = new GrooveOnsetTick?[count];
        for (var n = 0; n < count; n++) {
            var grid = startTick + n * stepTicks;
            var best = onsets.Where(o => Math.Abs(o.Tick - grid) * 2 < stepTicks)
                .OrderByDescending(static o => o.Strength)
                .ThenBy(static o => o.Tick)
                .Cast<GrooveOnsetTick?>()
                .FirstOrDefault();
            found[n] = best;
            strongest = Math.Max(strongest, best?.Strength ?? 0);
        }

        for (var n = 0; n < count; n++) {
            points[n] = found[n] is { } o && strongest > 0
                ? new(o.Tick - startTick, o.Strength / strongest)
                : new(n * stepTicks, 1);
        }

        return new(id, name, lengthTicks, stepTicks, points);
    }

    public static (long Tick, double VelocityScale) Target(GrooveTemplate groove, long tick, double strength) {
        var k = (long)Math.Round((double)tick / groove.StepTicks, MidpointRounding.AwayFromZero);
        var count = groove.Points.Count;
        var n = (int)((k % count + count) % count);
        var period = (long)Math.Floor((double)k / count);
        var target = period * groove.LengthTicks + groove.Points[n].Tick;
        return (tick + (long)Math.Round((target - tick) * strength, MidpointRounding.AwayFromZero),
            groove.Points[n].VelocityScale);
    }
}

/// <summary>The Transform rules the tests use, by the documented semantics (no randomness, crescendos or maps).</summary>
static class FakeTransform {
    public static TransformResult Apply(
        IReadOnlyList<Note> notes,
        IReadOnlyList<ControlEvent> controls,
        TransformSet set
    ) {
        var operate = set.Mode != TransformMode.SelectOnly;
        var selectedNotes = new List<long>();
        var selectedControls = new List<long>();
        var outNotes = notes.Select(n => {
                if (!Matches(set, Fields(n))) {
                    return n;
                }

                selectedNotes.Add(n.Id);
                if (!operate) {
                    return n;
                }

                var f = Operated(set, Fields(n));
                return n with {
                    StartTick = (long)Math.Max(0, f[TransformField.Position]),
                    LengthTicks = (long)Math.Max(1, f[TransformField.Length]),
                    Channel = (int)Math.Clamp(f[TransformField.Channel], 1, 16),
                    Pitch = (int)Math.Clamp(f[TransformField.Pitch], 0, 127),
                    Velocity = (int)Math.Clamp(f[TransformField.Velocity], 1, 127)
                };
            }
        );
        var outControls = controls.Select(e => {
                if (!Matches(set, Fields(e))) {
                    return e;
                }

                selectedControls.Add(e.Id);
                if (!operate) {
                    return e;
                }

                var f = Operated(set, Fields(e));
                var (min, max) = e.Kind == ControlKind.PitchBend ? (-8192, 8191) : (0, 127);
                return e with {
                    Tick = (long)Math.Max(0, f[TransformField.Position]),
                    Channel = (int)Math.Clamp(f[TransformField.Channel], 1, 16),
                    Number = e.Kind is ControlKind.ControlChange or ControlKind.PolyPressure
                        ? (int)Math.Clamp(f[TransformField.Pitch], 0, 127)
                        : 0,
                    Value = (int)Math.Clamp(f[TransformField.Velocity], min, max)
                };
            }
        );
        return new([..outNotes], [..outControls], selectedNotes, selectedControls);
    }

    static Dictionary<TransformField, double> Fields(Note n) =>
        new() {
            [TransformField.Position] = n.StartTick,
            [TransformField.Length] = n.LengthTicks,
            [TransformField.Channel] = n.Channel,
            [TransformField.Pitch] = n.Pitch,
            [TransformField.Velocity] = n.Velocity,
            [TransformField.Status] = 144,
            [TransformField.Subposition] = 0
        };

    static Dictionary<TransformField, double> Fields(ControlEvent e) =>
        new() {
            [TransformField.Position] = e.Tick,
            [TransformField.Length] = 0,
            [TransformField.Channel] = e.Channel,
            [TransformField.Pitch] = e.Number,
            [TransformField.Velocity] = e.Value,
            [TransformField.Status] = e.Kind switch {
                ControlKind.ControlChange => 176,
                ControlKind.PitchBend => 224,
                ControlKind.ChannelPressure => 208,
                ControlKind.ProgramChange => 192,
                _ => 160
            },
            [TransformField.Subposition] = 0
        };

    static bool Matches(TransformSet set, Dictionary<TransformField, double> f) =>
        set.Conditions.All(c => c.Op switch {
                TransformConditionOp.All => true,
                TransformConditionOp.Equal => f[c.Field] == c.A,
                TransformConditionOp.Unequal => f[c.Field] != c.A,
                TransformConditionOp.LessEqual => f[c.Field] <= c.A,
                TransformConditionOp.GreaterEqual => f[c.Field] >= c.A,
                TransformConditionOp.Inside => f[c.Field] >= c.A && f[c.Field] <= c.B,
                _ => f[c.Field] < c.A || f[c.Field] > c.B
            }
        );

    static Dictionary<TransformField, double> Operated(TransformSet set, Dictionary<TransformField, double> f) {
        foreach (var o in set.Operations.OrderBy(static o => o.Field)) {
            var x = f[o.Field];
            f[o.Field] = Math.Round(
                o.Op switch {
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
                    TransformOperationKind.Quantize => o.A <= 0 ? x : Math.Round(x / o.A) * o.A,
                    _ => x
                }
            );
        }

        return f;
    }
}
