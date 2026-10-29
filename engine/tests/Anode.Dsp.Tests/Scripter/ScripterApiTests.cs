using Anode.Dsp.Instruments;
using Anode.Dsp.MidiEffects;
using Anode.Dsp.MidiEffects.Scripter;
using Anode.Dsp.Tests.Support;
using System.Globalization;

namespace Anode.Dsp.Tests.Scripter;

/// <summary>The Scripter API (docs/scripter/README.md): each callback and class, parameters, helpers and state.</summary>
public sealed class ScripterApiTests {
    static readonly TimedEvent[] Mixed = [
        TimedEvent.On(10, 60), new(20, 0x80, 60, 40), new(30, 0x91, 62, 0), TimedEvent.Cc(40, 1, 64),
        TimedEvent.Bend(50, -8192), new(60, 0xC2, 5, 0), new(70, 0xD3, 33, 0), new(80, 0xAF, 61, 12),
        TimedEvent.Bend(90, 8191)
    ];

    [Fact]
    public void Without_HandleMIDI_events_pass_through_unchanged() {
        using var effect = Scripts.Create("// nothing");
        var output = Scripts.Run(effect, 6_400, Mixed);
        // A note-on at velocity 0 is a NoteOff to a script, and leaves as one.
        Assert.Equal(
            Mixed.Select(static e => (e.Status & 0xF0) == 0x90 && e.Data2 == 0
                    ? new OutputEvent(e.Frame, (byte)(0x80 | (e.Status & 0x0F)), e.Data1, 0)
                    : new OutputEvent(e.Frame, e.Status, e.Data1, e.Data2)
            ),
            output
        );
    }

    [Fact]
    public void HandleMIDI_gets_each_event_as_its_class() {
        using var effect = Scripts.Create(
            """
            function HandleMIDI(e) {
              var kinds = [Event, Note, NoteOn, NoteOff, PolyPressure, ControlChange, ProgramChange, ChannelPressure,
                PitchBend].filter(function (c) { return e instanceof c; }).map(function (c) { return c.name; });
              Trace(e + " " + kinds.join(",") + " beat " + e.beatPos.toFixed(6));
            }
            """
        );
        var output = Scripts.Run(effect, 6_400, Mixed);
        Assert.Empty(output); // HandleMIDI sent nothing
        static string Beat(long frame) => (1 + frame * 2.0 / Scripts.Rate).ToString("F6", CultureInfo.InvariantCulture);
        Assert.Equal(
            [
                $"[NoteOn channel:1 pitch:60 [C3] velocity:100] Event,Note,NoteOn beat {Beat(10)}",
                $"[NoteOff channel:1 pitch:60 [C3] velocity:40] Event,Note,NoteOff beat {Beat(20)}",
                $"[NoteOff channel:2 pitch:62 [D3] velocity:0] Event,Note,NoteOff beat {Beat(30)}",
                $"[ControlChange channel:1 number:1 [Modulation] value:64] Event,ControlChange beat {Beat(40)}",
                $"[PitchBend channel:1 value:-8192] Event,PitchBend beat {Beat(50)}",
                $"[ProgramChange channel:3 number:5] Event,ProgramChange beat {Beat(60)}",
                $"[ChannelPressure channel:4 value:33] Event,ChannelPressure beat {Beat(70)}",
                $"[PolyPressure channel:16 pitch:61 [C#3] value:12] Event,PolyPressure beat {Beat(80)}",
                $"[PitchBend channel:1 value:8191] Event,PitchBend beat {Beat(90)}"
            ],
            Scripts.Trace(effect)
        );
    }

    [Fact]
    public void Events_the_script_changes_and_creates_are_sent_with_their_bytes() {
        using var effect = Scripts.Create(
            """
            function HandleMIDI(e) {
              if (e instanceof NoteOn) {
                e.pitch += 7; e.velocity = 300; e.channel = 16; e.send();
                var cc = new ControlChange; cc.number = 74; cc.value = 99.6; cc.channel = 2; cc.send();
                var bend = new PitchBend; bend.value = 4096; bend.send();
                var program = new ProgramChange; program.number = 12; program.send();
                var pressure = new ChannelPressure; pressure.value = 7; pressure.send();
                var poly = new PolyPressure(e); poly.value = 3; poly.send();
                var off = new NoteOff(e); off.send();
                var raw = new Event; raw.status = 0xB5; raw.data1 = 7; raw.data2 = 90; raw.send();
              }
            }
            """
        );
        var output = Scripts.Run(effect, 640, [TimedEvent.On(100, 60, 90)]);
        Assert.Equal(
            [
                new(100, 0x9F, 67, 127), new(100, 0xB1, 74, 100), new(100, 0xE0, 0, 96), new(100, 0xC0, 12, 0),
                new(100, 0xD0, 7, 0), new(100, 0xAF, 67, 3), new(100, 0x8F, 67, 127), new(100, 0xB0, 7, 90)
            ],
            output
        );
    }

    [Fact]
    public void Copies_keep_the_fields_both_classes_have() {
        using var effect = Scripts.Create(
            """
            function HandleMIDI(e) {
              var off = new NoteOff(e);
              var cc = new ControlChange(e);
              Trace(off + " " + cc + " " + (off.beatPos === e.beatPos));
            }
            """
        );
        Scripts.Run(effect, 640, [new(5, 0x93, 72, 80)]);
        Assert.Equal(
            [
                "[NoteOff channel:4 pitch:72 [C4] velocity:80] [ControlChange channel:4 number:1 [Modulation] value:0] true"
            ],
            Scripts.Trace(effect)
        );
    }

    [Fact]
    public void ProcessMIDI_runs_once_a_block_after_the_blocks_events() {
        using var effect = Scripts.Create(
            """
            var blocks = 0, seen = 0;
            function HandleMIDI(e) { seen++; }
            function ProcessMIDI() {
              blocks++;
              var cc = new ControlChange; cc.number = 20; cc.value = seen; cc.send();
            }
            """
        );
        var output = Scripts.Run(
            effect,
            64 * 10,
            [TimedEvent.On(70, 60), TimedEvent.On(71, 61), TimedEvent.On(300, 62)]
        );
        Assert.Equal(10, output.Count);
        Assert.Equal(Enumerable.Range(0, 10).Select(static b => (long)b * 64), output.Select(static e => e.Frame));
        Assert.Equal([0, 2, 2, 2, 3, 3, 3, 3, 3, 3], output.Select(static e => (int)e.Data2));
    }

    [Fact]
    public void GetTimingInfo_describes_the_block() {
        using var effect = Scripts.Create(
            """
            var NeedsTimingInfo = true;
            function ProcessMIDI() {
              var t = GetTimingInfo();
              Trace([t.playing, t.blockStartBeat.toFixed(9), t.blockEndBeat.toFixed(9), t.blockLength.toFixed(9),
                t.tempo, t.meterNumerator, t.meterDenominator, t.cycling].join(" "));
            }
            """
        );
        var output = new MidiEventBuffer();
        effect.Process(new(true, 0, 4, 90, 3, 4, 3, Scripts.Rate, 480), [], output);
        effect.Process(new(false, 0, 2, 120, 6, 8, 0, Scripts.Rate, 64), [], output);
        static string F(double v) => v.ToString("F9", CultureInfo.InvariantCulture);
        var first = 480 * 90 / (60 * Scripts.Rate);
        var second = 64 * 120 / (60 * Scripts.Rate);
        Assert.Equal(
            [
                $"true {F(5)} {F(5 + first)} {F(first)} 90 3 4 false",
                $"false {F(3)} {F(3 + second)} {F(second)} 120 6 8 false"
            ],
            Scripts.Trace(effect)
        );
    }

    [Fact]
    public void PluginParameters_become_run_time_parameters() {
        using var effect = Scripts.Create(
            """
            var PluginParameters = [
              { name: "Amount", type: "lin", minValue: -10, maxValue: 10, numberOfSteps: 20, defaultValue: 2, unit: "st" },
              { name: "Cutoff", type: "log", minValue: 20, maxValue: 20000, defaultValue: 1000 },
              { name: "Mode", type: "menu", valueStrings: ["One", "Two", "Three"], defaultValue: 1 },
              { name: "On", type: "checkbox", defaultValue: true },
              { name: "Go", type: "momentary" },
              { name: "About", type: "text" },
              { name: "Amount", minValue: 0, maxValue: 1, disableAutomation: true }
            ];
            """
        );
        Assert.True(effect.TakeParametersChanged());
        Assert.False(effect.TakeParametersChanged());
        Assert.Equal(
            [
                new ScriptParameter(0, "Amount", ScriptParameterKind.Linear, -10, 10, 2, 20, null, "st", true),
                new ScriptParameter(
                    1,
                    "Cutoff",
                    ScriptParameterKind.Logarithmic,
                    20,
                    20000,
                    1000,
                    0,
                    null,
                    string.Empty,
                    true
                ),
                new ScriptParameter(2, "Mode", ScriptParameterKind.Menu, 0, 2, 1, 2, null, string.Empty, true),
                new ScriptParameter(3, "On", ScriptParameterKind.Checkbox, 0, 1, 1, 1, null, string.Empty, true),
                new ScriptParameter(4, "Go", ScriptParameterKind.Momentary, 0, 1, 0, 1, null, string.Empty, true),
                new ScriptParameter(5, "About", ScriptParameterKind.Text, 0, 0, 0, 0, null, string.Empty, false),
                new ScriptParameter(6, "Amount 2", ScriptParameterKind.Linear, 0, 1, 0, 0, null, string.Empty, false)
            ],
            effect.ScriptParameters.Select(static p => p with { ValueNames = null })
        );
        Assert.Equal(["One", "Two", "Three"], effect.ScriptParameters[2].ValueNames);
        Assert.Equal(["Off", "On"], effect.ScriptParameters[3].ValueNames);
        var runtime = effect.RuntimeParameters;
        Assert.Same(runtime, effect.Descriptor.Parameters);
        Assert.Equal(ScripterEffect.Id, effect.Descriptor.Id);
        Assert.Equal(["Amount", "Cutoff", "Mode", "On", "Go", "About", "Amount 2"], runtime.Select(static p => p.Id));
        Assert.Equal(
            new(
                2,
                "Mode",
                "Mode",
                0,
                2,
                1,
                string.Empty,
                2,
                effect.ScriptParameters[2].ValueNames,
                Kind: InstrumentParameterKind.Menu
            ),
            runtime[2]
        );
        // Each parameter shows as its PluginParameters type (a checkbox has value names, yet is no menu).
        Assert.Equal(
            [
                InstrumentParameterKind.Slider, InstrumentParameterKind.Slider, InstrumentParameterKind.Menu,
                InstrumentParameterKind.Checkbox, InstrumentParameterKind.Momentary, InstrumentParameterKind.Text,
                InstrumentParameterKind.Slider
            ],
            runtime.Select(static p => p.ShownAs)
        );
        Assert.Equal([2f, 1000f, 1f, 1f, 0f, 0f, 0f], Enumerable.Range(0, 7).Select(effect.GetParameter));

        effect.SetParameter(0, 3.4f); // snapped to its steps, clamped to its range
        Assert.Equal(3, effect.GetParameter(0));
        effect.SetParameter(0, 99);
        Assert.Equal(10, effect.GetParameter(0));
        effect.SetParameter(50, 1); // no such parameter: ignored
        Assert.Equal(0, effect.GetParameter(50));
    }

    [Fact]
    public void ParameterChanged_runs_after_the_script_and_for_each_host_change() {
        using var effect = Scripts.Create(
            """
            var PluginParameters = [
              { name: "Interval", type: "lin", minValue: 0, maxValue: 24, numberOfSteps: 24, defaultValue: 12 },
              { name: "Label", type: "text" },
              { name: "Mode", type: "menu", valueStrings: ["A", "B"] }
            ];
            function ParameterChanged(index, value) {
              Trace(index + "=" + value + " " + GetParameter(index) + " " + GetParameter(PluginParameters[index].name));
            }
            """
        );
        Assert.Equal(["0=12 12 12", "2=0 0 0"], Scripts.Trace(effect));
        effect.SetParameter(0, 7);
        effect.SetParameter(2, 1);
        effect.Process(Scripts.Block(), [], new());
        Assert.Equal(["0=7 7 7", "2=1 1 1"], Scripts.Trace(effect));
        effect.Process(Scripts.Block(), [], new());
        Assert.Empty(Scripts.Trace(effect)); // only changes notify
    }

    [Fact]
    public void SetParameter_from_the_script_reaches_the_host() {
        using var effect = Scripts.Create(
            """
            var PluginParameters = [{ name: "Last Note", minValue: 0, maxValue: 127, numberOfSteps: 127 }];
            function ParameterChanged(i, v) { Trace("changed " + v); }
            function HandleMIDI(e) { if (e instanceof NoteOn) SetParameter("Last Note", e.pitch); e.send(); }
            """
        );
        Scripts.Trace(effect);
        var version = effect.ParameterValuesVersion;
        Scripts.Run(effect, 640, [TimedEvent.On(3, 64)]);
        Assert.Equal(64, effect.GetParameter(0));
        Assert.True(effect.ParameterValuesVersion > version);
        Assert.Equal(["changed 64"], Scripts.Trace(effect));
    }

    [Fact]
    public void UpdatePluginParameters_changes_the_list_at_run_time_and_keeps_values_by_name() {
        using var effect = Scripts.Create(
            """
            var PluginParameters = [{ name: "Keep", minValue: 0, maxValue: 10, numberOfSteps: 10, defaultValue: 1 }];
            function HandleMIDI(e) {
              PluginParameters.unshift({ name: "New", type: "checkbox", defaultValue: 1 });
              UpdatePluginParameters();
              e.send();
            }
            """
        );
        Assert.True(effect.TakeParametersChanged());
        effect.SetParameter(0, 6);
        Scripts.Run(effect, 640, [TimedEvent.On(3, 64)]);
        Assert.True(effect.TakeParametersChanged());
        Assert.Equal(["New", "Keep"], effect.RuntimeParameters.Select(static p => p.Id));
        Assert.Equal(1, effect.GetParameter(0));
        Assert.Equal(6, effect.GetParameter(1));
        Assert.False(effect.TakeParametersChanged());
    }

    [Fact]
    public void Reset_and_Idle_are_called() {
        using var effect = Scripts.Create(
            """
            var idles = 0;
            function Reset() { Trace("reset"); }
            function Idle() { idles++; Trace("idle " + idles); }
            """
        );
        Scripts.Run(effect, 48_000, []);
        Assert.InRange(Scripts.Trace(effect).Count, 3, 4); // about four times a second
        effect.Reset();
        Assert.Equal(["reset"], Scripts.Trace(effect));
    }

    [Fact]
    public void MIDI_helpers_follow_Logic() {
        using var effect = Scripts.Create(
            """
            Trace([MIDI.noteName(60), MIDI.noteName(0), MIDI.noteName(127), MIDI.noteName(128), MIDI.noteNumber("C3"),
              MIDI.noteNumber("C#3"), MIDI.noteNumber("Db3"), MIDI.noteNumber("C-2"), MIDI.noteNumber("G8"),
              MIDI.noteNumber("H2"), MIDI.ccName(1), MIDI.ccName(64), MIDI.ccName(3), MIDI.normalizeChannel(20),
              MIDI.normalizeChannel(0), MIDI.normalizeData(200), MIDI.normalizeData(-3), MIDI.normalizeStatus(0x20),
              MIDI._noteNames.length, MIDI._ccNames[7]].join(","));
            """
        );
        Assert.Equal(
            ["C3,C-2,G8,,60,61,61,0,127,,Modulation,Sustain,Controller 3,16,1,127,0,128,128,Volume"],
            Scripts.Trace(effect)
        );
    }

    [Fact]
    public void AllNotesOff_ends_every_note_the_script_started() {
        using var effect = Scripts.Create(
            """
            function HandleMIDI(e) { if (e instanceof ControlChange) MIDI.allNotesOff(); else e.send(); }
            """
        );
        var output = Scripts.Run(
            effect,
            640,
            [
                TimedEvent.On(1, 60), TimedEvent.On(2, 64), new(3, 0x92, 67, 90), TimedEvent.Off(4, 64),
                TimedEvent.Cc(200, 1, 0)
            ]
        );
        Assert.Equal([new(192, 0x80, 60, 0), new(192, 0x82, 67, 0)], output.Where(static e => e.Frame >= 64));
    }

    [Fact]
    public void TargetEvents_are_listed_with_their_names() {
        using var effect = Scripts.Create(
            """
            function HandleMIDI(e) {
              var t = new TargetEvent; t.target = "Cutoff"; t.value = e.velocity / 127; t.send();
              var u = new TargetEvent(t); u.target = "Resonance"; u.value = 2; u.sendAfterMilliseconds(1);
            }
            """
        );
        Assert.True(effect.TakeParametersChanged());
        var output = new MidiEventBuffer();
        effect.Process(Scripts.Block(), [new(10, 0x90, 60, 127)], output);
        Assert.Empty(output.Events.ToArray());
        Assert.Equal([new ScriptTargetEvent(10, 0, 1), new ScriptTargetEvent(58, 1, 1)], effect.TargetEvents.ToArray());
        Assert.True(effect.TakeParametersChanged());
        Assert.Equal(["Cutoff", "Resonance"], effect.TargetNames);
        effect.Process(Scripts.Block(), [], output);
        Assert.Empty(effect.TargetEvents.ToArray());
    }

    [Fact]
    public void Trace_lines_are_cut_and_a_full_ring_counts_what_it_drops() {
        using var effect = Scripts.Create(
            """
            function HandleMIDI(e) {
              Trace("x".repeat(1000));
              Trace("");
              for (var i = 0; i < 300; i++) Trace("line " + i);
            }
            """
        );
        Scripts.Run(effect, 64, [TimedEvent.On(0, 60)]);
        var lines = Scripts.Trace(effect);
        Assert.Equal(ScripterEffect.TraceCapacity, lines.Count);
        Assert.Equal(new('x', 255), lines[0]);
        Assert.Equal(" ", lines[1]);
        Assert.Equal("line 253", lines[^1]);
        Assert.Equal(302 - ScripterEffect.TraceCapacity, effect.TakeTraceDropped());
        Assert.Equal(0, effect.TakeTraceDropped());
    }

    [Fact]
    public void State_round_trips_the_source_and_the_values_by_name() {
        const string Source = """
                              var PluginParameters = [
                                { name: "A", minValue: 0, maxValue: 10, defaultValue: 1 },
                                { name: "B", type: "menu", valueStrings: ["x", "y", "z"] }
                              ];
                              """;
        using var original = Scripts.Create(Source);
        original.SetParameter(0, 7.5f);
        original.SetParameter(1, 2);
        var state = original.SaveState();
        Assert.NotNull(state);

        using var restored = Scripts.Create(string.Empty);
        Assert.True(restored.LoadState(state));
        Assert.Equal(Source, restored.Source);
        Assert.Equal(7.5f, restored.GetParameter(0));
        Assert.Equal(2, restored.GetParameter(1));
        Assert.Equal(state, restored.SaveState());
        Assert.False(restored.LoadState([1, 2, 3]));
        using var transposer = MidiRun.Create(Transposer.Id);
        Assert.False(restored.LoadState(transposer.SaveState()));
    }

    [Fact]
    public void A_new_script_keeps_values_by_name_unless_it_resets_parameter_defaults() {
        using var effect = Scripts.Create("""var PluginParameters = [{ name: "A", maxValue: 10, defaultValue: 1 }];""");
        effect.SetParameter(0, 4);
        Assert.Null(
            effect.Compile("""var PluginParameters = [{ name: "B" }, { name: "A", maxValue: 10, defaultValue: 2 }];""")
        );
        Assert.Equal(4, effect.GetParameter(1));
        Assert.Null(
            effect.Compile(
                """
                var ResetParameterDefaults = true;
                var PluginParameters = [{ name: "A", maxValue: 10, defaultValue: 3 }];
                """
            )
        );
        Assert.Equal(3, effect.GetParameter(0));
    }

    [Fact]
    public void Without_the_native_library_it_passes_through_and_says_why() {
        using var effect = new ScripterEffect(1, 0, false);
        effect.Prepare((float)Scripts.Rate, 1_024);
        Assert.True(effect.TakeProblem(out var problem));
        Assert.Equal(ScriptProblemKind.Unavailable, problem?.Kind);
        Assert.Equal(problem, effect.Compile("function HandleMIDI(e) {}"));
        Assert.False(effect.Running);
        var output = Scripts.Run(effect, 640, Mixed);
        Assert.Equal(Mixed.Length, output.Count);
        Assert.True(effect.LoadState(effect.SaveState()));
        Assert.False(effect.TryReadTrace(out _));
        Assert.True(effect.TryCollect());
    }
}
