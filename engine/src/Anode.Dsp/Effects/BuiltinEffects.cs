using Anode.Dsp.Effects.Delay;
using Anode.Dsp.Effects.Drive;
using Anode.Dsp.Effects.Dynamics;
using Anode.Dsp.Effects.Eq;
using Anode.Dsp.Effects.Filter;
using Anode.Dsp.Effects.Metering;
using Anode.Dsp.Effects.Modulation;
using Anode.Dsp.Effects.Space;
using Anode.Dsp.Effects.Utility;

namespace Anode.Dsp.Effects;

/// <summary>
///     Anode's own effects (docs/05-phase-4.md, workstream D: the T1 suite of the plan's §7). The engine lists
///     <see cref="All" /> in its plugin catalogue.
/// </summary>
public static class BuiltinEffects {
    public static IReadOnlyList<EffectDescriptor> All { get; } = [
        ChannelEq.Info,
        Compressor.Info,
        Limiter.Info,
        AdaptiveLimiter.Info,
        NoiseGate.Info,
        DeEsser.Info,
        Gain.Info,
        StereoDelay.Info,
        TapeDelay.Info,
        Reverb.Info,
        Chorus.Info,
        Flanger.Info,
        Phaser.Info,
        Tremolo.Info,
        Overdrive.Info,
        Distortion.Info,
        Bitcrusher.Info,
        AutoFilter.Info,
        LevelMeter.Info,
        LoudnessMeterEffect.Info,
        MultiMeter.Info
    ];

    /// <summary>A new, unprepared instance of effect <paramref name="id" />, or null when there is none.</summary>
    public static IAudioEffect? Create(string id) =>
        id switch {
            ChannelEq.Id => new ChannelEq(),
            Compressor.Id => new Compressor(),
            Limiter.Id => new Limiter(),
            AdaptiveLimiter.Id => new AdaptiveLimiter(),
            NoiseGate.Id => new NoiseGate(),
            DeEsser.Id => new DeEsser(),
            Gain.Id => new Gain(),
            StereoDelay.Id => new StereoDelay(),
            TapeDelay.Id => new TapeDelay(),
            Reverb.Id => new Reverb(),
            Chorus.Id => new Chorus(),
            Flanger.Id => new Flanger(),
            Phaser.Id => new Phaser(),
            Tremolo.Id => new Tremolo(),
            Overdrive.Id => new Overdrive(),
            Distortion.Id => new Distortion(),
            Bitcrusher.Id => new Bitcrusher(),
            AutoFilter.Id => new AutoFilter(),
            LevelMeter.Id => new LevelMeter(),
            LoudnessMeterEffect.Id => new LoudnessMeterEffect(),
            MultiMeter.Id => new MultiMeter(),
            _ => null
        };
}
