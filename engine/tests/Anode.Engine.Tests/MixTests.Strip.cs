using Anode.Ipc;

namespace Anode.Engine.Tests;

/// <summary>
///     The strip's input stage, measured against the known samples: trim and polarity before the inserts, the delay in
///     both directions and under every delay compensation mode, dual pan, and the pan laws.
/// </summary>
public sealed partial class MixTests {
    static float Db(float gain) => 20f * MathF.Log10(gain);

    static void Near(float expected, float actual, int frame, string what) {
        if (MathF.Abs(expected - actual) > 2e-6f) {
            Assert.Fail($"{what}, frame {frame}: expected {expected}, got {actual}");
        }
    }

    [Theory]
    [InlineData(EnginePanLaw.ZeroDb, 0f, 0f)]
    [InlineData(EnginePanLaw.Minus3Db, -3.01f, 0f)]
    [InlineData(EnginePanLaw.Minus3DbCompensated, 0f, 3.01f)]
    [InlineData(EnginePanLaw.Minus4Point5Db, -4.52f, 0f)]
    [InlineData(EnginePanLaw.Minus6Db, -6.02f, 0f)]
    public void A_pan_law_sets_the_level_of_a_mono_source_at_the_centre_and_a_hard_pan(
        EnginePanLaw law,
        float centreDb,
        float hardDb
    ) {
        using var rig = new Rig(dir, 64, [Model(1, 0)], [Track(1, false, Strip())], Files);
        rig.Controller.SetMixer("all", false, 10, law);
        var (left, right) = Level(rig, 0f);
        Assert.Equal(centreDb, Db(left), 0.01f);
        Assert.Equal(centreDb, Db(right), 0.01f);
        (left, right) = Level(rig, -1f);
        Assert.Equal(hardDb, Db(left), 0.01f);
        Assert.True(right < 1e-6f, $"the far side at a hard pan is silent, not {right}");
        (left, right) = Level(rig, 1f);
        Assert.Equal(hardDb, Db(right), 0.01f);
        Assert.True(left < 1e-6f, $"the far side at a hard pan is silent, not {left}");
    }

    /// <summary>The gains a mono track sounds at with <paramref name="pan" />, once its ramp is over.</summary>
    static (float Left, float Right) Level(Rig rig, float pan) {
        rig.Controller.SetStrip(1, Strip(pan: pan));
        rig.Driver.Send(new(EngineCommandKind.Stop));
        var o = rig.Render(640);
        const int n = 600; // past the first block's ramp
        return (MathF.Abs(o[0][n] / M(n)), MathF.Abs(o[1][n] / M(n)));
    }

    [Fact]
    public void A_stereo_source_is_balanced_whatever_the_pan_law() {
        foreach (var law in Enum.GetValues<EnginePanLaw>()) {
            Assert.Equal((1f, 0.25f), Engine.Strip.PanGains(-0.75f, true, law));
            Assert.Equal((1f, 1f), Engine.Strip.PanGains(0f, true, law));
        }
    }

    [Fact]
    public void Trim_and_polarity_scale_the_source_before_the_inserts_and_its_channels_alike() {
        // The gain insert halves the signal, a trim of −2 undoes that with the polarity inverted; the fader stays out.
        var halve = TestEffects.Insert("half", TestEffects.Gain, Gain(0.5f));
        var strip = Strip(inserts: [halve]) with { Trim = -2f };
        using var rig = new Rig(dir, 64, [Model(1, 1)], [Track(1, true, strip)], Files);
        var o = rig.Render(4_800);
        for (var n = 0; n < o[0].Length; n++) {
            Near(-S(n).L, o[0][n], n, "left");
            Near(-S(n).R, o[1][n], n, "right");
        }
    }

    [Fact]
    public void A_trim_change_ramps_over_a_block() {
        using var rig = new Rig(dir, 64, [Model(1, 0)], [Track(1, false, Strip())], Files);
        rig.Render(64);
        rig.Controller.SetStrip(1, Strip() with { Trim = 0.5f });
        rig.Driver.Process();
        var block = rig.Driver.Output(0);
        for (var i = 0; i < block.Length; i++) {
            var position = 64 + i;
            Near(M(position) * (1f - 0.5f * (i + 1) / 64f), block[i], position, "the ramp");
        }
    }

    [Theory]
    [InlineData("all")]
    [InlineData("tracks")]
    [InlineData("off")]
    public void A_late_strip_is_late_by_its_delay_alone_in_every_compensation_mode(string pdc) {
        const int delay = 480;
        EngineTrack[] tracks = [
            Track(1, false, Strip(output: -1) with { DelayFrames = delay }),
            Track(2, false, Strip(output: -2))
        ];
        using var rig = new Rig(dir, 64, [Model(1, 0), Model(2, 0)], tracks, Files, outputs: 4);
        rig.Controller.SetMixer(pdc, false, 10);
        var o = rig.Render(2_400);
        for (var n = 0; n < o[0].Length; n++) {
            Same(M(n - delay), o[0][n], n, "the late strip");
            Same(M(n), o[2][n], n, "the other strip");
        }
    }

    [Theory]
    [InlineData("all")]
    [InlineData("tracks")]
    [InlineData("off")]
    public void An_early_strip_leads_the_others_into_their_summing_point(string pdc) {
        // Track 2 plays the same audio 480 frames further on, inverted: it nulls against track 1 only if everything
        // but the early strip is held back by exactly those frames.
        const int delay = 480;
        EngineTrack[] tracks = [
            Track(1, false, Strip() with { DelayFrames = -delay }),
            Track(2, false, Strip(inserts: [TestEffects.Insert("g", TestEffects.Gain, Gain(-1))]))
        ];
        using var rig = new Rig(dir, 64, [Model(1, 0), Model(2, 0, delay)], tracks, Files);
        rig.Controller.SetMixer(pdc, false, 10);
        var o = rig.Render(2_400);
        Assert.All(o[0].Skip(delay), static v => Assert.Equal(0f, v)); // before that the others have not started
        Assert.All(o[1].Skip(delay), static v => Assert.Equal(0f, v));

        // The early strip itself is not delayed.
        rig.Controller.SetStrip(2, Strip(mute: true, inserts: [TestEffects.Insert("g", TestEffects.Gain, Gain(-1))]));
        rig.Driver.Send(new(EngineCommandKind.Stop));
        o = rig.Render(2_400);
        for (var n = 64; n < o[0].Length; n++) {
            Same(M(n), o[0][n], n, "the early strip");
        }
    }

    [Fact]
    public void A_delay_set_while_playing_gets_its_line_at_once() {
        using var rig = new Rig(dir, 64, [Model(1, 0)], [Track(1, false, Strip())], Files);
        rig.Render(640);
        rig.Controller.SetStrip(1, Strip() with { DelayFrames = 4_800 });
        Assert.True(rig.Controller.Engine.Root.Mix.Order[0].Channel.Delay?.MaxDelay >= 4_800);
    }

    [Fact]
    public void Dual_pan_at_the_extremes_passes_a_stereo_signal_as_it_is() {
        var strip = Strip() with { DualPan = true, PanLeft = -1f, PanRight = 1f };
        using var rig = new Rig(dir, 64, [Model(1, 1)], [Track(1, true, strip)], Files);
        var o = rig.Render(2_400);
        for (var n = 0; n < o[0].Length; n++) {
            Same(S(n).L, o[0][n], n, "left");
            Same(S(n).R, o[1][n], n, "right");
        }
    }

    [Fact]
    public void Dual_pan_places_each_channel_on_its_own_and_mixes_them() {
        var strip = Strip() with { DualPan = true, PanLeft = 1f, PanRight = -1f };
        using var rig = new Rig(dir, 64, [Model(1, 1)], [Track(1, true, strip)], Files);
        var o = rig.Render(2_400);
        for (var n = 0; n < o[0].Length; n++) {
            Same(S(n).R, o[0][n], n, "the channels swapped, left");
            Same(S(n).L, o[1][n], n, "the channels swapped, right");
        }

        // Both to the centre: a mono fold at the law's centre, −3 dB with a hard pan at unity.
        rig.Controller.SetStrip(1, strip with { PanLeft = 0f, PanRight = 0f });
        rig.Driver.Send(new(EngineCommandKind.Stop));
        o = rig.Render(2_400);
        var centre = MathF.Sqrt(0.5f);
        for (var n = 64; n < o[0].Length; n++) {
            var fold = (S(n).L + S(n).R) * centre;
            Near(fold, o[0][n], n, "the fold, left");
            Near(fold, o[1][n], n, "the fold, right");
        }
    }

    [Fact]
    public void Dual_pan_leaves_a_mono_source_to_the_pan_and_the_sends_to_their_own() {
        var sends = new[] { new EngineSend("s", 1, 1f, 0f, true) };
        var strip = Strip(sends: sends) with { DualPan = true, PanLeft = 1f, PanRight = 1f };
        EngineTrack[] tracks = [Track(1, true, strip), Track(10, true, Strip(output: -2), 1)];
        using var rig = new Rig(dir, 64, [Model(1, 1)], tracks, Files, outputs: 4);
        var o = rig.Render(2_400);
        for (var n = 0; n < o[0].Length; n++) {
            Same(0f, o[0][n], n, "both channels went right");
            Same(S(n).L + S(n).R, o[1][n], n, "both channels went right");
            Same(S(n).L, o[2][n], n, "the send, left");
            Same(S(n).R, o[3][n], n, "the send, right");
        }

        strip = Strip(pan: -1f) with { DualPan = true, PanLeft = 1f, PanRight = 1f };
        using var mono = new Rig(dir, 64, [Model(1, 0)], [Track(1, false, strip)], Files);
        o = mono.Render(2_400);
        var (left, right) = Engine.Strip.PanGains(-1f, false);
        for (var n = 0; n < o[0].Length; n++) {
            Same(M(n) * left, o[0][n], n, "a mono source follows the pan");
            Same(M(n) * right, o[1][n], n, "a mono source follows the pan");
        }
    }
}
