using Anode.Dsp.Instruments;
using Anode.Dsp.MidiEffects;
using Anode.Dsp.MidiEffects.Scripter;
using Anode.Dsp.Tests.Support;
using System.Diagnostics;

namespace Anode.Dsp.Tests.Scripter;

/// <summary>
///     ADR-0009's guarantees: the budget stops a runaway script, the arena bounds its memory, errors report their line
///     and bypass the script until an edit, runs are deterministic, and processing allocates nothing outside the arena.
/// </summary>
public sealed class ScripterSafetyTests(ITestOutputHelper output) {
    [Fact]
    public void A_runaway_script_is_stopped_within_its_budget_and_bypassed() {
        using var effect = Scripts.Create(
            """
            function HandleMIDI(e) {
              if (e.pitch === 61) {
                while (true) {}
              }
              e.send();
            }
            """
        );
        var buffer = new MidiEventBuffer();
        effect.Process(Scripts.Block(), [new(0, 0x90, 60, 100)], buffer);
        Assert.Equal([new MidiEvent(0, 0x90, 60, 100)], buffer.Events.ToArray());

        effect.BudgetMicroseconds = 0; // the default: 25 % of the block period
        buffer.Clear();
        var watch = Stopwatch.StartNew();
        effect.Process(Scripts.Block(frames: 512), [new(5, 0x90, 61, 100), new(9, 0x90, 62, 100)], buffer);
        watch.Stop();
        // 25 % of 512 frames is 2.7 ms; allow for a loaded machine, but not for a hang.
        Assert.InRange(watch.Elapsed.TotalMilliseconds, 2, 500);
        Assert.False(effect.Running);
        Assert.True(effect.TakeProblem(out var problem));
        Assert.Equal(ScriptProblemKind.Interrupted, problem?.Kind);
        // QuickJS places an interruption at the last position it recorded: in a tight loop, the statement before.
        Assert.InRange(problem?.Line ?? 0, 2, 3);
        // The block passes through, and the note the script started ends.
        Assert.Equal(
            [new MidiEvent(0, 0x80, 60, 0), new(5, 0x90, 61, 100), new(9, 0x90, 62, 100)],
            buffer.Events.ToArray()
        );

        buffer.Clear();
        effect.Process(Scripts.Block(), [new(1, 0x90, 61, 100)], buffer);
        Assert.Equal([new MidiEvent(1, 0x90, 61, 100)], buffer.Events.ToArray());
        Assert.False(effect.TakeProblem(out _));
    }

    [Fact]
    public void A_runaway_top_level_is_stopped_while_compiling() {
        Scripts.SkipWithoutNative();
        using var effect = new ScripterEffect(1);
        var watch = Stopwatch.StartNew();
        var problem = effect.Compile("var x = 0;\nfor (;;) { x++; }\n");
        Assert.InRange(watch.Elapsed.TotalSeconds, 1.5, 30);
        Assert.Equal(ScriptProblemKind.Interrupted, problem?.Kind);
        Assert.InRange(problem?.Line ?? 0, 1, 2);
    }

    [Fact]
    public void Running_out_of_the_arena_is_a_problem_not_growth() {
        using var effect = Scripts.Create(
            """
            var hoard = [];
            function HandleMIDI(e) {
              for (;;) hoard.push(new Array(4096).fill(e.pitch + 0.5));
            }
            """,
            arenaBytes: 1024 * 1024
        );
        Assert.Equal(1024 * 1024, effect.ArenaBytes, 64.0);
        var buffer = new MidiEventBuffer();
        effect.Process(Scripts.Block(), [new(0, 0x90, 60, 100)], buffer);
        Assert.Equal([new MidiEvent(0, 0x90, 60, 100)], buffer.Events.ToArray());
        Assert.Equal(ScriptProblemKind.OutOfMemory, effect.Problem?.Kind);
        Assert.InRange(effect.MemoryUsed, 900 * 1024, effect.ArenaBytes);
        Assert.True(effect.CheckArena());

        // An edit recovers: a fresh context in the same arena.
        Assert.Null(effect.Compile("function HandleMIDI(e) { e.pitch++; e.send(); }"));
        Assert.InRange(effect.MemoryUsed, 1, 400 * 1024);
        buffer.Clear();
        effect.Process(Scripts.Block(), [new(0, 0x90, 60, 100)], buffer);
        Assert.Equal([new MidiEvent(0, 0x90, 61, 100)], buffer.Events.ToArray());
    }

    [Fact]
    public void A_syntax_error_reports_its_line() {
        Scripts.SkipWithoutNative();
        using var effect = new ScripterEffect(1);
        var problem = effect.Compile("function HandleMIDI(e) {\n  e.send();\n}\nvar broken = ;\n");
        Assert.Equal(ScriptProblemKind.Error, problem?.Kind);
        Assert.Equal(4, problem?.Line);
        Assert.StartsWith("SyntaxError", problem?.Message, StringComparison.Ordinal);
        Assert.False(effect.Running);
        Assert.True(effect.TakeProblem(out var taken));
        Assert.Equal(problem, taken);
    }

    [Fact]
    public void An_uncaught_exception_reports_its_line_even_through_the_API() {
        using var effect = Scripts.Create(
            """
            function HandleMIDI(e) {
              if (e instanceof ControlChange) {
                e.sendAtBeat(Symbol("not a beat"));
              }
              if (e.pitch === 62) {
                undefinedThing.go();
              }
              e.send();
            }
            """
        );
        var output = Scripts.Run(effect, 640, [TimedEvent.On(3, 62)]);
        Assert.Equal([new(3, 0x90, 62, 100)], output); // passed through
        Assert.Equal(ScriptProblemKind.Error, effect.Problem?.Kind);
        Assert.Equal(6, effect.Problem?.Line);
        Assert.Contains("undefinedThing", effect.Problem?.Message, StringComparison.Ordinal);

        Assert.Null(effect.Compile(effect.Source));
        Scripts.Run(effect, 640, [TimedEvent.Cc(3, 1, 2)]);
        Assert.Equal(3, effect.Problem?.Line); // thrown in the prelude, reported where the script called it
    }

    [Fact]
    public void An_edit_recovers_and_clears_the_problem() {
        Scripts.SkipWithoutNative();
        using var effect = new ScripterEffect(1);
        effect.BudgetMicroseconds = Scripts.RelaxedBudget;
        effect.Prepare((float)Scripts.Rate, 1_024);
        Assert.NotNull(effect.Compile("function HandleMIDI(e) { throw new Error('boom'); }\n)"));
        Assert.True(effect.TakeProblem(out var problem));
        Assert.NotNull(problem);
        Assert.Null(effect.Compile("function HandleMIDI(e) { e.velocity = 1; e.send(); }"));
        Assert.True(effect.Running);
        Assert.True(effect.TakeProblem(out problem));
        Assert.Null(problem);
        var output = Scripts.Run(effect, 64, [TimedEvent.On(0, 60, 99)]);
        Assert.Equal([new(0, 0x90, 60, 1)], output);
    }

    [Fact]
    public void A_swapped_in_script_keeps_values_and_the_old_one_releases_its_notes() {
        using var old = Scripts.Create(
            """
            var PluginParameters = [{ name: "Interval", minValue: 0, maxValue: 12, numberOfSteps: 12 }];
            function HandleMIDI(e) { e.send(); }
            """
        );
        old.SetParameter(0, 5);
        Scripts.Run(old, 64, [TimedEvent.On(0, 60), new(1, 0x93, 70, 1)]);
        using var next = ScripterEffect.CreateCompiled(ScripterTimingTests.OctaveEcho, (float)Scripts.Rate, 1_024, old);
        Assert.Null(next.Problem);
        Assert.Equal(5, next.GetParameter(0));
        var released = new MidiEventBuffer();
        old.ReleaseNotes(released);
        Assert.Equal([new MidiEvent(0, 0x80, 60, 0), new(0, 0x83, 70, 0)], released.Events.ToArray());
    }

    [Fact]
    public void Math_random_is_seeded_so_runs_repeat() {
        var events = Enumerable.Range(0, 200).Select(static i => TimedEvent.On(i * 100, 40 + i % 40, 80)).ToList();

        List<OutputEvent> RunWith(ulong seed) {
            using var effect = Scripts.Create(ScripterPresets.DrumHumaniser.Source, seed);
            return Scripts.Run(effect, 30_000, events);
        }

        var first = RunWith(7);
        Assert.Equal(first, RunWith(7));
        Assert.NotEqual(first, RunWith(8));

        using var again = Scripts.Create(
            "function HandleMIDI(e) { e.velocity = 1 + Math.floor(Math.random() * 127); e.send(); }",
            3
        );
        var a = Scripts.Run(again, 30_000, events);
        Assert.Null(again.Compile(again.Source)); // a recompile reseeds
        Assert.Equal(a, Scripts.Run(again, 30_000, events));
    }

    [Fact]
    public void Processing_allocates_nothing_outside_the_arena() {
        using var effect = Scripts.Create(
            """
            var NeedsTimingInfo = true;
            var PluginParameters = [{ name: "Delay", minValue: 0, maxValue: 50, defaultValue: 3 }];
            var held = {};
            function ParameterChanged(i, v) { Trace("delay " + v); }
            function HandleMIDI(e) {
              if (e instanceof NoteOn) held[e.pitch] = e;
              if (e instanceof NoteOff) delete held[e.pitch];
              e.sendAfterMilliseconds(GetParameter("Delay"));
              var echo = new NoteOn(e);
              if (e instanceof NoteOn) { echo.pitch += 12; echo.sendAfterBeats(0.25); new NoteOff(echo).sendAfterBeats(0.5); }
              Trace(e.toString());
            }
            function ProcessMIDI() {
              var t = GetTimingInfo();
              if (t.playing && Math.random() < 0.05) { var cc = new ControlChange; cc.value = 3; cc.sendAtBeat(t.blockEndBeat); }
            }
            """
        );
        var contexts = MidiRun.Contexts(64 * 300, static b => 100 + b * 0.3);
        var buffer = new MidiEventBuffer();
        var chord = new MidiEvent[] {
            new(0, 0x90, 60, 100), new(3, 0x90, 64, 90), new(10, 0xB0, 1, 100), new(20, 0xE0, 0, 80),
            new(30, 0x91, 67, 80), new(63, 0x90, 72, 127)
        };
        var offs = new MidiEvent[] {
            new(0, 0x80, 64, 0), new(5, 0x81, 67, 0), new(9, 0x90, 60, 0), new(60, 0x80, 72, 0)
        };
        var allocated = AllocationProbe.Measure(() => {
                for (var b = 0; b < contexts.Count; b++) {
                    buffer.Clear();
                    effect.SetParameter(0, b % 7);
                    ReadOnlySpan<MidiEvent> input = (b % 40) switch {
                        0 => chord,
                        30 => offs,
                        _ => []
                    };
                    effect.Process(contexts[b], input, buffer);
                }

                effect.Reset();
            }
        );
        Assert.Equal(0, allocated);
        Assert.True(effect.Running);
        Assert.True(effect.CheckArena());
        Assert.True(effect.MemoryUsed < effect.ArenaBytes / 2);
        Assert.True(effect.TryReadTrace(out _));
    }

    [Fact]
    public void The_collector_frees_cycles_between_blocks() {
        using var effect = Scripts.Create(
            """
            function HandleMIDI(e) {
              for (var i = 0; i < 50; i++) { var a = {}; var b = { a: a }; a.b = b; }
              e.send();
            }
            """
        );
        Scripts.Run(effect, 64 * 100, Enumerable.Range(0, 100).Select(static i => TimedEvent.On(i * 64, 60)).ToList());
        var before = effect.MemoryUsed;
        Assert.True(effect.TryCollect());
        Assert.True(effect.MemoryUsed < before - 100_000, $"{effect.MemoryUsed} of {before}");
        Assert.True(effect.CheckArena());
    }

    [Fact]
    public void Too_many_waiting_events_are_dropped_and_counted() {
        using var effect = Scripts.Create(
            "function HandleMIDI(e) { for (var i = 0; i < 3000; i++) e.sendAfterMilliseconds(10); }"
        );
        var output = Scripts.Run(effect, 64, [TimedEvent.On(0, 60)]);
        Assert.Empty(output);
        Assert.Equal(ScripterEffect.PendingCapacity, effect.PendingEvents);
        Assert.Equal(3000 - ScripterEffect.PendingCapacity, effect.DroppedEvents);
    }

    [Fact]
    public void The_cost_per_event_is_measured() {
        using var effect = Scripts.Create(ScripterPresets.Transposer.Source);
        effect.SetParameter(0, 5);
        var buffer = new MidiEventBuffer();
        var notes = new MidiEvent[32];
        for (var i = 0; i < notes.Length; i++) {
            notes[i] = new(i * 2, (byte)(i % 2 == 0 ? 0x90 : 0x80), (byte)(48 + i / 2), 100);
        }

        void RunBlocks(int blocks) {
            for (var b = 0; b < blocks; b++) {
                buffer.Clear();
                effect.Process(Scripts.Block(b / 375.0), notes, buffer);
            }
        }

        RunBlocks(200); // warm up
        var best = double.MaxValue;
        var empty = double.MaxValue;
        for (var round = 0; round < 5; round++) {
            var watch = Stopwatch.StartNew();
            RunBlocks(500);
            best = Math.Min(best, watch.Elapsed.TotalMicroseconds / (500.0 * notes.Length));
        }

        for (var round = 0; round < 5; round++) {
            var watch = Stopwatch.StartNew();
            for (var b = 0; b < 500; b++) {
                buffer.Clear();
                effect.Process(Scripts.Block(b / 375.0), [], buffer);
            }

            empty = Math.Min(empty, watch.Elapsed.TotalMicroseconds / 500.0);
        }

        output.WriteLine(
            $"Transposer: {best:F2} µs per event; {empty:F2} µs per block without events ({ScripterEffect.EngineVersion})"
        );
        Assert.True(effect.Running);
        // A 64-frame block at 48 kHz lasts 1333 µs: 32 events in it should cost a small share of it, even loaded.
        Assert.True(best < 25, $"{best:F2} µs per event");
        Assert.True(empty < 100, $"{empty:F2} µs per block");
    }
}
