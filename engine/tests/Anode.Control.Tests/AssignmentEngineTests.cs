using Anode.Contracts;
using Anode.Control.Assignments;
using CsCheck;

namespace Anode.Control.Tests;

/// <summary>The controller assignment engine (docs/09-phase-8.md, "Assignments"; docs/control/README.md).</summary>
public sealed class AssignmentEngineTests {
    const string Port = "port:a";

    static readonly ControlTarget Volume = new(ControlTargetKind.Parameter, "t2", "volume");
    static readonly ControlTarget Mute = new(ControlTargetKind.TrackSwitch, Switch: TrackSwitchAction.Mute);
    static readonly ControlTarget Play = new(ControlTargetKind.Transport, Transport: ControlTransportAction.Play);

    sealed class Values : ITargetValues {
        public Dictionary<ControlTarget, double> Current { get; } = [];

        public double? Value(ControlTarget target) => Current.TryGetValue(target, out var v) ? v : null;

        /// <summary>Applies changes as the server would.</summary>
        public void Apply(IEnumerable<TargetChange> changes) {
            foreach (var c in changes) {
                Current[c.Target] = c.Value;
            }
        }
    }

    static ControlAssignment Assignment(
        string id,
        ControlInputKind kind,
        int number,
        ControlTarget target,
        ControlValueMode mode = ControlValueMode.Absolute,
        double min = 0,
        double max = 1,
        bool pickup = false,
        int? channel = 1,
        string? port = Port
    ) =>
        new(id, null, new(port, kind, channel, number), target, mode, min, max, pickup);

    static AssignmentEngine Engine(params ControlAssignment[] assignments) =>
        new(new([new("z", "Zone", [new("m", "Mode", assignments)], "m")]));

    static double Single(IReadOnlyList<TargetChange> changes) => Assert.Single(changes).Value;

    static byte[] Cc(int controller, int value, int channel = 0) =>
        [(byte)(0xB0 | channel), (byte)controller, (byte)value];

    [Fact]
    public void A_7_bit_control_change_sets_its_target_across_the_range() {
        var engine = Engine(Assignment("a", ControlInputKind.ControlChange, 21, Volume, min: 0.2, max: 0.6));
        var values = new Values();
        Assert.Equal(0.2, Single(engine.Receive(Port, Cc(21, 0), values)), 12);
        Assert.Equal(0.6, Single(engine.Receive(Port, Cc(21, 127), values)), 12);
        Assert.Equal(0.2 + 0.4 * 64 / 127, Single(engine.Receive(Port, Cc(21, 64), values)), 12);
        Assert.Empty(engine.Receive(Port, Cc(22, 64), values));
        Assert.Empty(engine.Receive(Port, Cc(21, 64, 1), values));
        Assert.Empty(engine.Receive("port:b", Cc(21, 64), values));
    }

    [Fact]
    public void Min_above_max_inverts() {
        var engine = Engine(Assignment("a", ControlInputKind.ControlChange, 21, Volume, min: 1, max: 0));
        Assert.Equal(1, Single(engine.Receive(Port, Cc(21, 0), new Values())), 12);
        Assert.Equal(0, Single(engine.Receive(Port, Cc(21, 127), new Values())), 12);
    }

    [Fact]
    public void Any_port_and_any_channel_match_every_port_and_channel() {
        var engine = Engine(Assignment("a", ControlInputKind.ControlChange, 21, Volume, channel: null, port: null));
        Assert.Single(engine.Receive("x", Cc(21, 1, 15), new Values()));
        Assert.Single(engine.Receive("y", Cc(21, 1), new Values()));
    }

    [Fact]
    public void A_14_bit_pair_reads_on_its_LSB_after_its_MSB() {
        var engine = Engine(Assignment("a", ControlInputKind.ControlChange14, 7, Volume));
        var values = new Values();
        Assert.Empty(engine.Receive(Port, Cc(39, 0), values)); // An LSB before any MSB.
        Assert.Empty(engine.Receive(Port, Cc(7, 64), values)); // The MSB alone is taken without a change.
        Assert.Equal(((64 << 7) | 1) / 16383.0, Single(engine.Receive(Port, Cc(39, 1), values)), 12);
        Assert.Empty(engine.Receive(Port, Cc(7, 127), values));
        Assert.Equal(1, Single(engine.Receive(Port, Cc(39, 127), values)), 12);
    }

    [Fact]
    public void An_NRPN_reads_on_its_data_LSB_and_steps_on_increment_and_decrement() {
        var engine = Engine(Assignment("a", ControlInputKind.Nrpn, (3 << 7) | 5, Volume));
        var values = new Values();
        Assert.Empty(engine.Receive(Port, Cc(99, 3), values));
        Assert.Empty(engine.Receive(Port, Cc(98, 5), values));
        Assert.Empty(engine.Receive(Port, Cc(6, 100), values));
        var value = Single(engine.Receive(Port, Cc(38, 3), values));
        Assert.Equal(((100 << 7) | 3) / 16383.0, value, 12);
        values.Current[Volume] = 0.5;
        Assert.Equal(0.5 + 1 / 128.0, Single(engine.Receive(Port, Cc(96, 0), values)), 12);
        Assert.Equal(0.5 - 1 / 128.0, Single(engine.Receive(Port, Cc(97, 0), values)), 12);

        // Another parameter's data is not this assignment's.
        engine.Receive(Port, Cc(99, 0), values);
        engine.Receive(Port, Cc(98, 1), values);
        engine.Receive(Port, Cc(6, 1), values);
        Assert.Empty(engine.Receive(Port, Cc(38, 1), values));
    }

    [Fact]
    public void An_RPN_selection_ends_the_NRPN() {
        var engine = Engine(Assignment("a", ControlInputKind.Nrpn, 5, Volume));
        var values = new Values();
        engine.Receive(Port, Cc(99, 0), values);
        engine.Receive(Port, Cc(98, 5), values);
        engine.Receive(Port, Cc(101, 0), values);
        engine.Receive(Port, Cc(6, 1), values);
        Assert.Empty(engine.Receive(Port, Cc(38, 1), values));
    }

    [Fact]
    public void Notes_press_on_and_release_off() {
        var engine = Engine(Assignment("a", ControlInputKind.Note, 36, Mute, ControlValueMode.Momentary));
        var values = new Values();
        Assert.Equal(1, Single(engine.Receive(Port, [0x90, 36, 100], values)));
        Assert.Equal(0, Single(engine.Receive(Port, [0x80, 36, 0], values)));
        Assert.Equal(1, Single(engine.Receive(Port, [0x90, 36, 1], values)));
        Assert.Equal(0, Single(engine.Receive(Port, [0x90, 36, 0], values))); // Note on, velocity 0.
    }

    [Fact]
    public void Pitch_bend_is_14_bit() {
        var engine = Engine(Assignment("a", ControlInputKind.PitchBend, 0, Volume));
        Assert.Equal(8192 / 16383.0, Single(engine.Receive(Port, [0xE0, 0x00, 0x40], new Values())), 12);
        Assert.Equal(1, Single(engine.Receive(Port, [0xE0, 0x7F, 0x7F], new Values())), 12);
    }

    [Fact]
    public void A_program_change_presses() {
        var engine = Engine(Assignment("a", ControlInputKind.ProgramChange, 4, Play));
        Assert.Equal(1, Single(engine.Receive(Port, [0xC0, 4], new Values())));
        Assert.Empty(engine.Receive(Port, [0xC0, 5], new Values()));
    }

    [Fact]
    public void Functions_get_1_on_press_and_0_on_release_whatever_the_range() {
        var engine = Engine(Assignment("a", ControlInputKind.Note, 40, Play, min: 0.3, max: 0.4));
        Assert.Equal(1, Single(engine.Receive(Port, [0x90, 40, 20], new Values())));
        Assert.Equal(0, Single(engine.Receive(Port, [0x80, 40, 0], new Values())));
    }

    [Theory]
    [InlineData(ControlValueMode.RelativeTwosComplement, 1, 1)]
    [InlineData(ControlValueMode.RelativeTwosComplement, 3, 3)]
    [InlineData(ControlValueMode.RelativeTwosComplement, 127, -1)]
    [InlineData(ControlValueMode.RelativeTwosComplement, 65, -63)]
    [InlineData(ControlValueMode.RelativeSignedBit, 1, 1)]
    [InlineData(ControlValueMode.RelativeSignedBit, 65, -1)]
    [InlineData(ControlValueMode.RelativeSignedBit, 127, -63)]
    [InlineData(ControlValueMode.RelativeBinaryOffset, 65, 1)]
    [InlineData(ControlValueMode.RelativeBinaryOffset, 63, -1)]
    [InlineData(ControlValueMode.RelativeBinaryOffset, 0, -64)]
    public void Relative_encodings_step_by_a_128th_of_the_range(ControlValueMode mode, int raw, int steps) {
        var engine = Engine(Assignment("a", ControlInputKind.ControlChange, 21, Volume, mode, 0, 0.5));
        var values = new Values { Current = { [Volume] = 0.25 } };
        Assert.Equal(steps, AssignmentEngine.Relative(mode, raw, 7));
        Assert.Equal(
            Math.Clamp(0.25 + steps * 0.5 / 128, 0, 0.5),
            Single(engine.Receive(Port, Cc(21, raw), values)),
            12
        );
    }

    [Fact]
    public void Relative_steps_clamp_to_the_range_and_need_the_target() {
        var engine = Engine(
            Assignment("a", ControlInputKind.ControlChange, 21, Volume, ControlValueMode.RelativeTwosComplement)
        );
        var values = new Values();
        Assert.Empty(engine.Receive(Port, Cc(21, 1), values)); // No target value: nothing to step.
        values.Current[Volume] = 1;
        Assert.Equal(1, Single(engine.Receive(Port, Cc(21, 10), values)));
        Assert.Empty(engine.Receive(Port, Cc(21, 0), values)); // Zero steps.
    }

    [Fact]
    public void Relative_14_bit_encodings_read_14_bits() {
        Assert.Equal(-1, AssignmentEngine.Relative(ControlValueMode.RelativeTwosComplement, 16383, 14));
        Assert.Equal(-2, AssignmentEngine.Relative(ControlValueMode.RelativeSignedBit, 8194, 14));
        Assert.Equal(3, AssignmentEngine.Relative(ControlValueMode.RelativeBinaryOffset, 8195, 14));
    }

    [Fact]
    public void Toggle_flips_the_target_on_each_press() {
        var engine = Engine(Assignment("a", ControlInputKind.Note, 36, Mute, ControlValueMode.Toggle));
        var values = new Values();
        Assert.Equal(1, Single(engine.Receive(Port, [0x90, 36, 127], values))); // Unknown target: its own state.
        Assert.Empty(engine.Receive(Port, [0x80, 36, 0], values));
        values.Current[Mute] = 1;
        Assert.Equal(0, Single(engine.Receive(Port, [0x90, 36, 127], values)));
        values.Current[Mute] = 0;
        Assert.Equal(1, Single(engine.Receive(Port, [0x90, 36, 127], values)));
    }

    [Fact]
    public void Toggle_on_a_control_change_presses_at_half() {
        var engine = Engine(Assignment("a", ControlInputKind.ControlChange, 50, Mute, ControlValueMode.Toggle));
        var values = new Values { Current = { [Mute] = 0 } };
        Assert.Empty(engine.Receive(Port, Cc(50, 63), values));
        Assert.Equal(1, Single(engine.Receive(Port, Cc(50, 64), values)));
    }

    [Fact]
    public void Momentary_holds_max_while_pressed() {
        var engine = Engine(
            Assignment("a", ControlInputKind.ControlChange, 50, Volume, ControlValueMode.Momentary, 0.1, 0.9)
        );
        Assert.Equal(0.9, Single(engine.Receive(Port, Cc(50, 127), new Values())));
        Assert.Equal(0.1, Single(engine.Receive(Port, Cc(50, 0), new Values())));
    }

    [Fact]
    public void Pickup_holds_until_the_control_crosses_the_target() {
        var engine = Engine(Assignment("a", ControlInputKind.ControlChange, 21, Volume, pickup: true));
        var values = new Values { Current = { [Volume] = 0.5 } };
        Assert.Empty(engine.Receive(Port, Cc(21, 10), values));
        Assert.Empty(engine.Receive(Port, Cc(21, 40), values));
        var crossed = Single(engine.Receive(Port, Cc(21, 70), values)); // 40 → 70 crosses 63.5.
        Assert.Equal(70 / 127.0, crossed, 12);
        values.Apply([new("a", Volume, crossed)]);
        Assert.Equal(80 / 127.0, Single(engine.Receive(Port, Cc(21, 80), values)), 12);
        values.Current[Volume] = 80 / 127.0;

        // The editor moves the target away: pickup holds again until the control reaches it.
        values.Current[Volume] = 0.1;
        Assert.Empty(engine.Receive(Port, Cc(21, 70), values));
        Assert.Single(engine.Receive(Port, Cc(21, 13), values)); // Within a step of 0.1 × 127 = 12.7.
    }

    [Fact]
    public void Pickup_keeps_the_target_while_values_sent_have_not_reached_it() {
        var engine = Engine(Assignment("a", ControlInputKind.ControlChange, 21, Volume, pickup: true));
        var values = new Values { Current = { [Volume] = 0.5 } };
        Assert.Empty(engine.Receive(Port, Cc(21, 40), values));

        // Nothing sent has landed yet, then the first value does, then the control turns back.
        Assert.Equal(110 / 127.0, Single(engine.Receive(Port, Cc(21, 110), values)), 12);
        Assert.Equal(112 / 127.0, Single(engine.Receive(Port, Cc(21, 112), values)), 12);
        values.Current[Volume] = 110 / 127.0;
        Assert.Equal(114 / 127.0, Single(engine.Receive(Port, Cc(21, 114), values)), 12);
        Assert.Equal(100 / 127.0, Single(engine.Receive(Port, Cc(21, 100), values)), 12);

        // Once it has caught up, a target off that way has moved elsewhere.
        values.Current[Volume] = 100 / 127.0;
        Assert.Equal(101 / 127.0, Single(engine.Receive(Port, Cc(21, 101), values)), 12);
        values.Current[Volume] = 0.1;
        Assert.Empty(engine.Receive(Port, Cc(21, 102), values));
    }

    [Fact]
    public void Pickup_takes_a_value_that_lands_on_the_target() {
        var engine = Engine(Assignment("a", ControlInputKind.ControlChange, 21, Volume, pickup: true));
        var values = new Values { Current = { [Volume] = 64 / 127.0 } };
        Assert.Single(engine.Receive(Port, Cc(21, 64), values));
    }

    [Fact]
    public void Reset_forgets_pickup() {
        var engine = Engine(Assignment("a", ControlInputKind.ControlChange, 21, Volume, pickup: true));
        var values = new Values { Current = { [Volume] = 0.5 } };
        engine.Receive(Port, Cc(21, 10), values);
        engine.Reset();
        Assert.Empty(engine.Receive(Port, Cc(21, 100), values)); // No last value to cross from.
    }

    [Fact]
    public void Only_the_active_mode_matches_and_a_zone_mode_target_switches_it() {
        var assignments = new ControllerAssignments(
            [
                new(
                    "z",
                    "Zone",
                    [
                        new(
                            "mixer",
                            "Mixer",
                            [
                                Assignment("vol", ControlInputKind.ControlChange, 21, Volume),
                                Assignment(
                                    "toFx",
                                    ControlInputKind.Note,
                                    60,
                                    new(ControlTargetKind.ZoneMode, ZoneId: "z", ModeId: "fx")
                                )
                            ]
                        ),
                        new(
                            "fx",
                            "Effects",
                            [
                                Assignment(
                                    "cut",
                                    ControlInputKind.ControlChange,
                                    21,
                                    new(ControlTargetKind.SmartControl, Address: "knob1")
                                ),
                                Assignment(
                                    "toMixer",
                                    ControlInputKind.Note,
                                    61,
                                    new(ControlTargetKind.ZoneMode, ZoneId: "z", ModeId: "mixer")
                                )
                            ]
                        )
                    ],
                    "mixer"
                )
            ]
        );
        var engine = new AssignmentEngine(assignments);
        var values = new Values();
        Assert.Equal("vol", Assert.Single(engine.Receive(Port, Cc(21, 1), values)).AssignmentId);
        Assert.Empty(engine.Receive(Port, [0x90, 61, 127], values)); // Not live in the mixer mode.
        var switched = Assert.Single(engine.Receive(Port, [0x90, 60, 127], values));
        Assert.Equal(ControlTargetKind.ZoneMode, switched.Target.Kind);
        Assert.Equal("fx", engine.Assignments.Zones[0].ActiveModeId);
        Assert.Equal("cut", Assert.Single(engine.Receive(Port, Cc(21, 1), values)).AssignmentId);
        engine.Receive(Port, [0x90, 61, 127], values);
        Assert.Equal("mixer", engine.Assignments.Zones[0].ActiveModeId);
    }

    [Fact]
    public void The_first_assignment_in_order_takes_an_input() {
        var engine = Engine(
            Assignment("first", ControlInputKind.ControlChange, 21, Volume),
            Assignment("second", ControlInputKind.ControlChange, 21, Mute)
        );
        Assert.Equal("first", Assert.Single(engine.Receive(Port, Cc(21, 1), new Values())).AssignmentId);
    }

    [Fact]
    public void Every_mode_contributes_matchers_with_their_families() {
        var assignments = new ControllerAssignments(
            [
                new(
                    "z",
                    "Zone",
                    [
                        new(
                            "a",
                            "A",
                            [
                                Assignment("cc", ControlInputKind.ControlChange, 21, Volume, port: null, channel: null),
                                Assignment("cc14", ControlInputKind.ControlChange14, 7, Volume, channel: 2),
                                Assignment("nrpn", ControlInputKind.Nrpn, 300, Volume)
                            ]
                        ),
                        new(
                            "b",
                            "B",
                            [
                                Assignment("note", ControlInputKind.Note, 36, Mute),
                                Assignment("bend", ControlInputKind.PitchBend, 0, Volume),
                                Assignment("program", ControlInputKind.ProgramChange, 9, Play),
                                Assignment("again", ControlInputKind.ControlChange, 21, Mute, port: null, channel: null)
                            ]
                        )
                    ],
                    "a"
                )
            ]
        );
        Assert.Equal(
            [
                new ControlMatcher(null, 0xB0, -1, 21, MatcherFamily.Single),
                new ControlMatcher(Port, 0xB0, 1, 7, MatcherFamily.ControlChange14),
                new ControlMatcher(Port, 0xB0, 0, -1, MatcherFamily.Nrpn),
                new ControlMatcher(Port, 0x90, 0, 36, MatcherFamily.Single),
                new ControlMatcher(Port, 0xE0, 0, -1, MatcherFamily.Single),
                new ControlMatcher(Port, 0xC0, 0, 9, MatcherFamily.Single)
            ],
            new AssignmentEngine(assignments).Matchers
        );
    }

    [Fact]
    public void Invalid_assignments_are_refused() {
        Assert.Throws<ArgumentException>(static () => Engine(
                Assignment("a", ControlInputKind.ControlChange, 21, Volume),
                Assignment("a", ControlInputKind.ControlChange, 22, Volume)
            )
        );
        Assert.Throws<ArgumentException>(static () =>
            Engine(Assignment("m", ControlInputKind.ControlChange, 21, Volume))
        );
        Assert.Throws<ArgumentException>(static () =>
            Engine(Assignment("a", ControlInputKind.ControlChange, 128, Volume))
        );
        Assert.Throws<ArgumentException>(static () =>
            Engine(Assignment("a", ControlInputKind.ControlChange14, 32, Volume))
        );
        Assert.Throws<ArgumentException>(static () =>
            Engine(Assignment("a", ControlInputKind.ControlChange, 1, Volume, channel: 17))
        );
        Assert.Throws<ArgumentException>(static () =>
            Engine(Assignment("a", ControlInputKind.ControlChange, 1, Volume, max: 1.5))
        );
        Assert.Throws<ArgumentException>(static () => Engine(
                Assignment("a", ControlInputKind.Note, 1, new(ControlTargetKind.ZoneMode, ZoneId: "z", ModeId: "nope"))
            )
        );
        Assert.Throws<ArgumentException>(static () => new AssignmentEngine(
                new([new("z", "Zone", [new("m", "M", [])], "other")])
            )
        );
    }

    [Fact]
    public void Learn_reads_each_kind_of_input() {
        var engine = new AssignmentEngine(ControllerAssignments.Empty);
        Assert.Equal(new(Port, ControlInputKind.ControlChange, 1, 21), engine.Learn(Port, Cc(21, 5)));
        Assert.Equal(new(Port, ControlInputKind.Note, 10, 36), engine.Learn(Port, [0x99, 36, 100]));
        Assert.Null(engine.Learn(Port, [0x89, 36, 0]));
        Assert.Equal(new(Port, ControlInputKind.PitchBend, 1, 0), engine.Learn(Port, [0xE0, 0, 64]));
        Assert.Equal(new(Port, ControlInputKind.ProgramChange, 2, 7), engine.Learn(Port, [0xC1, 7]));
        Assert.Null(engine.Learn(Port, [0xD0, 7])); // Channel pressure is not assignable.
        Assert.Null(engine.Learn(Port, [0xF8]));
    }

    [Fact]
    public void Learn_reads_a_pair_as_14_bit_once_its_LSB_follows_the_MSB() {
        var engine = new AssignmentEngine(ControllerAssignments.Empty);
        Assert.Equal(ControlInputKind.ControlChange, engine.Learn(Port, Cc(7, 64))!.Kind);
        Assert.Equal(new(Port, ControlInputKind.ControlChange14, 1, 7), engine.Learn(Port, Cc(39, 0)));
        Assert.Equal(new(Port, ControlInputKind.ControlChange14, 1, 7), engine.Learn(Port, Cc(7, 65)));
        Assert.Equal(ControlInputKind.ControlChange, engine.Learn(Port, Cc(40, 1))!.Kind); // Not after 8.
    }

    [Fact]
    public void Learn_reads_an_NRPN_after_its_parameter() {
        var engine = new AssignmentEngine(ControllerAssignments.Empty);
        Assert.Null(engine.Learn(Port, Cc(99, 2)));
        Assert.Null(engine.Learn(Port, Cc(98, 9)));
        var nrpn = new ControlInput(Port, ControlInputKind.Nrpn, 1, (2 << 7) | 9);
        Assert.Equal(nrpn, engine.Learn(Port, Cc(6, 1)));
        Assert.Equal(nrpn, engine.Learn(Port, Cc(38, 1)));
        Assert.Equal(nrpn, engine.Learn(Port, Cc(96, 0)));
        engine.Learn(Port, Cc(101, 0));
        Assert.Equal(ControlInputKind.ControlChange, engine.Learn(Port, Cc(6, 1))!.Kind);
    }

    [Fact]
    public void Learned_assignments_go_to_the_first_zone_made_when_there_is_none() {
        var n = 0;
        string Id() => "id" + n++;
        var input = new ControlInput(Port, ControlInputKind.ControlChange, 1, 21);
        var (assignments, made) = AssignmentEngine.Assign(ControllerAssignments.Empty, input, Volume, null, null, Id);
        var zone = Assert.Single(assignments.Zones);
        var mode = Assert.Single(zone.Modes);
        Assert.Equal(mode.Id, zone.ActiveModeId);
        Assert.Equal(made, Assert.Single(mode.Assignments));
        Assert.Equal(
            made with {
                Name = null,
                Input = input,
                Target = Volume,
                Mode = ControlValueMode.Absolute,
                Min = 0,
                Max = 1,
                Pickup = false
            },
            made
        );
        _ = new AssignmentEngine(assignments);

        // The same input again replaces it; a note on mute is a toggle.
        var (again, replaced) = AssignmentEngine.Assign(assignments, input, Mute, null, null, Id);
        Assert.Equal(replaced, Assert.Single(again.Zones[0].Modes[0].Assignments));
        var note = new ControlInput(Port, ControlInputKind.Note, 1, 36);
        Assert.Equal(
            ControlValueMode.Toggle,
            AssignmentEngine.Assign(again, note, Mute, null, null, Id).Assignment.Mode
        );
        Assert.Throws<ArgumentException>(() => AssignmentEngine.Assign(again, note, Mute, "nope", null, Id));
    }

    [Fact]
    public void A_learned_14_bit_pair_replaces_the_7_bit_assignment_its_MSB_made() {
        var n = 0;
        string Id() => "id" + n++;
        var engine = new AssignmentEngine(ControllerAssignments.Empty);
        var assignments = ControllerAssignments.Empty;
        foreach (var message in new[] { Cc(7, 64), Cc(39, 0) }) {
            assignments = AssignmentEngine.Assign(assignments, engine.Learn(Port, message)!, Volume, null, null, Id)
                .Assignments;
        }

        var only = Assert.Single(assignments.Zones[0].Modes[0].Assignments);
        Assert.Equal(ControlInputKind.ControlChange14, only.Input.Kind);
    }

    [Fact]
    public void Absolute_values_stay_in_range_for_any_message() =>
        Gen.Select(Gen.Int[0, 127], Gen.Int[0, 127], Gen.Double[0, 1], Gen.Double[0, 1])
            .Sample(static (controller, value, min, max) => {
                    var engine = Engine(
                        Assignment("a", ControlInputKind.ControlChange, controller, Volume, min: min, max: max)
                    );
                    var v = Single(engine.Receive(Port, Cc(controller, value), new Values()));
                    return v >= Math.Min(min, max) - 1e-12 && v <= Math.Max(min, max) + 1e-12;
                }
            );

    [Fact]
    public void Malformed_messages_change_nothing() =>
        Gen.Byte.Array[0, 4]
            .Sample(static bytes => {
                    var engine = Engine(
                        Assignment("a", ControlInputKind.ControlChange, 21, Volume, channel: null, port: null),
                        Assignment("b", ControlInputKind.Note, 36, Mute, channel: null, port: null)
                    );
                    var changes = engine.Receive(Port, bytes, new Values());
                    _ = engine.Learn(Port, bytes);
                    return changes.Count <= 1;
                }
            );
}
