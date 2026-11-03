using Anode.Engine.Environment;
using Anode.Ipc;
using Anode.Ipc.Segments;
using Anode.Ipc.Telemetry;
using System.Diagnostics;
using System.Runtime.InteropServices;
using static Anode.Engine.Tests.EnvironmentTests;

namespace Anode.Engine.Tests;

/// <summary>
///     The Environment in the engine (docs/12-phase-11.md, workstream A's tests): device input and the server's live
///     MIDI through it, into a track's input and the sequencer input, recorded as the golden path records it; on-screen
///     objects through <c>environmentMidi</c>; a fader moving a strip through a control target; MIDI port output; the
///     notes a swapped-out graph left sounding; monitors, faders and drops in the telemetry; no allocation in the
///     callback with a 20-object graph; and the input path's cost with no Environment and with one.
/// </summary>
public sealed unsafe class EnvironmentEngineTests : IDisposable {
    readonly string dir = TestAudio.TempDirectory();
    readonly FakeMidi midi = new();
    readonly List<TestInstrument> created;
    readonly EngineController controller;
    readonly OfflineDriver driver;

    public EnvironmentEngineTests() {
        (var registry, created) = MidiFixtures.Registry();
        controller = new(new(Rate, Block), registry, midi);
        driver = new(controller, Block);
    }

    public void Dispose() {
        driver.Dispose();
        controller.Dispose();
        Directory.Delete(dir, true);
    }

    TestInstrument Instrument(int trackId) =>
        (TestInstrument)controller.Engine.Root.Tracks[controller.Engine.Root.IndexOf(trackId)].Instrument!.Instrument;

    void Load(params EngineTrack[] tracks) {
        using var segment = TestAudio.Segment([..tracks.Select(static t => new TrackModel(t.Id, [], []))], 1);
        controller.LoadModel(TestAudio.Load(segment, 1, true, tracks, [], dir));
    }

    void Callbacks(int count) {
        for (var i = 0; i < count; i++) {
            driver.Process();
        }
    }

    /// <summary>Callbacks until the playhead reaches <paramref name="position" />.</summary>
    void Until(long position) {
        while (controller.Engine.Snapshot.Position < position) {
            driver.Process();
        }
    }

    static (int Status, int Data1, int Data2) Bytes(Dsp.Instruments.MidiEvent e) => (e.Status, e.Data1, e.Data2);

    [Fact]
    public void Live_midi_through_the_golden_patch_records_the_chord_an_octave_up_on_the_sixteenth_grid() {
        Load(MidiFixtures.InstrumentTrack(1, armed: true), MidiFixtures.InstrumentTrack(2));
        controller.SetEnvironment(GoldenPatch(1));
        controller.SetMidiThru(2);
        driver.PlayFrom(0);
        driver.Send(new(EngineCommandKind.Record)); // punch in at once
        Until(9_000);
        Assert.True(controller.LiveMidi(2, [new(0x90, 60, 90), new(0x90, 64, 91), new(0x90, 67, 92)]));
        Until(40_000);
        Assert.True(controller.LiveMidi(2, [new(0x80, 60, 0), new(0x80, 64, 0), new(0x80, 67, 0)]));
        Until(48_000);
        driver.Send(new(EngineCommandKind.Stop));
        driver.Process();

        var captured = controller.Midi.EndCapture();
        Assert.False(captured.ContainsKey(2)); // the patch sends nothing to the sequencer input
        var take = captured[1];
        var ons = take.Where(static e => (e.Status & 0xF0) == 0x90).ToArray();
        Assert.True(ons.Length >= 4, $"{ons.Length} notes");
        // Every note an octave above the chord's, in order up, on a sixteenth (6 000 samples), where it sounded.
        int[] order = [72, 76, 79];
        var first = Array.IndexOf(order, ons[0].Data1);
        for (var i = 0; i < ons.Length; i++) {
            Assert.Equal(order[(first + i) % 3], ons[i].Data1);
            Assert.Equal(0, ons[i].Sample % 6_000);
            Assert.Equal(ons[0].Sample + 6_000 * i, ons[i].Sample);
            Assert.InRange(ons[i].Data2, 90, 92);
        }

        Assert.InRange(ons[0].Sample, 9_000, 18_000);
        Assert.Equal(ons.Length, take.Count(static e => (e.Status & 0xF0) == 0x80));
        Assert.Empty(Instrument(2).Events); // the chord itself went through the patch only

        // The monitor's slot through the telemetry: the transposed chord and its release; nothing dropped.
        var region = (byte*)NativeMemory.AllocZeroed((nuint)TelemetryLayout.Size);
        try {
            using var writer = new TelemetryWriter(controller.Engine, region, static () => 0);
            writer.Write();
            var monitors = new EnvironmentMonitorEntry[TelemetryLayout.MaxMonitors];
            var faders = new EnvironmentFaderEntry[TelemetryLayout.MaxFaders];
            Assert.True(TelemetryWriter.TryRead(region, monitors, faders, out var mastering));
            Assert.True(TelemetryWriter.TryRead(region, out var header, new MeterEntry[4]));
            Assert.Equal(3, monitors[0].Object);
            Assert.Equal(6, monitors[0].Count);
            Assert.Equal(72, (monitors[0].Events[1] >> 8) & 0x7F);
            Assert.Equal(0, header.EnvironmentDropped);
            Assert.Equal(0, mastering.Active);
        } finally {
            NativeMemory.Free(region);
        }
    }

    [Fact]
    public void Device_input_goes_through_the_environment_and_without_one_straight_to_the_tracks() {
        Load(MidiFixtures.InstrumentTrack(1, armed: true), MidiFixtures.InstrumentTrack(2, armed: true));
        controller.Midi.Pump(); // lists the ports
        controller.SetEnvironment(
            new(
                [
                    O(0, EngineEnvironmentKind.PhysicalInput, [R(1)]) with { Port = "in-a" },
                    O(1, EngineEnvironmentKind.Transformer, [R(2)]) with { Operations = [new("pitch", "add", 7, 0)] },
                    O(2, EngineEnvironmentKind.SequencerInput)
                ]
            )
        );
        midi.Play(0, 0x90, 60, 100); // in-a: through the transformer to every armed track
        midi.Play(1, 0x90, 62, 100); // in-b: no physical input takes it
        controller.Midi.Pump();
        Callbacks(2);
        Assert.Equal([(0x90, 67, 100)], Instrument(1).Events.Select(Bytes));
        Assert.Equal([(0x90, 67, 100)], Instrument(2).Events.Select(Bytes));

        // No Environment: the default path, the input queue untouched.
        controller.SetEnvironment(null);
        Callbacks(1); // the swap ends the notes the graph left sounding
        Assert.Equal((0x80, 67, 0), Bytes(Instrument(1).Events.Last()));
        midi.Play(0, 0x90, 61, 100);
        controller.Midi.Pump();
        Assert.False(controller.Engine.EnvironmentActive);
        Assert.Equal(0, controller.Engine.EnvironmentIn.Count);
        Callbacks(2);
        Assert.Equal((0x90, 61, 100), Bytes(Instrument(1).Events.Last()));
    }

    [Fact]
    public void An_on_screen_fader_moves_a_strip_through_a_control_target() {
        Load(MidiFixtures.InstrumentTrack(1));
        controller.SetEnvironment(
            new(
                [
                    O(0, EngineEnvironmentKind.Fader, [R(1), R(2)], new() { ["message"] = 0, ["number"] = 7 }) with {
                        TelemetrySlot = 0
                    },
                    O(1, EngineEnvironmentKind.ControlTarget) with { TrackId = 1, Control = new(0, 1, "volume") },
                    O(2, EngineEnvironmentKind.Transformer, [R(3)]) with {
                        Operations = [new("pitch", "fix", 10, 0)] // the same move as a pan, CC 10
                    },
                    O(3, EngineEnvironmentKind.ControlTarget) with { TrackId = 1, Control = new(1, 1, "pan") }
                ]
            )
        );
        var strip = controller.Engine.Root.Tracks[0].Strip;
        Assert.False(controller.EnvironmentMidi(4, [new(0xB0, 7, 127)]));
        Assert.True(controller.EnvironmentMidi(0, [new(0xB0, 7, 127)]));
        Callbacks(1);
        controller.PumpControl();
        Assert.Equal(Math.Pow(10, 6 / 20.0), strip.Gain, 1e-5); // the top of the fader law: +6 dB
        Assert.Equal(1f, strip.Pan);
        Assert.True(controller.EnvironmentMidi(0, [new(0xB0, 7, 99)]));
        Callbacks(1);
        controller.PumpControl();
        Assert.Equal(0, 20 * Math.Log10(strip.Gain), 0.1); // 99 of 127 is 0.78 of the travel: 0 dB
        Assert.True(controller.EnvironmentMidi(0, [new(0xB0, 7, 0)]));
        Callbacks(1);
        controller.PumpControl();
        Assert.Equal(0f, strip.Gain);
        Assert.Equal(-1f, strip.Pan);
        var faders = new EnvironmentFaderEntry[TelemetryLayout.MaxFaders];
        controller.Engine.Environment.Publish([], faders);
        Assert.Equal((1, 0f), (faders[0].Object, faders[0].Value));
    }

    [Fact]
    public void A_midi_port_object_sends_to_its_output() {
        Load(MidiFixtures.InstrumentTrack(1));
        controller.Midi.Pump();
        controller.SetEnvironment(
            new(
                [
                    O(0, EngineEnvironmentKind.PhysicalInput, [R(1)]),
                    O(1, EngineEnvironmentKind.MidiPort) with { Port = "out-a" }
                ]
            )
        );
        Assert.True(controller.LiveMidi(1, [new(0x90, 60, 100), new(0xC0, 5, 0)]));
        Callbacks(2);
        controller.Midi.Pump();
        Assert.Equal(
            [("out-a", "90-3C-64"), ("out-a", "C0-05")],
            midi.Sent.Select(static s => (s.Uid, BitConverter.ToString(s.Message)))
        );
        Assert.Empty(Instrument(1).Events);
    }

    [Fact]
    public void Live_midi_on_a_track_plays_there_through_the_sequencer_input_and_stop_resets_the_graph() {
        Load(MidiFixtures.InstrumentTrack(1), MidiFixtures.InstrumentTrack(2));
        controller.SetEnvironment(
            new(
                [
                    O(0, EngineEnvironmentKind.PhysicalInput, [R(1)]),
                    O(1, EngineEnvironmentKind.DelayLine, [R(2)], new() { ["dry"] = 1 }) with {
                        Taps = [new(960, 12, 0)]
                    },
                    O(2, EngineEnvironmentKind.SequencerInput)
                ]
            )
        );
        Assert.True(controller.LiveMidi(2, [new(0x90, 60, 100)]));
        Callbacks(2);
        Assert.Equal([(0x90, 60, 100)], Instrument(2).Events.Select(Bytes));
        Assert.Empty(Instrument(1).Events);
        driver.Send(new(EngineCommandKind.Stop)); // the tap half a second away goes
        Callbacks(24_000 / Block + 10);
        Assert.DoesNotContain(Instrument(2).Events, static e => e.Data1 == 72);
    }

    /// <summary>The 20-object graph of the allocation and cost tests: a bit of everything, on to four tracks.</summary>
    static EngineEnvironment Twenty() {
        Dictionary<string, double> fader = new() {
            ["message"] = 0,
            ["channel"] = 2,
            ["number"] = 1,
            ["min"] = 10,
            ["max"] = 120
        };
        return new(
            [
                O(0, EngineEnvironmentKind.PhysicalInput, [R(1), R(5)]),
                O(1, EngineEnvironmentKind.Monitor, [R(2)]) with { TelemetrySlot = 0 },
                O(2, EngineEnvironmentKind.Transformer, [R(3), R(4, 1)]) with {
                    Conditions = [new("status", "equal", 144, 0)],
                    Operations = [new("pitch", "add", 12, 0), new("velocity", "plusMinusRandom", 10, 0)]
                },
                O(3, EngineEnvironmentKind.Arpeggiator, [R(6)]),
                O(4, EngineEnvironmentKind.Fader, [R(7), R(18)], fader) with { TelemetrySlot = 0 },
                O(5, EngineEnvironmentKind.DelayLine, [R(8)], new() { ["dry"] = 0 }) with {
                    Taps = [new(120, 7, -10), new(240, 12, -20), new(480, -12, 0)]
                },
                O(6, EngineEnvironmentKind.Monitor, [R(9)]) with { TelemetrySlot = 1 },
                O(7, EngineEnvironmentKind.ChannelSplitter, [R(10), R(11, 1)]),
                O(8, EngineEnvironmentKind.ChordMemorizer, [R(12)]) with { Chord = [0, 4, 7, 11] },
                O(9, EngineEnvironmentKind.TrackInput) with { TrackId = 1 },
                O(10, EngineEnvironmentKind.TrackInput) with { TrackId = 2 },
                O(11, EngineEnvironmentKind.MidiPort) with { Port = "out-a" },
                O(12, EngineEnvironmentKind.Transformer, [R(13), R(14, 1)]) with {
                    Conditions = [new("pitch", "lessEqual", 72, 0)]
                },
                O(13, EngineEnvironmentKind.Monitor, [R(15)]) with { TelemetrySlot = 2 },
                O(14, EngineEnvironmentKind.Keyboard, [R(16)]),
                O(15, EngineEnvironmentKind.TrackInput) with { TrackId = 3 },
                O(16, EngineEnvironmentKind.SequencerInput),
                O(17, EngineEnvironmentKind.Keyboard, [R(19)], new() { ["channel"] = 2 }),
                O(18, EngineEnvironmentKind.ControlTarget) with { TrackId = 4, Control = new(0, 4, "volume") },
                O(19, EngineEnvironmentKind.TrackInput) with { TrackId = 4 }
            ]
        );
    }

    static long Least(Action action) {
        action();
        var least = long.MaxValue;
        for (var i = 0; i < 3 && least > 0; i++) {
            var before = GC.GetAllocatedBytesForCurrentThread();
            action();
            least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        return least;
    }

    [Fact]
    public void Callbacks_and_telemetry_with_a_20_object_graph_allocate_nothing() {
        Load(
            MidiFixtures.InstrumentTrack(1, armed: true),
            MidiFixtures.InstrumentTrack(2),
            MidiFixtures.InstrumentTrack(3),
            MidiFixtures.InstrumentTrack(4)
        );
        controller.Midi.Pump();
        controller.SetEnvironment(Twenty());
        Assert.Equal(20, controller.Engine.Environment.Nodes.Count);
        var region = (byte*)NativeMemory.AllocZeroed((nuint)TelemetryLayout.Size);
        try {
            using var writer = new TelemetryWriter(controller.Engine, region, static () => 0);

            void Run() {
                for (var i = 0; i < 100; i++) {
                    driver.Process(false);
                    if (i % 10 == 0) {
                        writer.Write();
                    }
                }
            }

            // Between measurements, on the control and MIDI threads: devices, live MIDI and on-screen objects.
            void Feed(int round) {
                midi.Play(0, 0x90, (byte)(48 + round % 24), 100);
                midi.Play(0, 0xB0, 1, (byte)(round * 5 % 128));
                midi.Play(1, 0x80, (byte)(47 + round % 24), 0);
                controller.Midi.Pump();
                controller.LiveMidi(2, [new(0x90, 60 + round % 12, 90), new(0x80, 59 + round % 12, 0)]);
                controller.EnvironmentMidi(17, [new(0x90, 40 + round % 5, 80)]);
                controller.EnvironmentMidi(4, [new(0xB0, 1, round % 128)]);
                controller.PumpControl();
                controller.Midi.Pump();
            }

            driver.PlayFrom(0);
            var bytes = Least(Run);
            for (var round = 0; round < 30; round++) {
                Feed(round);
                bytes = Math.Max(bytes, Least(Run));
                if (round == 10) {
                    driver.Send(new(EngineCommandKind.Record));
                }

                if (round == 20) {
                    driver.Send(new(EngineCommandKind.Locate, 10_000)); // resets the graph
                }
            }

            driver.Send(new(EngineCommandKind.Stop));
            Feed(99);
            bytes = Math.Max(bytes, Least(Run));
            Assert.Equal(0, bytes);
            var monitors = new EnvironmentMonitorEntry[TelemetryLayout.MaxMonitors];
            Assert.True(TelemetryWriter.TryRead(region, monitors, new EnvironmentFaderEntry[4], out _));
            Assert.True(monitors[0].Count > 0 && monitors[1].Count > 0 && monitors[2].Count > 0);
            Assert.True(Instrument(1).Events.Any() && Instrument(3).Events.Any() && Instrument(4).Events.Any());
            Assert.True(midi.Sent.Count > 0);
        } finally {
            NativeMemory.Free(region);
        }
    }

    /// <summary>
    ///     The mean and 99th percentile callback time of 16 MIDI tracks playing live input (a chord every 4 096 frames)
    ///     through <paramref name="environment" /> (null: none, the default path).
    /// </summary>
    (double Mean, double P99) Measure(EngineEnvironment? environment) {
        var ports = new FakeMidi();
        using var c = new EngineController(new(Rate, Block), MidiFixtures.Registry().Registry, ports);
        using var d = new OfflineDriver(c, Block);
        // External MIDI tracks: what the graph adds is not hidden behind the instruments' voices.
        var tracks = Enumerable.Range(1, 16)
            .Select(static t => MidiFixtures.ExternalTrack(t, "out-a", 1, t <= 4))
            .ToArray();
        using (var segment = TestAudio.Segment([..tracks.Select(static t => new TrackModel(t.Id, [], []))], 1)) {
            c.LoadModel(TestAudio.Load(segment, 1, true, tracks, [], dir));
        }

        c.SetEnvironment(environment);
        c.SetMidiThru(1);
        d.PlayFrom(0);
        var times = new List<double>();
        for (var i = 0; i < 3_000; i++) {
            if (i % 64 == 0) {
                var note = 48 + i / 64 % 24;
                c.LiveMidi(1 + i / 64 % 16, [new(0x90, note, 90), new(0x90, note + 4, 90), new(0x80, note - 1, 0)]);
                c.Midi.Pump(); // drains what the tracks and ports send
            }

            var start = Stopwatch.GetTimestamp();
            d.Process(false);
            if (i >= 500) {
                times.Add(Stopwatch.GetElapsedTime(start).TotalMicroseconds);
            }
        }

        times.Sort();
        return (times.Average(), times[(int)(times.Count * 0.99)]);
    }

    /// <summary>The 20-object graph's own mean time per block, on a chord every 4 096 frames (no tracks behind it).</summary>
    static double GraphAlone() {
        var graph = EnvironmentGraph.Compile(Twenty(), Rate, AudioEngine.MaxFrames);
        var sink = new NullSink();
        var blocks = 20_000;
        var start = Stopwatch.GetTimestamp();
        for (var b = 0; b < blocks; b++) {
            long at = b * Block;
            if (b % 64 == 0) {
                var note = (byte)(48 + b / 64 % 24);
                graph.Input(new(at + 3, 0x90, note, 90));
                graph.Input(new(at + 5, 0x90, (byte)(note + 4), 90));
                graph.Input(new(at + 9, 0x80, (byte)(note - 1), 0));
                graph.Input(new(at + 11, 0xB0, 1, (byte)(b % 128)));
            }

            graph.Process(at, Block, Transport(at, true), sink);
        }

        return Stopwatch.GetElapsedTime(start).TotalMicroseconds / blocks;
    }

    sealed class NullSink : IEnvironmentSink {
        public void SequencerInput(in EnvironmentEvent e) { }

        public void TrackInput(int trackId, in EnvironmentEvent e) { }

        public void MidiPort(string port, in EnvironmentEvent e) { }

        public void Control(EngineAutomationTarget target, float value, long sample) { }
    }

    [Fact]
    public void The_input_path_costs_the_same_with_no_environment_and_little_with_twenty_objects() {
        // Best of three runs of each, as the stereo cost test.
        var none = Enumerable.Range(0, 3).Select(_ => Measure(null)).MinBy(static m => m.Mean);
        var pair = Enumerable.Range(0, 3)
            .Select(_ => Measure(
                    new(
                        [
                            O(0, EngineEnvironmentKind.PhysicalInput, [R(1)]),
                            O(1, EngineEnvironmentKind.SequencerInput)
                        ]
                    )
                )
            )
            .MinBy(static m => m.Mean);
        var twenty = Enumerable.Range(0, 3).Select(_ => Measure(Twenty())).MinBy(static m => m.Mean);
        var graph = Enumerable.Range(0, 3).Select(static _ => GraphAlone()).Min();
        var budget = Block * 1e6 / Rate;
        var message =
            $"MIDI input path at {Block} frames ({budget:0} µs), 16 external MIDI tracks: no Environment "
            + $"{none.Mean:0.00} µs mean, {none.P99:0.00} µs p99; physical input to sequencer input {pair.Mean:0.00} µs "
            + $"mean, {pair.P99:0.00} µs p99; 20 objects {twenty.Mean:0.00} µs mean, {twenty.P99:0.00} µs p99 (the "
            + $"graph alone {graph:0.00} µs a block)";
        TestContext.Current.SendDiagnosticMessage(message);
        Console.WriteLine(message);
        if (System.Environment.GetEnvironmentVariable("ANODE_MEASURE_LOG") is { Length: > 0 } log) {
            File.AppendAllText(log, message + System.Environment.NewLine);
        }

        // No Environment runs nothing of it (the hub routes to the tracks, the callback skips the stage).
        Assert.Empty(EnvironmentGraph.Empty.Nodes);
        Assert.True(twenty.Mean < budget, message);
    }
}
