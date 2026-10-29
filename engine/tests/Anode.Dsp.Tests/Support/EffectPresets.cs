using Anode.Dsp.Effects;
using Anode.Dsp.Effects.Delay;
using Anode.Dsp.Effects.Drive;
using Anode.Dsp.Effects.Dynamics;
using Anode.Dsp.Effects.Eq;
using Anode.Dsp.Effects.Filter;
using Anode.Dsp.Effects.Modulation;
using Anode.Dsp.Effects.Multi;
using Anode.Dsp.Effects.Pitch;
using Anode.Dsp.Effects.Space;
using Anode.Dsp.Effects.Specialized;
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
            ConvolutionReverb.Id => [(ConvolutionReverb.Mix, 40), (ConvolutionReverb.Predelay, 20)],
            LinearPhaseEq.Id => [
                (LinearPhaseEq.HighPassOn, 1), (LinearPhaseEq.HighPassFrequency, 60), (LinearPhaseEq.LowShelfGain, 3),
                (LinearPhaseEq.Peak2Gain, -4), (LinearPhaseEq.Peak2Q, 2), (LinearPhaseEq.Peak3Gain, 3),
                (LinearPhaseEq.HighShelfGain, 4)
            ],
            Multipressor.Id => [
                (Multipressor.Bands[0].Threshold, -30), (Multipressor.Bands[0].Ratio, 4),
                (Multipressor.Bands[1].Threshold, -30), (Multipressor.Bands[1].Ratio, 3),
                (Multipressor.Bands[2].Threshold, -35), (Multipressor.Bands[2].Ratio, 3),
                (Multipressor.Bands[3].Threshold, -35), (Multipressor.Bands[3].Ratio, 2),
                (Multipressor.Bands[3].Gain, 4), (Multipressor.Bands[0].Gain, 3)
            ],
            Enveloper.Id => [(Enveloper.AttackGain, 80), (Enveloper.ReleaseGain, -50), (Enveloper.Lookahead, 2)],
            Exciter.Id => [(Exciter.Mix, 60), (Exciter.Frequency, 3_000)],
            SubBass.Id => [(SubBass.Mix, 80), (SubBass.Frequency, 110)],
            DelayDesigner.Id => [(DelayDesigner.TapCount, 6), (DelayDesigner.Feedback, 30), (DelayDesigner.Mix, 40)],
            PitchShifter.Id => [(PitchShifter.Semitones, 7), (PitchShifter.Mix, 50)],
            PitchCorrection.Id => [(PitchCorrection.Scale, 1), (PitchCorrection.Response, 5)],
            Ensemble.Id => [(Ensemble.Voices, 6), (Ensemble.Depth, 60)],
            RotorCabinet.Id => [(RotorCabinet.Speed, 2)],
            PhatFx.Id => [(PhatFx.LfoDepth, 40), (PhatFx.LfoNote, NoteValues.Eighth), (PhatFx.Resonance, 50)],
            StepFx.Id => [(StepFx.PanOn, 1)],
            BeatBreaker.Id => [
                (BeatBreaker.Length, 0), (BeatBreaker.SliceParameterList[1].Source, 1),
                (BeatBreaker.SliceParameterList[3].Reverse, 1), (BeatBreaker.SliceParameterList[6].Source, 5),
                (BeatBreaker.SliceParameterList[9].Mute, 1), (BeatBreaker.SliceParameterList[14].Source, 13)
            ],
            RemixFx.Id => [
                (RemixFx.Pad1Active, 1), (RemixFx.Pad1Effect, RemixFx.Wobble), (RemixFx.Pad1X, 0.6f),
                (RemixFx.Pad1Y, 0.7f), (RemixFx.Pad2Active, 1), (RemixFx.Pad2Effect, RemixFx.Gater),
                (RemixFx.Pad2X, 0.6f), (RemixFx.Pad2Y, 0.8f)
            ],
            _ => []
        };
}
