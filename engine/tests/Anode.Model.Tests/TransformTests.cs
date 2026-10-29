using Anode.Contracts;
using Anode.Model.Time;
using Anode.Model.Transform;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Anode.Model.Tests;

/// <summary>
///     The MIDI Transform engine (docs/08-phase-7.md): rules worked by hand, and the vectors the server and the editor
///     share (<c>TransformVectors.json</c>, rewritten with <c>ANODE_UPDATE_VECTORS=1</c>).
/// </summary>
public sealed class TransformTests {
    static readonly SignatureMap FourFour = SignatureMap.Constant(4, 4);

    static Note N(long id, int pitch, int velocity, long start, long length = 240, int channel = 1) =>
        new(id, pitch, velocity, channel, start, length, 0);

    static ControlEvent C(long id, ControlKind kind, int number, int value, long tick, int channel = 1) =>
        new(id, kind, channel, number, value, tick);

    static TransformSet Set(
        IReadOnlyList<TransformCondition> conditions,
        IReadOnlyList<TransformOperation> operations,
        TransformMode mode = TransformMode.SelectAndOperate,
        long seed = 0,
        IReadOnlyList<int>? map = null,
        string id = "custom"
    ) =>
        new(id, id, conditions, operations, map, mode, seed);

    static TransformCondition Cond(TransformField field, TransformConditionOp op, double a = 0, double b = 0) =>
        new(field, op, a, b);

    static TransformOperation Op(TransformField field, TransformOperationKind op, double a = 0, double b = 0) =>
        new(field, op, a, b);

    static readonly TransformCondition Notes = Cond(TransformField.Status, TransformConditionOp.Equal, 144);

    [Fact]
    public void Conditions_are_anded_and_select_notes_and_controls_alike() {
        Note[] notes = [N(1, 60, 80, 0), N(2, 64, 90, 480), N(3, 67, 100, 960)];
        ControlEvent[] controls = [C(10, ControlKind.ControlChange, 64, 127, 480)];
        var set = Set(
            [
                Cond(TransformField.Pitch, TransformConditionOp.GreaterEqual, 62),
                Cond(TransformField.Position, TransformConditionOp.Inside, 0, 500)
            ],
            [Op(TransformField.Velocity, TransformOperationKind.Fix, 1)]
        );

        var result = TransformEngine.Apply(notes, controls, set, FourFour);

        Assert.Equal([2L], result.SelectedNoteIds);
        Assert.Equal([10L], result.SelectedControlIds); // a controller's pitch is its number: 64
        Assert.Equal([80, 1, 100], result.Notes.Select(static n => n.Velocity));
        Assert.Equal(1, result.Controls[0].Value); // a controller's velocity is its value
    }

    [Fact]
    public void Select_only_changes_nothing() {
        Note[] notes = [N(1, 60, 80, 0)];
        var result = TransformEngine.Apply(
            notes,
            [],
            Set([Notes], [Op(TransformField.Pitch, TransformOperationKind.Add, 12)], TransformMode.SelectOnly),
            FourFour
        );
        Assert.Equal(notes, result.Notes);
        Assert.Equal([1L], result.SelectedNoteIds);
    }

    [Fact]
    public void Results_round_half_away_from_zero_and_clamp_to_each_fields_range() {
        Note[] notes = [N(1, 60, 5, 100, 3), N(2, 125, 127, 7, 1, 15)];
        var result = TransformEngine.Apply(
            notes,
            [],
            Set(
                [Notes],
                [
                    Op(TransformField.Velocity, TransformOperationKind.Multiply, 0.5), // 2.5 → 3; 63.5 → 64
                    Op(TransformField.Pitch, TransformOperationKind.Add, 5), // 65; 130 → 127
                    Op(TransformField.Channel, TransformOperationKind.Add, 3), // 4; 18 → 16
                    Op(TransformField.Length, TransformOperationKind.Subtract, 2), // 1; −1 → 1
                    Op(TransformField.Position, TransformOperationKind.Subtract, 50) // 50; −43 → 0
                ]
            ),
            FourFour
        );
        Assert.Equal(N(1, 65, 3, 50, 1, 4), result.Notes[0]);
        Assert.Equal(N(2, 127, 64, 0, 1, 16), result.Notes[1]);
    }

    [Fact]
    public void Crescendo_ramps_over_the_selections_original_positions() {
        Note[] notes = [N(1, 60, 64, 0), N(2, 60, 64, 480), N(3, 60, 64, 960), N(4, 60, 64, 1920)];
        var result = TransformEngine.Apply(
            notes,
            [],
            Set(
                [Notes],
                [
                    Op(TransformField.Velocity, TransformOperationKind.Crescendo, 20, 100),
                    Op(TransformField.Position, TransformOperationKind.Add, 960)
                ]
            ),
            FourFour
        );
        Assert.Equal([20, 40, 60, 100], result.Notes.Select(static n => n.Velocity));
        Assert.Equal([960L, 1440, 1920, 2880], result.Notes.Select(static n => n.StartTick));
    }

    [Fact]
    public void Speed_and_reverse_keep_the_selection_in_place() {
        Note[] notes = [N(1, 60, 64, 3840, 480), N(2, 62, 64, 4320), N(3, 64, 64, 5760, 960)];
        var doubled = TransformEngine.Apply(notes, [], Preset(TransformPresetIds.DoubleSpeed), FourFour);
        Assert.Equal([3840L, 4080, 4800], doubled.Notes.Select(static n => n.StartTick));
        Assert.Equal([240L, 120, 480], doubled.Notes.Select(static n => n.LengthTicks));

        var reversed = TransformEngine.Apply(notes, [], Preset(TransformPresetIds.ReversePosition), FourFour);
        Assert.Equal([5760L, 5280, 3840], reversed.Notes.Select(static n => n.StartTick));

        var pitches = TransformEngine.Apply(notes, [], Preset(TransformPresetIds.ReversePitch), FourFour);
        Assert.Equal([60, 58, 56], pitches.Notes.Select(static n => n.Pitch));
    }

    [Fact]
    public void Subposition_is_measured_from_the_bar_line() {
        var signatures = new SignatureMap([new(0, 4, 4), new(3840, 3, 4)]);
        Note[] notes =
            [N(1, 60, 64, 0), N(2, 60, 64, 3840), N(3, 60, 64, 3840 + 2880), N(4, 60, 64, 3840 + 2880 + 960)];
        var result = TransformEngine.Apply(
            notes,
            [],
            Set([Cond(TransformField.Subposition, TransformConditionOp.Equal)], []),
            signatures
        );
        Assert.Equal([1L, 2, 3], result.SelectedNoteIds);
    }

    [Fact]
    public void Random_draws_are_seeded_by_the_set_and_the_event() {
        Note[] notes = [N(1, 60, 64, 0), N(2, 60, 64, 0)];
        var set = Set([Notes], [Op(TransformField.Velocity, TransformOperationKind.Random, 1, 127)], seed: 42);
        var a = TransformEngine.Apply(notes, [], set, FourFour);
        var b = TransformEngine.Apply([..notes.Reverse()], [], set, FourFour);
        Assert.Equal(a.Notes[0], b.Notes[1]); // by id, whatever the order
        Assert.Equal(a.Notes[1], b.Notes[0]);

        var draw = TransformEngine.Draws(42, 1, 1)[0];
        Assert.Equal(1 + (int)(draw % 127), a.Notes[0].Velocity);
        Assert.NotEqual(
            TransformEngine.Apply(notes, [], set with { Seed = 43 }, FourFour).Notes.Select(static n => n.Velocity),
            a.Notes.Select(static n => n.Velocity)
        );
    }

    [Fact]
    public void Split_mix_64_matches_its_reference_values() {
        // SplitMix64 from state 0 (seed 0, id 0): the reference sequence.
        Assert.Equal(
            [0xE220A8397B1DCDAFUL, 0x6E789E6AA1B965F4UL, 0x06C45D188009454FUL],
            TransformEngine.Draws(0, 0, 3)
        );
    }

    [Fact]
    public void Invalid_sets_are_refused() {
        Assert.Throws<ArgumentException>(static () => TransformEngine.Apply(
                [],
                [],
                Set([], [Op(TransformField.Status, TransformOperationKind.Fix, 1)]),
                FourFour
            )
        );
        Assert.Throws<ArgumentException>(static () => TransformEngine.Apply(
                [],
                [],
                Set(
                    [],
                    [
                        Op(TransformField.Pitch, TransformOperationKind.Fix, 1),
                        Op(TransformField.Pitch, TransformOperationKind.Add, 1)
                    ]
                ),
                FourFour
            )
        );
        Assert.Throws<ArgumentException>(static () => TransformEngine.Apply(
                [],
                [],
                Set([], [], map: [1, 2, 3]),
                FourFour
            )
        );
    }

    [Fact]
    public void The_presets_are_the_factory_ids_with_seed_0() {
        var presets = TransformEngine.Presets();
        Assert.Equal(
            [
                TransformPresetIds.Humanize, TransformPresetIds.Quantize, TransformPresetIds.Crescendo,
                TransformPresetIds.FixedVelocity, TransformPresetIds.Transpose, TransformPresetIds.DoubleSpeed,
                TransformPresetIds.HalfSpeed, TransformPresetIds.ReversePosition, TransformPresetIds.ReversePitch,
                TransformPresetIds.LengthLimits, TransformPresetIds.RandomVelocity, TransformPresetIds.VelocityCompress
            ],
            presets.Select(static p => p.Id)
        );
        Assert.All(presets, static p => Assert.Equal(0, p.Seed));
        Assert.All(presets, static p => Assert.StartsWith("factory.", p.Id, StringComparison.Ordinal));
    }

    static TransformSet Preset(string id) => TransformEngine.Presets().Single(p => p.Id == id);

    // ---- The shared vectors ----

    /// <summary>The events every vector case starts from: two bars of notes and one of each controller kind.</summary>
    static (Note[] Notes, ControlEvent[] Controls) Input() =>
        ([
            N(101, 48, 100, 0, 1_900), N(102, 60, 64, 3, 230), N(103, 62, 80, 245, 200, 2), N(104, 64, 96, 478, 480),
            N(105, 67, 110, 960, 240, 10), N(106, 72, 127, 1_441, 119), N(107, 76, 1, 1_920, 960),
            N(108, 59, 50, 2_895, 60, 16), N(109, 84, 75, 3_840, 1_920), N(110, 61, 33, 5_000, 333)
        ], [
            C(201, ControlKind.ControlChange, 1, 0, 0), C(202, ControlKind.ControlChange, 74, 90, 960, 2),
            C(203, ControlKind.PitchBend, 0, -8192, 1_200), C(204, ControlKind.PitchBend, 0, 4_000, 2_000),
            C(205, ControlKind.ChannelPressure, 0, 70, 2_400), C(206, ControlKind.ProgramChange, 0, 12, 0, 3),
            C(207, ControlKind.PolyPressure, 64, 100, 500), C(208, ControlKind.ControlChange, 7, 127, 4_000)
        ]);

    static IReadOnlyList<int> ReversedMap => [..Enumerable.Range(0, 128).Select(static i => 127 - i)];

    static IEnumerable<(string Name, TransformSet Set, SignatureMap? Signatures)> VectorCases() {
        foreach (var preset in TransformEngine.Presets()) {
            yield return ("preset " + preset.Id, preset, null);
        }

        yield return ("preset factory.humanize, seed 12345", Preset(TransformPresetIds.Humanize) with { Seed = 12_345 },
            null);
        yield return ("golden path: notes above C4 (60) get velocity 100 and a 1/16 length",
            Set(
                [Notes, Cond(TransformField.Pitch, TransformConditionOp.GreaterEqual, 61)],
                [
                    Op(TransformField.Velocity, TransformOperationKind.Fix, 100),
                    Op(TransformField.Length, TransformOperationKind.Fix, 240)
                ]
            ), null);
        yield return ("select only: notes at most C4",
            Set(
                [Notes, Cond(TransformField.Pitch, TransformConditionOp.LessEqual, 60)],
                [Op(TransformField.Velocity, TransformOperationKind.Fix, 1)],
                TransformMode.SelectOnly
            ), null);
        yield return ("operate only: every event, channel fixed to 5",
            Set([], [Op(TransformField.Channel, TransformOperationKind.Fix, 5)], TransformMode.OperateOnly), null);
        yield return ("condition all, thru on every field (a no-op that selects everything)",
            Set(
                [Cond(TransformField.Position, TransformConditionOp.All)],
                [
                    Op(TransformField.Position, TransformOperationKind.Thru),
                    Op(TransformField.Length, TransformOperationKind.Thru),
                    Op(TransformField.Channel, TransformOperationKind.Thru),
                    Op(TransformField.Pitch, TransformOperationKind.Thru),
                    Op(TransformField.Velocity, TransformOperationKind.Thru)
                ]
            ), null);
        yield return ("conditions: status control change, pitch (controller) unequal 7",
            Set(
                [
                    Cond(TransformField.Status, TransformConditionOp.Equal, 176),
                    Cond(TransformField.Pitch, TransformConditionOp.Unequal, 7)
                ],
                [Op(TransformField.Velocity, TransformOperationKind.Add, 50)]
            ), null);
        yield return ("conditions: velocity outside 40…100, channel inside 1…2",
            Set(
                [
                    Cond(TransformField.Velocity, TransformConditionOp.Outside, 40, 100),
                    Cond(TransformField.Channel, TransformConditionOp.Inside, 1, 2)
                ],
                [Op(TransformField.Pitch, TransformOperationKind.Subtract, 100)]
            ), null);
        yield return ("conditions: length at least 480 (controllers have length 0)",
            Set(
                [Cond(TransformField.Length, TransformConditionOp.GreaterEqual, 480)],
                [Op(TransformField.Position, TransformOperationKind.Quantize, 960)]
            ), null);
        yield return ("conditions: subposition 0 in 4/4 then 3/4 from tick 3840",
            Set(
                [Cond(TransformField.Subposition, TransformConditionOp.Equal)],
                [Op(TransformField.Velocity, TransformOperationKind.Fix, 99)]
            ),
            new SignatureMap([new(0, 4, 4), new(3_840, 3, 4)]));
        yield return ("conditions: subposition inside 0…479 (the first beat) in 6/8",
            Set(
                [Cond(TransformField.Subposition, TransformConditionOp.Inside, 0, 479)],
                [Op(TransformField.Pitch, TransformOperationKind.Add, 1)]
            ), SignatureMap.Constant(6, 8));
        yield return ("min on velocity (raises to at least 40)",
            Set([], [Op(TransformField.Velocity, TransformOperationKind.Min, 40)]), null);
        yield return ("max on velocity", Set([], [Op(TransformField.Velocity, TransformOperationKind.Max, 90)]), null);
        yield return ("flip pitch around 64.5 and velocity around 0 (clamped)",
            Set(
                [],
                [
                    Op(TransformField.Pitch, TransformOperationKind.Flip, 64.5),
                    Op(TransformField.Velocity, TransformOperationKind.Flip)
                ]
            ), null);
        yield return ("flip position around the selection's middle + 100",
            Set([Notes], [Op(TransformField.Position, TransformOperationKind.Flip, 100)]), null);
        yield return ("multiply length by 1.5, position by 3 from the first",
            Set(
                [Notes],
                [
                    Op(TransformField.Length, TransformOperationKind.Multiply, 1.5),
                    Op(TransformField.Position, TransformOperationKind.Multiply, 3)
                ]
            ), null);
        yield return ("divide velocity by 0 (unchanged) and channel by 3",
            Set(
                [],
                [
                    Op(TransformField.Velocity, TransformOperationKind.Divide),
                    Op(TransformField.Channel, TransformOperationKind.Divide, 3)
                ]
            ), null);
        yield return ("scale velocity x * -1 + 128 (pitch bend clamps to −8192…8191)",
            Set([], [Op(TransformField.Velocity, TransformOperationKind.Scale, -1, 128)]), null);
        yield return ("scale position x * 0.5 + 480 from the selection's first",
            Set([Notes], [Op(TransformField.Position, TransformOperationKind.Scale, 0.5, 480)]), null);
        yield return ("range velocity 100…30 (either order)",
            Set([], [Op(TransformField.Velocity, TransformOperationKind.Range, 100, 30)]), null);
        yield return ("random pitch 70…60 and plus-minus random velocity 20, seed 7",
            Set(
                [],
                [
                    Op(TransformField.Velocity, TransformOperationKind.PlusMinusRandom, 20),
                    Op(TransformField.Pitch, TransformOperationKind.Random, 70, 60)
                ],
                seed: 7
            ), null);
        yield return ("random on every field, seed -3 (draws even where a field is missing)",
            Set(
                [],
                [
                    Op(TransformField.Position, TransformOperationKind.PlusMinusRandom, 30.4),
                    Op(TransformField.Length, TransformOperationKind.Random, 10, 20),
                    Op(TransformField.Channel, TransformOperationKind.Random, 1, 16),
                    Op(TransformField.Pitch, TransformOperationKind.PlusMinusRandom, 2),
                    Op(TransformField.Velocity, TransformOperationKind.Random, -100, 100)
                ],
                seed: -3
            ), null);
        yield return ("quantize position to 1/8 triplets (320) and velocity to 16",
            Set(
                [],
                [
                    Op(TransformField.Position, TransformOperationKind.Quantize, 320),
                    Op(TransformField.Velocity, TransformOperationKind.Quantize, 16)
                ]
            ), null);
        yield return ("exponent 2 on velocity, 0.5 on pitch",
            Set(
                [],
                [
                    Op(TransformField.Velocity, TransformOperationKind.Exponent, 2),
                    Op(TransformField.Pitch, TransformOperationKind.Exponent, 0.5)
                ]
            ), null);
        yield return ("exponent 2 on position and length (over the selection's extent)",
            Set(
                [Notes],
                [
                    Op(TransformField.Position, TransformOperationKind.Exponent, 2),
                    Op(TransformField.Length, TransformOperationKind.Exponent, 2)
                ]
            ), null);
        yield return ("crescendo velocity 127→1 over every event (pitch bends too)",
            Set([], [Op(TransformField.Velocity, TransformOperationKind.Crescendo, 127, 1)]), null);
        yield return ("relative crescendo -20→+20 on note velocity",
            Set([Notes], [Op(TransformField.Velocity, TransformOperationKind.RelativeCrescendo, -20, 20)]), null);
        yield return ("crescendo on pitch with a single event selected (a)",
            Set(
                [Notes, Cond(TransformField.Pitch, TransformConditionOp.Equal, 84)],
                [Op(TransformField.Pitch, TransformOperationKind.Crescendo, 50, 70)]
            ), null);
        yield return ("use a reversed map on pitch and velocity",
            Set(
                [],
                [
                    Op(TransformField.Pitch, TransformOperationKind.UseMap),
                    Op(TransformField.Velocity, TransformOperationKind.UseMap)
                ],
                map: ReversedMap
            ), null);
        yield return ("use the identity map (null) on velocity",
            Set([], [Op(TransformField.Velocity, TransformOperationKind.UseMap)]), null);
        yield return ("operations listed out of order apply position first; crescendo uses original positions",
            Set(
                [Notes],
                [
                    Op(TransformField.Velocity, TransformOperationKind.Crescendo, 10, 110),
                    Op(TransformField.Position, TransformOperationKind.Add, 100_000)
                ]
            ), null);
        yield return ("nothing matches", Set(
            [Cond(TransformField.Pitch, TransformConditionOp.Equal, 1)],
            [Op(TransformField.Velocity, TransformOperationKind.Fix, 1)]
        ), null);
    }

    static readonly JsonSerializerOptions Indented = new() {
        WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    const string About =
        """MIDI Transform vectors (docs/08-phase-7.md, "MIDI Transform"): Anode.Model's TransformEngine computes these; """
        + "the server applies them and the editor's TypeScript port must reproduce them exactly. Each case applies "
        + "`set` to `notes` and `controls` (positions in project ticks, 960 per quarter note) under `signatures` (absent: "
        + "4/4 from tick 0) and expects every event back in the order given (changed when matched, unless the mode is "
        + "selectOnly) and the matching ids in input order. "
        + "FIELDS (doubles): a note's position = startTick, length = lengthTicks, channel, pitch, velocity, status 144; a "
        + "controller's position = tick, length 0, channel, pitch = number for controlChange and polyPressure (else 0), "
        + "velocity = value, status 176 controlChange, 224 pitchBend, 208 channelPressure, 192 programChange, 160 "
        + "polyPressure; subposition = position - the bar line at or before it. "
        + "CONDITIONS are ANDed; all matches; equal x == a; unequal x != a; lessEqual x <= a; greaterEqual x >= a; "
        + "inside a <= x <= b; outside x < a || x > b. "
        + "SELECTION: P0 and P1 are the least and greatest position of the matching events before any operation; for "
        + "exponent on length, L0 and L1 the least and greatest length of the matching notes. "
        + "OPERATIONS apply to each matching event in the field order position, length, channel, pitch, velocity "
        + "(whatever their order in the list; at most one per field; none on status or subposition). With x the "
        + "field's current value: thru x; fix a; add x + a; subtract x - a; min max(x, a); max min(x, a); flip 2a - x, "
        + "but on position P0 + P1 + 2a - x; multiply x * a, on position P0 + (x - P0) * a; divide x / a (a = 0: x), on "
        + "position P0 + (x - P0) / a; scale x * a + b, on position P0 + (x - P0) * a + b; range clamp(x, min(a, b), "
        + "max(a, b)); random an integer in round(min(a, b))…round(max(a, b)); plusMinusRandom x + an integer in "
        + "-round(|a|)…round(|a|); quantize round(x / a) * a (a <= 0: x); exponent lo + ((x - lo) / (hi - lo))^a * "
        + "(hi - lo), the ratio clamped to 0…1, with lo…hi the field's range (channel 1…16, pitch 0…127, velocity as "
        + "below), P0…P1 for position and L0…L1 for length (a <= 0 or hi <= lo: x); crescendo a + (b - a) * t and "
        + "relativeCrescendo x + a + (b - a) * t, t = (p - P0) / (P1 - P0) by the event's ORIGINAL position p (0 when "
        + "P1 = P0); useMap map[round(x)] when round(x) is 0…127 (map absent: round(x)), else x. "
        + "Each result is rounded half away from zero, then clamped: position >= 0, a note's length >= 1, channel "
        + "1…16, pitch 0…127, a note's velocity 1…127, a pitch bend's -8192…8191, other controllers' 0…127. A "
        + "controller has no length, and only controlChange and polyPressure have a pitch: an operation on a field an "
        + "event lacks leaves it (but still draws, below). "
        + "RANDOM: each matching event has its own SplitMix64, state = seed XOR (id * 0xD1B54A32D192ED03) (unsigned "
        + "64-bit, wrapping; seed as two's complement); next(): state += 0x9E3779B97F4A7C15; z = state; z = (z ^ (z >> "
        + "30)) * 0xBF58476D1CE4E5B9; z = (z ^ (z >> 27)) * 0x94D049BB133111EB; return z ^ (z >> 31). Every random and "
        + "plusMinusRandom operation draws one next(), in field order, whether or not the event has the field; an "
        + "integer in lo…hi is lo + next() mod (hi - lo + 1). `draws` pins the first numbers of some generators "
        + "(decimal strings). Factory presets (ids factory.*) have seed 0; the editor draws a new seed when it applies "
        + "a random one.";

    /// <summary>The vectors the server and the editor's Transform port must reproduce exactly.</summary>
    [Fact]
    public void The_transform_vectors_are_current() {
        var (notes, controls) = Input();
        var draws = new JsonArray();
        foreach (var (seed, id) in (ReadOnlySpan<(long, long)>)[(0, 0), (0, 101), (7, 101), (-3, 203), (12_345, 110)]) {
            draws.Add(
                new JsonObject {
                    ["seed"] = seed,
                    ["id"] = id,
                    ["values"] = new JsonArray(
                        [
                            ..TransformEngine.Draws(seed, id, 4)
                                .Select(static d => (JsonNode)d.ToString(CultureInfo.InvariantCulture))
                        ]
                    )
                }
            );
        }

        var cases = new JsonArray();
        foreach (var (name, set, signatures) in VectorCases()) {
            var result = TransformEngine.Apply(notes, controls, set, signatures ?? FourFour);
            var entry = new JsonObject {
                ["name"] = name,
                ["set"] = JsonSerializer.SerializeToNode(set, ContractsJsonContext.Default.TransformSet)
            };
            if (signatures is not null) {
                entry["signatures"] = new JsonArray(
                    [
                        ..Enumerable.Range(0, signatures.Count)
                            .Select(i => JsonSerializer.SerializeToNode(
                                    signatures[i],
                                    ContractsJsonContext.Default.SignatureEvent
                                )
                            )
                    ]
                );
            }

            entry["expected"] = new JsonObject {
                ["notes"] = Nodes(result.Notes),
                ["controls"] = Nodes(result.Controls),
                ["selectedNoteIds"] = new JsonArray([..result.SelectedNoteIds.Select(static i => (JsonNode)i)]),
                ["selectedControlIds"] = new JsonArray([..result.SelectedControlIds.Select(static i => (JsonNode)i)])
            };
            cases.Add(entry);
        }

        var document = new JsonObject {
            ["about"] = About,
            ["notes"] = Nodes(notes),
            ["controls"] = Nodes(controls),
            ["draws"] = draws,
            ["cases"] = cases
        };
        var json = document.ToJsonString(Indented) + "\n";
        var path = VectorsPath();
        if (Environment.GetEnvironmentVariable("ANODE_UPDATE_VECTORS") == "1") {
            File.WriteAllText(path, json);
        }

        Assert.Equal(json, File.ReadAllText(path));
    }

    /// <summary>Reads the vectors as a client does and applies each case, as the server's and editor's tests do.</summary>
    [Fact]
    public void The_transform_vectors_replay() {
        var document = JsonNode.Parse(File.ReadAllText(VectorsPath()))!;
        var notes = document["notes"].Deserialize(ContractsJsonContext.Default.IReadOnlyListNote)!;
        var controls = document["controls"].Deserialize(ContractsJsonContext.Default.IReadOnlyListControlEvent)!;
        foreach (var draw in document["draws"]!.AsArray()) {
            var values = TransformEngine.Draws((long)draw!["seed"]!, (long)draw["id"]!, 4);
            Assert.Equal(
                draw["values"]!.AsArray().Select(static v => ulong.Parse((string)v!, CultureInfo.InvariantCulture)),
                values
            );
        }

        var count = 0;
        foreach (var entry in document["cases"]!.AsArray()) {
            var set = entry!["set"].Deserialize(ContractsJsonContext.Default.TransformSet)!;
            var signatures = entry["signatures"] is { } s
                ? new SignatureMap(s.Deserialize(ContractsJsonContext.Default.IReadOnlyListSignatureEvent)!)
                : FourFour;
            var result = TransformEngine.Apply(notes, controls, set, signatures);
            var expected = entry["expected"]!;
            var name = (string)entry["name"]!;
            Assert.True(
                expected["notes"].Deserialize(ContractsJsonContext.Default.IReadOnlyListNote)!.SequenceEqual(
                    result.Notes
                ),
                name
            );
            Assert.True(
                expected["controls"]
                    .Deserialize(ContractsJsonContext.Default.IReadOnlyListControlEvent)!
                    .SequenceEqual(result.Controls),
                name
            );
            Assert.Equal(
                expected["selectedNoteIds"]!.AsArray().Select(static i => (long)i!),
                result.SelectedNoteIds
            );
            Assert.Equal(
                expected["selectedControlIds"]!.AsArray().Select(static i => (long)i!),
                result.SelectedControlIds
            );
            count++;
        }

        Assert.True(count >= 40, $"{count} cases");
    }

    static JsonArray Nodes(IEnumerable<Note> notes) =>
        [..notes.Select(static n => JsonSerializer.SerializeToNode(n, ContractsJsonContext.Default.Note))];

    static JsonArray Nodes(IEnumerable<ControlEvent> controls) =>
        [..controls.Select(static c => JsonSerializer.SerializeToNode(c, ContractsJsonContext.Default.ControlEvent))];

    static string VectorsPath([CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(source)!, "TransformVectors.json");
}
