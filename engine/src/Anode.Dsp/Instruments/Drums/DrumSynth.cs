using Anode.Dsp.Envelopes;
using Anode.Dsp.Filters;
using Anode.Dsp.Oscillators;

namespace Anode.Dsp.Instruments.Drums;

/// <summary>The sounds <see cref="DrumSynth" /> models.</summary>
public enum DrumModel {
    Kick,
    Snare,
    Clap,
    Hat,
    Tom,
    Cymbal,
    Percussion
}

/// <summary>
///     <c>anode.drum-synth</c>'s parameters. One instance is one drum sound (a Drum Machine Designer pad): the model
///     is a parameter, and every note plays it the same way, so the pad's note never changes the sound.
/// </summary>
public static class DrumSynthParameters {
    static readonly ParameterList List = new();

    static readonly string[] Models = ["Kick", "Snare", "Clap", "Hat", "Tom", "Cymbal", "Percussion"];

    public static readonly int Model = List.Choice("model", "Model", Models, 0);
    public static readonly int Tune = List.Add("tune", "Tune", -24, 24, 0, "st", 1);
    public static readonly int Decay = List.Add("decay", "Decay", 0, 100, 50, "%", 2);
    public static readonly int Tone = List.Add("tone", "Tone", 0, 100, 50, "%", 3);
    public static readonly int Snap = List.Add("snap", "Snap", 0, 100, 50, "%", 4);
    public static readonly int Sweep = List.Add("sweep", "Pitch Sweep", 0, 100, 50, "%", 5);
    public static readonly int Drive = List.Add("drive", "Drive", 0, 100, 0, "%", 6);

    public static readonly int VelocitySensitivity =
        List.Add("velocitySensitivity", "Velocity Sensitivity", 0, 100, 70, "%");

    public static readonly int Pan = List.Add("pan", "Pan", -100, 100, 0, "%");
    public static readonly int Volume = List.Add("volume", "Volume", -60, 6, -6, "dB", 7);

    public static IReadOnlyList<InstrumentParameter> All => List.All;
}

/// <summary>
///     <c>anode.drum-synth</c>: an analogue-style drum synthesizer, 8 voices, after the 808/909 circuits. A note-on
///     plays the selected model as a one-shot (a note-off does nothing), whatever the note: in a Drum Machine Designer kit
///     the pad's model and its settings are the instrument's parameters, and the note only triggers it.
/// </summary>
/// <remarks>
///     <list type="bullet">
///         <item>
///             Kick: a sine whose pitch falls exponentially from up to 7× the base (50 Hz) by Sweep, its fall time
///             shortened by Tone; a noise click by Snap.
///         </item>
///         <item>
///             Snare: two detuned sines (180 Hz and ×1.84) with a short pitch drop, and band-passed noise (Tone sets
///             its centre, Snap its level) that rings twice as long.
///         </item>
///         <item>Clap: band-passed noise in four bursts 10 ms apart, then a tail.</item>
///         <item>
///             Hat and Cymbal: six band-limited square waves at the 808's metallic ratios, and noise by Snap, through
///             a band-pass (Tone) and a high-pass; Decay goes from closed to open.
///         </item>
///         <item>Tom: a sine (120 Hz) with a gentle pitch drop and a second partial by Tone.</item>
///         <item>
///             Percussion: Tone morphs from a bongo (a sine with a pitch blip) to a cowbell (two squares at 540 and
///             800 Hz through a band-pass).
///         </item>
///     </list>
///     Decay maps 0 … 100 % exponentially onto each model's range of 60 dB decay times. Drive saturates (tanh). The
///     parameters are read when a note starts. Velocity scales the level: <c>1 − s + s·(v/127)²</c>.
/// </remarks>
public sealed class DrumSynth : IInstrument, IPolyphonic {
    public const string Id = "anode.drum-synth";
    public const int Polyphony = 8;

    readonly Engine engine = new();

    public static InstrumentDescriptor Info { get; } =
        new(Id, "Drum Synth", "Anode", "1.0.0", DrumSynthParameters.All, []);

    public InstrumentDescriptor Descriptor => Info;

    public int MaxVoices => Polyphony;

    public int ActiveVoices => engine.ActiveVoices;

    public bool IsSounding(int note) => engine.IsSounding(note);

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

    sealed class Engine() : PolyInstrument<DrumVoice>(
        Info,
        DrumSynth.Polyphony,
        4,
        static i => new(i),
        DrumSynthParameters.Volume
    ) {
        public override bool LoadFile(string path) => false;

        public override byte[]? SaveState() => InstrumentState.Write(Id, null, Info.Parameters, Parameters);

        public override bool LoadState(ReadOnlySpan<byte> state) {
            if (!InstrumentState.TryRead(state, Id, out _, out var values)) {
                return false;
            }

            InstrumentState.Apply(values, Parameters);
            return true;
        }

        protected override void OnReset() { }

        protected override void ControlTick() { }

        protected override void NoteOn(int note, int velocity) {
            var voice = Allocate();
            voice.Start(note, velocity, Parameters, SampleRate);
            Started(voice);
        }

        protected override void Render(Span<float> left, Span<float> right) {
            foreach (var voice in Voices) {
                if (voice.Active) {
                    voice.Render(left, right);
                }
            }
        }
    }
}

/// <summary>One hit of <see cref="DrumSynth" />: the model's oscillators, noise, filters and envelopes.</summary>
internal sealed class DrumVoice(int index) : Voice {
    /// <summary>The 808's six hat oscillators, in Hz.</summary>
    static readonly double[] Metal = [205.3, 304.4, 369.6, 522.7, 540, 800];

    readonly double[] metalPhase = new double[6];
    readonly double[] metalIncrement = new double[6];
    DrumModel model;
    double sampleRate;
    double phase;
    double phase2;
    double baseIncrement;
    double secondRatio;
    double sweepDepth;
    double sweepLevel;
    double sweepCoefficient;
    float body;
    float bodyCoefficient;
    float noiseLevel;
    float noiseCoefficient;
    float noiseGain;
    float clickLevel;
    float clickCoefficient;
    float burstLevel;
    float tone;
    float drive;
    float gainLeft;
    float gainRight;
    float fade = 1;
    float stealStep;
    long age;
    uint noise;
    SvfCoefficients band;
    SvfCoefficients high;
    SvfState bandState;
    SvfState highState;
    OnePole clickFilter;
    double clickCoefficientHp;

    public void Start(int note, int velocity, ParameterBank p, double rate) {
        Begin(note);
        sampleRate = rate;
        model = (DrumModel)(int)p[DrumSynthParameters.Model];
        var tune = Math.Pow(2, p[DrumSynthParameters.Tune] / 12.0);
        var decay = p[DrumSynthParameters.Decay] / 100.0;
        tone = p[DrumSynthParameters.Tone] / 100f;
        var snap = p[DrumSynthParameters.Snap] / 100f;
        var sweep = p[DrumSynthParameters.Sweep] / 100.0;
        drive = p[DrumSynthParameters.Drive] / 100f * 8;
        var gain = Pitch.Velocity(velocity, p[DrumSynthParameters.VelocitySensitivity] / 100f);
        (gainLeft, gainRight) = Pitch.Pan(p[DrumSynthParameters.Pan] / 100.0);
        gainLeft *= gain;
        gainRight *= gain;
        stealStep = (float)(1 / (PolyInstrument<DrumVoice>.StealFadeSeconds * rate));
        fade = 1;
        age = 0;
        phase = 0;
        phase2 = 0;
        noise = (0x9E3779B9u * (uint)(index + 1)) | 1;
        bandState.Reset();
        highState.Reset();
        clickFilter.Reset();
        clickCoefficientHp = OnePole.Coefficient(rate, 2_000);
        clickLevel = 0;
        clickCoefficient = Coefficient(0.004, rate);
        noiseLevel = 0;
        noiseGain = 0;
        body = 1;
        sweepLevel = 1;
        secondRatio = 0;
        sweepDepth = 0;
        sweepCoefficient = 0;

        switch (model) {
            case DrumModel.Kick:
                baseIncrement = 50 * tune / rate;
                sweepDepth = 6 * sweep;
                sweepCoefficient = Coefficient(0.01 + 0.06 * (1 - tone), rate);
                bodyCoefficient = Coefficient(Map(decay, 0.08, 2.5), rate);
                clickLevel = 0.6f * snap;
                break;
            case DrumModel.Snare: {
                baseIncrement = 180 * tune / rate;
                secondRatio = 1.84;
                sweepDepth = 0.6 * sweep;
                sweepCoefficient = Coefficient(0.02, rate);
                var time = Map(decay, 0.06, 1.2);
                bodyCoefficient = Coefficient(time * 0.6, rate);
                noiseCoefficient = Coefficient(time, rate);
                noiseLevel = 1;
                noiseGain = 0.3f + 1.2f * snap;
                band = SvfCoefficients.Design(rate, 1_500 * Math.Pow(5, tone) * tune, 0.8);
                high = SvfCoefficients.Design(rate, 400, 0.7);
                break;
            }
            case DrumModel.Clap:
                body = 0;
                noiseCoefficient = Coefficient(Map(decay, 0.1, 2), rate);
                noiseLevel = 1;
                noiseGain = 1.5f;
                burstLevel = 0.3f + 0.7f * snap;
                clickCoefficient = Coefficient(0.008, rate);
                band = SvfCoefficients.Design(rate, 1_100 * Math.Pow(2.5, tone) * tune, 1.6);
                break;
            case DrumModel.Hat:
            case DrumModel.Cymbal: {
                var cymbal = model == DrumModel.Cymbal;
                for (var k = 0; k < 6; k++) {
                    metalPhase[k] = k * 0.137;
                    metalIncrement[k] = Math.Min(Metal[k] * tune * (cymbal ? 1 : 1.6) / rate, 0.45);
                }

                var time = cymbal ? Map(decay, 0.4, 6) : Map(decay, 0.03, 1.5);
                bodyCoefficient = Coefficient(time, rate);
                noiseCoefficient = Coefficient(time * 0.7, rate);
                noiseLevel = 1;
                noiseGain = 0.8f * snap;
                band = SvfCoefficients.Design(
                    rate,
                    (cymbal ? 4_000 : 7_000) * Math.Pow(2, 2 * tone - 1) * Math.Sqrt(tune),
                    cymbal ? 0.9 : 1.4
                );
                high = SvfCoefficients.Design(rate, cymbal ? 3_000 : 6_000, 0.7);
                break;
            }
            case DrumModel.Tom:
                baseIncrement = 120 * tune / rate;
                secondRatio = 1.5;
                sweepDepth = 1.5 * sweep;
                sweepCoefficient = Coefficient(0.08, rate);
                bodyCoefficient = Coefficient(Map(decay, 0.1, 2.5), rate);
                clickLevel = 0.3f * snap;
                break;
            default: {
                // Percussion: the bongo's sine and the cowbell's squares.
                baseIncrement = 300 * tune / rate;
                sweepDepth = 0.5 * sweep;
                sweepCoefficient = Coefficient(0.01, rate);
                metalIncrement[0] = Math.Min(540 * tune / rate, 0.45);
                metalIncrement[1] = Math.Min(800 * tune / rate, 0.45);
                metalPhase[0] = 0;
                metalPhase[1] = 0;
                bodyCoefficient = Coefficient(Map(decay, 0.03, 1.5), rate);
                band = SvfCoefficients.Design(rate, 2_600 * tune, 1.2);
                clickLevel = 0.3f * snap;
                break;
            }
        }
    }

    /// <summary>The per-sample factor of a 60 dB decay in <paramref name="seconds" />.</summary>
    static float Coefficient(double seconds, double rate) => AdsrSettings.T60Coefficient(seconds, rate);

    /// <summary>0 … 1 onto <paramref name="min" /> … <paramref name="max" /> exponentially.</summary>
    static double Map(double x, double min, double max) => min * Math.Pow(max / min, x);

    protected override void OnRelease() { } // a one-shot: the hit plays out

    protected override void OnSteal() { }

    protected override void OnKill() => fade = 1;

    public void Render(Span<float> left, Span<float> right) {
        for (var i = 0; i < left.Length; i++) {
            var x = Next();
            if (drive > 0) {
                x = MathF.Tanh(x * (1 + drive)) / MathF.Tanh(1 + drive);
            }

            x *= fade;
            left[i] += x * gainLeft;
            right[i] += x * gainRight;
            age++;
            var silent = body < Adsr.Silence && noiseLevel < Adsr.Silence && clickLevel < Adsr.Silence;
            if ((silent && age > 64) || (Stolen && (fade -= stealStep) <= 0)) {
                Kill();
                return;
            }
        }
    }

    float White() => PolyBlep.White(ref noise);

    float Next() {
        switch (model) {
            case DrumModel.Kick: {
                var increment = baseIncrement * (1 + sweepDepth * sweepLevel);
                sweepLevel *= sweepCoefficient;
                var y = SineTable.Sin(phase) * body;
                phase += increment;
                body *= bodyCoefficient;
                return y + Click();
            }
            case DrumModel.Snare: {
                var bend = 1 + sweepDepth * sweepLevel;
                sweepLevel *= sweepCoefficient;
                var y = (SineTable.Sin(phase) + 0.6f * SineTable.Sin(phase2)) * 0.6f * body;
                phase += baseIncrement * bend;
                phase2 += baseIncrement * secondRatio * bend;
                body *= bodyCoefficient;
                var n = White();
                var filtered = bandState.Tick(band, n, SvfOutput.BandPass);
                var shaped = highState.Tick(high, filtered, SvfOutput.HighPass);
                y += (float)shaped * noiseGain * noiseLevel;
                noiseLevel *= noiseCoefficient;
                return y;
            }
            case DrumModel.Clap: {
                // Four bursts 10 ms apart restart a fast envelope; the tail rings under them.
                var burst = (long)(0.01 * sampleRate);
                if (age < 4 * burst && age % burst == 0) {
                    clickLevel = burstLevel;
                }

                var n = (float)bandState.Tick(band, White(), SvfOutput.BandPass);
                var y = n * noiseGain * (0.4f * clickLevel + 0.6f * noiseLevel * (age >= 3 * burst ? 1 : 0.3f));
                clickLevel *= clickCoefficient;
                noiseLevel *= noiseCoefficient;
                return y;
            }
            case DrumModel.Hat:
            case DrumModel.Cymbal: {
                double metal = 0;
                for (var k = 0; k < 6; k++) {
                    metal += PolyBlep.Pulse(metalPhase[k], metalIncrement[k], 0.5);
                    metalPhase[k] += metalIncrement[k];
                    if (metalPhase[k] >= 1) {
                        metalPhase[k] -= 1;
                    }
                }

                var x = metal / 6 * body + White() * noiseGain * noiseLevel;
                body *= bodyCoefficient;
                noiseLevel *= noiseCoefficient;
                var filtered = bandState.Tick(band, x, SvfOutput.BandPass);
                return (float)highState.Tick(high, filtered, SvfOutput.HighPass) * 2.5f;
            }
            case DrumModel.Tom: {
                var bend = 1 + sweepDepth * sweepLevel;
                sweepLevel *= sweepCoefficient;
                var y = (SineTable.Sin(phase) + 0.5f * tone * SineTable.Sin(phase2)) * body;
                phase += baseIncrement * bend;
                phase2 += baseIncrement * secondRatio * bend;
                body *= bodyCoefficient;
                return y + Click();
            }
            default: {
                var bend = 1 + sweepDepth * sweepLevel;
                sweepLevel *= sweepCoefficient;
                var bongo = SineTable.Sin(phase);
                phase += baseIncrement * bend;
                var bell = 0.0;
                for (var k = 0; k < 2; k++) {
                    bell += PolyBlep.Pulse(metalPhase[k], metalIncrement[k], 0.5);
                    metalPhase[k] += metalIncrement[k];
                    if (metalPhase[k] >= 1) {
                        metalPhase[k] -= 1;
                    }
                }

                var cowbell = bandState.Tick(band, bell * 0.5, SvfOutput.BandPass) * 2;
                var y = (float)((1 - tone) * bongo + tone * cowbell) * body;
                body *= bodyCoefficient;
                return y + Click();
            }
        }
    }

    /// <summary>The attack click: high-passed noise with a 4 ms decay.</summary>
    float Click() {
        if (clickLevel < Adsr.Silence) {
            clickLevel = 0;
            return 0;
        }

        var y = (float)clickFilter.HighPass(clickCoefficientHp, White()) * clickLevel;
        clickLevel *= clickCoefficient;
        return y;
    }
}
