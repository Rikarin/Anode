using Anode.Dsp.Effects;
using Anode.Dsp.Effects.Delay;
using Anode.Dsp.Effects.Dynamics;
using Anode.Dsp.Effects.Eq;
using Anode.Dsp.Effects.Modulation;
using Anode.Dsp.Effects.Multi;
using Anode.Dsp.Effects.Pitch;
using Anode.Dsp.Effects.Space;
using Anode.Dsp.Effects.Specialized;
using Anode.Dsp.Filters;
using Anode.Dsp.Tests.Support;
using Anode.Dsp.Transport;
using static Anode.Dsp.Tests.Support.Analysis;

namespace Anode.Dsp.Tests;

/// <summary>
///     The T2 effects of docs/07-phase-6.md, workstream D: what each one does (impulse and tone tests), the latencies,
///     tempo sync against a supplied <see cref="TransportContext" />, and the macro hints of every built-in effect. The
///     contract tests (<see cref="EffectContractTests" />) cover the rest: nulls at Mix 0, buffer-size independence,
///     state, no allocation.
/// </summary>
public sealed class T2EffectTests {
    const int Rate = (int)Fx.Rate;

    public static TheoryData<string> Ids => [..BuiltinEffects.All.Select(static d => d.Id)];

    static float[] Noise(int frames, uint seed, float amplitude = 0.5f) {
        var x = new float[frames];
        for (var i = 0; i < frames; i++) {
            seed ^= seed << 13;
            seed ^= seed >> 17;
            seed ^= seed << 5;
            x[i] = amplitude * (seed * (2f / uint.MaxValue) - 1);
        }

        return x;
    }

    static int PeakIndex(ReadOnlySpan<float> signal, int from, int to) {
        var best = from;
        for (var i = from; i < to; i++) {
            if (Math.Abs(signal[i]) > Math.Abs(signal[best])) {
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    ///     The frequency whose period maximises the normalised autocorrelation of <paramref name="length" /> samples
    ///     from <paramref name="start" />, searched within ±20 % of <paramref name="near" /> Hz and refined by a parabola.
    /// </summary>
    static double PeriodFrequency(ReadOnlySpan<float> signal, int start, int length, double near) {
        var from = (int)(Fx.Rate / near / 1.2);
        var to = (int)(Fx.Rate / near * 1.2) + 1;
        var r = new double[to + 2];
        for (var lag = from - 1; lag <= to + 1; lag++) {
            double sum = 0, a = 0, b = 0;
            for (var n = start; n < start + length; n++) {
                sum += (double)signal[n] * signal[n + lag];
                a += (double)signal[n] * signal[n];
                b += (double)signal[n + lag] * signal[n + lag];
            }

            r[lag] = sum / Math.Sqrt(a * b);
        }

        var best = from;
        for (var lag = from; lag <= to; lag++) {
            if (r[lag] > r[best]) {
                best = lag;
            }
        }

        var offset = 0.5 * (r[best - 1] - r[best + 1]) / (r[best - 1] - 2 * r[best] + r[best + 1]);
        return Fx.Rate / (best + offset);
    }

    // ---- Every effect ------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Ids))]
    public void Every_effect_has_macro_hints_in_order(string id) {
        var hinted = BuiltinEffects.All.Single(d => d.Id == id)
            .Parameters.Where(static p => p.Macro is not null)
            .OrderBy(static p => p.Macro)
            .ToArray();
        Assert.NotEmpty(hinted);
        Assert.Equal(Enumerable.Range(0, hinted.Length), hinted.Select(static p => p.Macro!.Value));
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Processing_with_a_transport_allocates_nothing(string id) {
        using var effect = Fx.Create(id, EffectPresets.For(id));
        var (music, musicRight) = Fx.Music(64 * 300);
        var left = new float[64];
        var right = new float[64];
        var allocated = AllocationProbe.Measure(() => {
                for (var block = 0; block < 300; block++) {
                    var transport = new TransportContext(
                        block % 50 < 40,
                        block * 64L,
                        block * 64 * 2 / Fx.Rate,
                        100 + block % 13,
                        4,
                        4,
                        0,
                        Fx.Rate,
                        64
                    );
                    effect.SetTransport(transport);
                    music.AsSpan(block * 64, 64).CopyTo(left);
                    musicRight.AsSpan(block * 64, 64).CopyTo(right);
                    effect.Process(left, right, right, right);
                }

                effect.Reset();
            }
        );
        Assert.Equal(0, allocated);
    }

    [Theory]
    [InlineData("anode.space")]
    [InlineData("anode.linear-eq")]
    [InlineData("anode.multipressor")]
    [InlineData("anode.enveloper")]
    [InlineData("anode.exciter")]
    [InlineData("anode.subbass")]
    [InlineData("anode.pitch-shifter")]
    [InlineData("anode.pitch-correction")]
    [InlineData("anode.ensemble")]
    [InlineData("anode.rotor-cabinet")]
    [InlineData("anode.channel-eq")]
    [InlineData("anode.compressor")]
    [InlineData("anode.reverb")]
    public void Unsynced_effects_ignore_the_transport(string id) {
        using var a = Fx.Create(id, EffectPresets.For(id));
        using var b = Fx.Create(id, EffectPresets.For(id));
        var (left, right) = Fx.Music(24_000);
        var plain = Fx.Run(a, left, right);
        var synced = new TransportRun(b, left, right, TransportRun.Ramp(70, 170, 24_000), 3.3);
        Assert.Equal(-300, NullResidualDb(plain.Left, synced.Left));
        Assert.Equal(-300, NullResidualDb(plain.Right, synced.Right));
    }

    // ---- Space -------------------------------------------------------------------------------------------------------

    static ConvolutionReverb Space(params (int, float)[] parameters) {
        var effect = (ConvolutionReverb)Fx.Create(ConvolutionReverb.Id, parameters);
        effect.SetParameter(ConvolutionReverb.Predelay, 0);
        effect.SetParameter(ConvolutionReverb.Mix, 100);
        return effect;
    }

    [Theory]
    [InlineData(4_000, 64)]
    [InlineData(20_000, 333)]
    public void Space_equals_direct_convolution(int taps, int block) {
        var irLeft = Noise(taps, 11, 0.2f);
        var irRight = Noise(taps, 12, 0.2f);
        for (var i = 0; i < taps; i++) {
            irLeft[i] *= MathF.Exp(-4f * i / taps);
            irRight[i] *= MathF.Exp(-4f * i / taps);
        }

        using var space = Space();
        space.LoadResponse([irLeft, irRight], Rate);
        var x = Noise(30_000, 3);
        var xr = Noise(30_000, 4);
        var (l, r) = Fx.Run(space, x, xr, block);
        var worst = 0.0;
        for (var n = 0; n < x.Length; n++) {
            double sl = 0, sr = 0;
            for (var k = 0; k < taps && k <= n; k++) {
                sl += (double)irLeft[k] * x[n - k];
                sr += (double)irRight[k] * xr[n - k];
            }

            worst = Math.Max(worst, Math.Max(Math.Abs(sl - l[n]), Math.Abs(sr - r[n])));
        }

        Assert.True(worst < 1e-5, $"worst error {worst:E2}");
    }

    [Fact]
    public void Space_has_no_latency_and_pre_delays_the_wet_path() {
        using var space = Space();
        var ir = new float[3_000];
        ir[0] = 0.5f;
        ir[2_000] = 0.25f;
        space.LoadResponse([ir], Rate);
        Assert.Equal(0, space.LatencySamples);
        var (l, r) = Fx.Run(space, Fx.Impulse(10_000, 100));
        Assert.Equal(0.5, l[100], 6);
        Assert.Equal(0.25, r[2_100], 6); // a mono response feeds both sides
        space.Reset();
        space.SetParameter(ConvolutionReverb.Predelay, 50);
        (l, _) = Fx.Run(space, Fx.Impulse(10_000, 100));
        Assert.Equal(100 + 2_400, PeakIndex(l, 0, l.Length));
    }

    [Fact]
    public void Space_length_shortens_the_response() {
        using var space = Space();
        var full = Fx.Run(space, Fx.Impulse(Rate * 3)).Left;
        space.Reset();
        space.SetParameter(ConvolutionReverb.Length, 30);
        var shortened = Fx.Run(space, Fx.Impulse(Rate * 3)).Left;
        var end = (int)(ConvolutionReverb.BuiltinSeconds * Rate * 0.3) + space.PartitionSize;
        Assert.True(Peak(shortened.AsSpan(end)) < 1e-9);
        Assert.True(Peak(full.AsSpan(end, Rate / 2)) > 1e-3);
        Assert.Equal(-300, NullResidualDb(full.AsSpan(0, 2_000), shortened.AsSpan(0, 2_000)));
    }

    [Fact]
    public void Space_loads_an_impulse_response_from_a_wav_and_keeps_it_in_its_state() {
        var folder = Play.TempFolder();
        var path = Path.Combine(folder, "hall.wav");
        var ir = Noise(22_050, 21, 0.3f);
        for (var i = 0; i < ir.Length; i++) {
            ir[i] *= MathF.Exp(-6f * i / ir.Length);
        }

        TestWav.Write(path, 44_100, [ir, ir], true);
        using var space = (ConvolutionReverb)Fx.Create(ConvolutionReverb.Id);
        Assert.Contains(".wav", space.FileTypes);
        Assert.False(space.LoadFile(Path.Combine(folder, "missing.wav")));
        Assert.True(space.LoadFile(path));
        Assert.Equal(path, space.File);
        // Resampled to 48 kHz and normalised to the built-in's energy.
        Assert.InRange(space.Response[0].Length, 23_990, 24_010);
        var energy = space.Response[0].Sum(static s => (double)s * s);
        Assert.Equal(0.25, energy, 2);

        var state = space.SaveState()!;
        using var restored = (ConvolutionReverb)BuiltinEffects.Create(ConvolutionReverb.Id)!;
        restored.Prepare(Fx.Rate, 1_024);
        Assert.True(restored.LoadState(state));
        Assert.Equal(path, restored.File);
        var (left, right) = Fx.Music(20_000);
        space.Reset();
        Assert.Equal(-300, NullResidualDb(Fx.Run(space, left, right).Left, Fx.Run(restored, left, right).Left));
        File.Delete(path);
        using var orphan = (ConvolutionReverb)Fx.Create(ConvolutionReverb.Id);
        Assert.False(orphan.LoadState(state)); // the file is gone: the built-in response stays
        Assert.Null(orphan.File);
    }

    // ---- Linear Phase EQ ---------------------------------------------------------------------------------------------

    [Fact]
    public void Linear_phase_eq_has_a_symmetric_impulse_response_at_its_latency() {
        using var eq = (LinearPhaseEq)Fx.Create(
            LinearPhaseEq.Id,
            (LinearPhaseEq.HighPassOn, 1),
            (LinearPhaseEq.HighPassFrequency, 80),
            (LinearPhaseEq.LowShelfGain, -4),
            (LinearPhaseEq.Peak2Frequency, 1_000),
            (LinearPhaseEq.Peak2Gain, 6),
            (LinearPhaseEq.Peak2Q, 2),
            (LinearPhaseEq.HighShelfGain, 3)
        );
        var latency = eq.LatencySamples;
        Assert.Equal(2_048, latency);
        var (l, _) = Fx.Run(eq, Fx.Impulse(3 * latency, 0));
        var peak = PeakIndex(l, 0, l.Length);
        Assert.Equal(latency, peak);
        var scale = Math.Abs(l[peak]);
        for (var j = 1; j < latency; j++) {
            Assert.True(
                Math.Abs(l[latency + j] - l[latency - j]) <= 1e-6 * scale,
                $"asymmetric at ±{j}: {l[latency - j]} vs {l[latency + j]}"
            );
        }

        Assert.Equal(0, Peak(l.AsSpan(2 * latency)), 6);
        // The magnitude is the bands' (the Channel EQ's RBJ curves), within the FIR's resolution.
        var response = l.AsSpan(0, 2 * latency);
        BiquadCoefficients[] bands = [
            BiquadCoefficients.Design(BiquadType.HighPass, Rate, 80, 0.54119610014619698),
            BiquadCoefficients.Design(BiquadType.HighPass, Rate, 80, 1.3065629648763766),
            BiquadCoefficients.Design(BiquadType.LowShelf, Rate, 80, 0.71, -4),
            BiquadCoefficients.Design(BiquadType.Peak, Rate, 1_000, 2, 6),
            BiquadCoefficients.Design(BiquadType.HighShelf, Rate, 10_000, 0.71, 3)
        ];
        foreach (var f in new[] { 150.0, 400, 800, 1_000, 1_300, 3_000, 8_000, 12_000, 18_000 }) {
            var expected = bands.Aggregate(0.0, (db, c) => db + ToDb(c.MagnitudeAt(f, Rate)));
            Assert.Equal(expected, ToDb(MagnitudeAt(response, f, Rate)), 0.25);
        }

        Assert.True(ToDb(MagnitudeAt(response, 20, Rate)) < -12);
    }

    [Fact]
    public void Linear_phase_eq_flat_is_a_pure_delay() {
        using var eq = Fx.Create(LinearPhaseEq.Id);
        var (left, right) = Fx.Music(20_000);
        var (l, r) = Fx.Run(eq, left, right);
        var latency = eq.LatencySamples;
        Assert.True(NullResidualDb(left.AsSpan(0, 20_000 - latency), l.AsSpan(latency)) < -110);
        Assert.True(NullResidualDb(right.AsSpan(0, 20_000 - latency), r.AsSpan(latency)) < -110);
    }

    // ---- Multipressor ------------------------------------------------------------------------------------------------

    [Fact]
    public void Multipressor_bands_sum_flat() {
        using var effect = Fx.Create(Multipressor.Id);
        var (l, _) = Fx.Run(effect, Fx.Impulse(16_384, 0, 0.001f));
        foreach (var f in new[] { 30.0, 150, 400, 1_000, 2_500, 5_000, 9_000, 16_000 }) {
            Assert.Equal(-60, ToDb(MagnitudeAt(l, f, Rate)), 0.05);
        }
    }

    [Fact]
    public void Multipressor_compresses_only_the_band_a_tone_is_in() {
        using var effect = (Multipressor)Fx.Create(Multipressor.Id);
        var tone = TestWav.Sine(500, Rate, 1, 0.8f);
        var (l, _) = Fx.Run(effect, tone);
        Assert.True(effect.BandGain(1) < -6, $"band 2: {effect.BandGain(1)}");
        Assert.True(effect.BandGain(0) > -1, $"band 1: {effect.BandGain(0)}");
        Assert.True(effect.BandGain(3) > -1, $"band 4: {effect.BandGain(3)}");
        Assert.True(Rms(l.AsSpan(Rate / 2)) < 0.6 * Rms(tone.AsSpan(Rate / 2)));
        Assert.True(effect.Reading < -6);
    }

    [Fact]
    public void Multipressor_expands_below_its_expander_threshold() {
        // Every band: the crossovers leak a little of the tone into the bands beside its own.
        using var effect = (Multipressor)Fx.Create(
            Multipressor.Id,
            [
                ..Multipressor.Bands.SelectMany(static band => new (int, float)[] {
                        (band.ExpThreshold, -30), (band.ExpRatio, 3), (band.Reduction, -40)
                    }
                )
            ]
        );
        var tone = TestWav.Sine(500, Rate, 1, 0.01f); // −40 dBFS: 10 dB under the expander threshold
        var (l, _) = Fx.Run(effect, tone);
        // 10 dB under at 3:1 is 20 dB of reduction.
        Assert.Equal(-20, ToDb(Rms(l.AsSpan(Rate / 2)) / Rms(tone.AsSpan(Rate / 2))), 2.5);
    }

    // ---- Enveloper, Exciter, SubBass ---------------------------------------------------------------------------------

    static float[] Hits(int frames) {
        var x = new float[frames];
        for (var hit = 0; hit * 12_000 < frames; hit++) {
            for (var i = 0; i < 9_000 && hit * 12_000 + i < frames; i++) {
                x[hit * 12_000 + i] = 0.5f * MathF.Sin(2 * MathF.PI * 200 * i / Rate) * MathF.Exp(-i / 3_000f);
            }
        }

        return x;
    }

    [Fact]
    public void Enveloper_boosts_attacks_and_cuts_tails() {
        var hits = Hits(48_000);
        using var attack = Fx.Create(Enveloper.Id, (Enveloper.AttackGain, 100));
        var (a, _) = Fx.Run(attack, hits);
        Assert.True(Peak(a.AsSpan(0, 2_000)) > 1.5 * Peak(hits.AsSpan(0, 2_000)));
        Assert.Equal(Rms(hits.AsSpan(6_000, 3_000)), Rms(a.AsSpan(6_000, 3_000)), 0.002);
        using var release = Fx.Create(Enveloper.Id, (Enveloper.ReleaseGain, -100));
        var (r, _) = Fx.Run(release, hits);
        Assert.True(Rms(r.AsSpan(6_000, 3_000)) < 0.5 * Rms(hits.AsSpan(6_000, 3_000)));
        Assert.Equal(Peak(hits.AsSpan(0, 500)), Peak(r.AsSpan(0, 500)), 0.02);
    }

    [Fact]
    public void Enveloper_lookahead_is_its_latency() {
        using var effect = Fx.Create(Enveloper.Id, (Enveloper.Lookahead, 5), (Enveloper.Mix, 100));
        Assert.Equal(240, effect.LatencySamples);
        var (l, _) = Fx.Run(effect, Fx.Impulse(4_000, 1_000, 0.001f));
        Assert.Equal(1_240, PeakIndex(l, 0, l.Length));
    }

    [Fact]
    public void Exciter_adds_harmonics_above_its_frequency_only() {
        using var effect = Fx.Create(Exciter.Id, (Exciter.Mix, 100), (Exciter.Frequency, 3_000), (Exciter.Drive, 24));
        var low = TestWav.Sine(200, Rate, 1, 0.5f);
        var (l, _) = Fx.Run(effect, low);
        Assert.True(NullResidualDb(low.AsSpan(Rate / 2), l.AsSpan(Rate / 2)) < -50);
        effect.Reset();
        var high = TestWav.Sine(4_000, Rate, 1, 0.5f);
        var (h, _) = Fx.Run(effect, high);
        var spectrum = Spectrum(h, Rate / 2, 16_384);
        var bin = 12_000 * 16_384 / Rate; // the third harmonic
        Assert.True(spectrum[bin].Magnitude > 1e-3 * spectrum[4_000 * 16_384 / Rate].Magnitude);
    }

    [Theory]
    [InlineData(0, 55.0)]
    [InlineData(1, 27.5)]
    public void SubBass_adds_a_tone_octaves_below(int octave, double expected) {
        using var effect = Fx.Create(SubBass.Id, (SubBass.Mix, 100), (SubBass.Frequency, 110), (SubBass.Octave, octave));
        var tone = TestWav.Sine(110, Rate, 3, 0.4f);
        var (l, _) = Fx.Run(effect, tone);
        var sub = new float[l.Length];
        for (var i = 0; i < l.Length; i++) {
            sub[i] = l[i] - tone[i];
        }

        Assert.True(Rms(sub.AsSpan(Rate)) > 0.05);
        Assert.Equal(expected, PeakFrequency(sub, Rate, Rate, 65_536), 0.5);
    }

    // ---- Delay Designer ----------------------------------------------------------------------------------------------

    [Fact]
    public void Delay_designer_taps_land_at_their_times_levels_and_pans() {
        var taps = DelayDesigner.Taps;
        using var effect = Fx.Create(
            DelayDesigner.Id,
            (DelayDesigner.TapCount, 3),
            (DelayDesigner.Mix, 100),
            (taps[0].Time, 100),
            (taps[0].Level, 0),
            (taps[0].Pan, -100),
            (taps[1].Time, 250),
            (taps[1].Level, -6),
            (taps[1].Pan, 0),
            (taps[2].Time, 400),
            (taps[2].Level, -12),
            (taps[2].Pan, 100),
            (taps[3].Time, 50) // beyond the tap count: silent
        );
        var (l, r) = Fx.Run(effect, Fx.Impulse(30_000, 0));
        Assert.Equal(1, l[4_800], 5);
        Assert.Equal(0, r[4_800], 5);
        Assert.Equal(Decibels.ToGain(-6.0) * Math.Sqrt(0.5), l[12_000], 4);
        Assert.Equal(Decibels.ToGain(-6.0) * Math.Sqrt(0.5), r[12_000], 4);
        Assert.Equal(Decibels.ToGain(-12.0), r[19_200], 4);
        Assert.Equal(0, l[19_200], 4);
        Assert.Equal(0, Peak(l.AsSpan(0, 4_700)));
        Assert.True(Peak(l.AsSpan(19_300)) < 1e-6);
    }

    [Fact]
    public void Delay_designer_feeds_its_feedback_tap_back() {
        var taps = DelayDesigner.Taps;
        using var effect = Fx.Create(
            DelayDesigner.Id,
            (DelayDesigner.TapCount, 2),
            (DelayDesigner.Mix, 100),
            (DelayDesigner.FeedbackTap, 1),
            (DelayDesigner.Feedback, 50),
            (taps[0].Time, 100),
            (taps[0].Pan, -100),
            (taps[0].Level, 0),
            (taps[1].Time, 300),
            (taps[1].Pan, 100),
            (taps[1].Level, 0)
        );
        var (l, r) = Fx.Run(effect, Fx.Impulse(60_000, 0));
        Assert.Equal(1, r[14_400], 5);
        Assert.Equal(0.5, l[14_400 + 4_800], 5); // tap A hears the fed-back repeat
        Assert.Equal(0.5, r[2 * 14_400], 5);
    }

    [Fact]
    public void Delay_designer_snaps_to_the_transport_grid() {
        var tap = DelayDesigner.Taps[0];
        using var effect = Fx.Create(
            DelayDesigner.Id,
            (DelayDesigner.TapCount, 1),
            (DelayDesigner.Mix, 100),
            (DelayDesigner.Sync, 1),
            (DelayDesigner.Grid, NoteValues.Sixteenth),
            (tap.Time, 250),
            (tap.Pan, -100),
            (tap.Level, 0)
        );
        // 120 bpm: a sixteenth is 125 ms, 250 ms stays. Then a step to 90 bpm: 166.7 ms, 250 snaps to 333.3 ms (the
        // delay glides there in about 50 ms).
        var input = new float[80_000];
        input[0] = 1;
        input[50_000] = 1;
        var run = new TransportRun(effect, input, null, TransportRun.Step(120, 90, 15_000));
        Assert.Equal(12_000, PeakIndex(run.Left, 1, 15_000));
        Assert.Equal(1, run.Left[12_000], 4);
        Assert.Equal(50_000 + 16_000, PeakIndex(run.Left, 50_001, 80_000));
        Assert.Equal(1, run.Left[66_000], 3);
    }

    // ---- Pitch -------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(12, 0, 440.0)]
    [InlineData(-12, 0, 440.0)]
    [InlineData(7, 0, 440.0)]
    [InlineData(0, 50, 330.0)]
    public void Pitch_shifter_shifts_a_tone(int semitones, int cents, double input) {
        using var effect = Fx.Create(
            PitchShifter.Id,
            (PitchShifter.Semitones, semitones),
            (PitchShifter.Cents, cents),
            (PitchShifter.Mix, 100)
        );
        var tone = TestWav.Sine(input, Rate, 3, 0.5f);
        var (l, _) = Fx.Run(effect, tone);
        var expected = input * Math.Pow(2, (semitones + cents / 100.0) / 12);
        // The heads' crossfades put sidebands at the window rate around the tone, so its spectrum peaks beside it; the
        // waveform's period (between the splices, where one head plays) is the shifted one.
        Assert.InRange(Cents(PeriodFrequency(l, Rate / 2, Rate, expected), expected), -5, 5);
        Assert.Equal(0, effect.LatencySamples);
    }

    [Fact]
    public void Pitch_shifter_at_unison_is_the_input() {
        using var effect = Fx.Create(PitchShifter.Id, (PitchShifter.Mix, 100));
        var (left, right) = Fx.Music(10_000);
        var (l, r) = Fx.Run(effect, left, right);
        Assert.Equal(-300, NullResidualDb(left, l));
        Assert.Equal(-300, NullResidualDb(right, r));
    }

    static float[] Voice(double frequency, double seconds, float amplitude = 0.4f) {
        // A tone with a few harmonics and a little vibrato-free drift, like a held sung note.
        var x = new float[(int)(seconds * Rate)];
        double phase = 0;
        for (var i = 0; i < x.Length; i++) {
            phase += frequency / Rate;
            var p = 2 * Math.PI * phase;
            x[i] = amplitude * (float)(Math.Sin(p) + 0.5 * Math.Sin(2 * p) + 0.25 * Math.Sin(3 * p)) / 1.75f;
        }

        return x;
    }

    [Theory]
    [InlineData(440.0, 35, 0, 0, 440.0)] // A4 35 ¢ sharp → A4 (chromatic)
    [InlineData(440.0, -42, 0, 0, 440.0)] // 42 ¢ flat → A4
    [InlineData(261.63, 25, 0, 0, 261.63)] // C4 sharp
    [InlineData(466.16, 30, 0, 1, 493.88)] // A♯4 30 ¢ sharp in C major → B4, 70 ¢ up
    [InlineData(196.0, -30, 7, 2, 196.0)] // G3 flat, G minor
    [InlineData(146.83, 20, 2, 1, 146.83)] // D3 sharp, D major
    public void Pitch_correction_lands_within_10_cents(
        double note,
        double detuneCents,
        int key,
        int scale,
        double expected
    ) {
        using var effect = (PitchCorrection)Fx.Create(
            PitchCorrection.Id,
            (PitchCorrection.Key, key),
            (PitchCorrection.Scale, scale),
            (PitchCorrection.Response, 0)
        );
        var input = Voice(note * Math.Pow(2, detuneCents / 1_200), 2);
        var (l, _) = Fx.Run(effect, input);
        var measured = PeakFrequency(l, Rate, Rate, 32_768);
        Assert.InRange(Cents(measured, expected), -10, 10);
        Assert.True(Rms(l.AsSpan(Rate)) > 0.5 * Rms(input.AsSpan(Rate)));
    }

    [Fact]
    public void Pitch_correction_detune_offsets_the_target() {
        using var effect = Fx.Create(PitchCorrection.Id, (PitchCorrection.Response, 0), (PitchCorrection.Detune, 30));
        var (l, _) = Fx.Run(effect, Voice(440 * Math.Pow(2, -20 / 1_200.0), 2));
        Assert.InRange(Cents(PeakFrequency(l, Rate, Rate, 32_768), 440), 20, 40);
    }

    [Fact]
    public void Pitch_correction_passes_its_input_at_its_latency() {
        using var effect = Fx.Create(PitchCorrection.Id);
        var latency = effect.LatencySamples;
        Assert.Equal(2 * 600 + 2, latency);
        // An impulse is unvoiced: the grains put it back where it was, one latency later.
        var (l, _) = Fx.Run(effect, Fx.Impulse(8_000, 1_000, 0.5f));
        Assert.Equal(1_000 + latency, PeakIndex(l, 0, l.Length));
        Assert.Equal(0.5, l[1_000 + latency], 5);
        // An in-tune note keeps its pitch and level.
        effect.Reset();
        var a = Voice(440, 2);
        var (y, _) = Fx.Run(effect, a);
        Assert.InRange(Cents(PeakFrequency(y, Rate, Rate, 32_768), 440), -2, 2);
        Assert.Equal(0, ToDb(Rms(y.AsSpan(Rate, Rate / 2)) / Rms(a.AsSpan(Rate, Rate / 2))), 1);
    }

    // ---- Ensemble, Rotor Cabinet ---------------------------------------------------------------------------------------

    [Fact]
    public void Ensemble_spreads_an_impulse_around_its_delay() {
        using var effect = Fx.Create(Ensemble.Id, (Ensemble.Mix, 100), (Ensemble.Delay, 15), (Ensemble.Voices, 8));
        var (l, r) = Fx.Run(effect, Fx.Impulse(4_000, 0));
        // The voices land within Delay ± (4 + 3) ms, and not all in the same place on both sides.
        Assert.Equal(0, Peak(l.AsSpan(0, (int)(0.007 * Rate))));
        Assert.True(Peak(l.AsSpan((int)(0.007 * Rate), (int)(0.016 * Rate))) > 0.05);
        Assert.Equal(0, Peak(l.AsSpan((int)(0.0225 * Rate))), 4);
        Assert.True(NullResidualDb(l, r) > -40);
    }

    [Fact]
    public void Rotor_cabinet_turns_at_its_speeds() {
        using var effect = (RotorCabinet)Fx.Create(RotorCabinet.Id, (RotorCabinet.Speed, 2));
        Fx.Run(effect, new float[Rate / 10]);
        Assert.Equal(6.8, effect.Rates.Horn, 6);
        effect.SetParameter(RotorCabinet.Speed, 0);
        Fx.Run(effect, new float[(int)(0.8 * Rate)]);
        // One time constant (0.8 s) later the horn has lost 63 % of its speed; the drum, three times slower, 23 %.
        Assert.Equal(6.8 / Math.E, effect.Rates.Horn, 0.05);
        Assert.Equal(5.9 * Math.Exp(-1 / 3.0), effect.Rates.Drum, 0.05);

        // Fast: the horn's tremolo, at the horn's rate, on a treble tone.
        using var fast = Fx.Create(RotorCabinet.Id, (RotorCabinet.Speed, 2), (RotorCabinet.Balance, -100));
        var (l, _) = Fx.Run(fast, TestWav.Sine(3_000, Rate, 2, 0.3f));
        var envelope = new float[l.Length / 240];
        for (var b = 0; b < envelope.Length; b++) {
            envelope[b] = (float)Rms(l.AsSpan(b * 240, 240));
        }

        var mean = envelope.Skip(100).Average();
        var wobble = envelope.Skip(100).Select(e => e - (float)mean).ToArray();
        Assert.True(Rms(wobble) > 0.02 * mean);
        Assert.Equal(6.8, ZeroCrossingFrequency(wobble, 0, wobble.Length, Rate / 240.0), 0.4);
    }

    // ---- Tempo-synced: Phat FX, Step FX, Beat Breaker, Remix FX, and the T1 delays --------------------------------------

    public static TheoryData<string> TempoMaps => ["step", "ramp"];

    static Func<long, double> Map(string kind) =>
        kind == "step" ? TransportRun.Step(120, 87, 50_000) : TransportRun.Ramp(140, 75, 140_000);

    [Theory]
    [MemberData(nameof(TempoMaps))]
    public void Phat_fx_lfo_follows_the_song_position(string kind) {
        using var effect = (PhatFx)Fx.Create(
            PhatFx.Id,
            (PhatFx.Cutoff, 1_000),
            (PhatFx.LfoDepth, 50),
            (PhatFx.LfoNote, NoteValues.Quarter)
        );
        var map = Map(kind);
        var cutoffs = new List<(double Quarter, double Hz)>();
        var quarter = 0.37;
        for (var at = 0; at < 150_000; at += AudioEffect.ControlInterval) {
            var bpm = map(at);
            effect.SetTransport(new(true, at, quarter, bpm, 4, 4, 0, Fx.Rate, AudioEffect.ControlInterval));
            effect.Process(new float[AudioEffect.ControlInterval], new float[AudioEffect.ControlInterval], default, default);
            cutoffs.Add((quarter, effect.CurrentCutoff));
            quarter += AudioEffect.ControlInterval * bpm / (60 * Fx.Rate);
        }

        foreach (var (q, hz) in cutoffs) {
            Assert.Equal(1_000 * Math.Pow(2, 2 * Math.Sin(2 * Math.PI * q)), hz, 1e-6 * hz);
        }
    }

    [Theory]
    [MemberData(nameof(TempoMaps))]
    public void Step_fx_steps_land_on_the_grid(string kind) {
        var parameters = new List<(int, float)> {
            (StepFx.FilterOn, 0), (StepFx.PanOn, 0), (StepFx.Smoothing, 0), (StepFx.Rate, NoteValues.Sixteenth)
        };
        for (var s = 0; s < StepFx.MaxSteps; s++) {
            parameters.Add((StepFx.VolumeSteps[s], s % 2 == 0 ? 100 : 0));
        }

        using var effect = Fx.Create(StepFx.Id, [..parameters]);
        var run = new TransportRun(effect, TestWav.Constant(0.5f, 150_000), null, Map(kind), 0.1);
        var checkedEdges = 0;
        for (var k = 1; k * 0.25 < run.EndQuarter - 0.25; k++) {
            var s = run.SampleOf(k * 0.25);
            var on = k % 2 == 0 ? 0.5f : 0f;
            var first = (int)Math.Ceiling(s - 1e-6);
            if (first - 1 < 0) {
                continue;
            }

            Assert.Equal(on, run.Left[first]);
            Assert.Equal(0.5f - on, run.Left[first - 1]);
            checkedEdges++;
        }

        Assert.True(checkedEdges > 15);
    }

    [Fact]
    public void Step_fx_runs_free_at_its_tempo_while_stopped() {
        var parameters = new List<(int, float)> {
            (StepFx.FilterOn, 0), (StepFx.Smoothing, 0), (StepFx.Rate, NoteValues.Eighth), (StepFx.Tempo, 100)
        };
        for (var s = 0; s < StepFx.MaxSteps; s++) {
            parameters.Add((StepFx.VolumeSteps[s], s % 2 == 0 ? 100 : 0));
        }

        using var effect = Fx.Create(StepFx.Id, [..parameters]);
        var (l, _) = Fx.Run(effect, TestWav.Constant(0.5f, 60_000));
        // An eighth at 100 bpm is 14,400 samples, from the reset.
        Assert.Equal(0.5f, l[14_399]);
        Assert.Equal(0f, l[14_400]);
        Assert.Equal(0.5f, l[28_800]);
    }

    [Theory]
    [MemberData(nameof(TempoMaps))]
    public void Beat_breaker_slices_land_on_the_grid(string kind) {
        var slices = BeatBreaker.SliceParameterList;
        var parameters = new List<(int, float)> { (BeatBreaker.Length, 2), (BeatBreaker.Fade, 0.5f) };
        for (var k = 1; k < BeatBreaker.Slices; k += 2) {
            parameters.Add((slices[k].Mute, 1));
        }

        using var effect = Fx.Create(BeatBreaker.Id, [..parameters]);
        var run = new TransportRun(effect, TestWav.Constant(0.5f, 150_000), null, Map(kind), 0.6);
        var checkedEdges = 0;
        for (var k = 3; k * 0.25 < run.EndQuarter - 0.25; k++) {
            var s = run.SampleOf(k * 0.25);
            // Slice k of the 4-beat pattern: odd ones are muted; the open ones fade over 24 samples beside them.
            var muted = k % 2 == 1;
            var start = (int)Math.Ceiling(s - 1e-6);
            Assert.Equal(muted ? 0 : 0.5f, run.Left[start + 30], 4);
            Assert.Equal(muted ? 0.5f : 0, run.Left[start - 30], 4);
            Assert.Equal(muted ? 0 : 0.5 * Math.Min(1, (start + 10 - s) / 24), run.Left[start + 10], 4);
            checkedEdges++;
        }

        Assert.True(checkedEdges > 10);
    }

    [Fact]
    public void Beat_breaker_plays_a_slice_from_its_source() {
        var slices = BeatBreaker.SliceParameterList;
        using var effect = Fx.Create(
            BeatBreaker.Id,
            (BeatBreaker.Length, 2),
            (slices[2].Source, 1), // slice 3 plays slice 1, from this pass: 2 slices ago
            (slices[4].Source, 9), // slice 5 plays slice 9, from the previous pass
            (slices[6].Reverse, 1)
        );
        var ramp = Enumerable.Range(0, 200_000).Select(static i => i * 1e-6f).ToArray();
        // 120 bpm: a 4-beat pattern is 96,000 samples, a slice 6,000.
        var run = new TransportRun(effect, ramp, null, _ => 120);
        Assert.Equal(ramp[96_000 + 12_500 - 12_000], run.Left[96_000 + 12_500], 6);
        Assert.Equal(ramp[96_000 + 24_500 - 96_000 + 24_000], run.Left[96_000 + 24_500], 6);
        // Reversed: 500 samples into slice 7 plays 500 samples before its end, of the previous pass (this pass's end is
        // still to come).
        Assert.Equal(ramp[36_000 + 6_000 - 500], run.Left[96_000 + 36_000 + 500], 5);
        Assert.Equal(ramp[96_000 + 20_000], run.Left[96_000 + 20_000]);
    }

    static RemixFx Remix(params (int, float)[] parameters) => (RemixFx)Fx.Create(RemixFx.Id, parameters);

    [Theory]
    [MemberData(nameof(TempoMaps))]
    public void Remix_gater_chops_on_the_grid(string kind) {
        using var effect = Remix(
            (RemixFx.Pad2Active, 1),
            (RemixFx.Pad2Effect, RemixFx.Gater),
            (RemixFx.Pad2X, 0.6f),
            (RemixFx.Pad2Y, 1)
        );
        var run = new TransportRun(effect, TestWav.Constant(0.5f, 150_000), null, Map(kind), 0.2);
        var checkedEdges = 0;
        for (var k = 2; k * 0.125 < run.EndQuarter - 0.125; k++) {
            var s = run.SampleOf(k * 0.125);
            var opening = k % 2 == 0;
            // The gate moves by a 1 ms one-pole from the first sample at or after the edge: half way 33.3 samples on.
            var cross = (int)Math.Ceiling(s - 1e-6);
            while (cross < run.Left.Length && (opening ? run.Left[cross] < 0.25f : run.Left[cross] > 0.25f)) {
                cross++;
            }

            Assert.InRange(cross - s, 32, 36);
            checkedEdges++;
        }

        Assert.True(checkedEdges > 30);
    }

    [Theory]
    [InlineData(120.0, 12_000)]
    [InlineData(100.0, 14_400)]
    public void Remix_repeater_loops_the_grid_division_it_was_touched_in(double bpm, int loop) {
        using var effect = Remix((RemixFx.Pad2Effect, RemixFx.Repeater), (RemixFx.Pad2X, 0.5f), (RemixFx.Pad2Y, 1));
        var ramp = Enumerable.Range(0, 120_000).Select(static i => i * 1e-5f).ToArray();
        const int touch = 30_016;
        var run = new TransportRun(
            effect,
            ramp,
            null,
            _ => bpm,
            before: (at, e) => {
                if (at == touch) {
                    e.SetParameter(RemixFx.Pad2Active, 1);
                }
            }
        );
        Assert.Equal(loop, ((RemixFx)effect).RepeatLength);
        // The eighth that began before the touch.
        var start = (int)(Math.Floor(touch / (double)loop) * loop);
        for (var t = start + loop; t < start + 4 * loop; t += 97) {
            var into = (t - start) % loop;
            if (into < 96) {
                continue; // the crossfade from the pass before
            }

            Assert.Equal(ramp[start + into], run.Left[t], 6);
        }

        Assert.Equal(-300, NullResidualDb(ramp.AsSpan(0, touch), run.Left.AsSpan(0, touch)));
    }

    [Fact]
    public void Remix_fx_is_transparent_until_a_pad_is_touched_and_fades_back() {
        using var effect = Remix((RemixFx.Pad1Effect, RemixFx.Filter), (RemixFx.Pad1X, 0.1f));
        var (left, right) = Fx.Music(30_000);
        var (l, _) = Fx.Run(effect, left, right);
        Assert.Equal(-300, NullResidualDb(left, l));
        effect.SetParameter(RemixFx.Pad1Active, 1);
        var (touched, _) = Fx.Run(effect, left, right);
        Assert.True(NullResidualDb(left, touched) > ToDb(Rms(left)) - 6);
        effect.SetParameter(RemixFx.Pad1Active, 0);
        var (released, _) = Fx.Run(effect, left, right);
        // 5 ms to fade out (to −80 dB in about 50 ms), then the input again.
        Assert.Equal(-300, NullResidualDb(left.AsSpan(4_000), released.AsSpan(4_000)));
    }

    [Theory]
    [InlineData(RemixFx.Wobble)]
    [InlineData(RemixFx.Orbit)]
    public void Remix_pad_one_effects_change_the_sound(int pad) {
        using var effect = Remix((RemixFx.Pad1Active, 1), (RemixFx.Pad1Effect, pad), (RemixFx.Pad1Y, 0.8f));
        var noise = Noise(48_000, 9);
        var (l, _) = Fx.Run(effect, noise);
        Assert.True(NullResidualDb(noise, l) > -20);
        Assert.All(l, static s => Assert.True(float.IsFinite(s)));
    }

    [Theory]
    [InlineData(RemixFx.Reverse)]
    [InlineData(RemixFx.Scratch)]
    [InlineData(RemixFx.Downsampler)]
    [InlineData(RemixFx.TapeStop)]
    public void Remix_pad_two_effects_change_the_sound(int pad) {
        using var effect = Remix((RemixFx.Pad2Active, 1), (RemixFx.Pad2Effect, pad), (RemixFx.Pad2X, 0.7f));
        var (left, right) = Fx.Music(72_000);
        var (l, _) = Fx.Run(effect, left, right);
        Assert.True(NullResidualDb(left.AsSpan(24_000), l.AsSpan(24_000)) > -20);
        Assert.All(l, static s => Assert.True(float.IsFinite(s)));
    }

    [Fact]
    public void Remix_tape_stop_slows_to_silence() {
        using var effect = Remix(
            (RemixFx.Pad2Active, 1),
            (RemixFx.Pad2Effect, RemixFx.TapeStop),
            (RemixFx.Pad2X, 0.3f),
            (RemixFx.Pad2Y, 1)
        );
        // X 0.3 picks 2 beats: one second at 120 bpm.
        var tone = TestWav.Sine(1_000, Rate, 2, 0.5f);
        var (l, _) = Fx.Run(effect, tone);
        Assert.Equal(0, Peak(l.AsSpan(Rate + 10)), 6);
        Assert.True(Peak(l.AsSpan(Rate / 4, 2_000)) > 0.3);
        // Slowing: the pitch falls (a quarter of the way in, three quarters of the speed is left).
        Assert.InRange(ZeroCrossingFrequency(l, Rate / 4 - 1_000, 2_000, Rate), 600, 900);
    }

    [Fact]
    public void T1_delays_take_the_tempo_from_the_transport() {
        using var delay = Fx.Create(StereoDelay.Id, (StereoDelay.Mix, 100), (StereoDelay.LeftFeedback, 0));
        // The tempo parameter says 120; the transport says 100: an eighth is 0.3 s.
        var run = new TransportRun(delay, Fx.Impulse(30_000), null, _ => 100);
        Assert.Equal(14_400, PeakIndex(run.Left, 1, 30_000));
        using var tape = Fx.Create(TapeDelay.Id, (TapeDelay.Mix, 100), (TapeDelay.Sync, 1), (TapeDelay.Wow, 0), (TapeDelay.Flutter, 0));
        var tapeRun = new TransportRun(tape, Fx.Impulse(40_000), null, _ => 90);
        Assert.InRange(PeakIndex(tapeRun.Left, 1, 40_000), 24_000 - 3, 24_000 + 3); // a dotted eighth

        using var tremolo = (Tremolo)Fx.Create(Tremolo.Id, (Tremolo.Sync, 1), (Tremolo.Note, NoteValues.Quarter));
        _ = new TransportRun(tremolo, new float[256], null, _ => 90);
        Assert.Equal(1.5, tremolo.RateHz, 9);
        using var unsynced = (Tremolo)Fx.Create(Tremolo.Id, (Tremolo.Sync, 1), (Tremolo.Note, NoteValues.Quarter));
        Fx.Run(unsynced, new float[256]);
        Assert.Equal(2, unsynced.RateHz, 9); // no transport: the tempo parameter's 120
    }
}
