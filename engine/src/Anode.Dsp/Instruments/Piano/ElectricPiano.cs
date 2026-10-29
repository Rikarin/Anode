using Anode.Dsp.Envelopes;
using Anode.Dsp.Filters;
using Anode.Dsp.Oscillators;

namespace Anode.Dsp.Instruments.Piano;

/// <summary><c>anode.electric-piano</c>'s parameters.</summary>
public static class ElectricPianoParameters {
    static readonly ParameterList List = new();

    public static readonly int Model = List.Choice("model", "Model", ["Tines", "Reeds"], 0);
    public static readonly int Bell = List.Add("bell", "Bell", 0, 100, 40, "%", 1);
    public static readonly int Bark = List.Add("bark", "Bark", 0, 100, 35, "%", 2);
    public static readonly int Decay = List.Add("decay", "Decay", 0, 100, 50, "%", 5);
    public static readonly int Release = List.Add("release", "Damper Release", 10, 2_000, 120, "ms", 6);
    public static readonly int VelocitySensitivity =
        List.Add("velocitySensitivity", "Velocity Sensitivity", 0, 100, 75, "%");
    public static readonly int TremoloRate = List.Add("tremoloRate", "Tremolo Rate", 0.5f, 10, 4.5f, "Hz", 4);
    public static readonly int TremoloDepth = List.Add("tremoloDepth", "Tremolo Depth", 0, 100, 0, "%", 3);
    public static readonly int Tune = List.Add("tune", "Tune", -50, 50, 0, "ct");
    public static readonly int BendRange = List.Stepped("bendRange", "Pitch Bend Range", 0, 12, 2, "st", 12);
    public static readonly int Volume = List.Add("volume", "Volume", -60, 6, -6, "dB", 7);

    public static IReadOnlyList<InstrumentParameter> All => List.All;
}

/// <summary>
///     <c>anode.electric-piano</c>: a modelled electric piano, 32 voices, with tines (a Rhodes-style tine, tonebar and
///     magnetic pickup) or reeds (a Wurlitzer-style reed and electrostatic pickup).
/// </summary>
/// <remarks>
///     <list type="bullet">
///         <item>Tines: the tine's fundamental, its tonebar's octave (−30 dB) and its bell partial (at 7.13× the
///             fundamental, decaying in 0.25 … 0.6 s, its level set by Bell and the velocity), struck by a 2 ms
///             felt-hammer thump. The magnetic pickup is an asymmetric curve, <c>p + b·p²</c> (Bark scaled by the
///             velocity), whose offset a 10 Hz high-pass removes.</item>
///         <item>Reeds: a reed's first three harmonics (1, −30 dB, −36 dB) through the electrostatic pickup,
///             <c>p / (1 − b·p)</c> — a capacitor whose gap the reed changes — which barks as the velocity rises.</item>
///     </list>
///     A note rings with a 60 dB decay time falling with pitch (tines 9 s at C1 to 1.4 s at C7, reeds two-fifths of
///     that), scaled by Decay (×0.25 … ×4 around 50 %); the key up damps it in Damper Release. The tremolo pans
///     (tines, the stereo of a suitcase piano) or modulates the level (reeds); it rests with the piano when quiet, so a
///     note sounds the same wherever it starts.
/// </remarks>
public sealed class ElectricPiano : IInstrument, IPolyphonic {
    public const string Id = "anode.electric-piano";
    public const int Polyphony = 32;

    readonly Engine engine = new();

    public static InstrumentDescriptor Info { get; } =
        new(Id, "Electric Piano", "Anode", "1.0.0", ElectricPianoParameters.All, []);

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

    sealed class Engine : PolyInstrument<ElectricPianoVoice> {
        readonly ElectricPianoControl control;
        readonly ParameterSmoother smoothed = new(ElectricPianoParameters.All);
        readonly float[] dry = new float[ControlInterval];
        double tremoloPhase;
        float tremoloDepth;
        double tremoloIncrement;
        bool pan;

        public Engine() : this(new()) { }

        Engine(ElectricPianoControl control) : base(
            Info,
            ElectricPiano.Polyphony,
            8,
            i => new(control, i),
            ElectricPianoParameters.Volume
        ) {
            this.control = control;
        }

        public override void Prepare(float sampleRate, int maxFrames) {
            control.SampleRate = sampleRate;
            control.StealStep = (float)(1 / (StealFadeSeconds * sampleRate));
            control.DcCoefficient = OnePole.Coefficient(sampleRate, 10);
            smoothed.Prepare(sampleRate, ControlInterval);
            base.Prepare(sampleRate, maxFrames);
        }

        public override bool LoadFile(string path) => false;

        public override byte[]? SaveState() => InstrumentState.Write(Id, null, Info.Parameters, Parameters);

        public override bool LoadState(ReadOnlySpan<byte> state) {
            if (!InstrumentState.TryRead(state, Id, out _, out var values)) {
                return false;
            }

            InstrumentState.Apply(values, Parameters);
            Snap();
            return true;
        }

        protected override void OnReset() {
            tremoloPhase = 0;
            Snap();
        }

        void Snap() {
            smoothed.Snap(Parameters);
            if (Prepared) {
                Compute();
            }
        }

        protected override void NoteOn(int note, int velocity) {
            if (ActiveVoices == 0) {
                tremoloPhase = 0; // the tremolo rests with the piano
                Snap();
            }

            // A key struck again strikes its tine again: a new voice, the old one damped (a fast steal).
            foreach (var voice in Voices) {
                if (voice.Active && !voice.Stolen && voice.Note == note) {
                    voice.Steal();
                }
            }

            var fresh = Allocate();
            fresh.Start(note, velocity);
            Started(fresh);
        }

        protected override void ControlTick() {
            if (ActiveVoices == 0) {
                smoothed.Snap(Parameters);
            } else {
                smoothed.Tick(Parameters);
            }

            Compute();
        }

        void Compute() {
            var s = smoothed;
            var c = control;
            c.Reeds = s[ElectricPianoParameters.Model] >= 0.5f;
            c.Bell = s[ElectricPianoParameters.Bell] / 100f;
            c.Bark = s[ElectricPianoParameters.Bark] / 100f;
            c.DecayScale = Math.Pow(4, (s[ElectricPianoParameters.Decay] - 50) / 50.0);
            c.Damper = AdsrSettings.T60Coefficient(s[ElectricPianoParameters.Release] * 0.001, c.SampleRate);
            c.VelocitySensitivity = s[ElectricPianoParameters.VelocitySensitivity] / 100f;
            c.PitchModulation = Bend * s[ElectricPianoParameters.BendRange] + s[ElectricPianoParameters.Tune] / 100;
            tremoloDepth = s[ElectricPianoParameters.TremoloDepth] / 100f;
            tremoloIncrement = s[ElectricPianoParameters.TremoloRate] / c.SampleRate;
            pan = !c.Reeds;
        }

        protected override void Render(Span<float> left, Span<float> right) {
            var frames = left.Length;
            var mono = dry.AsSpan(0, frames);
            mono.Clear();
            var sounding = false;
            foreach (var voice in Voices) {
                if (voice.Active) {
                    voice.Render(mono);
                    sounding = true;
                }
            }

            if (!sounding) {
                return;
            }

            for (var i = 0; i < frames; i++) {
                var x = mono[i];
                if (tremoloDepth > 0) {
                    var lfo = SineTable.Sin(tremoloPhase);
                    if (pan) {
                        left[i] += x * (1 - tremoloDepth * lfo);
                        right[i] += x * (1 + tremoloDepth * lfo);
                    } else {
                        var gain = 1 - tremoloDepth * (0.5f - 0.5f * lfo);
                        left[i] += x * gain;
                        right[i] += x * gain;
                    }
                } else {
                    left[i] += x;
                    right[i] += x;
                }

                tremoloPhase += tremoloIncrement;
            }

            tremoloPhase -= Math.Floor(tremoloPhase);
        }
    }
}

/// <summary>The control-rate values every <see cref="ElectricPianoVoice" /> reads.</summary>
internal sealed class ElectricPianoControl {
    public double SampleRate;
    public bool Reeds;
    public float Bell;
    public float Bark;
    public double DecayScale = 1;
    public float Damper;
    public float VelocitySensitivity;
    public double PitchModulation;
    public double DcCoefficient;
    public float StealStep;
}

/// <summary>One key of <see cref="ElectricPiano" />: a struck tine or reed and its pickup.</summary>
internal sealed class ElectricPianoVoice(ElectricPianoControl control, int index) : Voice {
    const double BellRatio = 7.13;

    double phase;
    double bellPhase;
    double increment;
    float amplitude;
    float ring;
    float bell;
    float bellCoefficient;
    float thump;
    float thumpCoefficient;
    float bark;
    float fade = 1;
    bool damped;
    bool reeds;
    int untilTick;
    uint noise;
    OnePole dc;

    public void Start(int note, int velocity) {
        Begin(note);
        var c = control;
        reeds = c.Reeds;
        phase = 0;
        bellPhase = 0;
        dc.Reset();
        noise = (0x9E3779B9u * (uint)(index + 1)) | 1;
        var v = velocity / 127f;
        amplitude = Pitch.Velocity(velocity, c.VelocitySensitivity) * 0.5f;
        // 60 dB in 9 s at C1 falling to 1.4 s at C7; reeds ring two-fifths as long.
        var t60 = 9 * Math.Pow(1.4 / 9, (note - 24) / 72.0) * c.DecayScale * (reeds ? 0.4 : 1);
        ring = AdsrSettings.T60Coefficient(Math.Clamp(t60, 0.2, 30), c.SampleRate);
        bell = reeds ? 0 : c.Bell * (0.3f + 0.7f * v) * 0.6f;
        bellCoefficient = AdsrSettings.T60Coefficient(0.25 + 0.35 * (1 - v), c.SampleRate);
        thump = 0.15f * v;
        thumpCoefficient = AdsrSettings.T60Coefficient(0.002, c.SampleRate);
        bark = reeds ? Math.Min(c.Bark * (0.2f + 0.8f * v * v) * 2, 1.25f) : c.Bark * (0.2f + 0.8f * v * v) * 3;
        damped = false;
        fade = 1;
        untilTick = 0;
    }

    protected override void OnRelease() => damped = true;

    protected override void OnSteal() { }

    protected override void OnKill() {
        amplitude = 0;
        fade = 1;
    }

    public void Render(Span<float> output) {
        var c = control;
        var decay = damped ? Math.Min(ring, c.Damper) : ring;
        for (var i = 0; i < output.Length; i++) {
            if (untilTick == 0) {
                increment = Math.Min(Pitch.Frequency(Note + c.PitchModulation) / c.SampleRate, 0.49);
                decay = damped ? Math.Min(ring, c.Damper) : ring;
                untilTick = PolyInstrument<ElectricPianoVoice>.ControlInterval;
            }

            untilTick--;
            float p;
            if (reeds) {
                p = amplitude * (SineTable.Sin(phase) + 0.03f * SineTable.Sin(2 * phase) + 0.015f * SineTable.Sin(3 * phase));
                p /= 1 - bark * p; // the pickup (its offset goes in the high-pass)
            } else {
                p = amplitude * (SineTable.Sin(phase) + 0.03f * SineTable.Sin(2 * phase))
                    + bell * SineTable.Sin(bellPhase);
                p += bark * p * p;
                bell *= bellCoefficient;
                bellPhase += increment * BellRatio;
                if (bellPhase >= 1) {
                    bellPhase -= 1;
                }
            }

            if (thump > Adsr.Silence) {
                p += thump * PolyBlep.White(ref noise) * 0.3f;
                thump *= thumpCoefficient;
            }

            var y = (float)dc.HighPass(c.DcCoefficient, p);
            phase += increment;
            if (phase >= 1) {
                phase -= 1;
            }

            amplitude *= decay;
            output[i] += y * fade;
            if (amplitude < Adsr.Silence || (Stolen && (fade -= c.StealStep) <= 0)) {
                Kill();
                return;
            }
        }
    }
}
