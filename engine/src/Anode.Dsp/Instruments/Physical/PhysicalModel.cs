using Anode.Dsp.Envelopes;
using Anode.Dsp.Filters;
using Anode.Dsp.Modulation;
using Anode.Dsp.Oscillators;

namespace Anode.Dsp.Instruments.Physical;

/// <summary>The vibrating object of <see cref="PhysicalModel" />: which modes it has and how they are shaped.</summary>
public enum PhysicalObject {
    String,
    Bar,
    Membrane,
    Tube
}

/// <summary>An exciter of <see cref="PhysicalModel" />: none, an impulse (pick, strike) or a sustained drive (bow, blow).</summary>
public enum PhysicalExciter {
    Off,
    Pick,
    Bow,
    Strike,
    Blow
}

/// <summary>The body resonance after the pickups: a set of resonant bells, or none.</summary>
public enum PhysicalBody {
    Off,
    Guitar,
    Violin,
    Piano,
    Box
}

/// <summary>
///     <c>anode.physical-model</c>'s parameters. Exciters are <c>exciter{n}…</c> (1 … 3: type, position, level, tone),
///     the pickups <c>pickup{n}Position</c> (1, 2) and <c>pickupSpread</c>; envelopes <c>env{n}…</c> (1: amp, 2: filter)
///     and LFOs <c>lfo{n}…</c> (1 moves the cutoff, 2 the pickups) as the T2 synths name them.
/// </summary>
public static class PhysicalModelParameters {
    public const int Exciters = 3;

    static readonly ParameterList List = new();

    static readonly string[] ExciterNames = ["Off", "Pick", "Bow", "Strike", "Blow"];
    static readonly string[] Shapes = ["Sine", "Triangle", "Saw", "Square", "Sample & Hold"];
    static readonly string[] Modes = ["Low-pass 24 dB", "Low-pass 12 dB", "High-pass 12 dB", "Band-pass 12 dB"];

    public static readonly int Object = List.Choice("object", "Object", ["String", "Bar", "Membrane", "Tube"], 0, 1);
    public static readonly int Stiffness = List.Add("stiffness", "Stiffness", 0, 100, 50, "%", 3);
    public static readonly int Damping = List.Add("damping", "Damping", 0, 100, 30, "%", 2);
    public static readonly int Inharmonicity = List.Add("inharmonicity", "Inharmonicity", 0, 100, 10, "%", 4);

    static readonly int[] ExciterIndices = ExcitersList();

    public static readonly int Pickup1Position = List.Add("pickup1Position", "Pickup A Position", 0, 100, 20, "%", 6);
    public static readonly int Pickup2Position = List.Add("pickup2Position", "Pickup B Position", 0, 100, 70, "%");
    public static readonly int PickupSpread = List.Add("pickupSpread", "Pickup Spread", 0, 100, 50, "%");

    public static readonly int Body =
        List.Choice("body", "Body", ["Off", "Guitar", "Violin", "Piano", "Box"], (int)PhysicalBody.Guitar);

    public static readonly int BodyAmount = List.Add("bodyAmount", "Body Amount", 0, 100, 40, "%", 7);
    public static readonly int FilterMode = List.Choice("filterMode", "Filter Mode", Modes, 1);
    public static readonly int Cutoff = List.Add("cutoff", "Cutoff", 20, 20_000, 12_000, "Hz", 8);
    public static readonly int Resonance = List.Add("resonance", "Resonance", 0, 100, 0, "%");
    public static readonly int KeyTracking = List.Add("keyTracking", "Key Tracking", 0, 100, 50, "%");

    public static readonly int FilterEnvAmount =
        List.Add("filterEnvAmount", "Filter Env Amount", -100, 100, 0, "%");

    static readonly int[] EnvelopeIndices = Envelopes();

    public static readonly int Lfo1Shape = List.Choice("lfo1Shape", "LFO 1 Shape", Shapes, 1);
    public static readonly int Lfo1Rate = List.Add("lfo1Rate", "LFO 1 Rate", 0.05f, 20, 0.6f, "Hz");
    public static readonly int Lfo1Cutoff = List.Add("lfo1Cutoff", "LFO 1 to Cutoff", 0, 100, 0, "%");
    public static readonly int Lfo2Shape = List.Choice("lfo2Shape", "LFO 2 Shape", Shapes, 0);
    public static readonly int Lfo2Rate = List.Add("lfo2Rate", "LFO 2 Rate", 0.05f, 20, 0.3f, "Hz");
    public static readonly int Lfo2Pickup = List.Add("lfo2Pickup", "LFO 2 to Pickups", 0, 100, 0, "%");
    public static readonly int VibratoRate = List.Add("vibratoRate", "Vibrato Rate", 0.1f, 12, 5.5f, "Hz");
    public static readonly int VibratoDepth = List.Add("vibratoDepth", "Vibrato Depth", 0, 100, 0, "ct");
    public static readonly int BendRange = List.Stepped("bendRange", "Pitch Bend Range", 0, 24, 2, "st", 24);

    public static readonly int VelocitySensitivity =
        List.Add("velocitySensitivity", "Velocity Sensitivity", 0, 100, 70, "%");

    public static readonly int Volume = List.Add("volume", "Volume", -60, 6, -6, "dB");

    public static IReadOnlyList<InstrumentParameter> All => List.All;

    /// <summary>Exciter <paramref name="n" />'s (1 … 3) type; its position, level and tone follow it.</summary>
    public static int ExciterType(int n) => ExciterIndices[n - 1];

    /// <summary>Envelope <paramref name="n" />'s (1, 2) attack; its decay, sustain and release follow it.</summary>
    public static int EnvelopeAttack(int n) => EnvelopeIndices[n - 1];

    static int[] ExcitersList() {
        var indices = new int[Exciters];
        float[] positions = [15, 30, 50];
        for (var n = 1; n <= Exciters; n++) {
            indices[n - 1] = List.Choice(
                $"exciter{n}Type",
                $"Exciter {n} Type",
                ExciterNames,
                n == 1 ? (int)PhysicalExciter.Pick : (int)PhysicalExciter.Off,
                n == 1 ? 0 : null
            );
            List.Add($"exciter{n}Position", $"Exciter {n} Position", 0, 100, positions[n - 1], "%", n == 1 ? 5 : null);
            List.Add($"exciter{n}Level", $"Exciter {n} Level", 0, 100, 100, "%");
            List.Add($"exciter{n}Tone", $"Exciter {n} Tone", 0, 100, 50, "%", n == 1 ? 9 : null);
        }

        return indices;
    }

    static int[] Envelopes() {
        var indices = new int[2];
        for (var n = 1; n <= 2; n++) {
            indices[n - 1] = List.Add($"env{n}Attack", $"Env {n} Attack", 0, 10_000, n == 1 ? 1 : 2, "ms");
            List.Add($"env{n}Decay", $"Env {n} Decay", 1, 10_000, n == 1 ? 1_000 : 600, "ms");
            List.Add($"env{n}Sustain", $"Env {n} Sustain", 0, 100, n == 1 ? 100 : 30, "%");
            List.Add($"env{n}Release", $"Env {n} Release", 1, 10_000, n == 1 ? 200 : 400, "ms", n == 1 ? 10 : null);
        }

        return indices;
    }
}

/// <summary>
///     <c>anode.physical-model</c>, the physical-modelling instrument (Sculpture-like): a 16-voice modal synthesizer. Per
///     voice, an object (string, bar, membrane or tube) of up to <see cref="MaxModes" /> modes, each a damped complex
///     resonator at the mode's frequency; up to three exciters at positions along it, each an impulse at the note's
///     start (pick, strike) or a sustained drive while the key is held (bow, blow); two pickups at positions, spread
///     across the stereo field; then a multimode filter with key tracking and its envelope, the amp envelope, two LFOs
///     (cutoff, pickup positions) and vibrato (its depth plus the mod wheel's 50 cents). A body resonance (resonant
///     bells per body type) colours the sum of the voices.
/// </summary>
/// <remarks>
///     <para>
///         <b>Modes.</b> Mode n's frequency is the note's times the object's ratio: a string's n, a free bar's
///         (β<sub>n</sub>/β<sub>1</sub>)² (β = 1.506, 2.500, 3.500, then n + ½), a circular membrane's Bessel zeros over
///         the first (1, 1.594, 2.136, 2.296, …), an open tube's n slightly flattened (end correction). Inharmonicity
///         stretches every mode by √(1 + B (n² − 1)) with B = 0.05·(inharmonicity)², so the first mode stays on the
///         note's pitch. Damping sets the first mode's −60 dB time (20 s at 0 % to 50 ms at 100 %, exponentially, a
///         little shorter for higher notes); stiffness how much faster the higher modes die (−60 dB time ∝
///         (f<sub>1</sub>/f<sub>n</sub>)<sup>α</sup>, α from 1.5 for a soft material to 0.2 for a stiff one). Modes above
///         0.45 of the sample rate are left out.
///     </para>
///     <para>
///         <b>Shapes.</b> An exciter or a pickup at position x (0 … 1 along the object) meets mode n with the weight of
///         its mode shape there: sin(nπx) for the string and the tube, cos(nπx) for the free bar, and a radial,
///         Bessel-like cos(π r<sub>n</sub> x)(1 − x/2) for the membrane (an approximation of J<sub>m</sub>).
///     </para>
///     <para>
///         <b>Exciters.</b> A pick sets each mode's displacement to its shape times 1/n through a low-pass whose corner
///         rises with the tone (a plectrum's spectrum); a strike gives every mode its shape through a steeper mallet
///         low-pass (the tone is the mallet's hardness). A bow or a blow is negative damping, as a bow's friction and a
///         jet's feedback are: while the key is held each mode is driven toward a target amplitude (the shape times a
///         bowed string's 1/n or a flute's falling spectrum, odd modes stronger), regulated so it settles there, from a
///         faint seed of noise; the bow's hair and the breath add their own noise to the pickups (low-passed, the breath
///         more of it), and the tone sets how fast it speaks and how much noise there is. (Noise fed into the modes at
///         that level would make their phases wander, and the pitch with them.) Velocity scales every exciter (by the
///         velocity sensitivity).
///     </para>
///     <para>
///         Voices update on their own 16-sample grid from their start; the body runs on the instrument's grid from
///         <see cref="Reset" /> and, being linear and time-invariant from silence, does not change where a note falls.
///         A voice ends when its amp envelope does, or when its modes have died away with no drive.
///     </para>
/// </remarks>
public sealed class PhysicalModel : IInstrument, IPolyphonic {
    public const string Id = "anode.physical-model";
    public const int Polyphony = 16;

    /// <summary>The most modes a voice runs.</summary>
    public const int MaxModes = 32;

    readonly Engine engine = new();

    public static InstrumentDescriptor Info { get; } =
        new(Id, "Physical Model", "Anode", "1.0.0", PhysicalModelParameters.All, []);

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

    /// <summary>
    ///     The frequency ratio of mode <paramref name="n" /> (1 …) of <paramref name="kind" /> with inharmonicity
    ///     coefficient <paramref name="b" />: the first is 1.
    /// </summary>
    public static double Ratio(PhysicalObject kind, int n, double b) {
        var ratio = kind switch {
            PhysicalObject.Bar => BarRatio(n),
            PhysicalObject.Membrane => n <= MembraneRatios.Length ? MembraneRatios[n - 1] : MembraneRatio(n),
            PhysicalObject.Tube => n * (1 - 0.0015 * (n - 1)),
            _ => n
        };
        return ratio * Math.Sqrt(1 + b * (ratio * ratio - 1));
    }

    /// <summary>Mode <paramref name="n" />'s shape at position <paramref name="x" /> (0 … 1) of <paramref name="kind" />.</summary>
    public static double Shape(PhysicalObject kind, int n, double x) =>
        kind switch {
            PhysicalObject.Bar => Math.Cos(n * Math.PI * x),
            PhysicalObject.Membrane => Math.Cos(Math.PI * Ratio(kind, n, 0) * x) * (1 - 0.5 * x),
            _ => Math.Sin(n * Math.PI * x)
        };

    /// <summary>
    ///     The −60 dB time of the first mode (s) at <paramref name="damping" /> (0 … 1) for a note of
    ///     <paramref name="hz" />.
    /// </summary>
    public static double DecaySeconds(double damping, double hz) =>
        0.05 * Math.Pow(400, 1 - Math.Clamp(damping, 0, 1)) * Math.Clamp(Math.Pow(261.63 / hz, 0.3), 0.5, 1.6);

    static readonly double[] BarBetas = [1.50562, 2.49975, 3.50001];

    static double BarRatio(int n) {
        var beta = n <= BarBetas.Length ? BarBetas[n - 1] : n + 0.5;
        return beta * beta / (BarBetas[0] * BarBetas[0]);
    }

    /// <summary>Zeros of the Bessel functions J<sub>m</sub>, in order, over the first (2.4048).</summary>
    static readonly double[] MembraneRatios = [
        1, 1.5933, 2.1355, 2.2954, 2.6531, 2.9173, 3.1555, 3.5001, 3.5985, 3.6475, 4.0589, 4.1541, 4.2304, 4.6010,
        4.8319, 4.9031, 5.1308, 5.4121, 5.4936, 5.5379, 5.9469, 6.0544, 6.2066, 6.2071, 6.3003, 6.5280, 6.7456,
        6.7499, 6.9818, 7.0601, 7.2482, 7.4589
    ];

    static double MembraneRatio(int n) => MembraneRatios[^1] + 0.23 * (n - MembraneRatios.Length);

    sealed class Engine : PolyInstrument<PhysicalVoice> {
        readonly PhysicalControl control;
        readonly ParameterSmoother smoothed = new(PhysicalModelParameters.All);
        readonly SvfEqState[] bodyLeft = new SvfEqState[BodyBells];
        readonly SvfEqState[] bodyRight = new SvfEqState[BodyBells];
        readonly SvfEqCoefficients[] body = new SvfEqCoefficients[BodyBells];
        bool bodyOn;

        const int BodyBells = 3;

        /// <summary>Each body's bells: frequency (Hz), quality and gain at 100 % (dB).</summary>
        static readonly (double Hz, double Q, double Db)[][] Bodies = [
            [],
            [(105, 2.5, 7), (215, 1.8, 5), (2_400, 1.2, 3)],
            [(285, 3, 6), (470, 2.5, 5), (3_000, 1.5, 6)],
            [(160, 1.2, 4), (650, 1.5, 3), (2_200, 1.5, -2)],
            [(310, 4, 7), (930, 3.5, 5), (1_850, 3, 4)]
        ];

        public Engine() : this(new()) { }

        Engine(PhysicalControl control) : base(
            Info,
            PhysicalModel.Polyphony,
            4,
            _ => new(control),
            PhysicalModelParameters.Volume
        ) {
            this.control = control;
        }

        public override void Prepare(float sampleRate, int maxFrames) {
            control.SampleRate = sampleRate;
            control.StealStep = (float)(1 / (StealFadeSeconds * sampleRate));
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
            for (var b = 0; b < BodyBells; b++) {
                bodyLeft[b].Reset();
                bodyRight[b].Reset();
            }

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
                Snap();
            }

            foreach (var voice in Voices) {
                if (voice.Active && !voice.Stolen && voice.Note == note) {
                    voice.Retrigger(note, velocity);
                    Started(voice);
                    return;
                }
            }

            var fresh = Allocate();
            fresh.Start(note, velocity);
            Started(fresh);
        }

        protected override void Render(Span<float> left, Span<float> right) {
            foreach (var voice in Voices) {
                if (voice.Active) {
                    voice.Render(left, right);
                }
            }

            if (!bodyOn) {
                return;
            }

            for (var i = 0; i < left.Length; i++) {
                double l = left[i];
                double r = right[i];
                for (var b = 0; b < BodyBells; b++) {
                    l = bodyLeft[b].Tick(body[b], l);
                    r = bodyRight[b].Tick(body[b], r);
                }

                left[i] = (float)l;
                right[i] = (float)r;
            }
        }

        protected override void ControlTick() {
            if (ActiveVoices == 0) {
                smoothed.Snap(Parameters);
            } else {
                smoothed.Tick(Parameters);
            }

            // On the grid, so the flush does not depend on the buffers.
            for (var b = 0; b < BodyBells; b++) {
                bodyLeft[b].FlushDenormals();
                bodyRight[b].FlushDenormals();
            }

            Compute();
        }

        static bool Change<T>(ref T field, T value) {
            if (EqualityComparer<T>.Default.Equals(field, value)) {
                return false;
            }

            field = value;
            return true;
        }

        void Compute() {
            var s = smoothed;
            var c = control;
            var rate = c.SampleRate;
            var tickRate = rate / ControlInterval;
            var kind = (PhysicalObject)(int)s[PhysicalModelParameters.Object];
            var material = Change(ref c.Kind, kind);
            material |= Change(ref c.Inharmonicity, s[PhysicalModelParameters.Inharmonicity] / 100.0);
            material |= Change(ref c.Stiffness, s[PhysicalModelParameters.Stiffness] / 100.0);
            material |= Change(ref c.Damping, s[PhysicalModelParameters.Damping] / 100.0);
            c.Revision += material ? 1 : 0;
            c.B = 0.05 * c.Inharmonicity * c.Inharmonicity;
            c.DampingExponent = 1.5 - 1.3 * c.Stiffness;
            var shapes = false;
            for (var n = 0; n < PhysicalModelParameters.Exciters; n++) {
                var at = PhysicalModelParameters.ExciterType(n + 1);
                shapes |= Change(ref c.ExciterKind[n], (PhysicalExciter)(int)s[at]);
                shapes |= Change(ref c.ExciterPosition[n], s[at + 1] / 100.0);
                shapes |= Change(ref c.ExciterLevel[n], s[at + 2] / 100.0);
                shapes |= Change(ref c.ExciterTone[n], s[at + 3] / 100.0);
            }

            shapes |= Change(ref c.Pickup1, s[PhysicalModelParameters.Pickup1Position] / 100.0);
            shapes |= Change(ref c.Pickup2, s[PhysicalModelParameters.Pickup2Position] / 100.0);
            shapes |= Change(ref c.Spread, s[PhysicalModelParameters.PickupSpread] / 100.0);
            c.ShapeRevision += shapes ? 1 : 0;
            c.Mode = (FilterMode)(int)s[PhysicalModelParameters.FilterMode];
            c.Cutoff = s[PhysicalModelParameters.Cutoff];
            c.Resonance = s[PhysicalModelParameters.Resonance] / 100.0;
            c.KeyTracking = s[PhysicalModelParameters.KeyTracking] / 100.0;
            c.FilterEnvelope = s[PhysicalModelParameters.FilterEnvAmount] / 100.0 * 6;
            for (var n = 0; n < 2; n++) {
                var at = PhysicalModelParameters.EnvelopeAttack(n + 1);
                c.Envelopes[n] = AdsrSettings.From(
                    s[at] * 0.001,
                    0,
                    s[at + 1] * 0.001,
                    s[at + 2] / 100.0,
                    s[at + 3] * 0.001,
                    n == 0 ? rate : tickRate
                );
            }

            c.Lfo1Shape = (LfoShape)(int)s[PhysicalModelParameters.Lfo1Shape];
            c.Lfo1Increment = s[PhysicalModelParameters.Lfo1Rate] / tickRate;
            c.Lfo1Cutoff = s[PhysicalModelParameters.Lfo1Cutoff] / 100.0 * 2;
            c.Lfo2Shape = (LfoShape)(int)s[PhysicalModelParameters.Lfo2Shape];
            c.Lfo2Increment = s[PhysicalModelParameters.Lfo2Rate] / tickRate;
            c.Lfo2Pickup = s[PhysicalModelParameters.Lfo2Pickup] / 100.0 * 0.25;
            c.VibratoIncrement = s[PhysicalModelParameters.VibratoRate] / tickRate;
            c.VibratoCents = s[PhysicalModelParameters.VibratoDepth] + 50 * ModWheel;
            c.PitchBend = Bend * s[PhysicalModelParameters.BendRange];
            c.VelocitySensitivity = s[PhysicalModelParameters.VelocitySensitivity] / 100f;

            var bodyKind = (int)s[PhysicalModelParameters.Body];
            var amount = s[PhysicalModelParameters.BodyAmount] / 100.0;
            bodyOn = bodyKind != (int)PhysicalBody.Off && amount > 0;
            if (bodyOn) {
                var bells = Bodies[bodyKind];
                for (var b = 0; b < BodyBells; b++) {
                    var (hz, q, db) = bells[b];
                    body[b] = SvfEqCoefficients.Design(SvfEqType.Bell, rate, hz, q, db * amount);
                }
            }
        }
    }
}

/// <summary>The control-rate values every <see cref="PhysicalVoice" /> reads.</summary>
internal sealed class PhysicalControl {
    public AdsrSettings[] Envelopes { get; } = new AdsrSettings[2];
    public PhysicalExciter[] ExciterKind { get; } = new PhysicalExciter[PhysicalModelParameters.Exciters];
    public double[] ExciterPosition { get; } = new double[PhysicalModelParameters.Exciters];
    public double[] ExciterLevel { get; } = new double[PhysicalModelParameters.Exciters];
    public double[] ExciterTone { get; } = new double[PhysicalModelParameters.Exciters];
    public double SampleRate;
    public PhysicalObject Kind;
    public double Inharmonicity;
    public double B;
    public double Stiffness;
    public double DampingExponent;
    public double Damping;

    /// <summary>Counts the changes of the object or its material, so a voice knows to recompute its modes.</summary>
    public long Revision;

    /// <summary>Counts the changes of the exciters and pickups, so a voice knows to recompute their weights.</summary>
    public long ShapeRevision;

    public double Pickup1;
    public double Pickup2;
    public double Spread;
    public FilterMode Mode;
    public double Cutoff;
    public double Resonance;
    public double KeyTracking;
    public double FilterEnvelope;
    public LfoShape Lfo1Shape;
    public double Lfo1Increment;
    public double Lfo1Cutoff;
    public LfoShape Lfo2Shape;
    public double Lfo2Increment;
    public double Lfo2Pickup;
    public double VibratoIncrement;
    public double VibratoCents;
    public double PitchBend;
    public float VelocitySensitivity;
    public float StealStep;
}

/// <summary>
///     One voice of <see cref="PhysicalModel" />: its modes as complex one-pole resonators (z ← g·e<sup>iω</sup>·z;
///     the displacement is Im z), so a mode's frequency can move (bend, vibrato) without its amplitude jumping.
/// </summary>
internal sealed class PhysicalVoice(PhysicalControl control) : Voice {
    const int Modes = PhysicalModel.MaxModes;

    /// <summary>The level of the voice's output for an exciter at full level.</summary>
    const double OutputGain = 0.7;

    /// <summary>The noise a sustained exciter seeds each mode with, relative to its target.</summary>
    const double SeedNoise = 1e-4;

    /// <summary>The bow's noise in the pickups at its lowest tone (the breath's is 3×).</summary>
    const double TextureNoise = 0.006;

    readonly double[] real = new double[Modes];
    readonly double[] imaginary = new double[Modes];
    readonly double[] cosines = new double[Modes];
    readonly double[] sines = new double[Modes];
    readonly double[] decays = new double[Modes];
    readonly double[] ratios = new double[Modes];
    readonly double[] pickupLeft = new double[Modes];
    readonly double[] pickupRight = new double[Modes];
    readonly double[] targets = new double[Modes];
    readonly double[] noiseGains = new double[Modes];
    Adsr amp;
    Adsr filterEnvelope;
    Lfo lfo1;
    Lfo lfo2;
    MultimodeFilter filterLeft;
    MultimodeFilter filterRight;
    MultimodeCoefficients coefficients;
    int modes;
    double frequency;
    double vibratoPhase;
    double drive;
    double driveStep;
    double driveRise;
    double driveFall;
    double growth;
    double noiseLevel;
    double noiseCoefficient;
    double noiseState;
    double velocityGain;
    long revision = -1;
    long shapeRevision = -1;
    float fade = 1;
    int untilTick;
    int quietTicks;
    bool sustained;
    uint noise;

    public void Start(int note, int v) {
        Begin(note);
        Array.Clear(real);
        Array.Clear(imaginary);
        amp.Reset();
        filterEnvelope.Reset();
        lfo1.Reset();
        lfo2.Reset();
        filterLeft.Reset();
        filterRight.Reset();
        vibratoPhase = 0;
        drive = 0;
        noiseState = 0;
        fade = 1;
        untilTick = 0;
        quietTicks = 0;
        revision = -1;
        noise = (0x9E3779B9u * (uint)(note + 1)) | 1;
        Trigger(v);
    }

    public void Retrigger(int note, int v) {
        Begin(note);
        Trigger(v);
    }

    void Trigger(int v) {
        velocityGain = Pitch.Velocity(v, control.VelocitySensitivity);
        frequency = Pitch.Frequency(Note + control.PitchBend);
        Tune(frequency);
        Strike();
        amp.Trigger();
        filterEnvelope.Trigger();
    }

    protected override void OnRelease() {
        amp.Release();
        filterEnvelope.Release();
    }

    protected override void OnSteal() { }

    protected override void OnKill() {
        amp.Reset();
        filterEnvelope.Reset();
        filterLeft.Reset();
        filterRight.Reset();
        fade = 1;
        drive = 0;
    }

    /// <summary>The modes for a fundamental of <paramref name="hz" />: their count, rotations, decays and targets.</summary>
    void Tune(double hz) {
        var c = control;
        var nyquist = 0.45 * c.SampleRate;
        var t60 = PhysicalModel.DecaySeconds(c.Damping, Pitch.Frequency(Note));
        modes = 0;
        for (var n = 1; n <= Modes; n++) {
            var ratio = PhysicalModel.Ratio(c.Kind, n, c.B);
            if (hz * ratio >= nyquist) {
                break;
            }

            ratios[modes] = ratio;
            var seconds = t60 * Math.Pow(1 / ratio, c.DampingExponent);
            decays[modes] = Math.Exp(-6.907755278982137 / (seconds * c.SampleRate));
            modes++;
        }

        // Modes no longer below the limit (a higher note retriggered, a bend up) are let go.
        for (var n = modes; n < Modes; n++) {
            real[n] = 0;
            imaginary[n] = 0;
        }

        Rotate(hz);
        Shapes();
        revision = c.Revision;
    }

    /// <summary>Each mode's rotation for a fundamental of <paramref name="hz" />.</summary>
    void Rotate(double hz) {
        var step = hz / control.SampleRate;
        for (var n = 0; n < modes; n++) {
            var phase = step * ratios[n];
            var cos = SineTable.Sin(phase + 0.25);
            var sin = SineTable.Sin(phase);
            // Exactly unit length, so the decay alone sets how a mode dies.
            var norm = 1 / Math.Sqrt(cos * (double)cos + sin * (double)sin);
            cosines[n] = cos * norm;
            sines[n] = sin * norm;
        }
    }

    /// <summary>The pickups' weights and the sustained exciters' targets at the current positions.</summary>
    void Shapes(double pickupOffset = 0) {
        var c = control;
        shapeRevision = c.ShapeRevision;
        var p1 = Math.Clamp(c.Pickup1 + pickupOffset, 0, 1);
        var p2 = Math.Clamp(c.Pickup2 - pickupOffset, 0, 1);
        var a = 0.5 + 0.5 * c.Spread;
        var b = 0.5 - 0.5 * c.Spread;
        sustained = false;
        noiseLevel = 0;
        for (var n = 0; n < modes; n++) {
            var one = PhysicalModel.Shape(c.Kind, n + 1, p1);
            var two = PhysicalModel.Shape(c.Kind, n + 1, p2);
            pickupLeft[n] = a * one + b * two;
            pickupRight[n] = b * one + a * two;
            targets[n] = 0;
        }

        var speed = 0.0;
        for (var e = 0; e < PhysicalModelParameters.Exciters; e++) {
            var kind = c.ExciterKind[e];
            if (kind is not (PhysicalExciter.Bow or PhysicalExciter.Blow) || c.ExciterLevel[e] <= 0) {
                continue;
            }

            sustained = true;
            var tone = c.ExciterTone[e];
            speed = Math.Max(speed, tone);
            noiseLevel = Math.Max(
                noiseLevel,
                TextureNoise * c.ExciterLevel[e] * (1 + 2 * tone) * (kind == PhysicalExciter.Blow ? 3 : 1)
            );
            for (var n = 0; n < modes; n++) {
                var k = n + 1;
                // A bow's 1/n, or a flute's falling spectrum with its odd modes stronger.
                var odd = (k & 1) == 1 ? 1.0 : 0.45;
                var spectrum = kind == PhysicalExciter.Bow ? 1.0 / k : odd / (k * k) * (1 + tone * (k - 1) * 0.5);
                targets[n] += c.ExciterLevel[e] * PhysicalModel.Shape(c.Kind, k, c.ExciterPosition[e]) * spectrum;
            }
        }

        // A sustained exciter speaks in 150 … 25 ms (its tone) and lets go in 40 ms.
        var rate = control.SampleRate;
        driveRise = 1 / ((0.15 - 0.125 * speed) * rate);
        driveFall = 1 / (0.04 * rate);
        growth = 1 / ((0.03 - 0.02 * speed) * rate);
        noiseLevel *= velocityGain;
        noiseCoefficient = 1 - Math.Exp(-2 * Math.PI * 2_500 / rate);
        for (var n = 0; n < modes; n++) {
            targets[n] = Math.Abs(targets[n]) * velocityGain;
            noiseGains[n] = targets[n] * SeedNoise;
        }
    }

    /// <summary>The impulse exciters (pick, strike): each mode's displacement set by its shape and spectrum.</summary>
    void Strike() {
        var c = control;
        for (var e = 0; e < PhysicalModelParameters.Exciters; e++) {
            var kind = c.ExciterKind[e];
            if (kind is not (PhysicalExciter.Pick or PhysicalExciter.Strike) || c.ExciterLevel[e] <= 0) {
                continue;
            }

            var tone = c.ExciterTone[e];
            // A plectrum's corner rises with the tone and the velocity; a mallet's with its hardness.
            var corner = kind == PhysicalExciter.Pick
                ? frequency * (3 + 30 * tone * tone) * (0.5 + velocityGain)
                : frequency * (1.5 + 14 * tone * tone) * (0.5 + velocityGain);
            var level = c.ExciterLevel[e] * velocityGain;
            for (var n = 0; n < modes; n++) {
                var hz = frequency * ratios[n];
                var x = hz / corner;
                var spectrum = kind == PhysicalExciter.Pick
                    ? 1.0 / ((n + 1) * Math.Sqrt(1 + x * x))
                    : 1.0 / (1 + x * x * x * x);
                // A displacement: on the real part, so the output (the imaginary part) starts from zero.
                real[n] += level * PhysicalModel.Shape(c.Kind, n + 1, c.ExciterPosition[e]) * spectrum;
            }
        }
    }

    void Tick() {
        var c = control;
        filterLeft.FlushDenormals();
        filterRight.FlushDenormals();
        var env2 = filterEnvelope.Next(c.Envelopes[1]);
        var lfoCutoff = lfo1.Next(c.Lfo1Shape, c.Lfo1Increment);
        var lfoPickup = lfo2.Next(c.Lfo2Shape, c.Lfo2Increment);
        var vibrato = c.VibratoCents > 0 ? SineTable.Sin(vibratoPhase) * c.VibratoCents / 100 : 0;
        vibratoPhase += c.VibratoIncrement;
        if (vibratoPhase >= 1) {
            vibratoPhase -= 1;
        }

        var hz = Pitch.Frequency(Note + c.PitchBend + vibrato);
        if (revision != c.Revision) {
            Tune(hz);
            frequency = hz;
        } else if (hz != frequency) {
            Rotate(hz);
            frequency = hz;
        }

        if (c.Lfo2Pickup > 0 || shapeRevision != c.ShapeRevision) {
            Shapes(c.Lfo2Pickup * lfoPickup);
        }

        var octaves = c.KeyTracking * (Note - 60) / 12 + c.FilterEnvelope * env2 + c.Lfo1Cutoff * lfoCutoff;
        coefficients = MultimodeCoefficients.Design(
            c.Mode,
            c.SampleRate,
            c.Cutoff * Math.Pow(2, octaves),
            MultimodeCoefficients.Quality(c.Mode, c.Resonance)
        );

        // The drive of a sustained exciter: up while the key (or the pedal) holds the note.
        var holding = sustained && (Gate || Sustained) && !Stolen;
        driveStep = holding ? driveRise : -driveFall;

        // With no drive left and every mode below −110 dB, the note is over.
        var energy = 0.0;
        for (var n = 0; n < modes; n++) {
            energy += real[n] * real[n] + imaginary[n] * imaginary[n];
        }

        quietTicks = !holding && drive <= 0 && energy < 1e-11 ? quietTicks + 1 : 0;
    }

    public void Render(Span<float> left, Span<float> right) {
        var c = control;
        var done = 0;
        while (done < left.Length && Active) {
            if (untilTick == 0) {
                Tick();
                if (quietTicks > 2) {
                    Kill();
                    return;
                }

                untilTick = PolyInstrument<PhysicalVoice>.ControlInterval;
            }

            var count = Math.Min(untilTick, left.Length - done);
            for (var i = done; i < done + count; i++) {
                drive = Math.Clamp(drive + driveStep, 0, 1);
                double l = 0, r = 0;
                if (drive > 0) {
                    var white = PolyBlep.White(ref noise);
                    noiseState += (white - noiseState) * noiseCoefficient;
                    l = r = drive * noiseLevel * noiseState;
                    for (var n = 0; n < modes; n++) {
                        var re = real[n];
                        var im = imaginary[n];
                        // Negative damping toward the target amplitude: a bow's or a jet's feedback, regulated.
                        var target = targets[n];
                        var g = decays[n];
                        if (target > 0) {
                            var level = (re * re + im * im) / (target * target);
                            g += drive * growth * Math.Max(-1, 1 - level);
                        }

                        var nr = g * (cosines[n] * re - sines[n] * im) + drive * noiseGains[n] * white;
                        var ni = g * (sines[n] * re + cosines[n] * im);
                        real[n] = nr;
                        imaginary[n] = ni;
                        l += pickupLeft[n] * ni;
                        r += pickupRight[n] * ni;
                    }
                } else {
                    for (var n = 0; n < modes; n++) {
                        var re = real[n];
                        var im = imaginary[n];
                        var g = decays[n];
                        var nr = g * (cosines[n] * re - sines[n] * im);
                        var ni = g * (sines[n] * re + cosines[n] * im);
                        real[n] = nr;
                        imaginary[n] = ni;
                        l += pickupLeft[n] * ni;
                        r += pickupRight[n] * ni;
                    }
                }

                var gain = amp.Next(c.Envelopes[0]) * fade * OutputGain;
                left[i] += (float)(filterLeft.Tick(coefficients, l) * gain);
                right[i] += (float)(filterRight.Tick(coefficients, r) * gain);
                if (amp.IsIdle || (Stolen && (fade -= c.StealStep) <= 0)) {
                    Kill();
                    return;
                }
            }

            untilTick -= count;
            done += count;
        }
    }
}
