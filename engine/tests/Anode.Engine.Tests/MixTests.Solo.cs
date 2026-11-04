using Anode.Ipc;

namespace Anode.Engine.Tests;

/// <summary>
///     Solo safe, AFL and PFL, and the monitor section, against the known samples: what the speakers get, and that the mix
///     (the master's meter, a bounce) stays what it was.
/// </summary>
public sealed partial class MixTests {
    static void Listen(Rig rig, EngineSoloMode mode) => rig.Controller.SetMixer("all", false, 10, soloMode: mode);

    [Fact]
    public void A_solo_safe_strip_stays_audible_under_solo_in_place_and_keeps_its_path_open() {
        EngineSend[] sends = [new("s", 1, 1f, 0f, true)];
        EngineTrack[] tracks = [
            Track(1, false, Strip(solo: true)),
            Track(2, false, Strip()), // not soloed: silent
            Track(3, false, Strip(output: 1)), // silent, so its bus has nothing for the safe aux
            Track(10, true, Strip(output: -2) with { SoloSafe = true }, 1), // the aux keeps its own stream, here none
            Track(4, false, Strip(output: -2, sends: sends) with { SoloSafe = true }) // safe: its send and output play
        ];
        using var rig = new Rig(
            dir,
            64,
            [Model(1, 0), Model(2, 0, 100), Model(3, 0, 200), Model(4, 0, 300)],
            tracks,
            Files,
            outputs: 4
        );
        var o = rig.Render(2_400);
        for (var n = 0; n < o[0].Length; n++) {
            Same(M(n), o[0][n], n, "the soloed track alone on the master");
            Same(M(n + 300) + M(n + 300), o[2][n], n, "the safe track and, through bus 1, the safe aux");
        }
    }

    [Fact]
    public void AFL_sends_the_soloed_strip_after_its_fader_to_the_monitors_and_leaves_the_mix_alone() {
        EngineTrack[] tracks = [Track(1, false, Strip(0.5f, -0.5f, solo: true)), Track(2, true, Strip(0.25f))];
        using var rig = new Rig(dir, 64, [Model(1, 0), Model(2, 1)], tracks, Files);
        Listen(rig, EngineSoloMode.Afl);
        var (l, r) = Coef(0.5f, -0.5f, false);
        var o = rig.Render(2_400);
        var mix = 0f;
        for (var n = 0; n < o[0].Length; n++) {
            Same(M(n) * l, o[0][n], n, "the monitors, left");
            Same(M(n) * r, o[1][n], n, "the monitors, right");
            mix = float.Max(mix, MathF.Abs(M(n) * l + S(n).L * 0.25f));
        }

        Assert.Equal(mix, rig.Controller.Engine.Master.TakePeaks().Left, 1e-5f);
    }

    [Fact]
    public void PFL_sends_the_soloed_strip_before_its_fader_and_pan() {
        EngineTrack[] tracks = [Track(1, false, Strip(0.1f, -0.5f, solo: true)), Track(2, true, Strip(0.25f))];
        using var rig = new Rig(dir, 64, [Model(1, 0), Model(2, 1)], tracks, Files);
        Listen(rig, EngineSoloMode.Pfl);
        var o = rig.Render(2_400);
        for (var n = 0; n < o[0].Length; n++) {
            Same(M(n), o[0][n], n, "the monitors, left");
            Same(M(n), o[1][n], n, "the monitors, right");
        }
    }

    [Theory]
    [InlineData(EngineSoloMode.Afl)]
    [InlineData(EngineSoloMode.Pfl)]
    public void Without_a_solo_listen_modes_change_nothing(EngineSoloMode mode) {
        EngineTrack[] tracks = [Track(1, false, Strip(0.5f)), Track(2, true, Strip(0.25f))];
        using var rig = new Rig(dir, 64, [Model(1, 0), Model(2, 1)], tracks, Files);
        Listen(rig, mode);
        var o = rig.Render(2_400);
        var (l, _) = Coef(0.5f, 0f, false);
        for (var n = 0; n < o[0].Length; n++) {
            Same(M(n) * l + S(n).L * 0.25f, o[0][n], n, "the mix");
        }
    }

    [Fact]
    public void The_monitor_section_scales_and_folds_the_speakers_and_not_the_meters() {
        using var rig = new Rig(dir, 64, [Model(1, 1)], [Track(1, true, Strip())], Files);
        var engine = rig.Controller.Engine;
        var o = rig.Render(640);
        for (var n = 0; n < o[0].Length; n++) {
            Same(S(n).L, o[0][n], n, "flat: bit for bit");
        }

        var before = engine.Master.TakePeaks();
        engine.SetMonitorSection(0.5f, true);
        rig.Driver.Send(new(EngineCommandKind.Stop));
        o = rig.Render(2_400);
        for (var n = 64; n < o[0].Length; n++) {
            var fold = 0.25f * (S(n).L + S(n).R);
            Near(fold, o[0][n], n, "half, folded, left");
            Near(fold, o[1][n], n, "half, folded, right");
        }

        Assert.True(engine.Master.TakePeaks().Left >= before.Left, "the meter reads the master, not the speakers");
    }

    [Theory]
    [InlineData(EngineMeterTap.Input, 2f)]
    [InlineData(EngineMeterTap.PreFader, 1f)]
    [InlineData(EngineMeterTap.PostFader, 0.5f)]
    public void A_strip_meter_reads_at_its_tap_point(EngineMeterTap tap, float scale) {
        // A trim of 2, a halving insert, a fader at 0.5: each tap sees its own level of the source.
        var halve = TestEffects.Insert("half", TestEffects.Gain, Gain(0.5f));
        var strip = Strip(0.5f, inserts: [halve]) with { Trim = 2f, MeterTap = tap };
        using var rig = new Rig(dir, 64, [Model(1, 1)], [Track(1, true, strip)], Files);
        var o = rig.Render(2_400);
        var peak = 0f;
        for (var n = 0; n < o[0].Length; n++) {
            peak = float.Max(peak, MathF.Abs(S(n).L));
        }

        var (left, _) = rig.Controller.Engine.Root.Tracks[0].Strip.TakePeaks();
        Assert.Equal(peak * scale, left, 1e-4f);
    }

    [Theory]
    [InlineData(EngineMeterTap.Input)]
    [InlineData(EngineMeterTap.PreFader)]
    [InlineData(EngineMeterTap.PostFader)]
    public void A_masters_meter_has_the_same_tap_points(EngineMeterTap tap) {
        var master = Strip(0.5f) with { MeterTap = tap };
        using var rig = new Rig(dir, 64, [Model(1, 1)], [Track(1, true, Strip())], Files, master);
        var o = rig.Render(2_400);
        var peak = 0f;
        for (var n = 0; n < o[0].Length; n++) {
            peak = float.Max(peak, MathF.Abs(S(n).L));
        }

        var expected = tap == EngineMeterTap.PostFader ? peak * 0.5f : peak;
        Assert.Equal(expected, rig.Controller.Engine.Master.TakePeaks().Left, 1e-4f);
    }

    [Fact]
    public void A_meter_that_wants_the_signal_changes_nothing_of_the_mix() {
        var strip = Strip(0.5f) with { MeterType = EngineMeterType.Lufs };
        using var rig = new Rig(dir, 64, [Model(1, 1)], [Track(1, true, strip)], Files);
        var o = rig.Render(9_600);
        for (var n = 0; n < o[0].Length; n++) {
            Same(S(n).L * 0.5f, o[0][n], n, "the mix");
        }

        Assert.True(rig.Controller.Engine.Root.Tracks[0].Strip.MeterLevels().Lufs > -120f);
    }

    [Fact]
    public void The_callback_load_is_the_share_of_its_deadline_it_takes() {
        // A 64-frame callback has 1.33 ms; an insert that takes 4 ms makes it about 3 times too slow.
        var slow = TestEffects.Insert("s", TestEffects.Slow, new Dictionary<string, double> { ["ms"] = 4 });
        using var rig = new Rig(dir, 64, [Model(1, 0)], [Track(1, false, Strip(inserts: [slow]))], Files);
        rig.Render(1_920);
        var (load, peak) = rig.Controller.Engine.TakeLoad();
        Assert.True(peak >= 2.9f, $"the worst callback took 3 deadlines at least, not {peak}");
        Assert.True(load > 0.2f, $"the average climbs towards it, at {load}");

        Assert.True(rig.Controller.SetInsertParameter(1, "s", "ms", 0));
        rig.Driver.Send(new(EngineCommandKind.Stop));
        rig.Render(640);
        rig.Controller.Engine.TakeLoad();
        rig.Driver.Process();
        Assert.True(rig.Controller.Engine.TakeLoad().Peak < peak, "the peak is since the last time it was taken");
    }

    [Fact]
    public void The_monitor_level_ramps_and_takes_the_click_along() {
        using var rig = new Rig(dir, 64, [Model(1, 0)], [Track(1, false, Strip())], Files);
        rig.Render(64);
        rig.Controller.Engine.SetMonitorSection(0.25f, false);
        rig.Driver.Process();
        var block = rig.Driver.Output(0);
        for (var i = 0; i < block.Length; i++) {
            var position = 64 + i;
            Near(M(position) * (1f - 0.75f * (i + 1) / 64f), block[i], position, "the ramp");
        }
    }
}
