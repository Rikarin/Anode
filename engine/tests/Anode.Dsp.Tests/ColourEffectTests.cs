using Anode.Dsp.Effects.Drive;
using Anode.Dsp.Effects.Filter;
using Anode.Dsp.Effects.Metering;
using Anode.Dsp.Effects.Modulation;
using Anode.Dsp.Effects.Utility;
using Anode.Dsp.Tests.Support;
using static Anode.Dsp.Tests.Support.Analysis;

namespace Anode.Dsp.Tests;

/// <summary>Modulation, drive, the auto filter, the gain utility and the meters.</summary>
public sealed class ColourEffectTests {
    static float[] Tone(double hz = 1_000, double seconds = 1, float amplitude = 0.5f) =>
        TestWav.Sine(hz, 48_000, seconds, amplitude);

    [Fact]
    public void Gain_inverts_swaps_balances_and_sums_to_mono() {
        var left = TestWav.Constant(0.5f, 256);
        var right = TestWav.Constant(0.25f, 256);
        using (var gain = Fx.Create(Gain.Id, (Gain.Level, -6.0206f), (Gain.InvertLeft, 1))) {
            var (l, r) = Fx.Run(gain, left, right);
            Assert.Equal(-0.25, l[^1], 5);
            Assert.Equal(0.125, r[^1], 5);
        }

        using (var swap = Fx.Create(Gain.Id, (Gain.Swap, 1), (Gain.Balance, 50))) {
            var (l, r) = Fx.Run(swap, left, right);
            Assert.Equal(0.125, l[^1], 5); // swapped, then the left at half
            Assert.Equal(0.5, r[^1], 5);
        }

        using (var mono = Fx.Create(Gain.Id, (Gain.Mono, 1))) {
            var (l, r) = Fx.Run(mono, left, right);
            Assert.Equal(0.375, l[^1], 5);
            Assert.Equal(0.375, r[^1], 5);
        }

        using var silent = Fx.Create(Gain.Id, (Gain.Level, -96));
        Assert.Equal(0, Peak(Fx.Run(silent, left).Left));
    }

    [Fact]
    public void Gain_changes_are_smoothed() {
        using var gain = Fx.Create(Gain.Id);
        var input = TestWav.Constant(1, 24_000);
        Fx.Run(gain, input);
        gain.SetParameter(Gain.Level, -20);
        var (l, _) = Fx.Run(gain, input);
        // No step bigger than a ramp's: the 20 ms smoothing spreads the change over many control intervals.
        for (var i = 1; i < l.Length; i++) {
            Assert.True(Math.Abs(l[i] - l[i - 1]) < 0.01, $"{i}");
        }

        Assert.Equal(0.1, l[^1], 3);
    }

    [Fact]
    public void Tremolo_swings_by_its_depth_at_its_rate() {
        using var tremolo = Fx.Create(Tremolo.Id, (Tremolo.Rate, 5), (Tremolo.Depth, 60), (Tremolo.Phase, 180));
        var (l, r) = Fx.Run(tremolo, TestWav.Constant(1, 48_000));
        Assert.Equal(1, l.Max(), 3);
        Assert.Equal(0.4, l.Min(), 3);
        // One cycle per 200 ms: the left is at its peak a quarter cycle in, the right (180° on) at its trough.
        Assert.Equal(1, l[2_400], 3);
        Assert.Equal(0.4, r[2_400], 3);

        using var synced = Fx.Create(
            Tremolo.Id,
            (Tremolo.Sync, 1),
            (Tremolo.Tempo, 120),
            (Tremolo.Note, 7) // eighths: 4 Hz at 120 bpm
        );
        Assert.Equal(4, ((Tremolo)synced).RateHz, 6);
    }

    [Fact]
    public void Chorus_and_flanger_move_their_delay() {
        // A chorus with no dry signal shifts a steady tone's pitch up and down around it (the Doppler of a moving
        // delay); at zero depth it is a plain delay.
        using var chorus = Fx.Create(Chorus.Id, (Chorus.Mix, 100), (Chorus.Rate, 2), (Chorus.Depth, 100));
        var (wet, _) = Fx.Run(chorus, Tone(seconds: 2));
        var rates = Enumerable.Range(0, 15)
            .Select(k => ZeroCrossingFrequency(wet, 4_800 + k * 4_800, 1_200, 48_000))
            .ToArray();
        Assert.True(rates.Max() > 1_010 && rates.Min() < 990, $"{rates.Min()} … {rates.Max()}");

        using var still = Fx.Create(Chorus.Id, (Chorus.Mix, 100), (Chorus.Depth, 0), (Chorus.Delay, 10));
        var (echo, _) = Fx.Run(still, Fx.Impulse(2_000));
        Assert.Equal(1, echo[480], 5);

        // A flanger at 50 % mix makes notches; their depth shows as a comb in the response of a short burst.
        using var flanger = Fx.Create(Flanger.Id, (Flanger.Depth, 0), (Flanger.Delay, 1), (Flanger.Feedback, 0));
        var (comb, _) = Fx.Run(flanger, Fx.Impulse(4_096));
        // 1 ms delay: notches at 500 Hz, 1.5 kHz …; peaks at 1 kHz, 2 kHz …
        Assert.True(ToDb(MagnitudeAt(comb, 500, 48_000)) < -30);
        Assert.Equal(0, ToDb(MagnitudeAt(comb, 1_000, 48_000)), 0.1);
    }

    [Fact]
    public void Phaser_notches_sit_where_its_allpasses_put_them() {
        // Four first-order allpasses at f each shift by 4 · 2 atan(f'/f); at mix 50 % the sum cancels where that is
        // 180°: at f' = f tan(22.5°) and f' = f tan(67.5°).
        using var phaser = Fx.Create(
            Phaser.Id,
            (Phaser.Depth, 0),
            (Phaser.Center, 1_000),
            (Phaser.Feedback, 0),
            (Phaser.Stages, 1)
        );
        var (ir, _) = Fx.Run(phaser, Fx.Impulse(16_384));
        var warp = Math.Tan(Math.PI * 1_000 / 48_000);
        double Notch(double degrees) => Math.Atan(warp * Math.Tan(degrees * Math.PI / 180)) * 48_000 / Math.PI;
        Assert.True(ToDb(MagnitudeAt(ir, Notch(22.5), 48_000)) < -40);
        Assert.True(ToDb(MagnitudeAt(ir, Notch(67.5), 48_000)) < -40);
        Assert.Equal(0, ToDb(MagnitudeAt(ir, 1_000, 48_000)), 0.1);
    }

    [Fact]
    public void Drives_add_harmonics_and_stay_band_limited() {
        foreach (var (id, shapes) in new[] {
                     (Overdrive.Id, new[] { 0 }),
                     (Distortion.Id, new[] { Distortion.Hard, Distortion.Fuzz, Distortion.Fold })
                 }) {
            foreach (var shape in shapes) {
                using var drive = Fx.Create(id, (Overdrive.Tone, 16_000));
                if (id == Distortion.Id) {
                    drive.SetParameter(Distortion.Type, shape);
                    drive.SetParameter(Distortion.Tone, 16_000);
                }

                // 3 kHz on an exact bin: the 3rd harmonic is at 9 kHz; aliases of the 9th (27 kHz) would land at 21 kHz.
                const int size = 16_384;
                const double hz = 48_000.0 / size * 1_024;
                var (output, _) = Fx.Run(drive, Tone(hz));
                var spectrum = Spectrum(output, 24_000, size);
                var fundamental = spectrum[1_024].Magnitude;
                Assert.True(spectrum[3 * 1_024].Magnitude > 0.01 * fundamental, $"{id} {shape}: no 3rd harmonic");
                // Off the harmonic grid (where only aliases could be), far below the fundamental.
                double worst = 0;
                for (var k = 600; k < size / 2 - 600; k++) {
                    if (Math.Abs(k % 1_024 - 512) < 500 && k % 1_024 is > 12 and < 1_012) {
                        worst = Math.Max(worst, spectrum[k].Magnitude);
                    }
                }

                Assert.True(
                    ToDb(worst / fundamental) < -60,
                    $"{id} {shape}: aliasing at {ToDb(worst / fundamental):0.0} dB"
                );
            }
        }
    }

    [Fact]
    public void Overdrive_is_asymmetric_and_level_sets_the_output() {
        using var drive =
            Fx.Create(Overdrive.Id, (Overdrive.Drive, 30), (Overdrive.Level, 0), (Overdrive.Tone, 16_000));
        var (output, _) = Fx.Run(drive, Tone());
        var spectrum = Spectrum(output, 24_000, 16_384);
        var bin = 1_000 * 16_384 / 48_000.0;
        Assert.True(spectrum[(int)Math.Round(2 * bin)].Magnitude > 0.003 * spectrum[(int)Math.Round(bin)].Magnitude);
        using var quiet =
            Fx.Create(Overdrive.Id, (Overdrive.Drive, 30), (Overdrive.Level, -12), (Overdrive.Tone, 16_000));
        var (lower, _) = Fx.Run(quiet, Tone());
        Assert.Equal(-12, ToDb(Rms(lower.AsSpan(24_000)) / Rms(output.AsSpan(24_000))), 0.01);
    }

    [Fact]
    public void Bitcrusher_quantizes_and_holds() {
        using var crusher = Fx.Create(Bitcrusher.Id, (Bitcrusher.Bits, 3), (Bitcrusher.Downsample, 4));
        var (output, _) = Fx.Run(crusher, Tone(100, 0.2, 0.9f));
        // 3 bits: steps of 2/8, held for four samples at a time.
        Assert.All(output, static s => Assert.Equal(0, s / 0.25f % 1, 4));
        for (var i = 0; i + 4 <= output.Length; i += 4) {
            Assert.Equal(output[i], output[i + 3]);
        }
    }

    [Fact]
    public void Auto_filter_sweeps_its_cutoff_with_the_lfo_and_the_envelope() {
        using var filter = (AutoFilter)Fx.Create(
            AutoFilter.Id,
            (AutoFilter.Cutoff, 1_000),
            (AutoFilter.LfoDepth, 50),
            (AutoFilter.LfoRate, 1)
        );
        var cutoffs = new List<double>();
        var block = new float[480];
        for (var i = 0; i < 100; i++) {
            filter.Process(block, (float[])block.Clone(), default, default);
            cutoffs.Add(filter.CurrentCutoff);
        }

        // ±2 octaves around 1 kHz.
        Assert.Equal(4_000, cutoffs.Max(), 40.0);
        Assert.Equal(250, cutoffs.Min(), 3.0);

        using var follower = (AutoFilter)Fx.Create(
            AutoFilter.Id,
            (AutoFilter.Cutoff, 500),
            (AutoFilter.LfoDepth, 0),
            (AutoFilter.EnvelopeAmount, 50)
        );
        Fx.Run(follower, TestWav.Constant(1, 9_600));
        Assert.Equal(2_000, follower.CurrentCutoff, 1.0);

        // At rest, the 24 dB low-pass at its cutoff: −3 dB there (no resonance), −24 dB an octave above … roughly.
        using var still = Fx.Create(AutoFilter.Id, (AutoFilter.LfoDepth, 0), (AutoFilter.Resonance, 0));
        var (ir, _) = Fx.Run(still, Fx.Impulse(16_384));
        Assert.Equal(-3, ToDb(MagnitudeAt(ir, 1_000, 48_000)), 0.1);
        Assert.True(ToDb(MagnitudeAt(ir, 4_000, 48_000)) < -45);
    }

    [Fact]
    public void Level_meter_reads_peak_and_rms() {
        using var meter = (LevelMeter)Fx.Create(LevelMeter.Id);
        var left = Tone(1_000, 0.5);
        var right = Tone(1_000, 0.5, 0.25f);
        var (l, r) = Fx.Run(meter, left, right);
        Assert.Equal(left, l);
        Assert.Equal(right, r);
        Assert.Equal(-6.02, meter.Reading, 0.02);
        Assert.Equal(-12.04, meter.RightDb, 0.02);
        meter.SetParameter(LevelMeter.Mode, 1);
        var longer = Tone(1_000, 4);
        Fx.Run(meter, longer, longer);
        Assert.Equal(-6.02, meter.Reading, 0.05);

        // In silence the peak falls at its rate: 20 dB/s, so 2 dB after 100 ms.
        meter.SetParameter(LevelMeter.Mode, 0);
        Fx.Run(meter, left, right);
        Fx.Run(meter, new float[4_800]);
        Assert.Equal(-8.02, meter.Reading, 0.05);
    }

    [Fact]
    public void Loudness_meter_reads_lufs() {
        using var meter = (LoudnessMeterEffect)Fx.Create(LoudnessMeterEffect.Id);
        // A 1 kHz sine at −18 dBFS in both channels is −18 LUFS: K-weighting is +0.69 dB at 1 kHz, which −0.691 offsets,
        // and two channels of mean square A²/2 sum to A².
        var tone = Tone(1_000, 4, (float)Math.Pow(10, -18 / 20.0));
        Fx.Run(meter, tone, tone);
        Assert.Equal(-18, meter.Momentary, 0.05);
        Assert.Equal(-18, meter.ShortTerm, 0.05);
        Assert.Equal(-18, meter.Integrated, 0.05);
        Assert.Equal(meter.Momentary, meter.Reading);
        meter.SetParameter(LoudnessMeterEffect.Shows, 2);
        Fx.Run(meter, tone, tone);
        Assert.Equal(meter.Integrated, meter.Reading);
        meter.Reset();
        Fx.Run(meter, new float[48_000]);
        Assert.Equal(-120, meter.Reading);
    }

    [Fact]
    public void Multimeter_reads_correlation_and_balance() {
        using var meter = (MultiMeter)Fx.Create(MultiMeter.Id);
        var tone = Tone(440);
        Fx.Run(meter, tone, tone);
        Assert.Equal(1, meter.Reading, 3);
        Assert.Equal(0, meter.Balance, 3);
        meter.Reset();
        Fx.Run(meter, tone, [..tone.Select(static s => -0.5f * s)]);
        Assert.Equal(-1, meter.Reading, 3);
        Assert.Equal(-6.02, meter.Balance, 0.05);
        meter.Reset();
        Fx.Run(meter, tone, Tone(440 * 1.618));
        Assert.Equal(0, meter.Reading, 1);
    }
}
