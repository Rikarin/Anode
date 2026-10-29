using Anode.Dsp.Effects;
using Anode.Dsp.Effects.Dynamics;
using Anode.Dsp.Instruments;
using Anode.Dsp.Tests.Support;
using static Anode.Dsp.Tests.Support.Analysis;

namespace Anode.Dsp.Tests;

/// <summary>What the <see cref="IAudioEffect" /> contract asks of every built-in effect.</summary>
public sealed class EffectContractTests {
    public static TheoryData<string> Ids => [..BuiltinEffects.All.Select(static d => d.Id)];

    static readonly string[] Categories = [
        EffectCategories.Eq, EffectCategories.Dynamics, EffectCategories.Delay, EffectCategories.Reverb,
        EffectCategories.Modulation, EffectCategories.Distortion, EffectCategories.Filter, EffectCategories.Utility,
        EffectCategories.Metering, EffectCategories.Pitch, EffectCategories.Specialized, EffectCategories.MultiEffects
    ];

    /// <summary>The T1 suite of docs/05-phase-4.md, by id.</summary>
    static readonly string[] Suite = [
        "anode.channel-eq", "anode.compressor", "anode.limiter", "anode.adaptive-limiter", "anode.noise-gate",
        "anode.deesser", "anode.gain", "anode.stereo-delay", "anode.tape-delay", "anode.reverb", "anode.chorus",
        "anode.flanger", "anode.phaser", "anode.tremolo", "anode.overdrive", "anode.distortion", "anode.bitcrusher",
        "anode.auto-filter", "anode.level-meter", "anode.loudness-meter", "anode.multimeter",
        // The T2 suite of docs/07-phase-6.md.
        "anode.space", "anode.linear-eq", "anode.multipressor", "anode.enveloper", "anode.exciter", "anode.subbass",
        "anode.delay-designer", "anode.pitch-shifter", "anode.pitch-correction", "anode.ensemble",
        "anode.rotor-cabinet", "anode.phat-fx", "anode.step-fx", "anode.beat-breaker", "anode.remix-fx"
    ];

    [Fact]
    public void Descriptors_are_consistent_and_creatable() {
        Assert.Equal(Suite, BuiltinEffects.All.Select(static d => d.Id));
        Assert.Null(BuiltinEffects.Create("nope"));
        foreach (var descriptor in BuiltinEffects.All) {
            using var effect = BuiltinEffects.Create(descriptor.Id);
            Assert.NotNull(effect);
            Assert.Same(descriptor, effect.Descriptor);
            Assert.StartsWith("anode.", descriptor.Id, StringComparison.Ordinal);
            Assert.Contains(descriptor.Category, Categories);
            Assert.Equal("Anode", descriptor.Vendor);
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Name));
            Assert.Equal(
                descriptor.Parameters.Count,
                descriptor.Parameters.Select(static p => p.Id).Distinct().Count()
            );
            for (var i = 0; i < descriptor.Parameters.Count; i++) {
                var p = descriptor.Parameters[i];
                Assert.Equal(i, p.Index);
                Assert.InRange(p.Default, p.Min, p.Max);
                Assert.True(p.Max > p.Min, p.Id);
                Assert.Equal(p.Default, effect.GetParameter(i));
                if (p.ValueNames is not null) {
                    Assert.Equal(p.Steps + 1, p.ValueNames.Count);
                }

                // A choice shows as a menu, an Off/On switch as a checkbox, everything else as a slider.
                Assert.Equal(
                    p.ValueNames switch {
                        ["Off", "On"] => InstrumentParameterKind.Checkbox,
                        not null => InstrumentParameterKind.Menu,
                        _ => InstrumentParameterKind.Slider
                    },
                    p.ShownAs
                );

                if (p.Steps > 0) {
                    var step = (p.Max - p.Min) / p.Steps;
                    Assert.Equal(0, (p.Default - p.Min) / step % 1, 4);
                }
            }
        }

        Assert.True(Compressor.Info.HasSidechain);
        Assert.True(NoiseGate.Info.HasSidechain);
        Assert.Equal(2, BuiltinEffects.All.Count(static d => d.HasSidechain));
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Parameters_are_clamped_and_snapped(string id) {
        using var effect = Fx.Create(id);
        var parameters = effect.Descriptor.Parameters;
        for (var i = 0; i < parameters.Count; i++) {
            var p = parameters[i];
            effect.SetParameter(i, p.Max + 1_000);
            Assert.Equal(p.Max, effect.GetParameter(i));
            effect.SetParameter(i, p.Min - 1_000);
            Assert.Equal(p.Min, effect.GetParameter(i));
            effect.SetParameter(i, float.NaN);
            Assert.Equal(p.Min, effect.GetParameter(i));
        }

        effect.SetParameter(-1, 1);
        effect.SetParameter(parameters.Count, 1);
        Assert.Equal(0, effect.GetParameter(parameters.Count));
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Processing_allocates_nothing(string id) {
        using var effect = Fx.Create(id);
        var parameters = effect.Descriptor.Parameters;
        var (music, musicRight) = Fx.Music(64 * 200);
        var left = new float[64];
        var right = new float[64];
        var side = new float[64];
        var allocated = AllocationProbe.Measure(() => {
                for (var block = 0; block < 200; block++) {
                    // Sweep every parameter across its range while processing, sidechain on and off.
                    var p = parameters[block % parameters.Count];
                    effect.SetParameter(p.Index, p.Min + (p.Max - p.Min) * (block % 7) / 6f);
                    music.AsSpan(block * 64, 64).CopyTo(left);
                    musicRight.AsSpan(block * 64, 64).CopyTo(right);
                    music.AsSpan(block * 64, 64).CopyTo(side);
                    effect.Process(left, right, block % 2 == 0 ? side : default, block % 2 == 0 ? side : default);
                    _ = effect.Reading;
                    _ = effect.LatencySamples;
                }

                effect.Reset();
                _ = effect.GetParameter(0);
            }
        );
        Assert.Equal(0, allocated);
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Output_is_finite_under_extreme_settings(string id) {
        using var effect = Fx.Create(id);
        var parameters = effect.Descriptor.Parameters;
        var (music, musicRight) = Fx.Music(24_000);
        foreach (var extreme in new[] { 0f, 1f }) {
            for (var i = 0; i < parameters.Count; i++) {
                var p = parameters[i];
                effect.SetParameter(i, extreme == 0 ? p.Min : p.Max);
            }

            var (l, r) = Fx.Run(effect, [..music.Select(static s => s * 4)], musicRight, sidechainLeft: music);
            Assert.All(l, static s => Assert.True(float.IsFinite(s)));
            Assert.All(r, static s => Assert.True(float.IsFinite(s)));
            Assert.True(float.IsNaN(effect.Reading) || float.IsFinite(effect.Reading));
        }
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Output_does_not_depend_on_the_buffer_size(string id) {
        using var a = Fx.Create(id);
        using var b = Fx.Create(id);
        using var c = Fx.Create(id);
        var (left, right) = Fx.Music(20_000);
        var one = Fx.Run(a, left, right, sidechainLeft: right);
        var two = Fx.Run(b, left, right, 1_000, sidechainLeft: right);
        var three = Fx.Run(c, left, right, blocks: [1, 17, 128, 3, 512, 64], sidechainLeft: right);
        Assert.True(Rms(one.Left) > 1e-3);
        Assert.Equal(-300, NullResidualDb(one.Left, two.Left));
        Assert.Equal(-300, NullResidualDb(one.Right, three.Right));
        Assert.Equal(-300, NullResidualDb(one.Left, three.Left));
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Reset_clears_history(string id) {
        using var effect = Fx.Create(id);
        var (left, right) = Fx.Music(12_000);
        var first = Fx.Run(effect, left, right);
        var other = TestWav.Pluck(220, 48_000, 0.25, 7);
        Fx.Run(effect, other, other, sidechainLeft: other);
        effect.Reset();
        var again = Fx.Run(effect, left, right);
        Assert.Equal(-300, NullResidualDb(first.Left, again.Left));
        Assert.Equal(-300, NullResidualDb(first.Right, again.Right));
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void State_round_trips(string id) {
        using var original = Fx.Create(id);
        var parameters = original.Descriptor.Parameters;
        for (var i = 0; i < parameters.Count; i++) {
            var p = parameters[i];
            var elsewhere = p.Min + (p.Max - p.Min) * (0.3f + 0.1f * (i % 4));
            original.SetParameter(i, p.Default + (elsewhere - p.Default) * 0.25f);
        }

        var state = original.SaveState();
        Assert.NotNull(state);
        using var restored = BuiltinEffects.Create(id)!;
        restored.Prepare(Fx.Rate, 1_024);
        Assert.True(restored.LoadState(state));
        for (var i = 0; i < parameters.Count; i++) {
            Assert.Equal(original.GetParameter(i), restored.GetParameter(i));
        }

        Assert.Equal(state, restored.SaveState());
        Assert.Equal(original.LatencySamples, restored.LatencySamples);
        original.Reset();
        var (left, right) = Fx.Music(9_600);
        var a = Fx.Run(original, left, right);
        var b = Fx.Run(restored, left, right);
        Assert.Equal(-300, NullResidualDb(a.Left, b.Left));

        Assert.False(restored.LoadState([1, 2, 3]));
        Assert.False(restored.LoadState([]));
        var otherId = id == "anode.gain" ? "anode.compressor" : "anode.gain";
        using var other = BuiltinEffects.Create(otherId)!;
        Assert.False(restored.LoadState(other.SaveState()));
        Assert.Equal(state, restored.SaveState());
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Unprepared_effects_pass_audio_through(string id) {
        using var effect = BuiltinEffects.Create(id)!;
        var (left, right) = Fx.Music(256);
        var (l, r) = Fx.Run(effect, left, right);
        Assert.Equal(left, l);
        Assert.Equal(right, r);
        Assert.Equal(0, effect.LatencySamples);
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Latency_is_where_an_impulse_comes_out(string id) {
        // With the wet path off where an effect has one (or a setting that passes a quiet impulse unchanged), the
        // impulse comes out exactly LatencySamples later.
        using var effect = Fx.Create(id);
        foreach (var p in effect.Descriptor.Parameters) {
            if (p.Id == "mix") {
                effect.SetParameter(p.Index, 0);
            }
        }

        if (id == "anode.noise-gate") {
            effect.SetParameter(NoiseGate.Reduction, 0);
        }

        if (id == "anode.tremolo") {
            effect.SetParameter(Effects.Modulation.Tremolo.Depth, 0);
        }

        if (id is "anode.compressor" or "anode.deesser") {
            return; // dry at Mix 0 is trivial; the compressor and de-esser are zero-latency by construction
        }

        var input = Fx.Impulse(4_096, 1_000, 0.25f);
        var (l, r) = Fx.Run(effect, input, input);
        var latency = effect.LatencySamples;
        if (effect.Descriptor.Category is EffectCategories.Distortion
            or EffectCategories.Filter
            or EffectCategories.Modulation
            or EffectCategories.Reverb
            or EffectCategories.Delay
            or EffectCategories.Specialized
            or EffectCategories.MultiEffects) {
            // Mix 0 or depth: the dry signal, untouched.
            Assert.Equal(0, latency);
            Assert.Equal(-300, NullResidualDb(input, l));
            return;
        }

        var peak = 0;
        for (var i = 1; i < l.Length; i++) {
            if (Math.Abs(l[i]) > Math.Abs(l[peak])) {
                peak = i;
            }
        }

        Assert.Equal(1_000 + latency, peak);
        Assert.Equal(0.25, l[peak], 3);
        Assert.Equal(0.25, r[peak], 3);
    }
}
