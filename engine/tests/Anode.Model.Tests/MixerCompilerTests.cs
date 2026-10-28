using Anode.Contracts;
using Anode.Ipc.Segments;
using Anode.Model.Compile;
using Anode.Model.Editing;

namespace Anode.Model.Tests;

/// <summary>Compiling the mixer (docs/05-phase-4.md): engine strips, VCA folding, aux buses and automation events.</summary>
public sealed class MixerCompilerTests {
    readonly SequentialIds ids = new();
    ProjectState state = ProjectDefaults.NewProject("prj_1", "Song", 48_000);

    void Apply(params EditCommand[] commands) =>
        state = Edits.Apply(state, commands, ids, RandomProjects.Plugins).State;

    Track T(string name) => state.Tracks.Single(t => t.Name == name);

    static PlaybackEvent[] Automation(TrackModel model, int target) =>
        [..model.Events.Where(e => e.Kind == PlaybackEventKind.Automation && e.Target == target)];

    [Fact]
    public void Strips_carry_inserts_sends_outputs_and_sidechains_in_engine_units() {
        Apply(new AddTrackCommand("Gtr"), new AddTrackCommand("Kick"), new AddAuxCommand(3, "Verb"));
        var gtr = T("Gtr").Id;
        Apply(
            new AddInsertCommand("anode.test-compressor", gtr),
            new AddSendCommand(gtr, 3, -6, true),
            new SetOutputCommand(gtr, "output:2"),
            new SetOutputCommand(T("Verb").Id, "bus:5"),
            new AddInsertCommand("com.example.fx")
        );
        var insert = T("Gtr").Strip.Inserts[0].Id;
        Apply(new SetInsertCommand(insert, gtr, true, T("Kick").Id));
        var engine = EngineIds.Empty.With(state);
        var strip = Strips.Engine(state, T("Gtr"), engine, i => i.Id == insert ? "c3RhdGU=" : null);
        Assert.Equal(-2, strip.Output);
        var ins = Assert.Single(strip.Inserts!);
        Assert.Equal(
            (insert, "anode.test-compressor", true, engine.Track(T("Kick").Id), 0, "c3RhdGU="),
            (ins.Id, ins.PluginId, ins.Bypassed, ins.SidechainTrack, ins.SidechainBus, ins.State)
        );
        var send = Assert.Single(strip.Sends!);
        Assert.Equal((3, true), (send.Bus, send.PreFader));
        Assert.InRange(send.Gain, 0.5011f, 0.5012f);
        Assert.Equal(5, Strips.Engine(state, T("Verb"), engine).Output);
        var master = Strips.Engine(state, null, engine);
        Assert.Equal((0, 1, 0), (master.Output, master.Inserts!.Count, master.Sends!.Count));

        var moved = Strips.Engine(
            state with {
                Tracks = [
                    ..state.Tracks.Select(t => t.Id == gtr ? t with { Strip = t.Strip with { VolumeDb = -20 } } : t)
                ]
            },
            T("Gtr"),
            engine
        );
        Assert.False(Strips.StructureDiffers(strip, moved));
        Assert.True(Strips.StructureDiffers(strip, strip with { Output = 0 }));
    }

    [Fact]
    public void Volume_lanes_become_gain_events_sampled_where_they_bend_at_most_every_10_ms() {
        Apply(new AddTrackCommand("Gtr"));
        var gtr = T("Gtr").Id;
        // A dB ramp over 2 s (a curve in gain), a curved segment, then a jump.
        Apply(
            new SetAutomationCommand(
                "volume",
                [new(0, -40, 0), new(3840, 0, 0.5), new(7680, -20, 0), new(7680, -6, 0)],
                gtr
            )
        );
        var engine = EngineIds.Empty.With(state);
        var model = new ModelCompiler().Compile(state, engine);
        var events = Automation(model.Tracks[0], engine.Target(gtr, "volume"));
        Assert.Equal(PlaybackEventKind.Automation, events[0].Kind);
        Assert.Equal((0L, 1), (events[0].SamplePos, (int)events[0].Data1));
        Assert.InRange(events[0].Value, Units.DbToGain(-40) * 0.999f, Units.DbToGain(-40) * 1.001f);
        // Sorted, never more than 10 ms apart inside the bending segments.
        for (var i = 1; i < events.Length; i++) {
            Assert.True(events[i].SamplePos >= events[i - 1].SamplePos);
            if (events[i].SamplePos <= 192_000) {
                Assert.True(events[i].SamplePos - events[i - 1].SamplePos <= 480, $"gap at {events[i].SamplePos}");
            }
        }

        // The jump: the ramp ends at −20 dB, then a step to −6 dB, both at 2 bars (192 000 samples at 120 BPM).
        var jump = events.Where(static e => e.SamplePos == 192_000).ToArray();
        Assert.Equal(2, jump.Length);
        Assert.InRange(jump[0].Value, Units.DbToGain(-20) * 0.999f, Units.DbToGain(-20) * 1.001f);
        Assert.Equal((1, Units.DbToGain(-6)), ((int)jump[1].Data1, jump[1].Value));
        // Linear interpolation between events stays within 0.05 dB of the curve.
        var curve = state.LaneOf(gtr, "volume")!.Points;
        foreach (var at in (long[])[12_345, 50_000, 95_999, 120_000, 170_000]) {
            var tick = state.TempoMap.TickAtSample(at, 48_000);
            var a = curve[0].Tick <= tick && tick < curve[1].Tick ? (curve[0], curve[1]) : (curve[1], curve[2]);
            var x = (double)(tick - a.Item1.Tick) / (a.Item2.Tick - a.Item1.Tick);
            var expected = a.Item1.Value + (a.Item2.Value - a.Item1.Value) * AutomationEvents.Shaped(x, a.Item1.Curve);
            var before = events.Last(e => e.SamplePos <= at);
            var after = events.First(e => e.SamplePos > at);
            var line = before.Value
                + (after.Value - before.Value) * (at - before.SamplePos) / (float)(after.SamplePos - before.SamplePos);
            Assert.InRange(20 * Math.Log10(line) - expected, -0.05, 0.05);
        }
    }

    [Fact]
    public void Mute_steps_pan_and_parameters_are_linear_and_targets_keep_their_indices() {
        Apply(new AddTrackCommand("Gtr"), new AddTrackCommand("Keys", Kind: TrackKind.Instrument));
        var (gtr, keys) = (T("Gtr").Id, T("Keys").Id);
        Apply(new AddInsertCommand("anode.test-compressor", gtr));
        var insert = T("Gtr").Strip.Inserts[0].Id;
        Apply(
            new SetAutomationCommand("mute", [new(0, 0, 0), new(960, 1, 0), new(1920, 1, 0), new(2880, 0, 0)], gtr),
            new SetAutomationCommand("pan", [new(960, -1, 0), new(1920, 1, 0)], gtr),
            new SetAutomationCommand($"insert:{insert}:threshold", [new(0, -40, 0), new(960, -20, 0)], gtr),
            new SetAutomationCommand("instrument:cutoff", [new(0, 100, -0.5), new(960, 5000, 0)], keys)
        );
        var engine = EngineIds.Empty.With(state);
        var model = new ModelCompiler().Compile(state, engine);
        var mute = Automation(model.Tracks[0], engine.Target(gtr, "mute"));
        Assert.Equal(
            [(0L, 0f, 1), (24_000, 1, 1), (72_000, 0, 1)],
            mute.Select(static e => (e.SamplePos, e.Value, (int)e.Data1))
        );
        var pan = Automation(model.Tracks[0], engine.Target(gtr, "pan"));
        Assert.Equal([(0L, -1f), (24_000, -1), (48_000, 1)], pan.Select(static e => (e.SamplePos, e.Value)));
        Assert.Equal(2, Automation(model.Tracks[0], engine.Target(gtr, $"insert:{insert}:threshold")).Length);
        var cutoff = Automation(model.Tracks[1], engine.Target(keys, "instrument:cutoff"));
        Assert.True(
            cutoff.Length > 3,
            string.Join(" ", model.Tracks[1].Events.Select(static e => $"{e.SamplePos}:{e.Value}:{e.Target}:{e.Kind}"))
        ); // a curve, sampled

        var targets = engine.AutomationTargets(state);
        Assert.Equal(targets.Select(static t => t.Index), Enumerable.Range(0, targets.Count));
        var threshold = targets.Single(static t => t.Kind == "insert");
        Assert.Equal(
            (engine.Track(gtr), insert, "threshold"),
            (threshold.TrackId, threshold.Id, threshold.ParameterId)
        );
        Assert.Equal("instrument", targets.Single(static t => t.ParameterId == "cutoff").Kind);

        // Indices stay put as lanes come and go.
        var index = engine.Target(keys, "instrument:cutoff");
        Apply(new SetAutomationCommand("pan", [], gtr), new SetAutomationCommand("volume", [new(0, 0, 0)], keys));
        var later = engine.With(state);
        Assert.Equal(index, later.Target(keys, "instrument:cutoff"));
        Assert.Equal(targets.Count, later.Target(keys, "volume"));
    }

    [Fact]
    public void Off_sends_nothing_frozen_keeps_the_fader_and_VCA_and_master_lanes_fold_in() {
        Apply(new AddTrackCommand("Gtr"), new AddVcaCommand("V"), new AddInsertCommand("com.example.vst3.gain"));
        var (gtr, vca) = (T("Gtr").Id, T("V").Id);
        Apply(
            new AddInsertCommand("anode.test-compressor", gtr),
            new AssignVcaCommand(gtr, vca),
            new SetStripCommand(gtr, -6)
        );
        var insert = T("Gtr").Strip.Inserts[0].Id;
        Apply(
            new SetAutomationCommand("volume", [new(0, -10, 0)], vca),
            new SetAutomationCommand("mute", [new(0, 0, 0), new(960, 1, 0)], vca),
            new SetAutomationCommand($"insert:{insert}:ratio", [new(0, 2, 0)], gtr),
            new SetAutomationCommand($"insert:{state.Master.Inserts[0].Id}:0", [new(0, -3, 0), new(960, 3, 0)])
        );
        var engine = EngineIds.Empty.With(state);
        var model = new ModelCompiler().Compile(state, engine);
        // The VCA's lane folds into the member's volume: −6 + −10 dB; its mute into the member's mute.
        var volume = Automation(model.Tracks[0], engine.Target(gtr, "volume"));
        Assert.InRange(volume[0].Value, Units.DbToGain(-16) * 0.999f, Units.DbToGain(-16) * 1.001f);
        Assert.Equal([0f, 1f], Automation(model.Tracks[0], engine.Target(gtr, "mute")).Select(static e => e.Value));
        // The master's lanes live under engine track −1.
        var master = model.Tracks.Single(static t => t.TrackId == AutomationEvents.MasterTrack);
        Assert.All(
            master.Events,
            static e => Assert.Equal((AutomationEvents.MasterTrack, PlaybackEventKind.Automation), (e.Track, e.Kind))
        );

        state = state.Apply(new AudioFileUpserted(new("aud_f", "F", "Audio Files/F.wav", 48_000, 2, 48_000, 32, null)));
        state = Edits.Freeze(state, gtr, "aud_f").State;
        Assert.DoesNotContain($"insert:{insert}:ratio", AutomationEvents.Targets(state, T("Gtr")));
        Assert.Contains("volume", AutomationEvents.Targets(state, T("Gtr")));

        Apply(new SetAutomationModeCommand(AutomationMode.Off, gtr));
        Assert.Empty(AutomationEvents.Targets(state, T("Gtr")));
        var compiler = new ModelCompiler();
        var off = compiler.Compile(state, engine.With(state));
        Assert.DoesNotContain(off.Tracks[0].Events, static e => e.Kind == PlaybackEventKind.Automation);
        // Recompiling an unchanged project reuses every model; a lane edit recompiles its track only.
        var again = compiler.Compile(state, engine.With(state));
        Assert.Empty(again.Recompiled);
        Apply(new SetAutomationCommand($"insert:{state.Master.Inserts[0].Id}:0", [new(0, 0, 0)]));
        Assert.Equal([AutomationEvents.MasterTrack], compiler.Compile(state, engine.With(state)).Recompiled);
    }
}
