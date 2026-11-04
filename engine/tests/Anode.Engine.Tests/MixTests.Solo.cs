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
