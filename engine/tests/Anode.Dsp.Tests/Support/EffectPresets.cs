using Anode.Dsp.Effects.Delay;
using Anode.Dsp.Effects.Drive;
using Anode.Dsp.Effects.Dynamics;
using Anode.Dsp.Effects.Eq;
using Anode.Dsp.Effects.Filter;
using Anode.Dsp.Effects.Modulation;
using Anode.Dsp.Effects.Space;
using Anode.Dsp.Effects.Utility;

namespace Anode.Dsp.Tests.Support;

/// <summary>A setting per effect that makes it clearly audible, for the golden renders, the demos and the CPU table.</summary>
public static class EffectPresets {
    public static (int, float)[] For(string id) =>
        id switch {
            ChannelEq.Id => [
                (ChannelEq.HighPassOn, 1), (ChannelEq.HighPassFrequency, 60), (ChannelEq.LowShelfGain, 3),
                (ChannelEq.Peak2Gain, -4), (ChannelEq.Peak2Q, 2), (ChannelEq.Peak3Gain, 3),
                (ChannelEq.HighShelfGain, 4), (ChannelEq.LowPassOn, 1), (ChannelEq.LowPassFrequency, 16_000)
            ],
            Compressor.Id => [
                (Compressor.Threshold, -30), (Compressor.Ratio, 6), (Compressor.Attack, 5), (Compressor.Release, 80),
                (Compressor.Makeup, 4)
            ],
            Limiter.Id => [(Limiter.InputGain, 9)],
            AdaptiveLimiter.Id => [(AdaptiveLimiter.InputGain, 9)],
            NoiseGate.Id => [(NoiseGate.Threshold, -14), (NoiseGate.Release, 40)],
            DeEsser.Id => [(DeEsser.Threshold, -40), (DeEsser.Frequency, 4_000)],
            Gain.Id => [(Gain.Level, -3), (Gain.Balance, 30), (Gain.InvertRight, 1)],
            StereoDelay.Id => [(StereoDelay.Mix, 40), (StereoDelay.Crossfeed, 30), (StereoDelay.HighCut, 6_000)],
            TapeDelay.Id => [(TapeDelay.Mix, 40), (TapeDelay.Time, 180), (TapeDelay.Feedback, 55)],
            Reverb.Id => [(Reverb.Mix, 40)],
            Chorus.Id => [(Chorus.Depth, 60)],
            Flanger.Id => [(Flanger.Rate, 1)],
            Phaser.Id => [(Phaser.Rate, 1.5f), (Phaser.Stages, 2)],
            Tremolo.Id => [(Tremolo.Rate, 6), (Tremolo.Depth, 80), (Tremolo.Phase, 90)],
            Overdrive.Id => [(Overdrive.Drive, 20)],
            Distortion.Id => [(Distortion.Type, Distortion.Fuzz)],
            Bitcrusher.Id => [(Bitcrusher.Bits, 6), (Bitcrusher.Downsample, 6)],
            AutoFilter.Id => [(AutoFilter.Resonance, 60), (AutoFilter.LfoRate, 3), (AutoFilter.EnvelopeAmount, 30)],
            _ => []
        };
}
