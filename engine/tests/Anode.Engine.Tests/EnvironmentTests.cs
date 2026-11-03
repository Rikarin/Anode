using Anode.Dsp.Transport;
using Anode.Engine.Environment;
using Anode.Ipc;
using Anode.Ipc.Telemetry;

namespace Anode.Engine.Tests;

/// <summary>
///     The Environment's graph (docs/12-phase-11.md, workstream A's tests): every object kind at sample accuracy, run
///     block by block as the callback runs it (64 frames, at 120 bpm and 48 kHz: a quarter note is 24 000 samples, a
///     sixteenth 6 000), against events worked out by hand; the golden path's patch; the queue's bound; and the
///     telemetry of monitors and faders.
/// </summary>
public sealed class EnvironmentTests {
    internal const int Rate = 48_000;
    internal const int Block = 64;
    internal const int Quarter = 24_000;

    internal static EngineEnvironmentRoute R(int to, int outlet = 0, int inlet = 0) => new(outlet, to, inlet);

    internal static EngineEnvironmentObject O(
        int index,
        EngineEnvironmentKind kind,
        EngineEnvironmentRoute[]? routes = null,
        Dictionary<string, double>? parameters = null
    ) =>
        new(index, kind, parameters ?? [], routes ?? []);

    internal static EnvironmentEvent On(long sample, int note, int velocity = 100, int channel = 1) =>
        new(sample, (byte)(0x90 | (channel - 1)), (byte)note, (byte)velocity);

    internal static EnvironmentEvent Off(long sample, int note, int channel = 1) =>
        new(sample, (byte)(0x80 | (channel - 1)), (byte)note, 0);

    internal static EnvironmentEvent Cc(long sample, int number, int value, int channel = 1) =>
        new(sample, (byte)(0xB0 | (channel - 1)), (byte)number, (byte)value);

    /// <summary>What left the graph, in order: where, and the event.</summary>
    internal sealed class Sink : IEnvironmentSink {
        public List<(string To, EnvironmentEvent Event)> Events { get; } = [];

        public List<(EngineAutomationTarget Target, float Value, long Sample)> Controls { get; } = [];

        public void SequencerInput(in EnvironmentEvent e) => Events.Add(("sequencer", e));

        public void TrackInput(int trackId, in EnvironmentEvent e) => Events.Add(($"track {trackId}", e));

        public void MidiPort(string port, in EnvironmentEvent e) => Events.Add(($"port {port}", e));

        public void Control(EngineAutomationTarget target, float value, long sample) =>
            Controls.Add((target, value, sample));

        /// <summary>The events that reached <paramref name="to" />: sample, status, data 1, data 2.</summary>
        public (long, int, int, int)[] At(string to) => [
            ..Events.Where(e => e.To == to)
                .Select(static e => (e.Event.Sample, (int)e.Event.Status, (int)e.Event.Data1,
                        (int)e.Event.Data2)
                )
        ];
    }

    /// <summary>The transport of a block from engine sample <paramref name="start" /> (project sample = engine sample).</summary>
    internal static TransportContext Transport(long start, bool playing) {
        var quarter = playing ? start / (double)Quarter : 0;
        return new(playing, playing ? start : 0, quarter, 120, 4, 4, Math.Floor(quarter / 4) * 4, Rate, Block);
    }

    /// <summary>
    ///     Runs <paramref name="graph" /> over <paramref name="samples" /> samples block by block, entering each input
    ///     (at a physical input, or into an object) in the block before its own, as the callback does.
    /// </summary>
    internal static Sink Run(
        EnvironmentGraph graph,
        long samples,
        IEnumerable<EnvironmentEvent>? input = null,
        IEnumerable<(int Object, EnvironmentEvent Event)>? into = null,
        bool playing = true,
        Sink? sink = null
    ) {
        sink ??= new();
        var physical = (input ?? []).OrderBy(static e => e.Sample).ToList();
        var screen = (into ?? []).OrderBy(static e => e.Event.Sample).ToList();
        for (long start = 0; start < samples; start += Block) {
            while (physical.Count > 0 && physical[0].Sample < start + Block) {
                graph.Input(physical[0]);
                physical.RemoveAt(0);
            }

            while (screen.Count > 0 && screen[0].Event.Sample < start + Block) {
                graph.InputAt(screen[0].Object, screen[0].Event);
                screen.RemoveAt(0);
            }

            graph.Process(start, Block, Transport(start, playing), sink);
        }

        return sink;
    }

    internal static EnvironmentGraph Compile(params EngineEnvironmentObject[] objects) =>
        EnvironmentGraph.Compile(new(objects), Rate, AudioEngine.MaxFrames);

    [Fact]
    public void The_empty_environment_is_the_default_path_and_compiles_to_nothing() {
        Assert.Same(EnvironmentGraph.Empty, EnvironmentGraph.Compile(null, Rate, Block));
        Assert.Same(EnvironmentGraph.Empty, EnvironmentGraph.Compile(new([]), Rate, Block));
        Assert.True(EnvironmentGraph.Empty.IsEmpty);
        Assert.Empty(EnvironmentGraph.Empty.Nodes);
        var sink = Run(EnvironmentGraph.Empty, 1_000, [On(10, 60)]);
        Assert.Empty(sink.Events); // the engine never runs it: input goes straight to the tracks
        Assert.Equal(0, EnvironmentGraph.Empty.Dropped);
    }

    [Fact]
    public void Physical_inputs_reach_the_sequencer_input_and_a_track_at_their_samples_a_port_filtering_one() {
        var graph = Compile(
            O(0, EngineEnvironmentKind.PhysicalInput, [R(2)]),
            O(1, EngineEnvironmentKind.PhysicalInput, [R(3)]) with { Port = "in-a" },
            O(2, EngineEnvironmentKind.SequencerInput),
            O(3, EngineEnvironmentKind.TrackInput) with { TrackId = 7 }
        );
        EnvironmentEvent[] input = [
            On(5, 60) with { Port = "in-a" }, On(63, 62) with { Port = "in-b" }, On(64, 64) with { Port = "in-a" },
            Off(1_000, 60) with { Port = "in-a" }, Cc(130_001, 1, 64)
        ];
        var sink = Run(graph, 131_072, input);
        Assert.Equal(
            [
                (5, 0x90, 60, 100), (63, 0x90, 62, 100), (64, 0x90, 64, 100), (1_000, 0x80, 60, 0),
                (130_001, 0xB0, 1, 64)
            ],
            sink.At("sequencer")
        );
        Assert.Equal([(5, 0x90, 60, 100), (64, 0x90, 64, 100), (1_000, 0x80, 60, 0)], sink.At("track 7"));
    }

    [Fact]
    public void A_keyboard_plays_what_it_is_given_on_its_channel_at_its_velocity() {
        var graph = Compile(
            O(0, EngineEnvironmentKind.Keyboard, [R(1)], new() { ["channel"] = 3, ["velocity"] = 90 }),
            O(1, EngineEnvironmentKind.TrackInput) with { TrackId = 1 }
        );
        var sink = Run(graph, 2_048, into: [(0, On(100, 60, 30)), (0, Off(1_500, 60))]);
        Assert.Equal([(100, 0x92, 60, 90), (1_500, 0x82, 60, 0)], sink.At("track 1"));
    }

    [Fact]
    public unsafe void A_monitor_passes_everything_and_keeps_its_last_events_for_its_slot() {
        var graph = Compile(
            O(0, EngineEnvironmentKind.PhysicalInput, [R(1)]),
            O(1, EngineEnvironmentKind.Monitor, [R(2)]) with { TelemetrySlot = 3 },
            O(2, EngineEnvironmentKind.SequencerInput)
        );
        var input = Enumerable.Range(0, 10).Select(static i => On(1_000 + 100 * i, 50 + i)).ToArray();
        var sink = Run(graph, 4_096, input);
        Assert.Equal(10, sink.Events.Count);
        var monitors = new EnvironmentMonitorEntry[TelemetryLayout.MaxMonitors];
        var faders = new EnvironmentFaderEntry[TelemetryLayout.MaxFaders];
        monitors[5].Object = 99; // a slot this graph has none in is freed
        graph.Publish(monitors, faders);
        ref var entry = ref monitors[3];
        Assert.Equal(2, entry.Object); // index + 1
        Assert.Equal(10, entry.Count);
        for (var n = 2; n < 10; n++) { // the last eight, event n at n % 8
            var at = n % EnvironmentMonitorEntry.Capacity;
            Assert.Equal(1_000 + 100 * n, entry.Events[2 * at]);
            Assert.Equal(0x90 | ((50 + n) << 8) | (100 << 16), entry.Events[2 * at + 1]);
        }

        Assert.Equal(0, monitors[5].Object);

        // Stopped, a monitor's events have no project sample.
        var stopped = Compile(
            O(0, EngineEnvironmentKind.PhysicalInput, [R(1)]),
            O(1, EngineEnvironmentKind.Monitor) with { TelemetrySlot = 0 }
        );
        Run(stopped, 1_024, [On(70, 60)], playing: false);
        stopped.Publish(monitors, faders);
        Assert.Equal(-1, monitors[0].Events[0]);
        Assert.Equal(0, monitors[3].Object);
    }

    [Fact]
    public void A_fader_moves_by_its_kind_sends_its_value_over_its_range_and_reports_it() {
        var graph = Compile(
            O(0, EngineEnvironmentKind.PhysicalInput, [R(1)]),
            O(
                1,
                EngineEnvironmentKind.Fader,
                [R(2)],
                new() {
                    ["message"] = 0,
                    ["channel"] = 2,
                    ["number"] = 7,
                    ["min"] = 20,
                    ["max"] = 100,
                    ["value"] = 64
                }
            ) with {
                TelemetrySlot = 1
            },
            O(2, EngineEnvironmentKind.TrackInput) with { TrackId = 4 }
        );
        var faders = new EnvironmentFaderEntry[TelemetryLayout.MaxFaders];
        graph.Publish([], faders);
        Assert.Equal((2, 64f), (faders[1].Object, faders[1].Value));
        var sink = Run(
            graph,
            2_048,
            [Cc(10, 7, 127, 2), Cc(20, 7, 0, 2), Cc(30, 7, 64), Cc(40, 10, 5, 2), On(50, 60, 90, 2)],
            [(1, Cc(1_000, 1, 127))] // on screen: any value moves it
        );
        Assert.Equal(
            [
                (10, 0xB1, 7, 100), (20, 0xB1, 7, 20), (30, 0xB0, 7, 64), (40, 0xB1, 10, 5), (50, 0x91, 60, 90),
                (1_000, 0xB1, 7, 100)
            ],
            sink.At("track 4")
        );
        graph.Publish([], faders);
        Assert.Equal(100f, faders[1].Value);

        // A note fader takes its note's velocity; a pitch bend fader its high byte, and sends it as one.
        var bend = Compile(
            O(0, EngineEnvironmentKind.PhysicalInput, [R(1)]),
            O(1, EngineEnvironmentKind.Fader, [R(2)], new() { ["message"] = 3, ["channel"] = 1 }),
            O(2, EngineEnvironmentKind.SequencerInput)
        );
        Assert.Equal([(5, 0xE0, 0, 96)], Run(bend, 64, [new(5, 0xE0, 3, 96)]).At("sequencer"));
    }

    [Fact]
    public void A_transformer_changes_what_matches_out_of_its_first_outlet_and_passes_the_rest_out_of_its_second() {
        var graph = Compile(
            O(0, EngineEnvironmentKind.PhysicalInput, [R(1)]),
            O(1, EngineEnvironmentKind.Transformer, [R(2), R(3, 1)]) with {
                Conditions = [new("status", "equal", 144, 0), new("pitch", "inside", 48, 72)],
                Operations = [new("velocity", "fix", 110, 0), new("pitch", "add", 12, 0), new("channel", "fix", 5, 0)]
            },
            O(2, EngineEnvironmentKind.TrackInput) with { TrackId = 1 },
            O(3, EngineEnvironmentKind.TrackInput) with { TrackId = 2 }
        );
        var sink = Run(graph, 1_024, [On(1, 60, 30), Off(200, 60), On(300, 80), Cc(400, 64, 127), On(500, 72, 1)]);
        Assert.Equal([(1, 0x94, 72, 110), (200, 0x84, 72, 110), (500, 0x94, 84, 110)], sink.At("track 1"));
        Assert.Equal([(300, 0x90, 80, 100), (400, 0xB0, 64, 127)], sink.At("track 2"));

        // Clamped to each field's range; random operations are seeded by the object, so the same each run.
        var clamp = Compile(
            O(0, EngineEnvironmentKind.PhysicalInput, [R(1)]),
            O(1, EngineEnvironmentKind.Transformer, [R(2)]) with {
                Operations = [new("pitch", "add", 100, 0), new("velocity", "subtract", 200, 0)]
            },
            O(2, EngineEnvironmentKind.SequencerInput)
        );
        Assert.Equal([(9, 0x90, 127, 1), (10, 0x80, 127, 0)], Run(clamp, 64, [On(9, 60), Off(10, 60)]).At("sequencer"));
        var random = O(1, EngineEnvironmentKind.Transformer, [R(2)]) with {
            Operations = [new("velocity", "random", 20, 100)]
        };

        EnvironmentGraph Random() =>
            Compile(
                O(0, EngineEnvironmentKind.PhysicalInput, [R(1)]),
                random,
                O(2, EngineEnvironmentKind.SequencerInput)
            );

        var notes = Enumerable.Range(0, 20).Select(static i => On(i, 60)).ToArray();
        var first = Run(Random(), 64, notes).At("sequencer");
        Assert.Equal(first, Run(Random(), 64, notes).At("sequencer"));
        Assert.All(first, static e => Assert.InRange(e.Item4, 20, 100));
        Assert.True(first.Select(static e => e.Item4).Distinct().Count() > 5);
    }

    [Fact]
    public void A_delay_line_repeats_each_event_at_its_taps_across_blocks() {
        var graph = Compile(
            O(0, EngineEnvironmentKind.PhysicalInput, [R(1)]),
            O(1, EngineEnvironmentKind.DelayLine, [R(2)], new() { ["dry"] = 1 }) with {
                Taps = [new(480, 7, -20), new(960, 12, 0), new(1, 70, 0)]
            },
            O(2, EngineEnvironmentKind.SequencerInput)
        );
        var sink = Run(graph, 40_000, [On(1_000, 60), Off(2_000, 60), Cc(3_000, 1, 9)]);
        // 480 ticks at 120 bpm: half a quarter, 12 000 samples; 960 ticks, 24 000; 1 tick, 25 samples (its note at
        // 130 is out of range and dropped).
        Assert.Equal(
            [
                (1_000, 0x90, 60, 100), (2_000, 0x80, 60, 0), (3_000, 0xB0, 1, 9), (3_025, 0xB0, 1, 9),
                (13_000, 0x90, 67, 80), (14_000, 0x80, 67, 0), (15_000, 0xB0, 1, 9), (25_000, 0x90, 72, 100),
                (26_000, 0x80, 72, 0), (27_000, 0xB0, 1, 9)
            ],
            sink.At("sequencer")
        );

        // Without the original, only the taps.
        var wet = Compile(
            O(0, EngineEnvironmentKind.PhysicalInput, [R(1)]),
            O(1, EngineEnvironmentKind.DelayLine, [R(2)], new() { ["dry"] = 0 }) with { Taps = [new(240, 0, 0)] },
            O(2, EngineEnvironmentKind.SequencerInput)
        );
        Assert.Equal([(6_010, 0x90, 60, 100)], Run(wet, 8_000, [On(10, 60)]).At("sequencer"));
    }

    [Fact]
    public void A_delay_line_past_the_queue_drops_and_counts() {
        var graph = Compile(
            O(0, EngineEnvironmentKind.PhysicalInput, [R(1)]),
            O(1, EngineEnvironmentKind.DelayLine, [R(2)], new() { ["dry"] = 0 }) with {
                Taps = [..Enumerable.Range(1, 16).Select(static t => new EngineDelayTap(3_840, t, 0))]
            },
            O(2, EngineEnvironmentKind.SequencerInput)
        );
        var input = Enumerable.Range(0, 300).Select(static i => Cc(i * 10, 1, i % 128)).ToArray();
        Run(graph, 3_008, input);
        Assert.Equal(300 * 16 - EnvironmentGraph.QueueCapacity, graph.Dropped);
    }

    /// <summary>
    ///     The golden path's patch (docs/12-phase-11.md, exit criterion 7): physical input → transformer (notes +12) →
    ///     arpeggiator (up, 1/16) → a track's input, and a monitor on the transformer's output.
    /// </summary>
    internal static EngineEnvironment GoldenPatch(int trackId, int monitorSlot = 0) =>
        new(
            [
                O(0, EngineEnvironmentKind.PhysicalInput, [R(1)]),
                O(1, EngineEnvironmentKind.Transformer, [R(2), R(3), R(2, 1)]) with {
                    Conditions = [new("status", "equal", 144, 0)], Operations = [new("pitch", "add", 12, 0)]
                },
                O(2, EngineEnvironmentKind.Monitor) with { TelemetrySlot = monitorSlot },
                O(3, EngineEnvironmentKind.Arpeggiator, [R(4)], new() { ["mode"] = 0, ["octaves"] = 1 }),
                O(4, EngineEnvironmentKind.TrackInput) with { TrackId = trackId }
            ]
        );

    [Fact]
    public unsafe void The_golden_patch_transposes_and_arpeggiates_on_the_sixteenth_grid() {
        var graph = EnvironmentGraph.Compile(GoldenPatch(1), Rate, AudioEngine.MaxFrames);
        // A chord at 10 000 (a little over the fifth sixteenth's start at 6 000), released at 40 000.
        var sink = Run(
            graph,
            48_000,
            [
                On(10_000, 60, 90), On(10_000, 64, 91), On(10_000, 67, 92), Off(40_000, 60),
                Off(40_000, 64), Off(40_000, 67)
            ]
        );
        // By hand: the first step at or after the chord is the sixteenth at 12 000; up through 72, 76, 79, one a
        // sixteenth (6 000 samples), each held 80 % of it (4 800 samples) at the velocity it was played with; the
        // chord's release ends the arpeggio, the last note at its gate.
        Assert.Equal(
            [
                (12_000, 0x90, 72, 90), (16_800, 0x80, 72, 0), (18_000, 0x90, 76, 91), (22_800, 0x80, 76, 0),
                (24_000, 0x90, 79, 92), (28_800, 0x80, 79, 0), (30_000, 0x90, 72, 90), (34_800, 0x80, 72, 0),
                (36_000, 0x90, 76, 91), (40_800, 0x80, 76, 0)
            ],
            sink.At("track 1")
        );
        Assert.All(sink.Events, static e => Assert.True(e.Event.Generated));

        // The monitor saw the transposed chord and its release.
        var monitors = new EnvironmentMonitorEntry[TelemetryLayout.MaxMonitors];
        graph.Publish(monitors, []);
        Assert.Equal(6, monitors[0].Count);
        int[] seen = [..Enumerable.Range(0, 6).Select(n => (int)((monitors[0].Events[2 * n + 1] >> 8) & 0x7F))];
        Assert.Equal([72, 76, 79, 72, 76, 79], seen);
        Assert.Equal(10_000, monitors[0].Events[0]);
        Assert.Equal(40_000, monitors[0].Events[2 * 5]);

        // Reset (stop, locate) forgets the chord: nothing more plays.
        Run(graph, 48_000, [On(10_000, 60)]);
        graph.Reset();
        var after = new Sink();
        for (long start = 48_000; start < 96_000; start += Block) {
            graph.Process(start, Block, Transport(start, true), after);
        }

        Assert.Empty(after.Events);
    }

    [Fact]
    public void A_chord_memoriser_plays_its_chord_on_every_key() {
        var graph = Compile(
            O(0, EngineEnvironmentKind.PhysicalInput, [R(1)]),
            O(1, EngineEnvironmentKind.ChordMemorizer, [R(2)]) with { Chord = [0, 3, 7] },
            O(2, EngineEnvironmentKind.SequencerInput)
        );
        var sink = Run(graph, 1_024, [On(100, 60, 80), Off(700, 60)]);
        Assert.Equal(
            [
                (100, 0x90, 60, 80), (100, 0x90, 63, 80), (100, 0x90, 67, 80), (700, 0x80, 60, 0), (700, 0x80, 63, 0),
                (700, 0x80, 67, 0)
            ],
            [
                ..sink.At("sequencer")
                    .OrderBy(static e => e.Item1)
                    .ThenBy(static e => e.Item2)
                    .ThenBy(static e => e.Item3)
            ]
        );
        Assert.All(sink.Events, static e => Assert.False(e.Event.Generated)); // played input's time

        // Without a chord, the Chord Trigger's own (major, its default).
        var major = Compile(
            O(0, EngineEnvironmentKind.PhysicalInput, [R(1)]),
            O(1, EngineEnvironmentKind.ChordMemorizer, [R(2)]),
            O(2, EngineEnvironmentKind.SequencerInput)
        );
        Assert.Equal(
            [62, 66, 69],
            Run(major, 64, [On(3, 62)]).At("sequencer").Select(static e => e.Item3).Order()
        );
    }

    [Fact]
    public void A_channel_splitter_sends_each_channel_out_of_its_own_outlet() {
        var graph = Compile(
            O(0, EngineEnvironmentKind.PhysicalInput, [R(1)]),
            O(1, EngineEnvironmentKind.ChannelSplitter, [R(2, 1), R(3, 4), R(4, 16)]),
            O(2, EngineEnvironmentKind.TrackInput) with { TrackId = 2 },
            O(3, EngineEnvironmentKind.TrackInput) with { TrackId = 5 },
            O(4, EngineEnvironmentKind.SequencerInput)
        );
        var sink = Run(graph, 128, [On(1, 60, 100, 2), On(2, 61, 100, 5), On(3, 62), Cc(70, 7, 1, 5)]);
        Assert.Equal([(1, 0x91, 60, 100)], sink.At("track 2"));
        Assert.Equal([(2, 0x94, 61, 100), (70, 0xB4, 7, 1)], sink.At("track 5"));
        Assert.Empty(sink.At("sequencer"));
    }

    [Fact]
    public void Ports_and_control_targets_take_their_events_at_their_samples() {
        var volume = new EngineAutomationTarget(3, 1, "volume");
        var graph = Compile(
            O(0, EngineEnvironmentKind.PhysicalInput, [R(1), R(2), R(3)]),
            O(1, EngineEnvironmentKind.MidiPort) with { Port = "out-a" },
            O(2, EngineEnvironmentKind.ControlTarget) with { TrackId = 1, Control = volume },
            O(3, EngineEnvironmentKind.MidiPort) // no port: nowhere
        );
        var sink = Run(graph, 256, [Cc(10, 7, 127), Off(20, 60), new(130, 0xE0, 0x7F, 0x7F), new(140, 0xC0, 0, 0)]);
        Assert.Equal(
            [(10, 0xB0, 7, 127), (20, 0x80, 60, 0), (130, 0xE0, 127, 127), (140, 0xC0, 0, 0)],
            sink.At("port out-a")
        );
        Assert.Equal([(volume, 1f, 10L), (volume, 1f, 130L), (volume, 0f, 140L)], sink.Controls);
    }

    [Fact]
    public void Routes_that_do_not_go_forward_are_left_out_and_objects_are_by_index() {
        var graph = Compile(
            O(1, EngineEnvironmentKind.SequencerInput),
            O(0, EngineEnvironmentKind.PhysicalInput, [R(1), R(0), R(9)])
        );
        Assert.Equal(EngineEnvironmentKind.PhysicalInput, graph.Nodes[0].Settings.Kind);
        Assert.Equal([R(1)], graph.Nodes[0].Settings.Routes);
        Assert.Single(Run(graph, 64, [On(1, 60)]).Events);
    }
}
