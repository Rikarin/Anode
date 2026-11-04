using Anode.Dsp.Effects;
using Anode.Dsp.Effects.Eq;
using Anode.Dsp.Filters;
using Anode.Dsp.Metering;
using Anode.Dsp.Tests.Support;
using static Anode.Dsp.Tests.Support.Analysis;

namespace Anode.Dsp.Tests;

/// <summary>The Channel EQ's responses, measured from impulse responses against the textbook designs.</summary>
public sealed class EqTests {
    static readonly double[] Probes = [20, 50, 100, 200, 400, 800, 1_000, 1_600, 3_000, 5_000, 8_000, 12_000, 16_000];

    static float[] ImpulseResponse(params (int, float)[] parameters) {
        using var eq = Fx.Create(ChannelEq.Id, parameters);
        return Fx.Run(eq, Fx.Impulse(32_768)).Left;
    }

    public static TheoryData<int, double, double, double> Bands =>
        new() {
            { 0, 80, 6, 0.71 }, // low shelf
            { 0, 300, -9, 1 },
            { 1, 250, 6, 1 }, // peaks
            { 2, 1_000, -12, 4 },
            { 3, 3_000, 9, 0.5 },
            { 4, 10_000, -6, 2 },
            { 5, 8_000, 4, 0.71 }, // high shelf
            { 5, 2_000, -10, 1 }
        };

    [Theory]
    [MemberData(nameof(Bands))]
    public void Bells_and_shelves_match_the_cookbook(int band, double frequency, double gainDb, double q) {
        var (on, f, g, quality, type) = band switch {
            0 => (ChannelEq.LowShelfOn, ChannelEq.LowShelfFrequency, ChannelEq.LowShelfGain, ChannelEq.LowShelfQ,
                BiquadType.LowShelf),
            1 => (ChannelEq.Peak1On, ChannelEq.Peak1Frequency, ChannelEq.Peak1Gain, ChannelEq.Peak1Q, BiquadType.Peak),
            2 => (ChannelEq.Peak2On, ChannelEq.Peak2Frequency, ChannelEq.Peak2Gain, ChannelEq.Peak2Q, BiquadType.Peak),
            3 => (ChannelEq.Peak3On, ChannelEq.Peak3Frequency, ChannelEq.Peak3Gain, ChannelEq.Peak3Q, BiquadType.Peak),
            4 => (ChannelEq.Peak4On, ChannelEq.Peak4Frequency, ChannelEq.Peak4Gain, ChannelEq.Peak4Q, BiquadType.Peak),
            _ => (ChannelEq.HighShelfOn, ChannelEq.HighShelfFrequency, ChannelEq.HighShelfGain, ChannelEq.HighShelfQ,
                BiquadType.HighShelf)
        };
        var ir = ImpulseResponse((on, 1), (f, (float)frequency), (g, (float)gainDb), (quality, (float)q));
        var reference = BiquadCoefficients.Design(type, Fx.Rate, (float)frequency, (float)q, (float)gainDb);
        foreach (var probe in Probes.Append(frequency)) {
            var measured = ToDb(MagnitudeAt(ir, probe, Fx.Rate));
            Assert.Equal(ToDb(reference.MagnitudeAt(probe, Fx.Rate)), measured, 0.02);
        }

        // At the centre of a bell, and far past a shelf, the gain is the setting.
        var at = type switch {
            BiquadType.Peak => frequency,
            BiquadType.LowShelf => 10,
            _ => 23_000
        };
        Assert.Equal(gainDb, ToDb(MagnitudeAt(ir, at, Fx.Rate)), type == BiquadType.Peak ? 0.01 : 0.3);
    }

    /// <summary>
    ///     One setting of every kind of band, measured: <c>eq-response.spec.ts</c> in the editor holds the same table, so
    ///     the curve the editor draws is the response the engine has.
    /// </summary>
    [Fact]
    public void Response_is_the_table_the_editors_curve_is_tested_against() {
        var ir = ImpulseResponse(
            (ChannelEq.HighPassOn, 1),
            (ChannelEq.HighPassFrequency, 80),
            (ChannelEq.HighPassSlope, 1),
            (ChannelEq.LowShelfFrequency, 120),
            (ChannelEq.LowShelfGain, 4),
            (ChannelEq.Peak2Frequency, 1_000),
            (ChannelEq.Peak2Gain, -6),
            (ChannelEq.Peak2Q, 2),
            (ChannelEq.Peak3Frequency, 3_000),
            (ChannelEq.Peak3Gain, 3),
            (ChannelEq.HighShelfFrequency, 8_000),
            (ChannelEq.HighShelfGain, 2),
            (ChannelEq.LowPassOn, 1),
            (ChannelEq.LowPassFrequency, 15_000),
            (ChannelEq.LowPassSlope, 0),
            (ChannelEq.Output, -1)
        );
        (double Hz, double Db)[] table = [
            (40, -21.149), (100, 1.006), (300, -1.042), (1_000, -6.634), (3_000, 1.812), (8_000, 0.286),
            (15_000, -2.000), (18_000, -7.891)
        ];
        foreach (var (hz, db) in table) {
            Assert.Equal(db, ToDb(MagnitudeAt(ir, hz, Fx.Rate)), 0.001);
        }
    }

    [Fact]
    public void Display_is_the_spectrum_of_a_tone_on_the_analysers_bands() {
        using var eq = Fx.Create(ChannelEq.Id);
        var tone = TestWav.Sine(1_000, (int)Fx.Rate, 0.5);
        Fx.Run(eq, tone);
        var display = Assert.IsAssignableFrom<IEffectDisplay>(eq);
        var all = new float[display.DisplaySize];
        Assert.Equal(all.Length, display.ReadDisplay(all));
        Assert.All(all.AsSpan(0, ChannelEq.DynamicBands).ToArray(), static gain => Assert.Equal(0, gain));
        var bands = all[ChannelEq.DynamicBands..];
        Assert.Equal(SpectrumAnalyzer.DisplayBands, bands.Length);
        // Bands run 20 Hz to 20 kHz evenly in log frequency, so 1 kHz is 56.6 % of the way up.
        var peak = Array.IndexOf(bands, bands.Max());
        Assert.Equal((int)(Math.Log(1_000.0 / 20) / Math.Log(1_000) * bands.Length), peak);
        // −6 dBFS, less the Hann window's scalloping for a tone between bins (at most 1.42 dB).
        Assert.InRange(bands[peak], -7.5, -5.9);
        Assert.True(bands[peak - 12] < bands[peak] - 40, $"{bands[peak - 12]} beside {bands[peak]}");

        using var linear = Fx.Create(LinearPhaseEq.Id);
        Fx.Run(linear, TestWav.Sine(1_000, (int)Fx.Rate, 0.5));
        var flat = new float[SpectrumAnalyzer.DisplayBands];
        Assert.Equal(flat.Length, ((IEffectDisplay)linear).ReadDisplay(flat));
        Assert.Equal(peak, Array.IndexOf(flat, flat.Max()));
    }

    // ---- Mid/side -------------------------------------------------------------------------------------------------------

    static (float[] Left, float[] Right) Stereo(float[] mid, float[] side) {
        var left = new float[mid.Length];
        var right = new float[mid.Length];
        for (var i = 0; i < left.Length; i++) {
            left[i] = mid[i] + side[i];
            right[i] = mid[i] - side[i];
        }

        return (left, right);
    }

    static float[] Mix(float[] a, float[] b, float sign) {
        var mix = new float[a.Length];
        for (var i = 0; i < mix.Length; i++) {
            mix[i] = 0.5f * (a[i] + sign * b[i]);
        }

        return mix;
    }

    public static TheoryData<int, double, double> Places =>
        new() { { ChannelEq.Stereo, 12, 12 }, { ChannelEq.Mid, 12, 0 }, { ChannelEq.Side, 0, 12 } };

    [Theory]
    [MemberData(nameof(Places))]
    public void A_band_works_on_its_placement_alone(int place, double midDb, double sideDb) {
        using var eq = Fx.Create(
            ChannelEq.Id,
            (ChannelEq.Peak2Frequency, 1_000),
            (ChannelEq.Peak2Gain, 12),
            (ChannelEq.Peak2Place, place)
        );
        var impulse = Fx.Impulse(16_384, 0, 0.25f);
        var silence = new float[16_384];
        var (left, right) = Fx.Run(eq, Stereo(impulse, silence).Left, Stereo(impulse, silence).Right);
        Assert.Equal(midDb, ToDb(MagnitudeAt(Mix(left, right, 1), 1_000, Fx.Rate) / 0.25), 0.02);
        eq.Reset();
        (left, right) = Fx.Run(eq, Stereo(silence, impulse).Left, Stereo(silence, impulse).Right);
        Assert.Equal(sideDb, ToDb(MagnitudeAt(Mix(left, right, -1), 1_000, Fx.Rate) / 0.25), 0.02);
    }

    [Fact]
    public void Mid_and_side_at_rest_are_the_input() {
        // A placed band at 0 dB is skipped; one that really runs at a tiny gain still rebuilds left and right.
        using var eq = Fx.Create(ChannelEq.Id, (ChannelEq.Peak1Place, ChannelEq.Side), (ChannelEq.Peak1Gain, 0.0001f));
        var (music, right) = Fx.Music(24_000);
        var (l, r) = Fx.Run(eq, music, right);
        Assert.True(NullResidualDb(music, l) < -90, $"{NullResidualDb(music, l):0.0} dB");
        Assert.True(NullResidualDb(right, r) < -90, $"{NullResidualDb(right, r):0.0} dB");
    }

    [Fact]
    public void A_band_on_the_side_leaves_the_mono_sum_as_it_was() {
        using var eq = Fx.Create(
            ChannelEq.Id,
            (ChannelEq.HighPassOn, 1),
            (ChannelEq.HighPassFrequency, 300),
            (ChannelEq.HighPassPlace, ChannelEq.Side),
            (ChannelEq.Peak3Gain, 9),
            (ChannelEq.Peak3Place, ChannelEq.Side)
        );
        var (music, right) = Fx.Music(24_000);
        var (l, r) = Fx.Run(eq, music, right);
        Assert.True(NullResidualDb(Mix(music, right, 1), Mix(l, r, 1)) < -120);
    }

    [Fact]
    public void A_low_cut_on_the_side_monos_the_bass() {
        using var eq = Fx.Create(
            ChannelEq.Id,
            (ChannelEq.HighPassOn, 1),
            (ChannelEq.HighPassFrequency, 200),
            (ChannelEq.HighPassSlope, 3),
            (ChannelEq.HighPassPlace, ChannelEq.Side)
        );
        var bass = TestWav.Sine(50, (int)Fx.Rate, 1, 0.25f);
        var (l, r) = Fx.Run(eq, bass, new float[bass.Length]); // hard left: mid and side alike
        var settled = Fx.Rate / 2;
        Assert.True(ToDb(Rms(Mix(l, r, -1).AsSpan((int)settled)) / Rms(bass.AsSpan((int)settled))) < -60);
    }

    [Fact]
    public void An_old_state_without_placement_or_dynamics_loads_with_them_off() {
        // A state of the original 31 parameters, as saved before these existed: only the ones it holds are set.
        using var saved = Fx.Create(ChannelEq.Id, (ChannelEq.Peak2Gain, 6));
        var state = saved.SaveState()!;
        var stripped = Keep(state, ChannelEq.Output + 1);
        using var loaded = BuiltinEffects.Create(ChannelEq.Id)!;
        loaded.Prepare(Fx.Rate, 1_024);
        Assert.True(loaded.LoadState(stripped));
        Assert.Equal(6, loaded.GetParameter(ChannelEq.Peak2Gain));
        Assert.Equal(ChannelEq.Stereo, loaded.GetParameter(ChannelEq.Peak2Place));
        Assert.Equal(0, loaded.GetParameter(ChannelEq.Dynamics[2].On));
    }

    /// <summary>The state with only its first <paramref name="count" /> parameters, as an older version wrote it.</summary>
    static byte[] Keep(byte[] state, int count) {
        var at = 5;
        at += 2 + BitConverter.ToUInt16(state, at); // the id
        at += 2 + BitConverter.ToUInt16(state, at); // the file
        var counted = at;
        at += 2;
        for (var i = 0; i < count; i++) {
            at += 2 + BitConverter.ToUInt16(state, at) + 4;
        }

        var kept = state[..at];
        BitConverter.GetBytes((ushort)count).CopyTo(kept, counted);
        return kept;
    }

    // ---- Dynamic bands ----------------------------------------------------------------------------------------------

    static (int, float)[] Dynamic(int band, float threshold, float ratio, float range) {
        var d = ChannelEq.Dynamics[band];
        return [(d.On, 1), (d.Threshold, threshold), (d.Ratio, ratio), (d.Range, range)];
    }

    static double Settled(IAudioEffect eq, double hz, float amplitude) {
        var tone = TestWav.Sine(hz, (int)Fx.Rate, 2, amplitude);
        var (left, _) = Fx.Run(eq, tone);
        var half = (int)Fx.Rate;
        return ToDb(Rms(left.AsSpan(half)) / Rms(tone.AsSpan(half)));
    }

    [Theory]
    [InlineData(-10, -30, 4, -24, -15)] // 20 dB over at 4:1: 15 dB
    [InlineData(-10, -30, 2, -24, -10)]
    [InlineData(-10, -30, 4, -6, -6)] // the range stops it
    [InlineData(-10, -60, 10, -12, -12)]
    [InlineData(-40, -30, 4, -24, 0)] // under the threshold
    [InlineData(-10, -30, 2, 9, 9)] // a positive range boosts when loud
    [InlineData(-20, -30, 2, 24, 5)]
    public void A_dynamic_bell_moves_as_a_compressor_would(
        float levelDb,
        float threshold,
        float ratio,
        float range,
        double gainDb
    ) {
        using var eq = Fx.Create(
            ChannelEq.Id,
            [
                (ChannelEq.Peak2Frequency, 1_000), (ChannelEq.Peak2Q, 2),
                (ChannelEq.DynamicRelease, 50), ..Dynamic(2, threshold, ratio, range)
            ]
        );
        // The detector reads the band's peak, a sine's amplitude.
        Assert.Equal(gainDb, Settled(eq, 1_000, Decibels.ToGain(levelDb)), 0.15);
        Assert.Equal(gainDb, ((ChannelEq)eq).DynamicGain(2), 0.15);
    }

    [Fact]
    public void A_dynamic_band_adds_to_its_own_gain() {
        using var eq = Fx.Create(
            ChannelEq.Id,
            [(ChannelEq.Peak2Frequency, 1_000), (ChannelEq.Peak2Gain, 4), ..Dynamic(2, -30, 4, -24)]
        );
        Assert.Equal(4 - 15, Settled(eq, 1_000, Decibels.ToGain(-10f)), 0.15);
    }

    [Fact]
    public void A_dynamic_band_hears_only_its_own_band() {
        using var eq = Fx.Create(
            ChannelEq.Id,
            [(ChannelEq.Peak4Frequency, 6_000), (ChannelEq.Peak4Q, 4), ..Dynamic(4, -40, 10, -24)]
        );
        Assert.Equal(0, Settled(eq, 100, 0.5f), 0.05);
        Assert.True(((ChannelEq)eq).DynamicGain(4) > -0.5, $"{((ChannelEq)eq).DynamicGain(4)}");
    }

    [Fact]
    public void A_dynamic_low_shelf_hears_the_bass_and_a_high_shelf_the_top() {
        using var low = Fx.Create(
            ChannelEq.Id,
            [(ChannelEq.LowShelfFrequency, 200), ..Dynamic(0, -30, 10, -9)]
        );
        Assert.Equal(-9, Settled(low, 60, 0.5f), 0.2);
        low.Reset();
        Assert.Equal(0, Settled(low, 4_000, 0.5f), 0.1);
        using var high = Fx.Create(
            ChannelEq.Id,
            [(ChannelEq.HighShelfFrequency, 4_000), ..Dynamic(5, -30, 10, -9)]
        );
        Assert.Equal(-9, Settled(high, 12_000, 0.5f), 0.3);
        high.Reset();
        Assert.Equal(0, Settled(high, 200, 0.5f), 0.1);
    }

    [Fact]
    public void A_dynamic_band_cuts_a_probe_tone_beside_the_one_that_triggers_it() {
        // A loud 1 kHz holds the band down; a quiet tone at the band's skirt is cut by the bell's own curve there.
        using var eq = Fx.Create(
            ChannelEq.Id,
            [(ChannelEq.Peak2Frequency, 1_000), (ChannelEq.Peak2Q, 1), ..Dynamic(2, -40, 10, -12)]
        );
        var loud = TestWav.Sine(1_000, (int)Fx.Rate, 2, 0.25f);
        var probe = TestWav.Sine(1_500, (int)Fx.Rate, 2, 0.002f);
        var both = new float[loud.Length];
        for (var i = 0; i < both.Length; i++) {
            both[i] = loud[i] + probe[i];
        }

        var (left, _) = Fx.Run(eq, both);
        var bell = BiquadCoefficients.Design(BiquadType.Peak, Fx.Rate, 1_000, 1, -12);
        var start = (int)Fx.Rate;
        var measured = Spectrum(left, start, 32_768)[(int)Math.Round(1_500 * 32_768.0 / Fx.Rate)].Magnitude;
        var reference = Spectrum(probe, start, 32_768)[(int)Math.Round(1_500 * 32_768.0 / Fx.Rate)].Magnitude;
        Assert.Equal(ToDb(bell.MagnitudeAt(1_500, Fx.Rate)), ToDb(measured / reference), 0.3);
    }

    [Fact]
    public void A_dynamic_band_reaches_its_gain_at_attack_and_lets_go_at_release() {
        using var eq = (ChannelEq)Fx.Create(
            ChannelEq.Id,
            [
                (ChannelEq.Peak2Frequency, 1_000), (ChannelEq.DynamicAttack, 20), (ChannelEq.DynamicRelease, 100),
                ..Dynamic(2, -40, 10, -12)
            ]
        );
        var loud = TestWav.Sine(1_000, (int)Fx.Rate, 0.02, 0.25f);
        Fx.Run(eq, loud);
        Assert.InRange(eq.DynamicGain(2), -12 * 0.72, -12 * 0.52); // one time constant: 63 %
        Fx.Run(eq, TestWav.Sine(1_000, (int)Fx.Rate, 0.5, 0.25f));
        Assert.Equal(-12, eq.DynamicGain(2), 0.05);
        Fx.Run(eq, new float[(int)(0.1 * Fx.Rate)]);
        // The detector falls first: it takes 50 ms to drop below the level the range clips at, then the gain lets go.
        Assert.InRange(eq.DynamicGain(2), -11, -6);
        Fx.Run(eq, new float[(int)(0.5 * Fx.Rate)]);
        Assert.InRange(eq.DynamicGain(2), -0.1, 0);
    }

    [Fact]
    public void A_dynamic_band_on_the_mid_hears_and_touches_only_the_mid() {
        using var eq = Fx.Create(
            ChannelEq.Id,
            [(ChannelEq.Peak2Frequency, 1_000), (ChannelEq.Peak2Place, ChannelEq.Mid), ..Dynamic(2, -40, 10, -12)]
        );
        var tone = TestWav.Sine(1_000, (int)Fx.Rate, 1, 0.25f);
        var silence = new float[tone.Length];
        // All side: the detector hears nothing.
        var (left, right) = Fx.Run(eq, Stereo(silence, tone).Left, Stereo(silence, tone).Right);
        Assert.Equal(0, ((ChannelEq)eq).DynamicGain(2), 0.05);
        Assert.True(NullResidualDb(Stereo(silence, tone).Left, left) < -100);
        eq.Reset();
        (left, right) = Fx.Run(eq, Stereo(tone, silence).Left, Stereo(tone, silence).Right);
        Assert.Equal(-12, ((ChannelEq)eq).DynamicGain(2), 0.1);
        Assert.True(Rms(right.AsSpan(24_000)) < 0.3f * Rms(tone.AsSpan(24_000)));
    }

    [Fact]
    public void Display_starts_with_the_dynamic_gains() {
        using var eq = Fx.Create(ChannelEq.Id, [(ChannelEq.Peak2Frequency, 1_000), ..Dynamic(2, -40, 10, -12)]);
        Fx.Run(eq, TestWav.Sine(1_000, (int)Fx.Rate, 1, 0.25f));
        var display = (IEffectDisplay)eq;
        var values = new float[display.DisplaySize];
        display.ReadDisplay(values);
        Assert.Equal(-12, values[2], 0.1);
        Assert.Equal(0, values[0]);
        Assert.Equal(0, values[5]);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 4)]
    [InlineData(2, 6)]
    [InlineData(3, 8)]
    public void Cuts_are_butterworth_of_the_slope(int slope, int order) {
        const double cutoff = 200;
        var low = ImpulseResponse(
            (ChannelEq.HighPassOn, 1),
            (ChannelEq.HighPassFrequency, (float)cutoff),
            (ChannelEq.HighPassSlope, slope)
        );
        const double top = 4_000;
        var high = ImpulseResponse(
            (ChannelEq.LowPassOn, 1),
            (ChannelEq.LowPassFrequency, (float)top),
            (ChannelEq.LowPassSlope, slope)
        );
        foreach (var probe in Probes) {
            // The bilinear transform of an analog Butterworth, prewarped at the cutoff.
            var w = Math.Tan(Math.PI * probe / Fx.Rate);
            var highPass = 1 / Math.Sqrt(1 + Math.Pow(Math.Tan(Math.PI * cutoff / Fx.Rate) / w, 2 * order));
            var lowPass = 1 / Math.Sqrt(1 + Math.Pow(w / Math.Tan(Math.PI * top / Fx.Rate), 2 * order));
            Assert.Equal(Math.Max(ToDb(highPass), -150), Math.Max(ToDb(MagnitudeAt(low, probe, Fx.Rate)), -150), 0.05);
            if (lowPass > 1e-4) {
                Assert.Equal(ToDb(lowPass), ToDb(MagnitudeAt(high, probe, Fx.Rate)), 0.05);
            }
        }

        Assert.Equal(-3.01, ToDb(MagnitudeAt(low, cutoff, Fx.Rate)), 0.02);
        Assert.Equal(-10 * Math.Log10(1 + Math.Pow(2, 2 * order)), ToDb(MagnitudeAt(low, cutoff / 2, Fx.Rate)), 0.1);
    }

    [Fact]
    public void Bands_combine_and_the_output_gain_applies() {
        var ir = ImpulseResponse(
            (ChannelEq.Peak1Gain, 6),
            (ChannelEq.Peak1Frequency, 100),
            (ChannelEq.Peak4Gain, -6),
            (ChannelEq.Peak4Frequency, 8_000),
            (ChannelEq.Output, -3)
        );
        Assert.Equal(3, ToDb(MagnitudeAt(ir, 100, Fx.Rate)), 0.1);
        Assert.Equal(-9, ToDb(MagnitudeAt(ir, 8_000, Fx.Rate)), 0.1);
        Assert.Equal(-3, ToDb(MagnitudeAt(ir, 1_000, Fx.Rate)), 0.2);
    }

    [Fact]
    public void Flat_by_default_and_off_bands_do_nothing() {
        var flat = ImpulseResponse();
        Assert.Equal(1, flat[0]);
        Assert.Equal(0, Peak(flat.AsSpan(1)));
        var off = ImpulseResponse((ChannelEq.Peak2Gain, 12), (ChannelEq.Peak2On, 0));
        Assert.Equal(1, off[0]);
    }

    [Fact]
    public void Sweeping_a_band_does_not_click() {
        using var eq = Fx.Create(ChannelEq.Id, (ChannelEq.Peak2Gain, 12), (ChannelEq.Peak2Q, 2));
        var sine = TestWav.Sine(440, (int)Fx.Rate, 1);
        var output = new float[sine.Length];
        for (var at = 0; at < sine.Length; at += 64) {
            eq.SetParameter(ChannelEq.Peak2Frequency, 200 + 4_000 * (at / 48_000f));
            var l = sine[at..(at + 64)];
            var r = (float[])l.Clone();
            eq.Process(l, r, default, default);
            l.CopyTo(output, at);
        }

        // No sample jumps further than a 440 Hz sine at +12 dB can move in one sample.
        var limit = 2 * Math.PI * 440 / Fx.Rate * 0.5 * 4 * 1.2;
        for (var i = 1; i < output.Length; i++) {
            Assert.True(Math.Abs(output[i] - output[i - 1]) < limit, $"step at {i}");
        }
    }

    [Fact]
    public void The_analyzer_reads_a_tone_in_its_band_and_the_reading_is_the_output_peak() {
        using var eq = (ChannelEq)Fx.Create(ChannelEq.Id);
        var sine = TestWav.Sine(1_000, (int)Fx.Rate, 0.5);
        Fx.Run(eq, sine);
        Assert.Equal(-6.02, eq.Reading, 0.05);
        var bands = new float[30];
        eq.ReadSpectrum(bands);
        // 30 bands over 20 Hz … 20 kHz: 1 kHz is in band ⌊30 · ln(50) / ln(1000)⌋ = 16.
        Assert.Equal(16, Array.IndexOf(bands, bands.Max()));
        Assert.InRange(bands[16], -6.02 - 1.45, -6.02 + 0.05); // a Hann window scallops by up to 1.42 dB
        Assert.True(bands[5] < -60);
    }
}
