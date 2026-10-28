using Anode.Dsp.Dynamics;
using Anode.Dsp.Effects;
using Anode.Dsp.Effects.Dynamics;
using Anode.Dsp.Tests.Support;
using static Anode.Dsp.Tests.Support.Analysis;

namespace Anode.Dsp.Tests;

/// <summary>The compressor's static curve and timing, the limiters' ceilings, the gate and the de-esser.</summary>
public sealed class DynamicsTests {
    /// <summary>The soft-knee curve of Giannoulis et al. (2012), eq. 4, written out again: the output level in dB.</summary>
    static double Curve(double x, double threshold, double ratio, double knee) {
        if (2 * (x - threshold) < -knee) {
            return x;
        }

        if (2 * Math.Abs(x - threshold) <= knee && knee > 0) {
            return x + (1 / ratio - 1) * Math.Pow(x - threshold + knee / 2, 2) / (2 * knee);
        }

        return threshold + (x - threshold) / ratio;
    }

    /// <summary>The settled output level (dB) of a constant input at <paramref name="levelDb" />.</summary>
    static double Settled(IAudioEffect effect, double levelDb, int frames = 24_000) {
        effect.Reset();
        var input = TestWav.Constant((float)Math.Pow(10, levelDb / 20), frames);
        var (left, _) = Fx.Run(effect, input);
        return ToDb(Math.Abs(left[^1]));
    }

    [Theory]
    [InlineData(-20, 4, 6)]
    [InlineData(-30, 2, 0)]
    [InlineData(-12, 30, 12)]
    [InlineData(-40, 1.5, 24)]
    public void Compressor_static_curve_is_the_gain_computer(float threshold, float ratio, float knee) {
        using var compressor = Fx.Create(
            Compressor.Id,
            (Compressor.Threshold, threshold),
            (Compressor.Ratio, ratio),
            (Compressor.Knee, knee),
            (Compressor.Attack, 1),
            (Compressor.Release, 20)
        );
        for (var level = -60.0; level <= 0; level += 2) {
            Assert.Equal(Curve(level, threshold, ratio, knee), Settled(compressor, level), 0.02);
        }
    }

    [Fact]
    public void Compressor_attack_and_release_are_time_constants() {
        const float attackMs = 10;
        const float releaseMs = 100;
        using var compressor = Fx.Create(
            Compressor.Id,
            (Compressor.Threshold, -20),
            (Compressor.Ratio, 4),
            (Compressor.Knee, 0),
            (Compressor.Attack, attackMs),
            (Compressor.Release, releaseMs)
        );
        // −40 dB, a step to 0 dB (15 dB of reduction once settled) for half a second, then back to −40 dB.
        const int step = 4_800;
        const int fall = step + 24_000;
        var input = new float[fall + 48_000];
        for (var i = 0; i < input.Length; i++) {
            input[i] = i is >= step and < fall ? 1f : 0.01f;
        }

        var (output, _) = Fx.Run(compressor, input);
        var gain = output.Select((s, i) => ToDb(s / input[i])).ToArray();
        Assert.Equal(-15, gain[fall - 1], 0.01);
        Assert.Equal(0, gain[step - 1], 1e-4);
        // A time constant: 1 − 1/e of the way after τ.
        var attackAt = Array.FindIndex(gain, step, static g => g <= -15 * (1 - 1 / Math.E)) - step;
        Assert.Equal(attackMs * 48, attackAt, 2);
        var releaseAt = Array.FindIndex(gain, fall, static g => g >= -15 / Math.E) - fall;
        Assert.Equal(releaseMs * 48, releaseAt, 10);
        Assert.Equal(0, gain[^1], 0.01);
    }

    [Fact]
    public void Compressor_reads_its_gain_reduction_and_auto_gain_makes_up_half() {
        using var compressor = Fx.Create(
            Compressor.Id,
            (Compressor.Threshold, -24),
            (Compressor.Ratio, 4),
            (Compressor.Knee, 0),
            (Compressor.AutoGain, 1),
            (Compressor.Makeup, 2)
        );
        var level = Settled(compressor, 0, 48_000);
        // 0 dBFS gets 18 dB of reduction; auto gain adds back 9, make-up 2.
        Assert.Equal(-18 + 9 + 2, level, 0.02);
        Assert.Equal(-18, compressor.Reading, 0.02);
    }

    [Fact]
    public void Compressor_listens_to_the_sidechain_when_asked() {
        var quiet = TestWav.Constant(0.05f, 24_000);
        var loud = TestWav.Constant(1f, 24_000);
        using var compressor = Fx.Create(
            Compressor.Id,
            (Compressor.Threshold, -20),
            (Compressor.Ratio, 4),
            (Compressor.Knee, 0),
            (Compressor.Attack, 1)
        );
        // Internal: the quiet input passes; the sidechain is ignored.
        Assert.Equal(0.05f, Fx.Run(compressor, quiet, sidechainLeft: loud).Left[^1], 4);
        compressor.SetParameter(Compressor.Sidechain, 1);
        compressor.Reset();
        Assert.Equal(0.05 * Math.Pow(10, -15 / 20.0), Fx.Run(compressor, quiet, sidechainLeft: loud).Left[^1], 4);
        // External without a sidechain falls back to the input.
        compressor.Reset();
        Assert.Equal(0.05f, Fx.Run(compressor, quiet).Left[^1], 4);
    }

    [Fact]
    public void Compressor_sidechain_filter_shapes_what_the_detector_hears() {
        var bass = TestWav.Sine(60, (int)Fx.Rate, 1, 0.9f);
        using var plain = Fx.Create(Compressor.Id, (Compressor.Threshold, -30), (Compressor.Ratio, 8));
        using var filtered = Fx.Create(
            Compressor.Id,
            (Compressor.Threshold, -30),
            (Compressor.Ratio, 8),
            (Compressor.SidechainMode, 1),
            (Compressor.SidechainFrequency, 1_000)
        );
        Fx.Run(plain, bass);
        Fx.Run(filtered, bass);
        Assert.True(plain.Reading < -15, $"{plain.Reading}");
        Assert.True(filtered.Reading > -3, $"{filtered.Reading}");
    }

    [Fact]
    public void Compressor_characters_differ_as_described() {
        var tone = TestWav.Sine(1_000, (int)Fx.Rate, 1, 0.9f);

        double Distortion(int character) {
            using var c = Fx.Create(Compressor.Id, (Compressor.Character, character), (Compressor.Threshold, -30));
            var (output, _) = Fx.Run(c, tone);
            var spectrum = Spectrum(output, 16_384, 16_384);
            var bin = (int)Math.Round(1_000 * 16_384 / Fx.Rate);
            return spectrum[3 * bin].Magnitude / spectrum[bin].Magnitude;
        }

        Assert.True(Distortion(Compressor.Fet) > 10 * Distortion(Compressor.Vca));

        // The opto lets go more slowly after deep reduction than the VCA at the same release.
        double Recovery(int character) {
            using var c = Fx.Create(
                Compressor.Id,
                (Compressor.Character, character),
                (Compressor.Threshold, -40),
                (Compressor.Ratio, 10),
                (Compressor.Release, 100)
            );
            var input = new float[48_000];
            for (var i = 0; i < input.Length; i++) {
                input[i] = i < 24_000 ? tone[i] : 0.001f * MathF.Sign(tone[i]);
            }

            var (output, _) = Fx.Run(c, input);
            return ToDb(Peak(output.AsSpan(24_000 + 4_800, 480)) / 0.001);
        }

        Assert.True(Recovery(Compressor.Opto) < Recovery(Compressor.Vca) - 3);
    }

    public static TheoryData<string, bool> Limiters =>
        new() {
            { Limiter.Id, true }, { Limiter.Id, false }, { AdaptiveLimiter.Id, true }, { AdaptiveLimiter.Id, false }
        };

    [Theory]
    [MemberData(nameof(Limiters))]
    public void Limiters_never_pass_the_ceiling(string id, bool truePeak) {
        const float ceiling = -1;
        var adaptive = id == AdaptiveLimiter.Id;
        using var limiter = Fx.Create(
            id,
            (adaptive ? AdaptiveLimiter.Ceiling : Limiter.Ceiling, ceiling),
            (adaptive ? AdaptiveLimiter.InputGain : Limiter.InputGain, 12),
            (adaptive ? AdaptiveLimiter.Mode : Limiter.TruePeakDetection, truePeak ? 1 : 0)
        );
        // Program material as a mix arrives at a limiter: band-limited to 20 kHz (the test noise is full-band, and the
        // detector's interpolator, like a BS.1770 meter's, is specified to 20 kHz at 48 kHz).
        using var band = Fx.Create(
            Effects.Eq.ChannelEq.Id,
            (Effects.Eq.ChannelEq.LowPassOn, 1),
            (Effects.Eq.ChannelEq.LowPassFrequency, 19_000),
            (Effects.Eq.ChannelEq.LowPassSlope, 3)
        );
        var (music, right) = Fx.Run(band, Fx.Music().Left, Fx.Music().Right);
        // Then the classic inter-sample over: a quarter-rate sine at 45°, whose samples are 3 dB under its peak.
        var hard = Enumerable.Range(0, 12_000)
            .Select(static i => (float)(0.7 * Math.Sin(Math.PI / 2 * (i % 4) + Math.PI / 4)))
            .ToArray();
        var input = music.Concat(hard).ToArray();
        var (l, r) = Fx.Run(limiter, input, [..right.Concat(hard)]);
        var limit = Math.Pow(10, ceiling / 20.0);
        Assert.True(Peak(l) <= limit * 1.000001 && Peak(r) <= limit * 1.000001);
        if (truePeak) {
            var over = ToDb(Math.Max(Fx.TruePeak(l), Fx.TruePeak(r))) - ceiling;
            Assert.True(over <= 0.001, $"{id} true peak {over:0.000} dB over the ceiling");
        } else {
            // Sample peaks only: the quarter-rate sine's true peak is 3 dB over its samples.
            Assert.True(ToDb(Fx.TruePeak(l.AsSpan(music.Length + 4_000))) - ceiling > 2);
        }

        Assert.True(limiter.Reading < -6);
        // Loud, not squashed: the limited music still peaks at the ceiling.
        Assert.True(ToDb(Peak(l.AsSpan(0, music.Length))) > ceiling - 0.2);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1.5f, false)]
    [InlineData(1.5f, true)]
    [InlineData(10, true)]
    public void Limiter_latency_is_the_lookahead_and_a_loud_click_is_caught_ahead(float lookaheadMs, bool truePeak) {
        using var limiter = Fx.Create(
            Limiter.Id,
            (Limiter.Lookahead, lookaheadMs),
            (Limiter.TruePeakDetection, truePeak ? 1 : 0),
            (Limiter.Ceiling, -6)
        );
        var expected = (int)Math.Round(lookaheadMs * 48) + (truePeak ? TruePeak.Delay : 0);
        Assert.Equal(expected, limiter.LatencySamples);
        var input = new float[4_800];
        for (var i = 0; i < input.Length; i++) {
            input[i] = 0.1f * MathF.Sin(2 * MathF.PI * 300 * i / Fx.Rate);
        }

        input[2_000] = 1;
        var (output, _) = Fx.Run(limiter, input);
        Assert.Equal(input[100], output[100 + expected], 5);
        // The detector aims a hair under the ceiling, for the interpolator's own error.
        Assert.Equal(Math.Pow(10, (-6 - LookaheadLimiter.MarginDb) / 20), output[2_000 + expected], 4);
        if (lookaheadMs > 1) {
            // The gain was already coming down before the click arrived.
            Assert.True(output[2_000 + expected - 10] / input[1_990] < 0.999);
        }
    }

    [Fact]
    public void Limiter_releases_with_its_time_constant() {
        using var limiter = Fx.Create(
            Limiter.Id,
            (Limiter.Lookahead, 0),
            (Limiter.TruePeakDetection, 0),
            (Limiter.Ceiling, -6),
            (Limiter.Release, 50)
        );
        var input = TestWav.Constant(0.1f, 24_000);
        input[1_000] = 1;
        var (output, _) = Fx.Run(limiter, input);
        // The reduction is 1 − 0.498 below unity at the click, and recovers as 1 − e^(−t/τ) toward unity.
        var depth = 1 - Math.Pow(10, (-6 - LookaheadLimiter.MarginDb) / 20);
        var at = 1_000 + 50 * 48;
        Assert.Equal(1 - depth / Math.E, output[at] / 0.1, 3);
    }

    [Fact]
    public void Gate_opens_above_the_threshold_holds_and_closes_to_the_reduction() {
        using var gate = Fx.Create(
            NoiseGate.Id,
            (NoiseGate.Threshold, -30),
            (NoiseGate.Hysteresis, -6),
            (NoiseGate.Reduction, -40),
            (NoiseGate.Attack, 0.1f),
            (NoiseGate.Hold, 50),
            (NoiseGate.Release, 5)
        );
        var input = new float[48_000];
        for (var i = 0; i < input.Length; i++) {
            var level = i < 12_000 ? 0.01f : i < 24_000 ? 0.5f : 0.01f;
            input[i] = level * MathF.Sin(2 * MathF.PI * 200 * i / Fx.Rate);
        }

        var (output, _) = Fx.Run(gate, input);
        Assert.Equal(-40, ToDb(Rms(output.AsSpan(6_000, 4_800)) / Rms(input.AsSpan(6_000, 4_800))), 0.1);
        Assert.Equal(0, ToDb(Rms(output.AsSpan(14_400, 4_800)) / Rms(input.AsSpan(14_400, 4_800))), 0.01);
        // Held open for 50 ms after the level falls, closed soon after.
        Assert.Equal(0, ToDb(Rms(output.AsSpan(24_480, 960)) / Rms(input.AsSpan(24_480, 960))), 0.1);
        Assert.Equal(-40, ToDb(Rms(output.AsSpan(36_000, 4_800)) / Rms(input.AsSpan(36_000, 4_800))), 0.1);
        Assert.Equal(-40, gate.Reading, 0.1);
    }

    [Fact]
    public void Gate_keys_from_the_sidechain() {
        using var gate = Fx.Create(
            NoiseGate.Id,
            (NoiseGate.Threshold, -20),
            (NoiseGate.Reduction, -60),
            (NoiseGate.Sidechain, 1)
        );
        var tone = TestWav.Sine(200, (int)Fx.Rate, 0.5);
        var key = new float[tone.Length];
        Array.Fill(key, 0.5f, 12_000, 6_000);
        var (output, _) = Fx.Run(gate, tone, sidechainLeft: key);
        Assert.True(ToDb(Rms(output.AsSpan(2_000, 8_000)) / Rms(tone.AsSpan(2_000, 8_000))) < -55);
        Assert.Equal(0, ToDb(Rms(output.AsSpan(13_000, 4_000)) / Rms(tone.AsSpan(13_000, 4_000))), 0.05);
    }

    [Fact]
    public void DeEsser_turns_down_sibilance_and_leaves_the_rest() {
        using var deesser = Fx.Create(
            DeEsser.Id,
            (DeEsser.Frequency, 5_000),
            (DeEsser.Threshold, -30),
            (DeEsser.Range, 12)
        );
        var low = TestWav.Sine(200, (int)Fx.Rate, 0.5);
        var ess = TestWav.Sine(8_000, (int)Fx.Rate, 0.5, 0.3f);
        var (lowOut, _) = Fx.Run(deesser, low);
        Assert.Equal(0, ToDb(Rms(lowOut.AsSpan(12_000)) / Rms(low.AsSpan(12_000))), 0.05);
        Assert.Equal(0, deesser.Reading, 0.01);
        deesser.Reset();
        var (essOut, _) = Fx.Run(deesser, ess);
        var reduction = ToDb(Rms(essOut.AsSpan(12_000)) / Rms(ess.AsSpan(12_000)));
        Assert.InRange(reduction, -12.5, -8);
        Assert.Equal(-12, deesser.Reading, 0.5);
    }
}
