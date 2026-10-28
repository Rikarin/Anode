using Anode.Contracts;
using Anode.Model.Compile;
using Anode.Model.Editing;
using Anode.Model.History;

namespace Anode.Model.Tests;

/// <summary>The mixer's edits (docs/05-phase-4.md): validation, routing loops, groups, VCAs and automation recording.</summary>
public sealed class MixerEditTests {
    readonly SequentialIds ids = new();
    ProjectState state = ProjectDefaults.NewProject("prj_1", "Song", 48_000);

    void Apply(params EditCommand[] commands) =>
        state = Edits.Apply(state, commands, ids, RandomProjects.Plugins, RandomProjects.Patches).State;

    string Fails(params EditCommand[] commands) =>
        Assert.Throws<EditException>(() => Edits.Apply(
                    state,
                    commands,
                    ids,
                    RandomProjects.Plugins,
                    RandomProjects.Patches
                )
            )
            .Message;

    Track T(string name) => state.Tracks.Single(t => t.Name == name);

    [Fact]
    public void Routing_loops_are_refused_directly_and_through_other_buses() {
        Apply(new AddAuxCommand(1, "A"), new AddAuxCommand(2, "B"), new AddTrackCommand("Gtr"));
        Assert.Contains("loop", Fails(new SetOutputCommand(T("A").Id, "bus:1")), StringComparison.Ordinal);
        Assert.Contains("loop", Fails(new AddSendCommand(T("A").Id, 1, 0)), StringComparison.Ordinal);
        Apply(new SetOutputCommand(T("A").Id, "bus:2"));
        Assert.Contains(
            "bus 1 → A → bus 2 → B → bus 1",
            Fails(new AddSendCommand(T("B").Id, 1, 0)),
            StringComparison.Ordinal
        );
        // A track that is not an aux cannot close a loop.
        Apply(new SetOutputCommand(T("Gtr").Id, "bus:1"), new AddSendCommand(T("Gtr").Id, 2));
        Assert.Equal("bus:1", T("Gtr").Strip.Output);
        Assert.Equal(Units.SilenceDb, T("Gtr").Strip.Sends[0].LevelDb);
        Assert.Null(Routing.Loop(state));
    }

    [Fact]
    public void Buses_groups_outputs_and_ids_are_checked() {
        Apply(new AddTrackCommand("Gtr"), new AddVcaCommand());
        var gtr = T("Gtr").Id;
        Assert.Contains("1…64", Fails(new AddSendCommand(gtr, 0)), StringComparison.Ordinal);
        Assert.Contains("1…64", Fails(new AddSendCommand(gtr, 65)), StringComparison.Ordinal);
        Assert.Contains("1…64", Fails(new AddAuxCommand(65)), StringComparison.Ordinal);
        Assert.Contains("1…32", Fails(new SetGroupCommand(33)), StringComparison.Ordinal);
        Assert.Contains("1…32", Fails(new SetGroupCommand(0)), StringComparison.Ordinal);
        Assert.Contains("setGroup", Fails(new SetTrackGroupsCommand(gtr, [4])), StringComparison.Ordinal);
        Assert.Contains("output", Fails(new SetOutputCommand(gtr, "bus:65")), StringComparison.Ordinal);
        Assert.Contains("No insert", Fails(new RemoveInsertCommand("ins_x", gtr)), StringComparison.Ordinal);
        Assert.Contains("no send", Fails(new SetSendCommand(gtr, "snd_x", -6)), StringComparison.Ordinal);
        Assert.Contains("instrument", Fails(new AddInsertCommand("anode.synth", gtr)), StringComparison.Ordinal);
        Assert.Contains("No plugin", Fails(new AddInsertCommand("nope", gtr)), StringComparison.Ordinal);
        Assert.Contains("addAux", Fails(new AddTrackCommand(Kind: TrackKind.Aux)), StringComparison.Ordinal);
        Assert.Contains("VCA", Fails(new AddInsertCommand("com.example.fx", T("VCA 1").Id)), StringComparison.Ordinal);
        Apply(new AddSendCommand(gtr, 3));
        Assert.Contains("already sends", Fails(new AddSendCommand(gtr, 3)), StringComparison.Ordinal);
    }

    [Fact]
    public void Inserts_add_move_bypass_sidechain_and_take_parameters_within_range() {
        Apply(new AddTrackCommand("Gtr"), new AddTrackCommand("Kick"));
        var gtr = T("Gtr").Id;
        Apply(
            new AddInsertCommand("anode.test-compressor", gtr),
            new AddInsertCommand("com.example.fx", gtr),
            new AddInsertCommand("com.example.vst3.gain", gtr, 0)
        );
        Assert.Equal(["Test Gain", "Test Compressor", "Test Delay"], T("Gtr").Strip.Inserts.Select(static i => i.Name));
        var comp = T("Gtr").Strip.Inserts[1];
        Assert.Equal(-20, comp.Parameters["threshold"]);
        Apply(new MoveInsertCommand(comp.Id, 0, gtr), new SetInsertCommand(comp.Id, gtr, true, T("Kick").Id));
        Assert.Equal(
            (comp.Id, true, T("Kick").Id),
            (T("Gtr").Strip.Inserts[0].Id, T("Gtr").Strip.Inserts[0].Bypassed, T("Gtr").Strip.Inserts[0].Sidechain)
        );
        Apply(new SetInsertParameterCommand(comp.Id, "character", 1.4, gtr));
        Assert.Equal(1, T("Gtr").Strip.Inserts[0].Parameters["character"]); // stepped
        Assert.Contains(
            "-60…0",
            Fails(new SetInsertParameterCommand(comp.Id, "threshold", 3, gtr)),
            StringComparison.Ordinal
        );
        var delay = T("Gtr").Strip.Inserts.Single(static i => i.PluginId == "com.example.fx").Id;
        Assert.Contains(
            "no sidechain",
            Fails(new SetInsertCommand(delay, gtr, Sidechain: "bus:2")),
            StringComparison.Ordinal
        );
        Assert.Contains(
            "sidechain",
            Fails(new SetInsertCommand(comp.Id, gtr, Sidechain: gtr)),
            StringComparison.Ordinal
        );
        // The engine sets tempo-synced plugins' tempo: inserts do not keep it, edits and lanes refuse it.
        var gain = T("Gtr").Strip.Inserts.Single(static i => i.PluginId == "com.example.vst3.gain");
        Assert.False(gain.Parameters.ContainsKey(Edits.HostTempoParameter));
        Assert.Contains(
            "tempo",
            Fails(new SetInsertParameterCommand(gain.Id, "tempo", 100, gtr)),
            StringComparison.Ordinal
        );
        Assert.Contains(
            "tempo",
            Fails(new SetAutomationCommand($"insert:{gain.Id}:tempo", [new(0, 100, 0)], gtr)),
            StringComparison.Ordinal
        );

        // Removing the track a sidechain comes from clears the sidechain.
        Apply(new RemoveTrackCommand(T("Kick").Id));
        Assert.Null(T("Gtr").Strip.Inserts[0].Sidechain);
    }

    [Fact]
    public void Removing_an_insert_or_send_removes_its_lanes() {
        Apply(new AddTrackCommand("Gtr"));
        var gtr = T("Gtr").Id;
        Apply(new AddInsertCommand("anode.test-compressor", gtr), new AddSendCommand(gtr, 1, -10));
        var (insert, send) = (T("Gtr").Strip.Inserts[0].Id, T("Gtr").Strip.Sends[0].Id);
        Apply(
            new SetAutomationCommand($"insert:{insert}:threshold", [new(0, -30, 0)], gtr),
            new SetAutomationCommand($"send:{send}", [new(0, -10, 0), new(960, 0, 0)], gtr),
            new SetAutomationCommand("volume", [new(0, 0, 0)], gtr)
        );
        Assert.Equal(3, state.Automation.Count);
        Apply(new RemoveInsertCommand(insert, gtr), new RemoveSendCommand(gtr, send));
        Assert.Equal("volume", Assert.Single(state.Automation.Values).Target);
        Assert.Contains(
            "mute lane",
            Fails(new SetAutomationCommand("mute", [new(0, 0.5, 0)], gtr)),
            StringComparison.Ordinal
        );
        Assert.Contains(
            "order",
            Fails(new SetAutomationCommand("pan", [new(960, 0, 0), new(0, 0, 0)], gtr)),
            StringComparison.Ordinal
        );
        Assert.Contains(
            "two",
            Fails(new SetAutomationCommand("pan", [new(0, 0, 0), new(0, 0.5, 0), new(0, 1, 0)], gtr)),
            StringComparison.Ordinal
        );
        Apply(new SetAutomationCommand("volume", [], gtr));
        Assert.Empty(state.Automation);
    }

    [Fact]
    public void Groups_link_the_properties_they_say_and_volume_keeps_offsets() {
        Apply(new AddTrackCommand("A"), new AddTrackCommand("B"), new AddTrackCommand("C"));
        Apply(
            new SetStripCommand(T("B").Id, -6),
            new SetGroupCommand(1, "Drums", Properties: new(true, true, false, false, true, true, true, true)),
            new SetTrackGroupsCommand(T("A").Id, [1]),
            new SetTrackGroupsCommand(T("B").Id, [1])
        );
        Apply(new SetStripCommand(T("A").Id, -3, 0.5, true, true));
        Assert.Equal(
            (-3.0, 0.5, true, true),
            (T("A").Strip.VolumeDb, T("A").Strip.Pan, T("A").Strip.Mute, T("A").Strip.Solo)
        );
        // Volume keeps B's offset, mute follows; pan and solo are not linked; C is in no group.
        Assert.Equal(
            (-9.0, 0.0, true, false),
            (T("B").Strip.VolumeDb, T("B").Strip.Pan, T("B").Strip.Mute, T("B").Strip.Solo)
        );
        Assert.Equal(0, T("C").Strip.VolumeDb);

        Apply(new SetTrackCommand(T("A").Id, RecordArmed: true));
        Assert.True(T("B").RecordArmed);
        Apply(new SetAutomationModeCommand(AutomationMode.Touch, T("B").Id));
        Assert.Equal(AutomationMode.Touch, T("A").AutomationMode);

        Apply(new AddSendCommand(T("A").Id, 4, -10), new AddSendCommand(T("B").Id, 4, -20));
        Apply(new SetSendCommand(T("A").Id, T("A").Strip.Sends[0].Id, -5));
        Assert.Equal(-15, T("B").Strip.Sends[0].LevelDb);

        // A disabled group links nothing; a deleted group's members leave it.
        Apply(new SetGroupCommand(1, Enabled: false), new SetStripCommand(T("A").Id, 0));
        Assert.Equal(-9, T("B").Strip.VolumeDb);
        Apply(new DeleteGroupCommand(1));
        Assert.Empty(T("A").Strip.Groups);
        Assert.Empty(state.Groups);
    }

    [Fact]
    public void A_VCA_folds_into_its_members_and_removing_it_releases_them() {
        Apply(new AddTrackCommand("A"), new AddVcaCommand("Drums VCA"));
        var vca = T("Drums VCA").Id;
        Apply(
            new AssignVcaCommand(T("A").Id, vca),
            new SetStripCommand(vca, -6, Mute: false, Solo: true),
            new SetStripCommand(T("A").Id, -3)
        );
        var effective = Stacks.EffectiveStrip(state, T("A"));
        Assert.Equal((-9.0, true), (effective.VolumeDb, effective.Solo));
        var engine = Strips.Engine(state, T("A"), EngineIds.Empty.With(state));
        Assert.InRange(engine.Gain, Units.DbToGain(-9) - 1e-6, Units.DbToGain(-9) + 1e-6);
        Assert.Contains("pan", Fails(new SetStripCommand(vca, Pan: 0.5)), StringComparison.Ordinal);
        Assert.Contains("VCA", Fails(new AssignVcaCommand(vca, vca)), StringComparison.Ordinal);
        Assert.Contains("not a VCA", Fails(new AssignVcaCommand(T("A").Id, T("A").Id)), StringComparison.Ordinal);
        Assert.False(Stacks.IsPlayable(T("Drums VCA")));

        Apply(new RemoveTrackCommand(vca));
        Assert.Null(T("A").Strip.VcaId);
    }

    [Fact]
    public void An_aux_takes_the_lowest_free_bus_and_cannot_record() {
        Apply(new AddTrackCommand("Gtr"));
        Apply(new AddSendCommand(T("Gtr").Id, 1), new AddAuxCommand());
        Assert.Equal(
            (TrackKind.Aux, 2, "Aux 2"),
            (state.Tracks[^1].Kind, state.Tracks[^1].InputBus, state.Tracks[^1].Name)
        );
        Assert.Contains("already feeds", Fails(new AddAuxCommand(2)), StringComparison.Ordinal);
        Assert.Contains(
            "records",
            Fails(new SetTrackCommand(state.Tracks[^1].Id, RecordArmed: true)),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Patches_replace_inserts_and_sends_with_new_ids_and_an_instrument_patch_needs_an_instrument_track() {
        Apply(new AddTrackCommand("Vox"), new AddTrackCommand("Keys", Kind: TrackKind.Instrument));
        Apply(new ApplyPatchCommand(T("Vox").Id, "pat_strip"));
        var strip = T("Vox").Strip;
        Assert.Equal(["anode.test-compressor", "com.example.fx"], strip.Inserts.Select(static i => i.PluginId));
        Assert.DoesNotContain(strip.Inserts, static i => i.Id.StartsWith("ins_p", StringComparison.Ordinal));
        Assert.Equal([2, 5], strip.Sends.Select(static s => s.Bus));
        Assert.Equal("bus:3", strip.Inserts[1].Sidechain);
        Assert.Contains(
            "instrument patch",
            Fails(new ApplyPatchCommand(T("Vox").Id, "pat_synth")),
            StringComparison.Ordinal
        );
        Apply(new ApplyPatchCommand(T("Keys").Id, "pat_synth"));
        Assert.Equal(1200, T("Keys").Instrument!.Parameters["cutoff"]);
        Assert.False(T("Keys").Strip.Inserts[0].Parameters.ContainsKey("tempo"));
        Assert.Contains("No patch", Fails(new ApplyPatchCommand(T("Vox").Id, "pat_none")), StringComparison.Ordinal);
    }

    [Fact]
    public void Freezing_is_one_step_and_a_frozen_track_keeps_its_plugins_until_unfrozen() {
        state = state.Apply(
            new AudioFileUpserted(new("aud_f", "Freeze", "Audio Files/Freeze.wav", 48_000, 2, 48_000, 32, null))
        );
        Apply(new AddTrackCommand("Keys", Kind: TrackKind.Instrument));
        var keys = T("Keys").Id;
        Apply(new AddInsertCommand("com.example.fx", keys));
        var before = state;
        var frozen = Edits.Freeze(state, keys, "aud_f");
        state = frozen.State;
        Assert.Equal("aud_f", T("Keys").FrozenFileId);
        Assert.Equal(RandomProjects.Json(before), RandomProjects.Json(state.Apply(frozen.Inverse)));
        Assert.Contains("frozen", Fails(new AddInsertCommand("com.example.fx", keys)), StringComparison.Ordinal);
        Assert.Contains("frozen", Fails(new SetParameterCommand(keys, "cutoff", 100)), StringComparison.Ordinal);
        Apply(new UnfreezeTrackCommand(keys));
        Assert.Null(T("Keys").FrozenFileId);
        Assert.Contains("not frozen", Fails(new UnfreezeTrackCommand(keys)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_touch_pass_records_moves_into_lanes_and_undoes_in_one_step() {
        Apply(new AddTrackCommand("Gtr"), new AddTrackCommand("Bass"));
        var gtr = T("Gtr").Id;
        Apply(
            new SetAutomationModeCommand(AutomationMode.Touch, gtr),
            new SetAutomationCommand("volume", [new(0, -10, 0), new(9600, -10, 0)], gtr)
        );
        var before = state;
        var time = new ManualTime();
        var history = new EditHistory(time);
        var pass = new AutomationPass(1, 0);

        void Move(long tick, EditCommand command) {
            var batch = new CommandBatch([command]);
            Assert.True(AutomationPass.Records(batch.Commands));
            var edit = Edits.Apply(state, batch.Commands, ids, RandomProjects.Plugins);
            var forward = new List<DeltaOp>(edit.Forward);
            var inverse = new List<DeltaOp>();
            var recorded = Edits.Step(edit.State, pass.Record(state, edit.State, tick, ids), forward, inverse);
            inverse.Reverse();
            history.Push("Automation", tick, new(forward, [..inverse, ..edit.Inverse]), pass.CoalesceKey);
            state = recorded;
            time.Advance(TimeSpan.FromSeconds(5)); // far beyond the drag window: the pass still coalesces
        }

        Move(1920, new SetStripCommand(gtr, -6));
        Move(2880, new SetStripCommand(gtr, -3));
        Move(3840, new SetStripCommand(gtr, Pan: 0.5));
        Move(4000, new SetStripCommand(T("Bass").Id, -1)); // Bass reads: no lane
        var volume = state.LaneOf(gtr, "volume")!;
        Assert.Equal(
            [(0L, -10.0), (1920, -6), (2880, -3), (9600, -10)],
            volume.Points.Select(static p => (p.Tick, p.Value))
        );
        Assert.Equal([(3840L, 0.5)], state.LaneOf(gtr, "pan")!.Points.Select(static p => (p.Tick, p.Value)));
        Assert.Null(state.LaneOf(T("Bass").Id, "volume"));
        Assert.Empty(pass.Finish(state, 6000, ids)); // touch: the lane takes over again

        var step = Assert.Single(history.History().Undo);
        Assert.Equal("Automation", step.Label);
        Assert.Equal(RandomProjects.Json(before), RandomProjects.Json(state.Apply(history.PeekUndo()!.Inverse)));
    }

    [Fact]
    public void Latch_holds_the_last_value_and_write_flattens_untouched_lanes_to_where_playback_stopped() {
        Apply(new AddTrackCommand("Gtr"), new AddTrackCommand("Keys"));
        var (gtr, keys) = (T("Gtr").Id, T("Keys").Id);
        Apply(
            new SetAutomationModeCommand(AutomationMode.Latch, gtr),
            new SetAutomationModeCommand(AutomationMode.Write, keys),
            new SetAutomationCommand("volume", [new(0, -10, 0), new(3000, -20, 0), new(9600, -10, 0)], gtr),
            new SetAutomationCommand("pan", [new(0, -1, 0), new(9600, 1, 0)], keys)
        );
        var pass = new AutomationPass(7, 960);
        var edit = Edits.Apply(state, [new SetStripCommand(gtr, -2), new SetStripCommand(keys, -4)], ids);
        state = edit.State.Apply(pass.Record(state, edit.State, 1920, ids));
        state = state.Apply(pass.Finish(state, 4800, ids));
        Assert.Equal(
            [(0L, -10.0), (1920, -2), (4800, -2), (9600, -10)],
            state.LaneOf(gtr, "volume")!.Points.Select(static p => (p.Tick, p.Value))
        );
        Assert.Equal(
            [(1920L, -4.0), (4800, -4)],
            state.LaneOf(keys, "volume")!.Points.Select(static p => (p.Tick, p.Value))
        );
        // Write: the untouched pan lane is the control's value over the pass.
        Assert.Equal(
            [(0L, -1.0), (960, 0), (4800, 0), (9600, 1)],
            state.LaneOf(keys, "pan")!.Points.Select(static p => (p.Tick, p.Value))
        );
        Assert.Equal("pass|7", pass.CoalesceKey);
        Assert.False(AutomationPass.Records([new SetStripCommand(gtr, 0), new AddTrackCommand()]));
    }

    sealed class ManualTime : TimeProvider {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }
}
