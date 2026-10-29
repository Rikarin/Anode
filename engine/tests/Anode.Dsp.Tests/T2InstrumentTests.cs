using Anode.Dsp.Instruments;
using Anode.Dsp.Instruments.Drums;
using Anode.Dsp.Instruments.Fm;
using Anode.Dsp.Instruments.Organ;
using Anode.Dsp.Instruments.Piano;
using Anode.Dsp.Instruments.Quick;
using Anode.Dsp.Instruments.Wavetable;
using Anode.Dsp.Tests.Support;
using Anode.Dsp.Transport;
using static Anode.Dsp.Tests.Support.Analysis;
using D = Anode.Dsp.Instruments.Drums.DrumSynthParameters;
using E = Anode.Dsp.Instruments.Piano.ElectricPianoParameters;
using F = Anode.Dsp.Instruments.Fm.FmParameters;
using O = Anode.Dsp.Instruments.Organ.OrganParameters;
using Q = Anode.Dsp.Instruments.Quick.QuickSamplerParameters;
using W = Anode.Dsp.Instruments.Wavetable.WavetableParameters;

namespace Anode.Dsp.Tests;

/// <summary>
///     The T2 instruments (docs/07-phase-6.md, workstream E): each renders, rests in silence, plays in tune, steals
///     voices, and does what its model says. The <see cref="IInstrument" /> contract itself is
///     <see cref="InstrumentContractTests" />'.
/// </summary>
public sealed class T2InstrumentTests : IDisposable {
    const float Rate = Play.Rate;
    readonly string folder = Play.TempFolder();

    public void Dispose() => Directory.Delete(folder, true);

    public static TheoryData<string> Ids => [
        DrumSynth.Id, FmSynth.Id, DrawbarOrgan.Id, ElectricPiano.Id, WavetableSynth.Id, QuickSampler.Id
    ];

    /// <summary>
    ///     The level in dB of <paramref name="frequency" /> in 16,384 Hann-windowed samples of <paramref name="signal" />
    ///     from <paramref name="start" /> (the window keeps a strong partial's leakage out of the others).
    /// </summary>
    static double LevelDb(float[] signal, int start, double frequency) {
        var windowed = new float[16_384];
        for (var i = 0; i < windowed.Length; i++) {
            windowed[i] = signal[start + i] * (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / windowed.Length));
        }

        return ToDb(MagnitudeAt(windowed, frequency, Rate));
    }

    /// <summary>A WAV in the test folder: <paramref name="channels" /> at 48 kHz, root <paramref name="root" />.</summary>
    string Wav(string name, float[][] channels, int root = -1) {
        var path = Path.Combine(folder, name);
        TestWav.Write(path, (int)Rate, channels, asFloat: true, rootNote: root);
        return path;
    }

    /// <summary>A prepared instrument; the Quick Sampler with a 3 s stereo 440 Hz tone (root A4) loaded.</summary>
    IInstrument Create(string id, params (int, float)[] parameters) {
        var instrument = Play.Create(id, parameters);
        if (id == QuickSampler.Id) {
            var tone = TestWav.Sine(440, (int)Rate, 3);
            Assert.True(instrument.LoadFile(Wav("tone.wav", [tone, tone], 69)));
        }

        return instrument;
    }

    /// <summary>A patch for each instrument that sounds a single sine at the note's pitch.</summary>
    IInstrument PureTone(string id) =>
        id switch {
            FmSynth.Id => Create(id, (F.Algorithm, 7), (F.Level(2), 0), (F.Level(3), 0), (F.Level(4), 0), (F.Sustain(1), 100)),
            DrawbarOrgan.Id => Create(
                id,
                (O.Drawbar(1), 0),
                (O.Drawbar(2), 0),
                (O.Percussion, 0),
                (O.KeyClick, 0),
                (O.VibratoChorus, 0),
                (O.Rotary, 0)
            ),
            WavetableSynth.Id => Create(
                id,
                (W.Osc1Position, 0),
                (W.Osc2Level, 0),
                (W.SlotSource(1) + 2, 0),
                (W.SlotSource(2) + 2, 0),
                (W.Cutoff, 20_000),
                (W.FilterEnvAmount, 0),
                (W.KeyTracking, 0),
                (W.Resonance, 0)
            ),
            _ => Create(id)
        };

    /// <summary>A patch for each instrument whose notes last while held.</summary>
    IInstrument Sustaining(string id) =>
        id switch {
            DrumSynth.Id => Create(id, (D.Model, (int)DrumModel.Cymbal), (D.Decay, 100)),
            ElectricPiano.Id => Create(id, (E.Decay, 100)),
            FmSynth.Id => Create(id, (F.Sustain(1), 100), (F.Sustain(3), 100)),
            _ => Create(id)
        };

    [Theory]
    [MemberData(nameof(Ids))]
    public void Renders_and_rests_in_silence(string id) {
        using var instrument = Create(id);
        var (left, right) = Play.Render(instrument, 4 * 48_000, [TimedEvent.On(0, 60), TimedEvent.Off(24_000, 60)]);
        Assert.True(Rms(left.AsSpan(0, 12_000)) > 0.005, $"{Rms(left.AsSpan(0, 12_000))}");
        Assert.True(Rms(right.AsSpan(0, 12_000)) > 0.005);
        Assert.True(Peak(left) < 4, "a note stays in range");
        Assert.Equal(0, Peak(left.AsSpan(3 * 48_000)) + Peak(right.AsSpan(3 * 48_000)));
        Assert.Equal(0, ((IPolyphonic)instrument).ActiveVoices);
    }

    public static TheoryData<string, int> PitchedNotes {
        get {
            var data = new TheoryData<string, int>();
            foreach (var id in new[] { FmSynth.Id, DrawbarOrgan.Id, ElectricPiano.Id, WavetableSynth.Id, QuickSampler.Id }) {
                foreach (var note in new[] { 36, 48, 60, 69, 84 }) {
                    data.Add(id, note);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(PitchedNotes))]
    public void Plays_in_tune_within_two_cents(string id, int note) {
        using var instrument = PureTone(id);
        var (left, _) = Play.Render(instrument, 60_000, [TimedEvent.On(0, note)]);
        var cents = Cents(PeakFrequency(left, Rate, 12_000), NoteFrequency(note));
        Assert.True(Math.Abs(cents) < 2, $"{id} note {note}: {cents:0.000} cents");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Electric_piano_models_are_in_tune_as_they_come(int model) {
        foreach (var note in new[] { 40, 60, 79 }) {
            using var piano = Create(ElectricPiano.Id, (E.Model, model));
            var (left, _) = Play.Render(piano, 60_000, [TimedEvent.On(0, note, 90)]);
            var cents = Cents(PeakFrequency(left, Rate, 12_000), NoteFrequency(note));
            Assert.True(Math.Abs(cents) < 2, $"model {model} note {note}: {cents:0.000} cents");
        }
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Steals_the_oldest_voice_beyond_its_polyphony(string id) {
        using var instrument = Sustaining(id);
        var voices = (IPolyphonic)instrument;
        var max = voices.MaxVoices;
        var events = Enumerable.Range(0, max + 3).Select(i => TimedEvent.On(i * 10, 30 + i)).ToList();
        Play.Render(instrument, 20 * (max + 3), events);
        for (var i = 0; i < 3; i++) {
            Assert.False(voices.IsSounding(30 + i), $"note {30 + i} should be stolen");
        }

        for (var i = 3; i < max + 3; i++) {
            Assert.True(voices.IsSounding(30 + i), $"note {30 + i} should sound");
        }

        Assert.InRange(voices.ActiveVoices, max, max + 3);
        Play.Render(instrument, 2_000, []); // the stolen voices fade out in 5 ms
        Assert.Equal(max, voices.ActiveVoices);
    }

    // Drum Synth.

    [Fact]
    public void Drum_synth_ignores_the_note() {
        foreach (var model in Enum.GetValues<DrumModel>()) {
            using var a = Create(DrumSynth.Id, (D.Model, (int)model));
            using var b = Create(DrumSynth.Id, (D.Model, (int)model));
            var (low, _) = Play.Render(a, 24_000, [TimedEvent.On(0, 36), TimedEvent.Off(100, 36)]);
            var (high, _) = Play.Render(b, 24_000, [TimedEvent.On(0, 81)]);
            Assert.True(Rms(low) > 1e-3, model.ToString());
            Assert.Equal(-300, NullResidualDb(low, high));
        }
    }

    [Fact]
    public void Drum_synth_models_sound_different() {
        var renders = Enum.GetValues<DrumModel>()
            .Select(m => {
                    using var drum = Create(DrumSynth.Id, (D.Model, (int)m));
                    return Play.Render(drum, 24_000, [TimedEvent.On(0, 36)]).Left;
                }
            )
            .ToList();
        for (var i = 0; i < renders.Count; i++) {
            for (var j = i + 1; j < renders.Count; j++) {
                Assert.True(NullResidualDb(renders[i], renders[j]) > -40, $"{i} and {j}");
            }
        }
    }

    [Theory]
    [InlineData(DrumModel.Kick, 50)]
    [InlineData(DrumModel.Tom, 120)]
    public void Drum_synth_tune_sets_the_pitch(DrumModel model, double hz) {
        foreach (var tune in new[] { -5, 0, 7 }) {
            using var drum = Create(
                DrumSynth.Id,
                (D.Model, (int)model),
                (D.Tune, tune),
                (D.Sweep, 0),
                (D.Snap, 0),
                (D.Tone, 0),
                (D.Decay, 100)
            );
            var (left, _) = Play.Render(drum, 48_000, [TimedEvent.On(0, 60)]);
            var cents = Cents(PeakFrequency(left, Rate, 2_000), hz * Math.Pow(2, tune / 12.0));
            Assert.True(Math.Abs(cents) < 2, $"{model} tune {tune}: {cents:0.000} cents");
        }
    }

    [Fact]
    public void Drum_synth_sweep_starts_the_kick_higher() {
        using var swept = Create(DrumSynth.Id, (D.Sweep, 100), (D.Snap, 0));
        using var flat = Create(DrumSynth.Id, (D.Sweep, 0), (D.Snap, 0));
        var (a, _) = Play.Render(swept, 4_800, [TimedEvent.On(0, 36)]);
        var (b, _) = Play.Render(flat, 4_800, [TimedEvent.On(0, 36)]);
        Assert.True(ZeroCrossingFrequency(a, 0, 960, Rate) > 3 * ZeroCrossingFrequency(b, 0, 960, Rate));
    }

    // FM synth.

    [Fact]
    public void Fm_carriers_sum_and_modulators_add_sidebands() {
        // Algorithm 8: four carriers at ratios 1 … 4, each a harmonic of its own.
        using var additive = Create(
            FmSynth.Id,
            (F.Algorithm, 7),
            (F.Ratio(1), 1),
            (F.Ratio(2), 2),
            (F.Ratio(3), 3),
            (F.Ratio(4), 4),
            (F.Fine(3), 0),
            (F.Level(2), 100),
            (F.Level(3), 100),
            (F.Level(4), 100),
            (F.Sustain(1), 100),
            (F.Sustain(2), 100),
            (F.Sustain(3), 100),
            (F.Sustain(4), 100)
        );
        var (left, _) = Play.Render(additive, 30_000, [TimedEvent.On(0, 57, 127)]);
        var levels = Enumerable.Range(1, 4).Select(h => LevelDb(left, 8_000, 220 * h)).ToArray();
        Assert.True(levels.Max() - levels.Min() < 0.5, string.Join(", ", levels));

        // Algorithm 1 with operator 2 alone modulating operator 1 at the same ratio: harmonics appear.
        using var modulated = Create(FmSynth.Id, (F.Algorithm, 0), (F.Level(2), 80), (F.Level(3), 0), (F.Level(4), 0), (F.Sustain(1), 100), (F.Sustain(2), 100));
        using var plain = Create(FmSynth.Id, (F.Algorithm, 0), (F.Level(2), 0), (F.Level(3), 0), (F.Level(4), 0), (F.Sustain(1), 100));
        var (m, _) = Play.Render(modulated, 30_000, [TimedEvent.On(0, 57, 127)]);
        var (p, _) = Play.Render(plain, 30_000, [TimedEvent.On(0, 57, 127)]);
        var second = LevelDb(m, 8_000, 440) - LevelDb(m, 8_000, 220);
        var none = LevelDb(p, 8_000, 440) - LevelDb(p, 8_000, 220);
        Assert.True(second > -20, $"{second:0.0} dB");
        Assert.True(none < -80, $"{none:0.0} dB");
    }

    [Fact]
    public void Fm_algorithms_route_differently() {
        var renders = Enumerable.Range(0, 8)
            .Select(a => {
                    using var fm = Create(FmSynth.Id, (F.Algorithm, a), (F.Feedback, 30));
                    return Play.Render(fm, 12_000, [TimedEvent.On(0, 60)]).Left;
                }
            )
            .ToList();
        for (var i = 0; i < 8; i++) {
            Assert.True(Rms(renders[i]) > 0.01);
            for (var j = i + 1; j < 8; j++) {
                Assert.True(NullResidualDb(renders[i], renders[j]) > -60, $"{i} and {j}");
            }
        }

        var (modulators, carriers) = FmAlgorithms.Routing(4);
        Assert.Equal([1, 3], carriers);
        Assert.Equal([2], modulators[0]);
        Assert.Equal([4], modulators[2]);
        Assert.Equal(8, FmAlgorithms.Names.Count);
    }

    // Drawbar organ.

    [Fact]
    public void Organ_drawbars_choose_the_harmonics() {
        // The 4' drawbar alone sounds an octave up; the 16' an octave down; the 2⅔' a twelfth up.
        (int Drawbar, double Semitones)[] cases = [(4, 12), (1, -12), (5, 19.02)];
        foreach (var (drawbar, semitones) in cases) {
            var parameters = Enumerable.Range(1, 9).Select(n => (O.Drawbar(n), n == drawbar ? 8f : 0f)).ToList();
            parameters.AddRange([(O.Percussion, 0), (O.KeyClick, 0), (O.VibratoChorus, 0), (O.Rotary, 0)]);
            using var organ = Create(DrawbarOrgan.Id, [.. parameters]);
            var (left, _) = Play.Render(organ, 60_000, [TimedEvent.On(0, 60)]);
            var cents = Cents(PeakFrequency(left, Rate, 12_000), NoteFrequency(60 + semitones));
            Assert.True(Math.Abs(cents) < 2, $"drawbar {drawbar}: {cents:0.00} cents");
        }
    }

    [Fact]
    public void Organ_percussion_is_single_triggered() {
        float[] Play2(bool percussion) {
            using var organ = Create(
                DrawbarOrgan.Id,
                (O.Percussion, percussion ? 1 : 0),
                (O.Rotary, 0),
                (O.VibratoChorus, 0),
                (O.KeyClick, 0)
            );
            // The second key goes down while the first is held: legato, no percussion.
            return Play.Render(organ, 96_000, [TimedEvent.On(0, 48), TimedEvent.On(48_000, 60)]).Left;
        }

        var on = Play2(true);
        var off = Play2(false);
        Assert.True(NullResidualDb(on.AsSpan(0, 4_800), off.AsSpan(0, 4_800)) > -40); // the first key has it
        Assert.True(NullResidualDb(on.AsSpan(48_000), off.AsSpan(48_000)) < -100); // the second does not
    }

    [Fact]
    public void Organ_rotary_spins_up_with_the_mod_wheel_and_spreads_the_stereo() {
        using var organ = (DrawbarOrgan)Create(DrawbarOrgan.Id);
        var (left, right) = Play.Render(organ, 48_000, [TimedEvent.On(0, 60)]);
        Assert.InRange(organ.HornSpeed, 0.82, 0.84);
        Assert.True(NullResidualDb(left, right) > -40, "the microphones hear different things");
        Play.Render(organ, 4 * 48_000, [TimedEvent.Cc(0, 1, 127)]);
        Assert.InRange(organ.HornSpeed, 6.5, 6.71);

        using var dry = Create(DrawbarOrgan.Id, (O.Rotary, 0));
        var (l, r) = Play.Render(dry, 12_000, [TimedEvent.On(0, 60)]);
        Assert.Equal(-300, NullResidualDb(l, r));
    }

    [Fact]
    public void Organ_vibrato_moves_the_pitch() {
        (int, float)[] plain = [(O.Drawbar(1), 0), (O.Drawbar(2), 0), (O.Rotary, 0), (O.Percussion, 0), (O.KeyClick, 0)];
        using var still = Create(DrawbarOrgan.Id, [.. plain, (O.VibratoChorus, 0)]);
        using var vibrato = Create(DrawbarOrgan.Id, [.. plain, (O.VibratoChorus, 3)]);
        var (a, _) = Play.Render(still, 48_000, [TimedEvent.On(0, 69)]);
        var (b, _) = Play.Render(vibrato, 48_000, [TimedEvent.On(0, 69)]);
        // Over 20 ms windows, the plain organ holds its frequency and the vibrato swings it.
        var spreadA = Spread(a);
        var spreadB = Spread(b);
        Assert.True(spreadA < 1, $"{spreadA}");
        Assert.True(spreadB > 5, $"{spreadB}");

        static double Spread(float[] signal) {
            var cents = Enumerable.Range(0, 20)
                .Select(w => Cents(ZeroCrossingFrequency(signal, 4_800 + w * 960, 960, Rate), 440 * 1))
                .ToArray();
            return cents.Max() - cents.Min();
        }
    }

    // Electric piano.

    [Fact]
    public void Electric_piano_velocity_brightens_and_models_differ() {
        double Brightness(int model, int velocity) {
            using var piano = Create(ElectricPiano.Id, (E.Model, model), (E.Bell, 0));
            var (left, _) = Play.Render(piano, 24_000, [TimedEvent.On(0, 57, velocity)]);
            return LevelDb(left, 4_000, 440) - LevelDb(left, 4_000, 220);
        }

        Assert.True(Brightness(0, 127) > Brightness(0, 30) + 6, $"tines {Brightness(0, 127):0.0} vs {Brightness(0, 30):0.0}");
        Assert.True(Brightness(1, 127) > Brightness(1, 30) + 6, $"reeds {Brightness(1, 127):0.0} vs {Brightness(1, 30):0.0}");
        using var tines = Create(ElectricPiano.Id);
        using var reeds = Create(ElectricPiano.Id, (E.Model, 1));
        Assert.True(
            NullResidualDb(
                Play.Render(tines, 12_000, [TimedEvent.On(0, 60)]).Left,
                Play.Render(reeds, 12_000, [TimedEvent.On(0, 60)]).Left
            )
            > -30
        );
    }

    [Fact]
    public void Electric_piano_damper_stops_a_note_and_the_pedal_holds_it() {
        using var damped = Create(ElectricPiano.Id);
        using var pedalled = Create(ElectricPiano.Id);
        var (a, _) = Play.Render(damped, 48_000, [TimedEvent.On(0, 60), TimedEvent.Off(4_800, 60)]);
        var (b, _) = Play.Render(
            pedalled,
            48_000,
            [TimedEvent.Cc(0, 64, 127), TimedEvent.On(0, 60), TimedEvent.Off(4_800, 60)]
        );
        Assert.Equal(0, Peak(a.AsSpan(40_000)));
        Assert.True(Rms(b.AsSpan(40_000)) > 0.01);
    }

    // Wavetable synth.

    [Fact]
    public void Wavetable_position_moves_through_the_frames() {
        double Harmonics(float position) {
            using var synth = Create(
                WavetableSynth.Id,
                (W.Osc1Position, position),
                (W.Osc2Level, 0),
                (W.SlotSource(1) + 2, 0),
                (W.SlotSource(2) + 2, 0),
                (W.SlotSource(3) + 2, 0),
                (W.Cutoff, 20_000),
                (W.FilterEnvAmount, 0),
                (W.EnvelopeAttack(1) + 2, 100)
            );
            var (left, _) = Play.Render(synth, 24_000, [TimedEvent.On(0, 45)]);
            return LevelDb(left, 4_000, 330) - LevelDb(left, 4_000, 110);
        }

        Assert.True(Harmonics(0) < -60); // sine
        Assert.True(Harmonics(57) > -15); // the saw/square frames
    }

    [Fact]
    public void Wavetables_are_band_limited() {
        Wavetables.Ensure();
        Assert.Equal(0, Wavetables.Level(10 / 48_000.0));
        Assert.True(Wavetables.Level(4_000 / 48_000.0) >= 7);
        // A high saw: nothing between its harmonics above the noise.
        using var synth = Create(
            WavetableSynth.Id,
            (W.Osc1Position, 4 * 100 / 7f),
            (W.Osc2Level, 0),
            (W.SlotSource(1) + 2, 0),
            (W.SlotSource(2) + 2, 0),
            (W.SlotSource(3) + 2, 0),
            (W.Cutoff, 20_000),
            (W.FilterEnvAmount, 0),
            (W.EnvelopeAttack(1) + 2, 100)
        );
        var (left, _) = Play.Render(synth, 24_000, [TimedEvent.On(0, 100)]);
        var spectrum = Spectrum(left, 4_000, 16_384);
        var bin = NoteFrequency(100) * 16_384 / Rate;
        double fundamental = 0, worst = 0;
        for (var k = 2; k < 8_192; k++) {
            var harmonic = k / bin;
            var m = spectrum[k].Magnitude;
            if (Math.Abs(harmonic - Math.Round(harmonic)) * bin < 4) {
                if (Math.Round(harmonic) == 1) {
                    fundamental = Math.Max(fundamental, m);
                }
            } else {
                worst = Math.Max(worst, m);
            }
        }

        Assert.True(ToDb(worst / fundamental) < -50, $"{ToDb(worst / fundamental):0.0} dB");
    }

    [Fact]
    public void Wavetable_synced_lfo_follows_the_transport() {
        float[] RenderAt(double quarter, bool playing) {
            using var synth = (WavetableSynth)Create(
                WavetableSynth.Id,
                (W.Lfo1Sync, 3), // 1/4
                (W.SlotSource(2) + 1, (int)WavetableDestination.Pitch),
                (W.SlotSource(2) + 2, 20)
            );
            var left = new float[24_000];
            var right = new float[24_000];
            var q = quarter;
            for (var at = 0; at < left.Length; at += 64) {
                var context = new TransportContext(playing, at, q, 120, 4, 4, 0, Rate, 64);
                synth.SetTransport(context);
                synth.Process(at == 0 ? [new(0, 0x90, 60, 100)] : [], left.AsSpan(at, 64), right.AsSpan(at, 64));
                q += 64 * context.QuartersPerSample;
            }

            return left;
        }

        // A beat later the LFO is in the same place; half a beat later it is not; stopped, it runs from the note.
        Assert.Equal(-300, NullResidualDb(RenderAt(0.5, true), RenderAt(1.5, true)));
        Assert.True(NullResidualDb(RenderAt(0.5, true), RenderAt(1, true)) > -40);
        Assert.Equal(-300, NullResidualDb(RenderAt(0.5, false), RenderAt(1, false)));
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Transport_and_processing_allocate_nothing(string id) {
        using var instrument = Create(id, id == WavetableSynth.Id ? [(W.Lfo1Sync, 4), (W.Lfo2Sync, 6)] : []);
        var left = new float[64];
        var right = new float[64];
        var events = new MidiEvent[] { new(0, 0x90, 60, 100), new(3, 0x90, 64, 90), new(40, 0x80, 60, 0) };
        var allocated = AllocationProbe.Measure(() => {
                var quarter = 0.0;
                for (var block = 0; block < 300; block++) {
                    var context = new TransportContext(block < 200, block * 64L, quarter, 100 + block, 3, 4, 0, Rate, 64);
                    instrument.SetTransport(context);
                    instrument.Process(block % 60 == 0 ? events : [], left, right);
                    quarter += 64 * context.QuartersPerSample;
                }

                instrument.Reset();
            }
        );
        Assert.Equal(0, allocated);
    }

    // Quick Sampler.

    [Fact]
    public void Quick_sampler_one_shot_plays_through_and_classic_stops() {
        var level = TestWav.Constant(0.25f, 24_000);
        var path = Wav("block.wav", [level]);
        using var oneShot = Play.Create(QuickSampler.Id, (Q.Mode, (int)QuickSamplerMode.OneShot));
        using var classic = Play.Create(QuickSampler.Id, (Q.Release, 1));
        Assert.True(oneShot.LoadFile(path));
        Assert.True(classic.LoadFile(path));
        var (a, _) = Play.Render(oneShot, 48_000, [TimedEvent.On(0, 60, 127), TimedEvent.Off(1_000, 60)]);
        var (b, _) = Play.Render(classic, 48_000, [TimedEvent.On(0, 60, 127), TimedEvent.Off(1_000, 60)]);
        Assert.Equal(0.25, a[20_000], 4);
        Assert.Equal(0, a[24_100]);
        Assert.Equal(0.25, b[500], 4);
        Assert.Equal(0, Peak(b.AsSpan(2_000)));
    }

    [Fact]
    public void Quick_sampler_follows_the_key_or_plays_at_its_root() {
        using var follows = (QuickSampler)Create(QuickSampler.Id);
        Assert.Equal(69, follows.GetParameter(Q.RootNote)); // from the smpl chunk
        var (a, _) = Play.Render(follows, 60_000, [TimedEvent.On(0, 81)]);
        Assert.True(Math.Abs(Cents(PeakFrequency(a, Rate, 8_000), 880)) < 2);

        using var pad = Create(QuickSampler.Id, (Q.KeyFollow, 0));
        var (b, _) = Play.Render(pad, 60_000, [TimedEvent.On(0, 36)]);
        Assert.True(Math.Abs(Cents(PeakFrequency(b, Rate, 8_000), 440)) < 2);
    }

    [Fact]
    public void Quick_sampler_slices_map_to_notes() {
        // Four equal parts at levels 0.1 … 0.4.
        var steps = Enumerable.Range(0, 48_000).Select(i => 0.1f * (1 + i / 12_000)).ToArray();
        using var sampler = (QuickSampler)Play.Create(QuickSampler.Id, (Q.Mode, (int)QuickSamplerMode.Slice), (Q.SliceMode, 1));
        Assert.True(sampler.LoadFile(Wav("steps.wav", [steps])));
        Assert.Equal([0, 12_000, 24_000, 36_000], sampler.Slices);
        for (var k = 0; k < 4; k++) {
            var (left, _) = Play.Render(sampler, 14_000, [TimedEvent.On(0, 36 + k, 127)]);
            Assert.Equal(0.1 * (k + 1), left[6_000], 4);
            Assert.Equal(0, left[12_100]); // the slice ends where the next begins
            sampler.Reset();
        }

        var (none, _) = Play.Render(sampler, 1_000, [TimedEvent.On(0, 40)]);
        Assert.Equal(0, Peak(none));

        // Transients: three plucks, three slices where they start.
        var plucks = new float[48_000];
        foreach (var at in new[] { 0, 15_000, 31_000 }) {
            TestWav.Pluck(300, (int)Rate, 0.2, at + 1).CopyTo(plucks, at);
        }

        sampler.SetParameter(Q.SliceMode, 0);
        Assert.True(sampler.LoadFile(Wav("plucks.wav", [plucks])));
        var slices = sampler.Slices;
        Assert.Equal(3, slices.Count);
        Assert.InRange(slices[1], 14_900, 15_100);
        Assert.InRange(slices[2], 30_900, 31_100);
    }

    [Fact]
    public void Quick_sampler_records_its_input_and_keeps_the_take() {
        using var sampler = (QuickSampler)Play.Create(
            QuickSampler.Id,
            (Q.Mode, (int)QuickSamplerMode.Recorder),
            (Q.RecordArm, 1),
            (Q.VelocitySensitivity, 0)
        );
        sampler.PrepareRecorder();
        Assert.Equal(0, sampler.Frames);
        var silence = new float[64];
        var take = Enumerable.Range(0, 6_400).Select(i => 0.5f * MathF.Cos(2 * MathF.PI * 330 * i / Rate)).ToArray();
        sampler.Feed(silence, silence);
        Assert.False(sampler.Recording);
        for (var at = 0; at < take.Length; at += 64) {
            sampler.Feed(take.AsSpan(at, 64), take.AsSpan(at, 64));
            Assert.True(sampler.Recording);
        }

        sampler.SetParameter(Q.RecordArm, 0);
        sampler.Feed(silence, silence);
        Assert.False(sampler.Recording);
        Assert.Equal(take.Length, sampler.Frames);
        Assert.Null(sampler.File);

        // Played at its root, the take comes back sample for sample.
        var (left, _) = Play.Render(sampler, take.Length, [TimedEvent.On(0, 60, 127)]);
        Assert.Equal(-300, NullResidualDb(left, take));

        using var restored = (QuickSampler)Play.Create(QuickSampler.Id);
        Assert.True(restored.LoadState(sampler.SaveState()));
        Assert.Equal(take.Length, restored.Frames);
        var (again, _) = Play.Render(restored, take.Length, [TimedEvent.On(0, 60, 127)]);
        Assert.Equal(-300, NullResidualDb(again, take));
        Assert.Equal(sampler.SaveState(), restored.SaveState());
    }

    [Fact]
    public void Quick_sampler_state_holds_its_file() {
        using var sampler = (QuickSampler)Create(QuickSampler.Id);
        using var restored = (QuickSampler)Play.Create(QuickSampler.Id);
        Assert.True(restored.LoadState(sampler.SaveState()));
        Assert.Equal(sampler.File, restored.File);
        Assert.Equal(sampler.Frames, restored.Frames);
        File.Delete(sampler.File!);
        using var missing = (QuickSampler)Play.Create(QuickSampler.Id);
        Assert.False(missing.LoadState(sampler.SaveState()));
    }

    [Fact]
    public void Every_instrument_has_macro_hints_in_order() {
        foreach (var descriptor in BuiltinInstruments.All) {
            var macros = descriptor.Parameters.Where(static p => p.Macro is not null).Select(static p => p.Macro!.Value).ToList();
            Assert.True(macros.Count >= 4, descriptor.Id);
            Assert.Equal(macros.Count, macros.Distinct().Count());
        }
    }
}
