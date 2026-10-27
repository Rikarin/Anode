using Anode.Dsp.Envelopes;
using Anode.Dsp.Filters;
using Anode.Dsp.Modulation;
using Anode.Dsp.Oscillators;
using P = Anode.Dsp.Instruments.Synth.SynthParameters;

namespace Anode.Dsp.Instruments.Synth;

/// <summary>
///     <c>anode.synth</c>: a 16-voice subtractive synthesizer. Per voice: two band-limited oscillators (saw,
///     pulse with width, triangle, sine, noise) with octave and fine tune, mixed; up to four detuned unison layers spread
///     in stereo; a resonant multimode filter (24 dB low-pass, 12 dB low-, high- and band-pass) with key tracking and
///     an envelope; amp and filter ADSRs; glide. One LFO (sine, triangle, saw, square, sample-and-hold) goes to pitch,
///     filter and amp, scaled by the mod wheel as much as LFO Mod Wheel says. Continuous parameters are smoothed at
///     control rate (a 10 ms time constant); when no voice sounds they jump.
/// </summary>
/// <remarks>
///     Velocity scales the amplitude: <c>1 − s + s·(v/127)²</c> for a sensitivity <c>s</c>. Glide slides each new note
///     from the last note played, linearly in pitch, over the glide time. A note already sounding is played again on
///     its own voice, from where its envelopes are.
/// </remarks>
public sealed class AnodeSynth : IInstrument {
    public const string Id = "anode.synth";
    public const int Polyphony = 16;

    /// <summary>The time constant of the parameter smoothing.</summary>
    const double SmoothingSeconds = 0.01;

    /// <summary>Unison layers spread up to this far either side of the centre (−1 … 1).</summary>
    const double UnisonSpread = 0.8;

    readonly Engine engine = new();

    public static InstrumentDescriptor Info { get; } =
        new(Id, "Anode Synth", "Anode", "1.0.0", P.All, []);

    public InstrumentDescriptor Descriptor => Info;

    /// <summary>Whether a voice that has not been stolen plays <paramref name="note" />.</summary>
    internal bool IsSounding(int note) => engine.IsSounding(note);

    internal int ActiveVoices => engine.ActiveVoices;

    public void Prepare(float sampleRate, int maxFrames) => engine.Prepare(sampleRate, maxFrames);

    public void SetParameter(int index, float value) => engine.SetParameter(index, value);

    public float GetParameter(int index) => engine.GetParameter(index);

    public void Process(ReadOnlySpan<MidiEvent> events, Span<float> left, Span<float> right) =>
        engine.Process(events, left, right);

    public void Reset() => engine.Reset();

    public bool LoadFile(string path) => false;

    public byte[]? SaveState() => engine.SaveState();

    public bool LoadState(ReadOnlySpan<byte> state) => engine.LoadState(state);

    public void Dispose() => engine.Dispose();

    sealed class Engine : PolyInstrument<SynthVoice> {
        readonly SynthControl control;
        readonly float[] smoothed = new float[P.All.Count];
        Lfo lfo;
        float lfoValue;
        double smoothing;
        int lastNote = -1;

        public Engine() : this(new()) { }

        Engine(SynthControl control) : base(Info, AnodeSynth.Polyphony, 4, i => new(control, i), P.Volume) {
            this.control = control;
        }

        public override void Prepare(float sampleRate, int maxFrames) {
            control.SampleRate = sampleRate;
            control.StealStep = (float)(1 / (StealFadeSeconds * sampleRate));
            smoothing = 1 - Math.Exp(-ControlInterval / (SmoothingSeconds * sampleRate));
            base.Prepare(sampleRate, maxFrames);
        }

        public override bool LoadFile(string path) => false;

        public override byte[]? SaveState() => InstrumentState.Write(Id, null, P.All, Parameters);

        public override bool LoadState(ReadOnlySpan<byte> state) {
            if (!InstrumentState.TryRead(state, Id, out _, out var values)) {
                return false;
            }

            InstrumentState.Apply(values, Parameters);
            Snap();
            return true;
        }

        protected override void OnReset() {
            lfo.Reset();
            lfoValue = 0;
            lastNote = -1;
            Snap();
        }

        protected override void NoteOn(int note, int velocity) {
            if (ActiveVoices == 0) {
                Snap(); // from silence, a note starts on the current values rather than gliding to them
            }

            var glide = smoothed[P.Glide] * 0.001;
            foreach (var voice in Voices) {
                if (voice.Active && !voice.Stolen && voice.Note == note) {
                    voice.Retrigger(note, velocity, lastNote, glide);
                    Started(voice);
                    lastNote = note;
                    return;
                }
            }

            var fresh = Allocate();
            fresh.Start(note, velocity, lastNote, glide);
            Started(fresh);
            lastNote = note;
        }

        protected override void Render(Span<float> left, Span<float> right) {
            foreach (var voice in Voices) {
                if (voice.Active) {
                    voice.Render(left, right);
                }
            }
        }

        protected override void ControlTick() {
            if (ActiveVoices == 0) {
                Snap();
            } else {
                for (var i = 0; i < smoothed.Length; i++) {
                    var target = Parameters[i];
                    smoothed[i] = P.All[i].Steps > 0
                        ? target
                        : smoothed[i] + (float)((target - smoothed[i]) * smoothing);
                }
            }

            lfoValue = lfo.Next(
                (LfoShape)(int)smoothed[P.LfoShape],
                smoothed[P.LfoRate] * ControlInterval / control.SampleRate
            );
            Compute();
        }

        void Snap() {
            for (var i = 0; i < smoothed.Length; i++) {
                smoothed[i] = Parameters[i];
            }

            if (Prepared) {
                Compute();
            }
        }

        /// <summary>The control values from the smoothed parameters, the controllers and the LFO.</summary>
        void Compute() {
            var s = smoothed;
            var c = control;
            var rate = c.SampleRate;
            c.Wave1 = (Waveform)(int)s[P.Osc1Wave];
            c.Wave2 = (Waveform)(int)s[P.Osc2Wave];
            c.Width1 = s[P.Osc1Width] / 100.0;
            c.Width2 = s[P.Osc2Width] / 100.0;

            var unison = Math.Clamp((int)s[P.Unison], 1, SynthControl.MaxUnison);
            c.Unison = unison;
            var mix = s[P.OscMix] / 100f;
            var layerGain = 1 / MathF.Sqrt(unison);
            c.Gain1 = (1 - mix) * layerGain;
            c.Gain2 = mix * layerGain;
            var tune1 = s[P.Osc1Octave] * 12 + s[P.Osc1Fine] / 100.0;
            var tune2 = s[P.Osc2Octave] * 12 + s[P.Osc2Fine] / 100.0;
            for (var k = 0; k < unison; k++) {
                // Spread evenly over −1 … 1: the detune in cents either way, the pan up to UnisonSpread.
                var spread = unison == 1 ? 0 : 2.0 * k / (unison - 1) - 1;
                var detune = spread * s[P.UnisonDetune] / 100.0;
                c.Ratio1[k] = Math.Pow(2, (tune1 + detune) / 12);
                c.Ratio2[k] = Math.Pow(2, (tune2 + detune) / 12);
                var angle = (spread * UnisonSpread + 1) * Math.PI / 4;
                c.PanLeft[k] = (float)(Math.Sqrt(2) * Math.Cos(angle));
                c.PanRight[k] = (float)(Math.Sqrt(2) * Math.Sin(angle));
            }

            c.Mode = (FilterMode)(int)s[P.FilterMode];
            c.Quality = MultimodeCoefficients.Quality(c.Mode, s[P.Resonance] / 100.0);
            c.Cutoff = s[P.Cutoff];
            c.KeyTracking = s[P.KeyTracking] / 100.0;
            c.EnvelopeOctaves = s[P.FilterEnvAmount] / 100.0 * P.EnvelopeOctaves;
            c.Amp = AdsrSettings.From(
                s[P.AmpAttack] * 0.001,
                0,
                s[P.AmpDecay] * 0.001,
                s[P.AmpSustain] / 100.0,
                s[P.AmpRelease] * 0.001,
                rate
            );
            c.Filter = AdsrSettings.From(
                s[P.FilterAttack] * 0.001,
                0,
                s[P.FilterDecay] * 0.001,
                s[P.FilterSustain] / 100.0,
                s[P.FilterRelease] * 0.001,
                rate
            );
            c.VelocitySensitivity = s[P.VelocitySensitivity] / 100f;

            var wheel = s[P.LfoModWheel] / 100f;
            var depth = 1 - wheel + wheel * ModWheel;
            var value = lfoValue * depth;
            c.PitchModulation = Bend * s[P.BendRange] + value * s[P.LfoPitch];
            c.FilterModulation = value * s[P.LfoFilter] / 100.0 * P.LfoFilterOctaves;
            // Tremolo dips from full level: the LFO's top is 1, its bottom 1 − depth.
            c.AmpModulation = 1 - s[P.LfoAmp] / 100f * depth * (0.5f - 0.5f * lfoValue);
        }
    }
}
