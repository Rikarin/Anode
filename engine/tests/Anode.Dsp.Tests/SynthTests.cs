using Anode.Dsp.Instruments;
using Anode.Dsp.Instruments.Synth;
using Anode.Dsp.Tests.Support;
using P = Anode.Dsp.Instruments.Synth.SynthParameters;
using static Anode.Dsp.Tests.Support.Analysis;

namespace Anode.Dsp.Tests;

public class SynthTests {
    const float Rate = Play.Rate;

    /// <summary>A pure tone: oscillator 1 alone, the filter wide open and still, a flat envelope, no modulation.</summary>
    static IInstrument Tone(int wave = 3, params (int, float)[] more) =>
        Play.Create(
            AnodeSynth.Id,
            [
                (P.Osc1Wave, wave), (P.OscMix, 0), (P.Cutoff, 20_000), (P.KeyTracking, 0), (P.FilterEnvAmount, 0),
                (P.Resonance, 0), (P.AmpAttack, 0), (P.AmpSustain, 100), (P.LfoPitch, 0), ..more
            ]
        );

    public static TheoryData<int> KeyboardNotes =>
        [21, 24, 30, 36, 43, 48, 55, 60, 64, 69, 72, 79, 84, 91, 96, 103, 108];

    [Theory]
    [MemberData(nameof(KeyboardNotes))]
    public void Sine_is_in_tune_within_a_cent_across_the_keyboard(int note) {
        using var synth = Tone();
        var (left, _) = Play.Note(synth, note, 45_000, 45_000);
        var cents = Cents(PeakFrequency(left, Rate, 4_000), NoteFrequency(note));
        Assert.True(Math.Abs(cents) < 1, $"note {note}: {cents:0.000} cents");
    }

    [Theory]
    [InlineData(0, 33)]
    [InlineData(0, 57)]
    [InlineData(1, 45)]
    [InlineData(1, 81)]
    [InlineData(2, 40)]
    [InlineData(2, 76)]
    public void Saw_pulse_and_triangle_are_in_tune(int wave, int note) {
        using var synth = Tone(wave);
        var (left, _) = Play.Note(synth, note, 45_000, 45_000);
        var cents = Cents(PeakFrequency(left, Rate, 4_000), NoteFrequency(note));
        Assert.True(Math.Abs(cents) < 1, $"wave {wave}, note {note}: {cents:0.000} cents");
    }

    [Fact]
    public void Octave_and_fine_tune_move_the_pitch() {
        using var synth = Tone(3, (P.Osc1Octave, -1), (P.Osc1Fine, 25));
        var (left, _) = Play.Note(synth, 69, 45_000, 45_000);
        var cents = Cents(PeakFrequency(left, Rate, 4_000), NoteFrequency(57.25));
        Assert.True(Math.Abs(cents) < 1, $"{cents:0.000} cents");
    }

    [Theory]
    [InlineData(8191, 2, 2.0)]
    [InlineData(-8192, 2, -2.0)]
    [InlineData(4096, 12, 6.0)]
    public void Pitch_bend_reaches_its_range(int bend, int range, double semitones) {
        using var synth = Tone(3, (P.BendRange, range));
        var (left, _) = Play.Render(synth, 45_000, [TimedEvent.Bend(0, bend), TimedEvent.On(0, 60)]);
        var cents = Cents(PeakFrequency(left, Rate, 4_000), NoteFrequency(60 + semitones));
        Assert.True(Math.Abs(cents) < 1, $"{cents:0.000} cents");
    }

    [Fact]
    public void Output_is_band_limited() {
        // A high saw: what aliases folds between the harmonics; PolyBLEP keeps it well below the fundamental.
        using var synth = Tone(0);
        var (left, _) = Play.Note(synth, 100, 20_000, 20_000); // 2637 Hz
        var spectrum = Spectrum(left, 2_000, 16_384);
        var bin = NoteFrequency(100) * 16_384 / Rate;
        double fundamental = 0, worstAlias = 0;
        for (var k = 2; k < 8_192; k++) {
            var harmonic = k / bin;
            var m = spectrum[k].Magnitude;
            if (Math.Abs(harmonic - Math.Round(harmonic)) * bin < 4) {
                if (Math.Round(harmonic) == 1) {
                    fundamental = Math.Max(fundamental, m);
                }
            } else {
                worstAlias = Math.Max(worstAlias, m);
            }
        }

        Assert.True(ToDb(worstAlias / fundamental) < -30, $"alias at {ToDb(worstAlias / fundamental):0.0} dB");
    }

    [Fact]
    public void Amp_envelope_attack_and_release_take_their_times() {
        using var synth = Tone(3, (P.AmpAttack, 100), (P.AmpRelease, 200));
        var (left, _) = Play.Note(synth, 69, 48_000, 24_000);
        var level = Rms(left.AsSpan(9_000, 2_400)); // 190–240 ms: full level
        Assert.InRange(Rms(left.AsSpan(2_400 - 240, 480)) / level, 0.45, 0.55); // half way through the attack
        Assert.InRange(Rms(left.AsSpan(4_800 + 240, 960)) / level, 0.95, 1.01); // just after it

        // The release is a T60: 60 dB down 200 ms after the key goes up, and the voice ends soon after.
        var released = Rms(left.AsSpan(24_000 + 9_600 - 240, 480));
        Assert.InRange(ToDb(released / level), -63, -57);
        Assert.Equal(0, synth is AnodeSynth s ? s.ActiveVoices : -1);
    }

    [Fact]
    public void Decay_settles_on_the_sustain_level() {
        using var synth = Tone(3, (P.AmpDecay, 100), (P.AmpSustain, 50));
        using var held = Tone();
        var (left, _) = Play.Note(synth, 69, 24_000, 24_000);
        var (full, _) = Play.Note(held, 69, 24_000, 24_000);
        Assert.InRange(
            Rms(left.AsSpan(4_800 - 240, 480)) / Rms(full.AsSpan(4_800 - 240, 480)),
            0.5005,
            0.5007
        ); // a T60
        Assert.InRange(Rms(left.AsSpan(20_000, 2_400)) / Rms(full.AsSpan(20_000, 2_400)), 0.4999, 0.5001);
    }

    [Fact]
    public void Velocity_scales_the_level_by_its_sensitivity() {
        using var full = Tone(3, (P.VelocitySensitivity, 100));
        using var none = Tone(3, (P.VelocitySensitivity, 0));
        var loud = Rms(Play.Note(full, 69, 4_800, 4_800, 127).Left);
        var soft = Rms(Play.Note(full, 69, 4_800, 4_800, 64).Left);
        Assert.InRange(soft / loud, 0.24, 0.27); // (64/127)²
        Assert.Equal(
            Rms(Play.Note(none, 69, 4_800, 4_800, 127).Left),
            Rms(Play.Note(none, 69, 4_800, 4_800, 20).Left),
            6
        );
    }

    [Fact]
    public void Sixteen_voices_then_the_oldest_released_then_the_oldest_is_stolen() {
        using var instrument = Play.Create(AnodeSynth.Id, (P.AmpRelease, 5_000));
        var synth = (AnodeSynth)instrument;
        var events = Enumerable.Range(40, 16).Select(static n => TimedEvent.On(0, n)).ToList();
        Play.Render(synth, 256, events);
        Assert.All(Enumerable.Range(40, 16), n => Assert.True(synth.IsSounding(n)));

        // Two keys go up (45 first, then 42): both ring on in their release.
        Play.Render(synth, 256, [TimedEvent.Off(0, 45), TimedEvent.Off(10, 42)]);
        Assert.True(synth.IsSounding(45) && synth.IsSounding(42));

        Play.Render(synth, 256, [TimedEvent.On(0, 70)]);
        Assert.False(synth.IsSounding(45)); // the oldest released
        Assert.True(synth.IsSounding(42) && synth.IsSounding(70));

        Play.Render(synth, 256, [TimedEvent.On(0, 71)]);
        Assert.False(synth.IsSounding(42)); // the other released
        Play.Render(synth, 256, [TimedEvent.On(0, 72)]);
        Assert.False(synth.IsSounding(40)); // none released: the oldest
        Assert.True(synth.IsSounding(41) && synth.IsSounding(72));
    }

    [Fact]
    public void A_stolen_voice_fades_instead_of_clicking() {
        // What the steal takes away is the stolen voice times (1 − its fade): with the 17th note (rendered alone) and
        // the 16 without it subtracted, that is all that is left.
        static float[] Render(IReadOnlyList<TimedEvent> events) {
            using var synth = Tone(3, (P.AmpRelease, 5_000));
            return Play.Render(synth, 9_600, events).Left;
        }

        var sixteen = Enumerable.Range(40, 16).Select(static n => TimedEvent.On(0, n, 127)).ToList();
        var withSteal = Render([..sixteen, TimedEvent.On(4_800, 80, 127)]);
        var without = Render(sixteen);
        var newcomer = Render([TimedEvent.On(4_800, 80, 127)]);
        var stolen = Render([TimedEvent.On(0, 40, 127)]);
        var removed = new float[withSteal.Length];
        for (var i = 0; i < removed.Length; i++) {
            removed[i] = without[i] + newcomer[i] - withSteal[i];
        }

        Assert.Equal(0, Peak(removed.AsSpan(0, 4_800)), 4);
        var fading = Rms(removed.AsSpan(4_800, 120)) / Rms(stolen.AsSpan(4_800, 120)); // the first 2.5 ms
        var gone = Rms(removed.AsSpan(4_800 + 300, 2_400)) / Rms(stolen.AsSpan(4_800 + 300, 2_400));
        Assert.InRange(fading, 0.05, 0.4);
        Assert.InRange(gone, 0.99, 1.01);
    }

    [Fact]
    public void Sustain_pedal_holds_released_notes_until_it_lifts() {
        using var instrument = Tone(3, (P.AmpRelease, 10));
        var synth = (AnodeSynth)instrument;
        Play.Render(synth, 4_800, [TimedEvent.Cc(0, 64, 127), TimedEvent.On(0, 60), TimedEvent.Off(100, 60)]);
        Assert.True(synth.IsSounding(60));
        var (held, _) = Play.Render(synth, 4_800, []);
        Assert.True(Rms(held) > 0.1);
        Play.Render(synth, 4_800, [TimedEvent.Cc(0, 64, 0)]);
        Assert.Equal(0, synth.ActiveVoices);
    }

    [Fact]
    public void All_sound_off_silences_at_once_and_all_notes_off_releases() {
        using var instrument = Tone(3, (P.AmpRelease, 1_000));
        var synth = (AnodeSynth)instrument;
        var (left, _) = Play.Render(
            synth,
            1_000,
            [TimedEvent.On(0, 60), TimedEvent.On(0, 64), TimedEvent.Cc(500, 120, 0)]
        );
        Assert.True(Peak(left.AsSpan(0, 500)) > 0.1);
        Assert.Equal(0, Peak(left.AsSpan(500)));

        Play.Render(synth, 1_000, [TimedEvent.Cc(0, 64, 127), TimedEvent.On(0, 60), TimedEvent.Cc(500, 123, 0)]);
        Assert.True(synth.IsSounding(60)); // releasing
        Play.Render(synth, 96_000, []);
        Assert.Equal(0, synth.ActiveVoices);
    }

    [Fact]
    public void Glide_slides_from_the_last_note() {
        using var synth = Tone(3, (P.Glide, 200), (P.AmpRelease, 1));
        var (left, _) = Play.Render(
            synth,
            48_000,
            [TimedEvent.On(0, 57), TimedEvent.Off(4_800, 57), TimedEvent.On(4_800, 69)]
        );
        var early = ZeroCrossingFrequency(left, 4_800, 960, Rate);
        var late = ZeroCrossingFrequency(left, 4_800 + 9_600 + 960, 4_800, Rate);
        Assert.InRange(Cents(early, NoteFrequency(57)), -150, 150);
        Assert.InRange(Cents(late, NoteFrequency(69)), -2, 2);
    }

    [Fact]
    public void Mod_wheel_scales_the_lfo() {
        var lfo = new (int, float)[] { (P.LfoPitch, 1), (P.LfoRate, 6), (P.LfoModWheel, 100) };
        using var still = Tone(3, lfo);
        using var plain = Tone(3, (P.LfoPitch, 0));
        using var wheeled = Tone(3, lfo);
        var a = Play.Note(still, 69, 24_000, 24_000).Left;
        var b = Play.Note(plain, 69, 24_000, 24_000).Left;
        Assert.Equal(-300, NullResidualDb(a, b));

        var (c, _) = Play.Render(wheeled, 24_000, [TimedEvent.Cc(0, 1, 127), TimedEvent.On(0, 69)]);
        var lowest = double.MaxValue;
        var highest = 0.0;
        for (var at = 0; at + 1_200 < c.Length; at += 600) {
            var f = ZeroCrossingFrequency(c, at, 1_200, Rate);
            lowest = Math.Min(lowest, f);
            highest = Math.Max(highest, f);
        }

        Assert.True(Cents(highest, lowest) > 100, $"vibrato {Cents(highest, lowest):0} cents");
    }

    [Fact]
    public void Unison_spreads_in_stereo_at_a_similar_level() {
        using var one = Play.Create(AnodeSynth.Id, (P.Unison, 1));
        using var four = Play.Create(AnodeSynth.Id, (P.Unison, 4));
        var (l1, r1) = Play.Note(one, 48, 24_000, 24_000);
        var (l4, r4) = Play.Note(four, 48, 24_000, 24_000);
        Assert.Equal(-300, NullResidualDb(l1, r1));
        Assert.True(NullResidualDb(l4, r4) > -40);
        Assert.InRange(ToDb(Rms(l4) / Rms(l1)), -6, 6);
    }

    [Theory]
    [InlineData(0, -3.01)] // 24 dB low-pass
    [InlineData(1, -3.01)]
    [InlineData(2, -3.01)]
    [InlineData(3, 0.0)] // band-pass peaks at the cutoff
    public void Filter_response_at_the_cutoff(int mode, double expectedDb) {
        // The same saw through the filter at 20 kHz and at the 20th harmonic: their ratio there is the response.
        const int note = 33; // 55 Hz
        var cutoff = 20 * NoteFrequency(note);

        float[] Render(float hz) {
            using var synth = Tone(0, (P.FilterMode, mode), (P.Cutoff, hz), (P.Osc1Wave, 0));
            return Play.Note(synth, note, 40_000, 40_000).Left;
        }

        var reference = Spectrum(Render(mode == 2 ? 20 : 20_000), 4_000, 32_768);
        var filtered = Spectrum(Render((float)cutoff), 4_000, 32_768);
        var bin = (int)Math.Round(cutoff * 32_768 / Rate);
        var db = ToDb(filtered[bin].Magnitude / reference[bin].Magnitude);
        if (mode == 3) {
            // The band-pass's reference is the unfiltered saw.
            using var open = Tone(0, (P.FilterMode, 0), (P.Cutoff, 20_000));
            var unfiltered = Spectrum(Play.Note(open, note, 40_000, 40_000).Left, 4_000, 32_768);
            db = ToDb(filtered[bin].Magnitude / unfiltered[bin].Magnitude);
        }

        Assert.InRange(db, expectedDb - 0.3, expectedDb + 0.3);
    }

    [Fact]
    public void Key_tracking_and_envelope_move_the_cutoff() {
        static double Brightness(int note, params (int, float)[] settings) {
            using var synth = Play.Create(
                AnodeSynth.Id,
                [(P.Cutoff, 500), (P.Resonance, 0), (P.OscMix, 0), ..settings]
            );
            var (left, _) = Play.Note(synth, note, 24_000, 24_000);
            var s = Spectrum(left, 8_000, 8_192);
            double high = 0, all = 0;
            for (var k = 1; k < 4_096; k++) {
                var e = s[k].Magnitude * s[k].Magnitude;
                all += e;
                if (k * Rate / 8_192 > 2_000) {
                    high += e;
                }
            }

            return high / all;
        }

        Assert.True(
            Brightness(48, (P.FilterEnvAmount, 80), (P.FilterSustain, 100), (P.KeyTracking, 0))
            > 10 * Brightness(48, (P.FilterEnvAmount, 0), (P.KeyTracking, 0))
        );
        Assert.True(
            Brightness(72, (P.FilterEnvAmount, 0), (P.KeyTracking, 100))
            > 3 * Brightness(72, (P.FilterEnvAmount, 0), (P.KeyTracking, 0))
        );
    }

    [Fact]
    public void Every_waveform_and_filter_mode_stays_finite_and_bounded() {
        for (var wave = 0; wave < 5; wave++) {
            for (var mode = 0; mode < 4; mode++) {
                using var synth = Play.Create(
                    AnodeSynth.Id,
                    (P.Osc1Wave, wave),
                    (P.Osc2Wave, 4 - wave),
                    (P.FilterMode, mode),
                    (P.Resonance, 100),
                    (P.Unison, 3),
                    (P.LfoShape, wave),
                    (P.LfoFilter, 100),
                    (P.LfoAmp, 100),
                    (P.LfoModWheel, 0)
                );
                var (left, right) = Play.Render(
                    synth,
                    9_600,
                    [TimedEvent.On(0, 30), TimedEvent.On(0, 60), TimedEvent.On(100, 100), TimedEvent.Off(4_000, 60)]
                );
                Assert.All(left, static s => Assert.True(float.IsFinite(s) && Math.Abs(s) < 8));
                Assert.All(right, static s => Assert.True(float.IsFinite(s) && Math.Abs(s) < 8));
                Assert.True(Rms(left) > 1e-3, $"wave {wave}, mode {mode} is silent");
            }
        }
    }

    [Fact]
    public void A_repeated_note_plays_on_its_own_voice() {
        using var instrument = Play.Create(AnodeSynth.Id);
        var synth = (AnodeSynth)instrument;
        Play.Render(synth, 1_000, [TimedEvent.On(0, 60), TimedEvent.On(500, 60), TimedEvent.On(600, 60)]);
        Assert.Equal(1, synth.ActiveVoices);
    }

    [Fact]
    public void Parameter_changes_are_smoothed_while_notes_sound() {
        using var synth = Tone();
        var before = Peak(Play.Render(synth, 4_800, [TimedEvent.On(0, 69)]).Left.AsSpan(2_400));
        synth.SetParameter(P.Volume, -40); // from −6 dB
        var (left, _) = Play.Render(synth, 4_800, []);
        // No jump: the first samples are still near the old level, and it settles on the new one.
        Assert.True(Peak(left.AsSpan(0, 100)) > 0.5 * before);
        Assert.InRange(ToDb(Peak(left.AsSpan(3_800)) / before), -35, -33);
    }
}
