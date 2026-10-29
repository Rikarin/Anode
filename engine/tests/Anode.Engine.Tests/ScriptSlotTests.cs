using Anode.Dsp.MidiEffects;
using Anode.Engine.Mixing;
using Anode.Ipc;
using Anode.Ipc.Segments;
using System.Diagnostics;

namespace Anode.Engine.Tests;

/// <summary>
///     Phase 7's MIDI FX slots with run-time parameters and scripts (docs/08-phase-7.md, workstream A), through a
///     stand-in for Scripter (<see cref="ScriptEffect" />): the parameter list is reported, addressed by id and automated
///     (also after the script changes it); a new script compiles while the old one plays and swaps in without a gap;
///     errors bypass the slot and are reported with their line; the log is limited to 50 lines a second per slot.
/// </summary>
public sealed class ScriptSlotTests : IDisposable {
    const int Rate = 48_000;
    const int Block = 64;
    readonly string dir = TestAudio.TempDirectory();
    readonly List<IMidiEffect> created = [];
    readonly ScriptGate gate = new();
    readonly List<SlotParametersMessage> parameters = [];
    readonly List<ScriptLogMessage> logs = [];
    readonly List<PluginProblemMessage> problems = [];
    readonly EngineController controller;
    readonly OfflineDriver driver;

    public ScriptSlotTests() {
        controller = new(new(Rate, Block), TestMidiEffects.Registry(created, gate)) {
            SlotParameters = parameters.Add, ScriptLog = logs.Add, PluginProblem = problems.Add
        };
        driver = new(controller, Block);
    }

    public void Dispose() {
        driver.Dispose();
        controller.Dispose();
        gate.Dispose();
        Directory.Delete(dir, true);
    }

    static EngineInsert Script(string id, string source, Dictionary<string, double>? values = null) =>
        new(id, TestMidiEffects.Script, false, values ?? [], Source: source);

    static EngineStrip Strip(params EngineInsert[] midiEffects) => TestAudio.Unity with { MidiEffects = midiEffects };

    void Load(EngineInsert slot, PlaybackEvent[]? events = null, EngineAutomationTarget[]? targets = null) {
        using var segment = TestAudio.Segment([new(1, events ?? [], [])], 1);
        controller.LoadModel(
            TestAudio.Load(
                segment,
                1,
                true,
                [MidiFixtures.InstrumentTrack(1) with { Strip = Strip(slot) }],
                [],
                dir
            ) with {
                AutomationTargets = targets
            }
        );
    }

    TestInstrument Instrument =>
        (TestInstrument)controller.Engine.Root.Tracks[controller.Engine.Root.IndexOf(1)].Instrument!.Instrument;

    ScriptEffect[] Scripts => [..created.OfType<ScriptEffect>()];

    /// <summary>Plays the live notes as note-on and note-off in one block; returns the note-ons the instrument heard.</summary>
    List<int> Live(params int[] notes) {
        var first = Instrument.Logged;
        Assert.True(controller.LiveMidi(1, [..notes.Select(static n => new EngineMidiEvent(0x90, n, 100))]));
        Assert.True(controller.LiveMidi(1, [..notes.Select(static n => new EngineMidiEvent(0x80, n, 0))]));
        driver.Process();
        return NoteOns(first);
    }

    List<int> NoteOns(int first) => [
        ..Instrument.Log.Skip(first)
            .Take(Instrument.Logged - first)
            .Where(static e => (e.Event.Status & 0xF0) == 0x90 && e.Event.Data2 > 0)
            .Select(static e => (int)e.Event.Data1)
    ];

    List<int> NoteOffs(int first) => [
        ..Instrument.Log.Skip(first)
            .Take(Instrument.Logged - first)
            .Where(static e => (e.Event.Status & 0xF0) == 0x80)
            .Select(static e => (int)e.Event.Data1)
    ];

    static PlaybackEvent Automation(long position, float value, bool step = false) =>
        new() {
            SamplePos = position,
            Track = 1,
            Kind = PlaybackEventKind.Automation,
            Data1 = step ? (byte)1 : (byte)0,
            Value = value,
            Target = 0
        };

    /// <summary>Plays the first <paramref name="frames" /> frames; returns the note-ons the instrument heard, by sample.</summary>
    List<(long Sample, int Note)> Play(int frames) {
        driver.PlayFrom(0);
        var rendered = Instrument.Rendered;
        for (var i = 0; i < frames / Block; i++) {
            driver.Process();
        }

        return [
            ..Instrument.Log.Take(Instrument.Logged)
                .Where(e => e.At >= rendered && (e.Event.Status & 0xF0) == 0x90 && e.Event.Data2 > 0)
                .Select(e => (e.At - rendered, (int)e.Event.Data1))
        ];
    }

    [Fact]
    public void Run_time_parameters_are_reported_addressed_by_id_and_automated_as_their_list_changes() {
        // shift is automated: 0 until 2,000, then 7.
        PlaybackEvent[] events = [
            ..MidiFixtures.Notes((1_000, 500, 60), (3_000, 500, 64))
                .Concat([Automation(0, 0), Automation(2_000, 7, true)])
                .OrderBy(static e => e.SamplePos)
        ];
        Load(
            Script("s", "param depth 0\nparam shift 0", new() { ["shift"] = 3 }),
            events,
            [new(0, 1, "midiEffect", "s", "shift")]
        );
        var reported = Assert.Single(parameters);
        Assert.Equal((1, "s"), (reported.TrackId, reported.SlotId));
        Assert.Equal(["depth", "shift"], reported.Parameters.Select(static p => p.Id));
        Assert.Equal((-64.0, 64.0), (reported.Parameters[1].Min, reported.Parameters[1].Max));
        Assert.Equal([(1_000L, 60), (3_000L, 71)], Play(4_096));

        // setInsertParameter by the slot's id and the parameter's.
        Assert.True(controller.SetInsertParameter(1, "s", "shift", -12));
        Assert.Equal([48], Live(60));
        Assert.False(controller.SetInsertParameter(1, "s", "nope", 1));

        // The script redefines its list (UpdatePluginParameters): shift moves to index 0, spread is new.
        var script = Assert.Single(Scripts);
        script.UpdateParameters(
            new(0, "shift", "shift", -64, 64, 0, string.Empty),
            new(1, "depth", "depth", -64, 64, 0, string.Empty),
            new(2, "spread", "spread", -64, 64, 0, string.Empty)
        );
        controller.PumpScripts();
        Assert.Equal(2, parameters.Count);
        Assert.Equal(["shift", "depth", "spread"], parameters[1].Parameters.Select(static p => p.Id));
        Assert.True(controller.SetInsertParameter(1, "s", "spread", 2));
        driver.Process(); // queued for the audio thread, which sets it by its new index
        Assert.Equal(2, script.GetParameter(2), 0);
        controller.PumpScripts();
        Assert.Equal(2, parameters.Count); // reported once per change

        // Automation follows shift to its new index.
        controller.Stop();
        Assert.Equal([(1_000L, 60), (3_000L, 71)], Play(4_096));
    }

    [Fact]
    public async Task A_new_script_compiles_while_the_old_one_plays_and_swaps_in_without_a_gap() {
        Load(Script("s", "param shift 0\nhold", new() { ["shift"] = 1 }));
        driver.Process();
        var first = Instrument.Logged;
        Assert.True(controller.LiveMidi(1, [new(0x90, 40, 100)])); // held, with its octave
        driver.Process();
        Assert.Equal([41, 53], NoteOns(first));
        var old = Assert.Single(Scripts);

        gate.Hold();
        var swap = Task.Run(
            () => controller.SetScript(1, "s", "param shift 0\ntranspose 12"),
            TestContext.Current.CancellationToken
        );
        Assert.True(gate.Entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        // While the new one compiles, the old one plays every block.
        var heard = new List<int>();
        var processed = old.Processed;
        for (var k = 0; k < 8; k++) {
            heard.AddRange(Live(60 + k));
        }

        Assert.Equal(processed + 8, old.Processed);
        Assert.False(swap.IsCompleted);
        first = Instrument.Logged;
        gate.Release();
        Assert.True(await swap);
        for (var k = 8; k < 16; k++) {
            heard.AddRange(Live(60 + k));
        }

        // Each block's note went through one script or the other: the old (shift 1, plus an octave held), then the
        // new (shift 1 carried over by id, plus 12), from the first block after the swap.
        int[] expected = [
            ..Enumerable.Range(0, 8).SelectMany(static k => new[] { 61 + k, 73 + k }),
            ..Enumerable.Range(8, 8).Select(static k => 73 + k)
        ];
        Assert.Equal(expected, heard);

        // The notes the old one left sounding end at the swap: 41 (its key is still down), 53 and the octaves.
        Assert.Equal(
            [41, 53, ..Enumerable.Range(0, 8).Select(static k => 73 + k)],
            NoteOffs(first).Where(static n => n is 41 or 53 or >= 73 and <= 80).Distinct().Order()
        );
        Assert.True(old.Disposed);
        var current = Scripts[1];
        Assert.Equal("param shift 0\ntranspose 12", current.Source);
        Assert.Equal(2, parameters.Count); // the load's, then the new script's

        // A strip with the same source keeps the instance; a non-script slot or an unknown one takes no script.
        Assert.True(controller.SetChannel(1, Strip(Script("s", "param shift 0\ntranspose 12"))));
        Assert.Equal(2, Scripts.Length);
        Assert.False(controller.SetScript(1, "missing", "hold"));
        Assert.False(controller.SetScript(99, "s", "hold"));
        Assert.True(controller.SetChannel(1, Strip(TestMidiEffects.Slot("t", TestMidiEffects.Transposer))));
        Assert.False(controller.SetScript(1, "t", "hold"));
        Assert.True(current.Disposed);
    }

    [Fact]
    public void A_script_that_fails_is_bypassed_and_reported_with_its_line_until_a_new_one_runs() {
        Load(Script("s", "transpose 3\nfail"));
        var problem = Assert.Single(problems);
        Assert.Equal(
            (1, "s", TestMidiEffects.Script, "line 2: SyntaxError: unexpected token"),
            (problem.TrackId, problem.InsertId, problem.PluginId, problem.Message)
        );
        Assert.Empty(Assert.Single(parameters).Parameters);
        driver.Process();
        Assert.Equal([60], Live(60)); // passed through
        controller.PumpScripts();
        Assert.Equal(["line 2: SyntaxError: unexpected token"], Assert.Single(logs).Lines);

        // A script that stops while it runs (a budget overrun): what it wrote in that block is discarded, the block
        // passes through, and the notes it left sounding end.
        Assert.True(controller.SetScript(1, "s", "hold\ncrash 64"));
        Assert.Single(problems);
        Assert.Equal([60, 72], Live(60));
        var first = Instrument.Logged;
        Assert.Equal([64], Live(64));
        Assert.Contains(72, NoteOffs(first));
        Assert.Equal([65], Live(65)); // bypassed from now on
        Assert.Single(problems); // reported by the control thread's pump
        controller.PumpScripts();
        Assert.Equal("line 2: InternalError: interrupted", problems[^1].Message);
        Assert.Equal(["line 2: InternalError: interrupted"], logs[^1].Lines);
        controller.PumpScripts();
        Assert.Equal(2, problems.Count); // once

        // Editing the script re-enables the slot.
        Assert.True(controller.SetScript(1, "s", "transpose 2"));
        Assert.Equal([62], Live(60));
        Assert.Equal(2, problems.Count);
    }

    [Fact]
    public void Trace_lines_go_out_at_most_50_a_second_per_slot_and_the_rest_are_counted_as_dropped() {
        Load(Script("s", "trace"));
        driver.Process();
        var second = Stopwatch.Frequency;
        var t0 = 1_000 * second;

        void Notes(int count) {
            for (var n = 0; n < count; n += 40) {
                Live([..Enumerable.Range(n, Math.Min(40, count - n)).Select(static i => i % 128)]);
            }
        }

        Notes(120);
        controller.PumpScripts(t0);
        var log = Assert.Single(logs);
        Assert.Equal((1, "s"), (log.TrackId, log.SlotId));
        Assert.Equal([..Enumerable.Range(0, 50).Select(static n => $"note {n}")], log.Lines);
        Assert.Equal(70, log.Dropped);

        // Half a second later the window is still full: nothing goes, and the count says so.
        Notes(10);
        controller.PumpScripts(t0 + second / 2);
        Assert.Equal((0, 10), (logs[^1].Lines.Count, logs[^1].Dropped));

        // Nothing new, nothing sent.
        controller.PumpScripts(t0 + second / 2 + 1);
        Assert.Equal(2, logs.Count);

        // A second after the first lines, the window has room again.
        Notes(30);
        controller.PumpScripts(t0 + second);
        Assert.Equal((30, 0), (logs[^1].Lines.Count, logs[^1].Dropped));

        // Lines the script's own ring could not hold count too (it holds 256).
        Notes(300);
        controller.PumpScripts(t0 + 3 * second);
        Assert.Equal((50, 300 - 50), (logs[^1].Lines.Count, logs[^1].Dropped));
        Assert.True(Scripts[0].Idles >= 5);
    }

    [Fact]
    public void The_log_window_slides() {
        var log = new ScriptLog();
        var second = Stopwatch.Frequency;
        var lines = Enumerable.Range(0, 30).Select(static n => $"{n}").ToList();
        Assert.Equal(30, log.Admit(lines, 0, second).Count);
        Assert.Equal(20, log.Admit(lines, 2, second + second / 2).Count);
        Assert.Equal(12, log.TakeDropped());
        Assert.Empty(log.Admit(lines.Take(1), 0, second * 2 - 1)); // the first 30 went less than a second ago
        Assert.Equal(30, log.Admit(lines, 0, second * 2).Count); // now they are a second old
        Assert.Equal(1, log.TakeDropped());
    }
}
