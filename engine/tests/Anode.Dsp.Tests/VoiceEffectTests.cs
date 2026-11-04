using Anode.Dsp.Effects;
using Anode.Dsp.Effects.Metering;
using Anode.Dsp.Effects.Pitch;
using Anode.Dsp.Effects.Specialized;
using Anode.Dsp.Tests.Support;
using static Anode.Dsp.Tests.Support.Analysis;

namespace Anode.Dsp.Tests;

/// <summary>The Vocoder and the Voice Shaper of docs/effects/parity.md, measured against known signals.</summary>
public sealed class VoiceEffectTests {
    const int Rate = (int)Fx.Rate;

    static float[] White(int frames, float amplitude, uint seed = 0x9E3779B9) {
        var x = new float[frames];
        var state = seed;
        for (var i = 0; i < frames; i++) {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            x[i] = amplitude * (state * (2f / uint.MaxValue) - 1);
        }

        return x;
    }

    /// <summary>
    ///     A source and a filter, by addition: the harmonics of <paramref name="f0" /> falling 3 dB an octave, in phase
    ///     as a glottal pulse's are, under a resonance at <paramref name="formant" />. So the fundamental stays the
    ///     strongest periodicity and the envelope's peak is exactly where it was put.
    /// </summary>
    static float[] Voice(double f0, double formant, double seconds, float peak = 0.5f) {
        var x = new float[(int)(seconds * Rate)];
        for (var k = 1; k * f0 < 7_000; k++) {
            var distance = (k * f0 - formant) / (0.15 * formant);
            var amplitude = (1 + 6 / (1 + distance * distance)) / Math.Sqrt(k);
            var step = 2 * Math.PI * k * f0 / Rate;
            for (var i = 0; i < x.Length; i++) {
                x[i] += (float)(amplitude * Math.Cos(step * i));
            }
        }

        var scale = peak / (float)Peak(x);
        for (var i = 0; i < x.Length; i++) {
            x[i] *= scale;
        }

        return x;
    }

    /// <summary>The pitch of a signal, as the Tuner hears it.</summary>
    static double PitchOf(float[] signal) {
        using var tuner = (Tuner)Fx.Create(Tuner.Id);
        Fx.Run(tuner, signal);
        return tuner.PitchHz;
    }

    /// <summary>Where a spectrum's energy sits: the centroid of its partials within 6 dB of the strongest.</summary>
    static double EnvelopePeak(ReadOnlySpan<float> signal, int start) {
        const int length = 16_384;
        var total = new double[length / 2];
        for (var at = start; at + length <= signal.Length; at += length / 2) {
            var spectrum = Spectrum(signal, at, length);
            for (var k = 0; k < length / 2; k++) {
                total[k] += spectrum[k].Magnitude * spectrum[k].Magnitude;
            }
        }

        var first = (int)(200.0 * length / Rate);
        var strongest = total.AsSpan(first).ToArray().Max();
        double sum = 0, weighted = 0;
        for (var k = first; k < total.Length; k++) {
            if (total[k] >= strongest / 4) {
                sum += total[k];
                weighted += total[k] * k * Rate / length;
            }
        }

        return weighted / sum;
    }

    // ---- Vocoder -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Vocoder_passes_a_tone_through_its_band_at_the_product_of_the_levels() {
        using var effect = Fx.Create(
            Vocoder.Id,
            (Vocoder.Source, Vocoder.SidechainCarrier),
            (Vocoder.Sibilance, 0),
            (Vocoder.Bands, 16)
        );
        const double hz = 1_000;
        var voice = TestWav.Sine(hz, Rate, 1);
        var carrier = TestWav.Sine(hz, Rate, 1);
        var (left, right) = Fx.Run(effect, voice, voice, sidechainLeft: carrier);
        Assert.Equal(-300, NullResidualDb(left, right));
        var measured = Rms(left.AsSpan(Rate / 2)) * Math.Sqrt(2);
        // A·C = 0.25, within what a tone between band centres loses and neighbouring bands add back.
        Assert.InRange(ToDb(measured / 0.25), -4, 3);
    }

    [Theory]
    [InlineData(300)]
    [InlineData(1_000)]
    [InlineData(3_000)]
    public void Vocoder_gives_white_noise_the_spectrum_of_the_voice(double hz) {
        using var effect = Fx.Create(
            Vocoder.Id,
            (Vocoder.Source, Vocoder.SidechainCarrier),
            (Vocoder.Sibilance, 0)
        );
        var voice = TestWav.Sine(hz, Rate, 1);
        var (left, _) = Fx.Run(effect, voice, voice, sidechainLeft: White(Rate, 0.5f));
        var peak = EnvelopePeak(left, Rate / 2);
        Assert.Equal(0, Cents(peak, hz), 200.0);
        var near = BandLevelDb(left, hz);
        Assert.True(BandLevelDb(left, hz * 4) < near - 25, $"{BandLevelDb(left, hz * 4):0.0} against {near:0.0}");
        Assert.True(BandLevelDb(left, hz / 3) < near - 25, $"{BandLevelDb(left, hz / 3):0.0} against {near:0.0}");
    }

    /// <summary>The RMS level of a third of an octave around <paramref name="hz" />.</summary>
    static double BandLevelDb(float[] signal, double hz) {
        const int length = 16_384;
        double sum = 0;
        for (var at = Rate / 2; at + length <= signal.Length; at += length / 2) {
            var spectrum = Spectrum(signal, at, length);
            var low = (int)(hz / Math.Pow(2, 1.0 / 6) * length / Rate);
            var high = (int)(hz * Math.Pow(2, 1.0 / 6) * length / Rate);
            for (var k = low; k <= high; k++) {
                sum += spectrum[k].Magnitude * spectrum[k].Magnitude;
            }
        }

        return ToDb(Math.Sqrt(sum));
    }

    [Fact]
    public void Vocoder_is_silent_without_a_voice_and_follows_one() {
        using var effect = Fx.Create(Vocoder.Id, (Vocoder.Source, Vocoder.Synth));
        var voice = TestWav.Sine(500, Rate, 1);
        for (var i = Rate / 2; i < voice.Length; i++) {
            voice[i] = 0;
        }

        var (left, _) = Fx.Run(effect, voice);
        Assert.True(Rms(left.AsSpan(Rate / 4, Rate / 8)) > 0.01, "it speaks while the voice does");
        Assert.True(ToDb(Peak(left.AsSpan(Rate * 9 / 10))) < -90, $"{ToDb(Peak(left.AsSpan(Rate * 9 / 10))):0.0} dB");
    }

    [Fact]
    public void Vocoder_synth_carrier_is_at_its_note() {
        // A voice at 430 Hz opens the band around it, where the sawtooth's second harmonic is: 440 Hz comes out.
        using var effect = Fx.Create(
            Vocoder.Id,
            (Vocoder.Source, Vocoder.Synth),
            (Vocoder.Note, 57),
            (Vocoder.Sibilance, 0)
        );
        var (left, _) = Fx.Run(effect, TestWav.Sine(430, Rate, 2));
        Assert.Equal(0, Cents(PeakFrequency(left, Rate, Rate / 4), 440), 1.0);
    }

    [Fact]
    public void Vocoder_modulates_the_input_with_the_sidechain_when_asked() {
        using var effect = Fx.Create(
            Vocoder.Id,
            (Vocoder.Source, Vocoder.SidechainVoice),
            (Vocoder.Sibilance, 0)
        );
        var noise = White(Rate, 0.5f);
        var voice = TestWav.Sine(1_000, Rate, 1);
        var (left, _) = Fx.Run(effect, noise, noise, sidechainLeft: voice);
        Assert.Equal(0, Cents(EnvelopePeak(left, Rate / 2), 1_000), 200.0);
    }

    [Theory]
    [InlineData(Vocoder.SidechainCarrier)]
    [InlineData(Vocoder.SidechainVoice)]
    public void Vocoder_leaves_the_audio_alone_until_a_sidechain_is_connected(int source) {
        using var effect = Fx.Create(Vocoder.Id, (Vocoder.Source, source));
        var (music, right) = Fx.Music(Rate / 2);
        var (l, r) = Fx.Run(effect, music, right);
        Assert.Equal(-300, NullResidualDb(music, l));
        Assert.Equal(-300, NullResidualDb(right, r));
    }

    [Fact]
    public void Vocoder_formant_moves_the_carrier_bank_against_the_voice() {
        using var effect = Fx.Create(
            Vocoder.Id,
            (Vocoder.Source, Vocoder.SidechainCarrier),
            (Vocoder.Sibilance, 0),
            (Vocoder.Formant, 12)
        );
        var voice = TestWav.Sine(800, Rate, 1);
        var (left, _) = Fx.Run(effect, voice, voice, sidechainLeft: White(Rate, 0.5f));
        Assert.Equal(0, Cents(EnvelopePeak(left, Rate / 2), 1_600), 200.0);
    }

    [Fact]
    public void Vocoder_sibilance_adds_the_voice_above_the_bank() {
        var hiss = TestWav.Sine(12_000, Rate, 0.5, 0.25f);
        using var quiet = Fx.Create(Vocoder.Id, (Vocoder.Sibilance, 0));
        using var open = Fx.Create(Vocoder.Id, (Vocoder.Sibilance, 100));
        Assert.True(Rms(Fx.Run(quiet, hiss).Left.AsSpan(Rate / 4)) < 0.005);
        Assert.Equal(0.25 * Math.Sqrt(0.5), Rms(Fx.Run(open, hiss).Left.AsSpan(Rate / 4)), 0.02);
    }

    [Fact]
    public void Vocoder_display_lights_the_band_a_tone_is_in() {
        using var effect = Fx.Create(Vocoder.Id, (Vocoder.Bands, 8), (Vocoder.Low, 100), (Vocoder.High, 6_400));
        Fx.Run(effect, TestWav.Sine(800, Rate, 0.5));
        var display = (IEffectDisplay)effect;
        var values = new float[display.DisplaySize];
        Assert.Equal(8, display.ReadDisplay(values));
        // Eight bands over six octaves: 800 Hz is the band at 100 × 2^(6 · b/7), between its fourth and fifth.
        var loudest = Array.IndexOf(values, values[..8].Max());
        Assert.InRange(loudest, 3, 4);
        Assert.InRange(values[loudest], -12, -5); // −6 dBFS at a band's centre; this tone is between two
        Assert.True(values[0] < values[loudest] - 30);
    }

    // ---- Voice Shaper ------------------------------------------------------------------------------------------------

    [Fact]
    public void Voice_shaper_at_rest_is_a_delay() {
        using var effect = Fx.Create(VoiceShaper.Id);
        var voice = Voice(160, 900, 1);
        var (left, _) = Fx.Run(effect, voice);
        var latency = effect.LatencySamples;
        var delayed = voice.AsSpan(Rate / 4, Rate / 2);
        var output = left.AsSpan(Rate / 4 + latency, Rate / 2);
        var residual = NullResidualDb(delayed, output) - ToDb(Rms(delayed));
        Assert.True(residual < -50, $"{residual:0.0} dB");
    }

    [Theory]
    [InlineData(150, 7)]
    [InlineData(150, 12)]
    [InlineData(220, -5)]
    [InlineData(330, -12)]
    [InlineData(200, 3.5)]
    public void Voice_shaper_moves_the_pitch_and_leaves_the_formant(double f0, float semitones) {
        using var effect = Fx.Create(VoiceShaper.Id, (VoiceShaper.Semitones, semitones));
        var voice = Voice(f0, 1_000, 2);
        var (left, _) = Fx.Run(effect, voice);
        var settled = left.AsSpan(Rate).ToArray();
        Assert.Equal(semitones * 100, Cents(PitchOf(settled), f0), 8.0);
        Assert.Equal(0, Cents(EnvelopePeak(left, Rate), 1_000), 200.0);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(12)]
    [InlineData(-6)]
    [InlineData(-12)]
    public void Voice_shaper_moves_the_formant_and_leaves_the_pitch(float semitones) {
        const double f0 = 150;
        const double formant = 1_200;
        using var effect = Fx.Create(VoiceShaper.Id, (VoiceShaper.Formant, semitones));
        var (left, _) = Fx.Run(effect, Voice(f0, formant, 2));
        var settled = left.AsSpan(Rate).ToArray();
        Assert.Equal(0, Cents(PitchOf(settled), f0), 8.0);
        Assert.Equal(semitones * 100, Cents(EnvelopePeak(left, Rate), formant), 220.0);
    }

    [Fact]
    public void Voice_shaper_moves_both_together() {
        using var effect = Fx.Create(VoiceShaper.Id, (VoiceShaper.Semitones, 7), (VoiceShaper.Formant, 5));
        var (left, _) = Fx.Run(effect, Voice(140, 1_000, 2));
        Assert.Equal(700, Cents(PitchOf([..left.AsSpan(Rate)]), 140), 8.0);
        Assert.Equal(500, Cents(EnvelopePeak(left, Rate), 1_000), 220.0);
    }

    [Fact]
    public void Voice_shaper_keeps_the_level_of_the_voice() {
        using var effect = Fx.Create(VoiceShaper.Id, (VoiceShaper.Semitones, 7), (VoiceShaper.Formant, -4));
        var voice = Voice(180, 900, 2);
        var (left, _) = Fx.Run(effect, voice);
        Assert.Equal(0, ToDb(Rms(left.AsSpan(Rate)) / Rms(voice.AsSpan(Rate))), 1.0);
    }

    [Theory]
    [InlineData(220, 330, 0)]
    [InlineData(220, 165, 0)]
    [InlineData(196, 261.63, 12)]
    public void Voice_shaper_sings_at_the_pitch_of_the_sidechain(double f0, double side, float semitones) {
        using var effect = Fx.Create(
            VoiceShaper.Id,
            (VoiceShaper.Target, VoiceShaper.FollowSidechain),
            (VoiceShaper.Semitones, semitones)
        );
        var (left, _) = Fx.Run(effect, Voice(f0, 900, 2), sidechainLeft: TestWav.Sine(side, Rate, 2, 0.4f));
        Assert.Equal(0, Cents(PitchOf([..left.AsSpan(Rate)]), side * Math.Pow(2, semitones / 12)), 8.0);
        var display = (IEffectDisplay)effect;
        var values = new float[display.DisplaySize];
        Assert.Equal(3, display.ReadDisplay(values));
        Assert.Equal(0, Cents(values[0], f0), 10.0);
        Assert.Equal(0, Cents(values[2], side), 10.0);
        Assert.Equal(semitones * 100 + 1_200 * Math.Log2(side / f0), values[1], 10.0);
    }

    [Fact]
    public void Voice_shaper_without_a_sidechain_holds_the_last_interval() {
        using var effect = Fx.Create(VoiceShaper.Id, (VoiceShaper.Target, VoiceShaper.FollowSidechain));
        var voice = Voice(220, 900, 2);
        var side = TestWav.Sine(330, Rate, 2, 0.4f);
        for (var i = Rate; i < side.Length; i++) {
            side[i] = 0;
        }

        var (left, _) = Fx.Run(effect, voice, sidechainLeft: side);
        Assert.Equal(0, Cents(PitchOf([..left.AsSpan(Rate + 4_800)]), 330), 10.0);
    }

    [Fact]
    public void Voice_shaper_reading_is_the_pitch_it_hears() {
        using var effect = Fx.Create(VoiceShaper.Id);
        Fx.Run(effect, Voice(160, 900, 0.5));
        Assert.Equal(0, Cents(effect.Reading, 160), 10.0);
        Fx.Run(effect, new float[Rate / 2]);
        Assert.Equal(0, effect.Reading);
    }
}
