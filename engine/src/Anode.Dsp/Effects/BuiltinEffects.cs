using Anode.Dsp.Effects.Delay;
using Anode.Dsp.Effects.Drive;
using Anode.Dsp.Effects.Dynamics;
using Anode.Dsp.Effects.Eq;
using Anode.Dsp.Effects.Filter;
using Anode.Dsp.Effects.Metering;
using Anode.Dsp.Effects.Modulation;
using Anode.Dsp.Effects.Multi;
using Anode.Dsp.Effects.Pitch;
using Anode.Dsp.Effects.Space;
using Anode.Dsp.Effects.Specialized;
using Anode.Dsp.Effects.Surround;
using Anode.Dsp.Effects.Utility;
using Anode.Dsp.Mastering;

namespace Anode.Dsp.Effects;

/// <summary>
///     Anode's own effects: the T1 suite of the plan's §7 (docs/05-phase-4.md, workstream D), then the T2 effects
///     (docs/07-phase-6.md, workstream D), and while <see cref="T3.Ready" /> the T3 ones (docs/12-phase-11.md, workstream
///     E): saturation and the Mastering Assistant. The engine lists <see cref="All" /> in its plugin catalogue.
/// </summary>
public static class BuiltinEffects {
    /// <summary>The T3 effects that are <see cref="IAudioEffect" />s (registered while <see cref="T3.Ready" />).</summary>
    public static IReadOnlyList<EffectDescriptor> Tier3 { get; } = [Saturation.Info, MasteringAssistant.Info];

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
        MultiMeter.Info,
        ConvolutionReverb.Info,
        LinearPhaseEq.Info,
        Multipressor.Info,
        Enveloper.Info,
        Exciter.Info,
        SubBass.Info,
        DelayDesigner.Info,
        PitchShifter.Info,
        PitchCorrection.Info,
        Ensemble.Info,
        RotorCabinet.Info,
        PhatFx.Info,
        StepFx.Info,
        BeatBreaker.Info,
        RemixFx.Info,
        ..T3.Ready ? Tier3 : []
    ];

    /// <summary>
    ///     The files effect <paramref name="id" /> loads (<see cref="IFileEffect.FileTypes" />: an impulse response for
    ///     the convolution reverb), empty when it loads none: what the catalogue lists, as for instruments.
    /// </summary>
    public static IReadOnlyList<string> FileTypes(string id) => id == ConvolutionReverb.Id ? [".wav"] : [];

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
            ConvolutionReverb.Id => new ConvolutionReverb(),
            LinearPhaseEq.Id => new LinearPhaseEq(),
            Multipressor.Id => new Multipressor(),
            Enveloper.Id => new Enveloper(),
            Exciter.Id => new Exciter(),
            SubBass.Id => new SubBass(),
            DelayDesigner.Id => new DelayDesigner(),
            PitchShifter.Id => new PitchShifter(),
            PitchCorrection.Id => new PitchCorrection(),
            Ensemble.Id => new Ensemble(),
            RotorCabinet.Id => new RotorCabinet(),
            PhatFx.Id => new PhatFx(),
            StepFx.Id => new StepFx(),
            BeatBreaker.Id => new BeatBreaker(),
            RemixFx.Id => new RemixFx(),
            Saturation.Id when T3.Ready => new Saturation(),
            MasteringAssistant.Id when T3.Ready => new MasteringAssistant(),
            _ => null
        };
}

/// <summary>
///     Anode's inserts that change their strip's channel format (<see cref="IFormatEffect" />; docs/12-phase-11.md): the
///     Down Mixer (<c>anode.down-mixer</c>) and Binaural Post-Processing (<c>anode.binaural</c>), while
///     <see cref="T3.Ready" />. The engine lists <see cref="All" /> in its plugin catalogue and creates them by id.
/// </summary>
public static class BuiltinFormatEffects {
    /// <summary>The T3 format effects (registered while <see cref="T3.Ready" />).</summary>
    public static IReadOnlyList<EffectDescriptor> Tier3 { get; } = [DownMixerInsert.Info, BinauralInsert.Info];

    public static IReadOnlyList<EffectDescriptor> All { get; } = T3.Ready ? Tier3 : [];

    /// <summary>A new, unprepared instance of format effect <paramref name="id" />, or null when there is none.</summary>
    public static IFormatEffect? Create(string id) =>
        id switch {
            DownMixerInsert.Id when T3.Ready => new DownMixerInsert(),
            BinauralInsert.Id when T3.Ready => new BinauralInsert(),
            _ => null
        };
}
