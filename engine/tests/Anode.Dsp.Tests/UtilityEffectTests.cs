using Anode.Dsp.Effects;
using Anode.Dsp.Effects.Dynamics;
using Anode.Dsp.Effects.Metering;
using Anode.Dsp.Effects.Utility;
using Anode.Dsp.Tests.Support;
using static Anode.Dsp.Tests.Support.Analysis;

namespace Anode.Dsp.Tests;

/// <summary>
///     The core utility effects and meters of docs/effects/parity.md, measured against known signals. The contract tests
///     (<see cref="EffectContractTests" />) cover the rest: buffer-size independence, state, no allocation.
/// </summary>
public sealed class UtilityEffectTests {
    const int Rate = (int)Fx.Rate;

    /// <summary>A tone of a few harmonics, as an instrument has.</summary>
    static float[] Tone(double frequency, double seconds, float amplitude = 0.3f) {
        var x = new float[(int)(seconds * Rate)];
        for (var i = 0; i < x.Length; i++) {
            var t = 2 * Math.PI * frequency * i / Rate;
            x[i] = amplitude * (float)(Math.Sin(t) + 0.4 * Math.Sin(2 * t + 1) + 0.2 * Math.Sin(3 * t + 2));
        }

        return x;
    }

    static float[] Display(IAudioEffect effect) {
        var display = (IEffectDisplay)effect;
        var values = new float[display.DisplaySize];
        Assert.Equal(values.Length, display.ReadDisplay(values));
        return values;
    }

    // ---- Tuner -------------------------------------------------------------------------------------------------------

    public static TheoryData<double, double> Notes =>
        new() {
            { 21, 0 }, // A0, a piano's lowest
            { 28, 12 }, // E1, a bass's low string, sharp
            { 40, -30 }, // E2
            { 45, 7.5 }, // A2
            { 57, -3 }, // A3
            { 69, 0 }, // A4
            { 69, 49 },
            { 76, -21 }, // E5
            { 88, 15 }, // E6
            { 100, -11 }, // E7
            { 108, 4 } // C8, a piano's highest
        };

    [Theory]
    [MemberData(nameof(Notes))]
    public void Tuner_reads_a_tone_within_half_a_cent(double note, double offset) {
        using var tuner = (Tuner)Fx.Create(Tuner.Id);
        var hz = NoteFrequency(note + offset / 100);
        var tone = note >= 100 ? TestWav.Sine(hz, Rate, 0.5, 0.3f) : Tone(hz, 0.5);
        var (left, _) = Fx.Run(tuner, tone);
        Assert.Equal(-300, NullResidualDb(tone, left)); // it passes the audio untouched
        Assert.Equal(note, tuner.Note);
        Assert.Equal(offset, tuner.Reading, 0.5);
        Assert.Equal(0, Cents(tuner.PitchHz, hz), 0.5);
        Assert.Equal([tuner.PitchHz, tuner.Note, tuner.Reading], Display(tuner));
    }

    [Fact]
    public void Tuner_follows_its_reference_and_hears_silence_as_nothing() {
        using var tuner = (Tuner)Fx.Create(Tuner.Id, (Tuner.Reference, 432));
        Fx.Run(tuner, Tone(432, 0.5));
        Assert.Equal(69, tuner.Note);
        Assert.Equal(0, tuner.Reading, 0.5);
        Fx.Run(tuner, new float[Rate / 2]);
        Assert.Equal(0, tuner.PitchHz);
        Assert.Equal(0, tuner.Reading);
    }

    [Fact]
    public void Tuner_mutes_when_asked_and_keeps_listening() {
        using var tuner = (Tuner)Fx.Create(Tuner.Id, (Tuner.Mute, 1));
        var (left, right) = Fx.Run(tuner, Tone(220, 0.5));
        Assert.Equal(0, Peak(left));
        Assert.Equal(0, Peak(right));
        Assert.Equal(57, tuner.Note);
    }

    // ---- Test Oscillator ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(TestOscillator.Sine, 1_000, -18)]
    [InlineData(TestOscillator.Sine, 60, -6)]
    [InlineData(TestOscillator.Sine, 15_000, -24)]
    [InlineData(TestOscillator.Triangle, 440, -12)]
    [InlineData(TestOscillator.Square, 440, -12)]
    [InlineData(TestOscillator.Sawtooth, 440, -12)]
    public void Test_oscillator_is_at_its_frequency_and_level(int wave, float hz, float level) {
        using var effect = Fx.Create(
            TestOscillator.Id,
            (TestOscillator.Wave, wave),
            (TestOscillator.Frequency, hz),
            (TestOscillator.Level, level)
        );
        var (left, right) = Fx.Run(effect, Fx.Music(Rate).Left, Fx.Music(Rate).Right);
        Assert.Equal(-300, NullResidualDb(left, right));
        Assert.Equal(0, Cents(ZeroCrossingFrequency(left, 4_800, Rate - 4_800, Rate), hz), 1.0);
        // A polyBLEP edge overshoots by a few percent: a square, saw or triangle is at its level within a dB.
        Assert.Equal(level, ToDb(Peak(left.AsSpan(4_800))), wave == TestOscillator.Sine ? 0.02 : 1.2);
        Assert.Equal(ToDb(Peak(left.AsSpan(Rate - Play.Block))), effect.Reading, 0.01);
    }

    [Fact]
    public void Test_oscillator_saw_keeps_its_aliases_low() {
        using var effect = Fx.Create(
            TestOscillator.Id,
            (TestOscillator.Wave, TestOscillator.Sawtooth),
            (TestOscillator.Frequency, 1_000),
            (TestOscillator.Level, -6)
        );
        var (left, _) = Fx.Run(effect, new float[65_536]);
        var spectrum = Spectrum(left, 0, 65_536);
        double worst = 0, fundamental = 0;
        for (var k = 100; k < 32_768; k++) {
            var hz = k * (double)Rate / 65_536;
            var harmonic = Math.Abs(hz / 1_000 - Math.Round(hz / 1_000)) * 1_000 < 12;
            if (hz is > 980 and < 1_020) {
                fundamental = Math.Max(fundamental, spectrum[k].Magnitude);
            } else if (!harmonic) {
                worst = Math.Max(worst, spectrum[k].Magnitude);
            }
        }

        Assert.True(ToDb(worst / fundamental) < -50, $"worst alias {ToDb(worst / fundamental):0.0} dB");
    }

    [Theory]
    [InlineData(1, true, false)]
    [InlineData(2, false, true)]
    public void Test_oscillator_goes_to_the_channel_chosen(int channel, bool toLeft, bool toRight) {
        using var effect = Fx.Create(TestOscillator.Id, (TestOscillator.Output, channel));
        var (left, right) = Fx.Run(effect, new float[4_800]);
        Assert.Equal(toLeft, Peak(left) > 0);
        Assert.Equal(toRight, Peak(right) > 0);
    }

    [Fact]
    public void Test_oscillator_noises_have_their_spectra() {
        static double Slope(int wave) {
            using var effect = Fx.Create(TestOscillator.Id, (TestOscillator.Wave, wave), (TestOscillator.Level, 0));
            var (left, _) = Fx.Run(effect, new float[1 << 20]);
            // Band power in octaves from 125 Hz to 8 kHz, averaged over frames.
            var bands = new double[6];
            for (var at = 0; at + 65_536 <= left.Length; at += 65_536) {
                var spectrum = Spectrum(left, at, 65_536);
                for (var o = 0; o < bands.Length; o++) {
                    var low = (int)(125 * Math.Pow(2, o) * 65_536 / Rate);
                    for (var k = low; k < low * 2; k++) {
                        bands[o] += spectrum[k].Magnitude * spectrum[k].Magnitude;
                    }
                }
            }

            return (10 * Math.Log10(bands[^1]) - 10 * Math.Log10(bands[0])) / (bands.Length - 1);
        }

        // White noise gains 3 dB an octave per octave band, pink noise is flat in them.
        Assert.Equal(3, Slope(TestOscillator.WhiteNoise), 0.1);
        Assert.Equal(0, Slope(TestOscillator.PinkNoise), 0.25);
    }

    [Fact]
    public void Test_oscillator_sweeps_logarithmically_and_repeats() {
        using var effect = Fx.Create(TestOscillator.Id, (TestOscillator.Sweep, 1), (TestOscillator.SweepTime, 2));
        var (left, _) = Fx.Run(effect, new float[3 * Rate]);
        // Half way, in log frequency: 632 Hz; at the second repeat's start, 20 Hz again.
        Assert.Equal(0, Cents(ZeroCrossingFrequency(left, Rate - 1_000, 2_000, Rate), 632.5), 20.0);
        Assert.Equal(0, Cents(ZeroCrossingFrequency(left, (int)(1.99 * Rate), 96, Rate), 19_724), 100.0);
        Assert.InRange(ZeroCrossingFrequency(left, 2 * Rate, 9_600, Rate), 25, 32); // 20 to 40 Hz in that time
    }

    // ---- Expander ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(-10, 0)] // above the threshold: untouched
    [InlineData(-30, -20)] // 10 dB under at 3:1: 20 dB down
    [InlineData(-34, -28)]
    public void Expander_follows_its_static_curve(float levelDb, double gainDb) {
        using var effect = Fx.Create(
            Expander.Id,
            (Expander.Threshold, -20),
            (Expander.Ratio, 3),
            (Expander.Knee, 0),
            (Expander.Range, -60)
        );
        var tone = TestWav.Sine(1_000, Rate, 2, Decibels.ToGain(levelDb));
        var (left, _) = Fx.Run(effect, tone);
        var measured = ToDb(Rms(left.AsSpan(Rate)) / Rms(tone.AsSpan(Rate)));
        Assert.Equal(gainDb, measured, 0.1);
        Assert.Equal(gainDb, effect.Reading, 0.1);
    }

    [Fact]
    public void Expander_stops_at_its_range() {
        using var effect =
            Fx.Create(Expander.Id, (Expander.Threshold, -20), (Expander.Ratio, 8), (Expander.Range, -18));
        var tone = TestWav.Sine(1_000, Rate, 2, Decibels.ToGain(-50f));
        var (left, _) = Fx.Run(effect, tone);
        Assert.Equal(-18, ToDb(Rms(left.AsSpan(Rate)) / Rms(tone.AsSpan(Rate))), 0.1);
    }

    [Fact]
    public void Expander_knee_is_smooth_and_half_way_at_the_threshold() {
        Assert.Equal(0, Anode.Dsp.Dynamics.GainComputer.Expansion(-17, -20, 3, 6), 6);
        Assert.Equal(-6, Anode.Dsp.Dynamics.GainComputer.Expansion(-23, -20, 3, 6), 6);
        // In the knee's middle the curve is a quarter of the way to where the slope alone would put it at its end.
        Assert.Equal(-1.5, Anode.Dsp.Dynamics.GainComputer.Expansion(-20, -20, 3, 6), 6);
    }

    [Fact]
    public void Expander_opens_at_attack_and_closes_at_release() {
        using var effect = Fx.Create(
            Expander.Id,
            (Expander.Threshold, -20),
            (Expander.Ratio, 4),
            (Expander.Knee, 0),
            (Expander.Attack, 1),
            (Expander.Release, 100)
        );
        var quiet = TestWav.Sine(1_000, Rate, 1, Decibels.ToGain(-30f));
        Fx.Run(effect, quiet);
        Assert.True(effect.Reading < -25, $"{effect.Reading}");
        var loud = TestWav.Sine(1_000, Rate, 0.05);
        var (left, _) = Fx.Run(effect, loud);
        // After 10 attack time constants it is open.
        Assert.Equal(-6.02, ToDb(Peak(left.AsSpan(480))), 0.2);
        // Closing takes about a release time constant to fall most of the way.
        var (tail, _) = Fx.Run(effect, TestWav.Sine(1_000, Rate, 0.05, Decibels.ToGain(-30f)));
        Assert.True(ToDb(Peak(tail.AsSpan(0, 96))) > -34, "it closes gently");
    }

    // ---- Frequency Spread --------------------------------------------------------------------------------------------

    [Fact]
    public void Spread_leaves_the_mono_sum_exactly_alone() {
        using var effect = Fx.Create(FrequencySpread.Id, (FrequencySpread.Width, 100), (FrequencySpread.Bands, 12));
        var (music, musicRight) = Fx.Music(Rate);
        var (left, right) = Fx.Run(effect, music, musicRight);
        var sum = new float[music.Length];
        var dry = new float[music.Length];
        for (var i = 0; i < sum.Length; i++) {
            sum[i] = left[i] + right[i];
            dry[i] = music[i] + musicRight[i];
        }

        Assert.True(NullResidualDb(sum, dry) < -130, $"{NullResidualDb(sum, dry):0.0} dB");
        // And a mono signal comes out wide.
        effect.Reset();
        var (wideLeft, wideRight) = Fx.Run(effect, music);
        Assert.True(Rms(Difference(wideLeft, wideRight)) > 0.5 * Rms(music));
    }

    static float[] Difference(float[] a, float[] b) {
        var d = new float[a.Length];
        for (var i = 0; i < d.Length; i++) {
            d[i] = a[i] - b[i];
        }

        return d;
    }

    [Fact]
    public void Spread_sends_neighbouring_bands_to_opposite_sides() {
        const double low = 300, high = 7_680;
        const int bands = 8;
        var ratio = Math.Pow(high / low, 1.0 / bands);
        for (var b = 0; b < bands; b++) {
            using var effect = Fx.Create(
                FrequencySpread.Id,
                (FrequencySpread.Low, (float)low),
                (FrequencySpread.High, (float)high),
                (FrequencySpread.Bands, bands),
                (FrequencySpread.Width, 100)
            );
            var centre = low * Math.Pow(ratio, b + 0.5);
            var (left, right) = Fx.Run(effect, TestWav.Sine(centre, Rate, 0.5, 0.25f));
            var (loud, quiet) = b % 2 == 0 ? (left, right) : (right, left);
            // At a band's centre at 100 %, the tone is 6 dB up on one side and gone from the other, less what the bands
            // beside it take back through their skirts.
            Assert.InRange(ToDb(Rms(loud.AsSpan(Rate / 4)) / (0.25 * Math.Sqrt(0.5))), 3.5, 6.1);
            Assert.True(ToDb(Rms(quiet.AsSpan(Rate / 4)) / (0.25 * Math.Sqrt(0.5))) < -6, $"band {b}");
        }
    }

    [Fact]
    public void Spread_leaves_the_bass_central() {
        using var effect = Fx.Create(FrequencySpread.Id, (FrequencySpread.Width, 100));
        var (left, right) = Fx.Run(effect, TestWav.Sine(50, Rate, 1, 0.25f));
        Assert.True(ToDb(Rms(Difference(left, right).AsSpan(Rate / 2)) / 0.25) < -14);
    }

    [Fact]
    public void Spread_at_no_width_is_the_input() {
        using var effect = Fx.Create(FrequencySpread.Id, (FrequencySpread.Width, 0));
        var (music, musicRight) = Fx.Music(Rate / 2);
        var (left, right) = Fx.Run(effect, music, musicRight);
        Assert.Equal(-300, NullResidualDb(music, left));
        Assert.Equal(-300, NullResidualDb(musicRight, right));
    }

    // ---- Oscilloscope ------------------------------------------------------------------------------------------------

    [Fact]
    public void Scope_triggers_on_the_rising_edge_and_a_tone_stands_still() {
        using var effect = Fx.Create(Scope.Id, (Scope.Time, 10));
        var tone = TestWav.Sine(480, Rate, 0.5);
        var (left, _) = Fx.Run(effect, tone);
        Assert.Equal(-300, NullResidualDb(tone, left));
        var trace = Display(effect);
        var l = trace.AsSpan(0, Scope.TracePoints);
        var r = trace.AsSpan(Scope.TracePoints);
        Assert.Equal(0, l[0], 0.05);
        Assert.True(l[1] > l[0], "the trace starts rising");
        // Ten milliseconds of 480 Hz is 4.8 periods.
        var crossings = 0;
        for (var i = 1; i < l.Length; i++) {
            crossings += l[i - 1] < 0 && l[i] >= 0 ? 1 : 0;
        }

        Assert.Equal(4, crossings);
        Assert.Equal(0.5, Peak(l), 0.02);
        Assert.Equal(-300, NullResidualDb(l, r));

        // More of the same tone: the trace does not move.
        Fx.Run(effect, [..tone.AsSpan(0, 1_733)]);
        var again = Display(effect);
        Assert.True(NullResidualDb(trace, again) < -30, $"{NullResidualDb(trace, again):0.0} dB");
    }

    [Fact]
    public void Scope_falls_from_its_level_when_asked() {
        using var effect = Fx.Create(Scope.Id, (Scope.Time, 10), (Scope.Trigger, Scope.Falling), (Scope.Level, 0.25f));
        Fx.Run(effect, TestWav.Sine(480, Rate, 0.5));
        var trace = Display(effect);
        Assert.Equal(0.25, trace[0], 0.05);
        Assert.True(trace[1] < trace[0]);
    }

    [Fact]
    public void Scope_runs_free_on_the_newest_audio_and_on_a_signal_that_never_triggers() {
        using var free = Fx.Create(Scope.Id, (Scope.Time, 5), (Scope.Trigger, Scope.Free));
        var ramp = new float[Rate / 2];
        for (var i = 0; i < ramp.Length; i++) {
            ramp[i] = i / (float)ramp.Length;
        }

        Fx.Run(free, ramp);
        var trace = Display(free);
        Assert.Equal(ramp[^240], trace[0], 0.001);
        Assert.Equal(ramp[^1], trace[Scope.TracePoints - 1], 0.002);

        using var flat = Fx.Create(Scope.Id, (Scope.Time, 5), (Scope.Level, 0.9f));
        Fx.Run(flat, TestWav.Constant(0.25f, Rate / 2));
        Assert.Equal(0.25, Display(flat)[0], 0.001);
    }

    [Fact]
    public void Scope_keeps_a_click_a_long_trace_would_lose() {
        using var effect = Fx.Create(Scope.Id, (Scope.Time, 1_000), (Scope.Trigger, Scope.Free));
        var quiet = new float[Rate * 2];
        quiet[(int)(1.5 * Rate) + 7] = -0.9f;
        Fx.Run(effect, quiet);
        Assert.Equal(-0.9, Display(effect).AsSpan(0, Scope.TracePoints).ToArray().Min(), 0.001);
    }

    [Fact]
    public void Scope_reads_the_peak_in_its_reading() {
        using var effect = Fx.Create(Scope.Id);
        Fx.Run(effect, TestWav.Sine(1_000, Rate, 0.1));
        Assert.Equal(-6.02, effect.Reading, 0.05);
    }

    // ---- Phase Meter -------------------------------------------------------------------------------------------------

    public static TheoryData<string, double, double> Images =>
        new() {
            { "mono", 1, 0 },
            { "out of phase", -1, 0 },
            { "quadrature", 0, 0 },
            { "right 6 dB down", 1, -6.02 },
            { "unrelated", 0, 0 }
        };

    static (float[] Left, float[] Right) Image(string name) {
        var left = TestWav.Sine(997, Rate, 2);
        var right = new float[left.Length];
        for (var i = 0; i < left.Length; i++) {
            right[i] = name switch {
                "mono" => left[i],
                "out of phase" => -left[i],
                "quadrature" => 0.5f * (float)Math.Cos(2 * Math.PI * 997 * i / Rate),
                "right 6 dB down" => 0.5f * left[i],
                _ => 0.5f * (float)Math.Sin(2 * Math.PI * 1_511.3 * i / Rate)
            };
        }

        return (left, right);
    }

    [Theory]
    [MemberData(nameof(Images))]
    public void Phase_meter_reads_the_correlation_of_a_known_image(string name, double correlation, double balance) {
        using var effect = (PhaseMeter)Fx.Create(PhaseMeter.Id);
        var (l, r) = Image(name);
        var (left, _) = Fx.Run(effect, l, r);
        Assert.Equal(-300, NullResidualDb(l, left));
        Assert.Equal(correlation, effect.Reading, 0.02);
        Assert.Equal(balance, effect.Balance, 0.05);
        var values = Display(effect);
        Assert.Equal(effect.Reading, values[0]);
        Assert.Equal(effect.Balance, values[1]);
    }

    [Fact]
    public void Goniometer_stands_mono_up_lays_out_of_phase_flat_and_tilts_one_channel() {
        static (double Side, double Mid) Extent(float[] l, float[] r) {
            using var effect = Fx.Create(PhaseMeter.Id);
            Fx.Run(effect, l, r);
            var values = Display(effect);
            double side = 0, mid = 0;
            for (var p = 0; p < PhaseMeter.Points; p++) {
                side = Math.Max(side, Math.Abs(values[2 + 2 * p]));
                mid = Math.Max(mid, Math.Abs(values[3 + 2 * p]));
            }

            return (side, mid);
        }

        var tone = TestWav.Sine(997, Rate, 0.5);
        var silent = new float[tone.Length];
        var mono = Extent(tone, tone);
        Assert.Equal(0, mono.Side, 1e-6);
        Assert.Equal(0.5 * Math.Sqrt(2), mono.Mid, 0.02);
        var inverted = Extent(tone, [..tone.Select(static s => -s)]);
        Assert.Equal(0, inverted.Mid, 1e-6);
        Assert.Equal(0.5 * Math.Sqrt(2), inverted.Side, 0.02);
        var leftOnly = Extent(tone, silent);
        Assert.Equal(leftOnly.Side, leftOnly.Mid, 1e-6); // 45°

        using var meter = Fx.Create(PhaseMeter.Id);
        Fx.Run(meter, tone, silent);
        var values = Display(meter);
        var loudest = Enumerable.Range(0, PhaseMeter.Points).MaxBy(p => Math.Abs(values[3 + 2 * p]));
        Assert.True(values[2 + 2 * loudest] * values[3 + 2 * loudest] < 0, "left is up and to the left");
    }
}
