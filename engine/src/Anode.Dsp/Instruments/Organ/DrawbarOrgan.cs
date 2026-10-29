using Anode.Dsp.Delays;
using Anode.Dsp.Envelopes;
using Anode.Dsp.Filters;
using Anode.Dsp.Oscillators;

namespace Anode.Dsp.Instruments.Organ;

/// <summary>
///     <c>anode.organ</c>'s parameters. The drawbars are <c>drawbar1</c> … <c>drawbar9</c> (16', 5⅓', 8', 4', 2⅔', 2',
///     1⅗', 1⅓', 1'), each 0 … 8 in whole steps.
/// </summary>
public static class OrganParameters {
    public const int Drawbars = 9;

    static readonly ParameterList List = new();

    /// <summary>The drawbars' footages, for labels.</summary>
    public static IReadOnlyList<string> Footages { get; } = ["16'", "5⅓'", "8'", "4'", "2⅔'", "2'", "1⅗'", "1⅓'", "1'"];

    static readonly int[] DrawbarIndices = Each();

    public static readonly int Percussion = List.Toggle("percussion", "Percussion", true, 12);

    public static readonly int PercussionHarmonic =
        List.Choice("percussionHarmonic", "Percussion Harmonic", ["Second", "Third"], 1);

    public static readonly int PercussionDecay =
        List.Choice("percussionDecay", "Percussion Decay", ["Fast", "Slow"], 0);

    public static readonly int PercussionVolume =
        List.Choice("percussionVolume", "Percussion Volume", ["Soft", "Normal"], 1);

    public static readonly int KeyClick = List.Add("keyClick", "Key Click", 0, 100, 30, "%", 13);

    public static readonly int VibratoChorus = List.Choice(
        "vibratoChorus",
        "Vibrato/Chorus",
        ["Off", "V1", "V2", "V3", "C1", "C2", "C3"],
        6,
        11
    );

    public static readonly int Rotary = List.Toggle("rotary", "Rotary", true);

    public static readonly int RotarySpeed =
        List.Choice("rotarySpeed", "Rotary Speed", ["Stop", "Slow", "Fast"], 1, 10);

    public static readonly int RotaryDrive = List.Add("rotaryDrive", "Rotary Drive", 0, 100, 20, "%", 14);
    public static readonly int RotaryBalance = List.Add("rotaryBalance", "Horn/Drum Balance", -100, 100, 0, "%");
    public static readonly int BendRange = List.Stepped("bendRange", "Pitch Bend Range", 0, 12, 2, "st", 12);
    public static readonly int Volume = List.Add("volume", "Volume", -60, 6, -6, "dB");

    public static IReadOnlyList<InstrumentParameter> All => List.All;

    /// <summary>The index of drawbar <paramref name="n" /> (1 … 9).</summary>
    public static int Drawbar(int n) => DrawbarIndices[n - 1];

    static int[] Each() {
        var indices = new int[Drawbars];
        for (var n = 1; n <= Drawbars; n++) {
            indices[n - 1] = List.Stepped(
                $"drawbar{n}",
                $"Drawbar {Footages[n - 1]}",
                0,
                8,
                n <= 3 ? 8 : 0,
                string.Empty,
                8,
                n
            );
        }

        return indices;
    }
}

/// <summary>
///     <c>anode.organ</c>: a tonewheel drawbar organ, 32 voices, with percussion, key click, the scanner
///     vibrato/chorus and a built-in rotary speaker.
/// </summary>
/// <remarks>
///     <list type="bullet">
///         <item>
///             Each key sums nine sines at the drawbars' harmonics (½, 1½, 1, 2, 3, 4, 5, 6, 8), each at 3 dB a
///             drawbar step (0 is off); a wheel above 5.9 kHz folds back an octave, as a tonewheel generator's top
///             octave does. The key contacts take 2 ms on and 10 ms off.
///         </item>
///         <item>
///             Percussion is single-triggered: only a key pressed with no other key down gets it; a decaying second or
///             third harmonic (fast: 0.4 s, slow: 1.4 s to −60 dB), and with it on, the 1' drawbar is silent.
///         </item>
///         <item>Key click: a 5 ms burst of band-passed noise at key down (and a softer one at key up).</item>
///         <item>
///             The scanner: a delay of 1 ms swept at 6.9 Hz by ±0.2, ±0.45 or ±0.8 ms (V1–V3); chorus (C1–C3) mixes
///             it equally with the dry signal.
///         </item>
///         <item>
///             The rotary: drive, an 800 Hz crossover, a horn (0.83 / 6.7 Hz) and a drum (0.67 / 5.7 Hz) rotor
///             accelerating toward their speeds (time constants 0.7 s and 3 s), each with Doppler (a delay swept
///             ±0.44 / ±0.2 ms) and amplitude modulation, heard by two microphones 90° apart. The mod wheel past half
///             way switches to fast.
///         </item>
///     </list>
///     The vibrato and rotary run while the organ sounds and for 50 ms after; once quiet they rest and start again from
///     the same point, so a note sounds the same wherever it starts.
/// </remarks>
public sealed class DrawbarOrgan : IInstrument, IPolyphonic {
    public const string Id = "anode.organ";
    public const int Polyphony = 32;

    readonly Engine engine = new();

    public static InstrumentDescriptor Info { get; } =
        new(Id, "Drawbar Organ", "Anode", "1.0.0", OrganParameters.All, []);

    public InstrumentDescriptor Descriptor => Info;

    public int MaxVoices => Polyphony;

    public int ActiveVoices => engine.ActiveVoices;

    public bool IsSounding(int note) => engine.IsSounding(note);

    /// <summary>The horn's rotation speed now, in Hz (tests).</summary>
    internal double HornSpeed => engine.HornSpeed;

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

    sealed class Engine : PolyInstrument<OrganVoice> {
        const double HornSlow = 0.83;
        const double HornFast = 6.7;
        const double DrumSlow = 0.67;
        const double DrumFast = 5.7;
        const double HornInertia = 0.7;
        const double DrumInertia = 3;
        const double TailSeconds = 0.05;

        readonly OrganControl control;
        readonly ParameterSmoother smoothed = new(OrganParameters.All);
        readonly float[] dry = new float[ControlInterval];
        readonly DelayLine scanner = new();
        readonly DelayLine horn = new();
        readonly DelayLine drum = new();
        OnePole crossover;
        double scannerPhase;
        double hornPhase;
        double drumPhase;
        double hornSpeed;
        double drumSpeed;
        double hornTarget;
        double drumTarget;
        double hornCoefficient;
        double drumCoefficient;
        double crossoverCoefficient;
        float scannerDepth;
        float chorusMix;
        float drive;
        float hornGain;
        float drumGain;
        bool rotary;
        int quiet;
        int tail;

        public Engine() : this(new()) { }

        Engine(OrganControl control) : base(
            Info,
            DrawbarOrgan.Polyphony,
            8,
            i => new(control, i),
            OrganParameters.Volume
        ) {
            this.control = control;
        }

        public double HornSpeed => hornSpeed;

        public override void Prepare(float sampleRate, int maxFrames) {
            control.SampleRate = sampleRate;
            control.StealStep = (float)(1 / (StealFadeSeconds * sampleRate));
            control.Keys = AdsrSettings.From(0.002, 0, 0, 1, 0.01, sampleRate);
            smoothed.Prepare(sampleRate, ControlInterval);
            scanner.Allocate((int)(0.004 * sampleRate) + 4);
            horn.Allocate((int)(0.004 * sampleRate) + 4);
            drum.Allocate((int)(0.004 * sampleRate) + 4);
            crossoverCoefficient = OnePole.Coefficient(sampleRate, 800);
            hornCoefficient = 1 - Math.Exp(-ControlInterval / (HornInertia * sampleRate));
            drumCoefficient = 1 - Math.Exp(-ControlInterval / (DrumInertia * sampleRate));
            tail = (int)(TailSeconds * sampleRate);
            base.Prepare(sampleRate, maxFrames);
        }

        public override bool LoadFile(string path) => false;

        public override byte[]? SaveState() => InstrumentState.Write(Id, null, Info.Parameters, Parameters);

        public override bool LoadState(ReadOnlySpan<byte> state) {
            if (!InstrumentState.TryRead(state, Id, out _, out var values)) {
                return false;
            }

            InstrumentState.Apply(values, Parameters);
            Rest();
            return true;
        }

        protected override void OnReset() => Rest();

        /// <summary>Back to rest: effects cleared, rotors at their set speeds from phase 0, parameters snapped.</summary>
        void Rest() {
            smoothed.Snap(Parameters);
            if (!Prepared) {
                return;
            }

            scanner.Reset();
            horn.Reset();
            drum.Reset();
            crossover.Reset();
            scannerPhase = 0;
            hornPhase = 0;
            drumPhase = 0.5;
            quiet = tail;
            Compute();
            hornSpeed = hornTarget;
            drumSpeed = drumTarget;
        }

        bool Resting => quiet >= tail;

        protected override void NoteOn(int note, int velocity) {
            if (ActiveVoices == 0 && Resting) {
                Rest();
            }

            // Percussion is single-triggered: only when no other key is down.
            var percussion = true;
            foreach (var voice in Voices) {
                if (voice.Active && voice.Gate) {
                    percussion = false;
                    break;
                }
            }

            foreach (var voice in Voices) {
                if (voice.Active && !voice.Stolen && voice.Note == note) {
                    voice.Retrigger(note, percussion);
                    Started(voice);
                    return;
                }
            }

            var fresh = Allocate();
            fresh.Start(note, percussion);
            Started(fresh);
        }

        protected override void ControlTick() {
            if (ActiveVoices == 0) {
                smoothed.Snap(Parameters);
            } else {
                smoothed.Tick(Parameters);
            }

            Compute();
            hornSpeed += (hornTarget - hornSpeed) * hornCoefficient;
            drumSpeed += (drumTarget - drumSpeed) * drumCoefficient;
        }

        void Compute() {
            var s = smoothed;
            var c = control;
            var percussion = s[OrganParameters.Percussion] >= 0.5f;
            for (var n = 1; n <= OrganParameters.Drawbars; n++) {
                var step = s[OrganParameters.Drawbar(n)];
                // 3 dB a step; the last drawbar is cut while the percussion is on, as on the instrument.
                var gain = step <= 0.001f || (percussion && n == 9) ? 0f : MathF.Pow(10, (step - 8) * 3 / 20f);
                c.Drawbar[n - 1] = gain * 0.12f;
            }

            c.Percussion = percussion;
            c.PercussionRatio = s[OrganParameters.PercussionHarmonic] >= 0.5f ? 3 : 2;
            c.PercussionLevel = s[OrganParameters.PercussionVolume] >= 0.5f ? 0.5f : 0.25f;
            c.PercussionCoefficient = AdsrSettings.T60Coefficient(
                s[OrganParameters.PercussionDecay] >= 0.5f ? 1.4 : 0.4,
                c.SampleRate
            );
            c.Click = s[OrganParameters.KeyClick] / 100f * 0.3f;
            c.PitchModulation = Bend * s[OrganParameters.BendRange];

            var mode = (int)s[OrganParameters.VibratoChorus];
            scannerDepth = (float)(mode switch {
                    1 or 4 => 0.0002,
                    2 or 5 => 0.00045,
                    3 or 6 => 0.0008,
                    _ => 0
                }
                * c.SampleRate);
            chorusMix = mode >= 4 ? 0.5f : 0;

            rotary = s[OrganParameters.Rotary] >= 0.5f;
            var speed = (int)s[OrganParameters.RotarySpeed];
            if (ModWheel >= 0.5f) {
                speed = 2;
            }

            hornTarget = speed switch {
                0 => 0,
                1 => HornSlow,
                _ => HornFast
            };
            drumTarget = speed switch {
                0 => 0,
                1 => DrumSlow,
                _ => DrumFast
            };
            drive = s[OrganParameters.RotaryDrive] / 100f * 4;
            var balance = s[OrganParameters.RotaryBalance] / 100f;
            hornGain = Math.Min(1, 1 + balance);
            drumGain = Math.Min(1, 1 - balance);
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

            if (sounding) {
                quiet = 0;
            } else if (Resting) {
                return; // at rest: nothing sounds, nothing moves
            } else {
                quiet += frames;
            }

            var rate = control.SampleRate;
            var scannerIncrement = 6.9 / rate;
            var hornIncrement = hornSpeed / rate;
            var drumIncrement = drumSpeed / rate;
            var hornReach = (float)(0.00044 * rate);
            var drumReach = (float)(0.0002 * rate);
            var center = (float)(0.002 * rate);
            var scannerCenter = (float)(0.001 * rate);
            for (var i = 0; i < frames; i++) {
                var x = mono[i];
                if (scannerDepth > 0) {
                    scanner.Write(x);
                    var swept = scanner.ReadCubic(scannerCenter + scannerDepth * SineTable.Sin(scannerPhase));
                    x = chorusMix > 0 ? (x + swept) * chorusMix : swept;
                    scannerPhase += scannerIncrement;
                }

                if (!rotary) {
                    left[i] += x;
                    right[i] += x;
                    continue;
                }

                if (drive > 0) {
                    x = MathF.Tanh(x * (1 + drive)) / MathF.Tanh(1 + drive);
                }

                var low = (float)crossover.LowPass(crossoverCoefficient, x);
                horn.Write(x - low);
                drum.Write(low);
                float l = 0, r = 0;
                // Each rotor as two microphones 90° apart hear it: Doppler (the delay) and the horn's beam (the gain).
                for (var mic = 0; mic < 2; mic++) {
                    var offset = mic * 0.25;
                    var hornSin = SineTable.Sin(hornPhase - offset);
                    var hornCos = SineTable.Sin(hornPhase - offset + 0.25);
                    var drumSin = SineTable.Sin(drumPhase - offset);
                    var drumCos = SineTable.Sin(drumPhase - offset + 0.25);
                    var y = horn.ReadCubic(center - hornReach * hornSin) * (0.6f + 0.4f * hornCos) * hornGain
                        + drum.ReadCubic(center - drumReach * drumSin) * (0.8f + 0.2f * drumCos) * drumGain;
                    if (mic == 0) {
                        l = y;
                    } else {
                        r = y;
                    }
                }

                left[i] += l;
                right[i] += r;
                hornPhase += hornIncrement;
                drumPhase += drumIncrement;
            }

            scannerPhase -= Math.Floor(scannerPhase);
            hornPhase -= Math.Floor(hornPhase);
            drumPhase -= Math.Floor(drumPhase);
        }
    }
}

/// <summary>The control-rate values every <see cref="OrganVoice" /> reads.</summary>
internal sealed class OrganControl {
    public float[] Drawbar { get; } = new float[OrganParameters.Drawbars];
    public AdsrSettings Keys;
    public double SampleRate;
    public double PitchModulation;
    public bool Percussion;
    public int PercussionRatio = 3;
    public float PercussionLevel;
    public float PercussionCoefficient;
    public float Click;
    public float StealStep;
}

/// <summary>One key of <see cref="DrawbarOrgan" />: nine tonewheels, the percussion and the key click.</summary>
internal sealed class OrganVoice(OrganControl control, int index) : Voice {
    const double FoldBack = 5_900;

    static readonly double[] Harmonics = [0.5, 1.5, 1, 2, 3, 4, 5, 6, 8];

    readonly double[] phase = new double[OrganParameters.Drawbars];
    readonly double[] increment = new double[OrganParameters.Drawbars];
    Adsr keys;
    double percussionPhase;
    double percussionIncrement;
    float percussion;
    float click;
    float clickCoefficient;
    float fade = 1;
    int untilTick;
    uint noise;
    SvfCoefficients clickBand;
    SvfState clickState;

    public void Start(int note, bool percussive) {
        Begin(note);
        for (var k = 0; k < phase.Length; k++) {
            // Tonewheels turn all the time: a key connects them where they are, not at phase 0.
            phase[k] = note * 0.618033988749895 * (k + 1) % 1;
        }

        percussionPhase = 0;
        keys.Reset();
        clickState.Reset();
        noise = (0x9E3779B9u * (uint)(index + 1)) | 1;
        fade = 1;
        untilTick = 0;
        Trigger(note, percussive);
    }

    public void Retrigger(int note, bool percussive) {
        Begin(note);
        Trigger(note, percussive);
    }

    void Trigger(int note, bool percussive) {
        keys.Trigger();
        percussion = percussive && control.Percussion ? control.PercussionLevel : 0;
        click = control.Click;
        clickCoefficient = AdsrSettings.T60Coefficient(0.005, control.SampleRate);
        clickBand = SvfCoefficients.Design(control.SampleRate, 2_500 + 20 * note, 0.9);
    }

    protected override void OnRelease() {
        keys.Release();
        click = Math.Max(click, control.Click * 0.4f);
    }

    protected override void OnSteal() { }

    protected override void OnKill() {
        keys.Reset();
        percussion = 0;
        click = 0;
        fade = 1;
    }

    void Tick() {
        var c = control;
        var frequency = Pitch.Frequency(Note + c.PitchModulation);
        for (var k = 0; k < Harmonics.Length; k++) {
            var f = frequency * Harmonics[k];
            while (f > FoldBack) {
                f *= 0.5;
            }

            increment[k] = f / c.SampleRate;
        }

        percussionIncrement = Math.Min(frequency * c.PercussionRatio / c.SampleRate, 0.49);
    }

    /// <summary>Adds the key's next samples into <paramref name="output" /> (mono: the effects come after).</summary>
    public void Render(Span<float> output) {
        var c = control;
        for (var i = 0; i < output.Length; i++) {
            if (untilTick == 0) {
                Tick();
                untilTick = PolyInstrument<OrganVoice>.ControlInterval;
            }

            untilTick--;
            float y = 0;
            for (var k = 0; k < Harmonics.Length; k++) {
                var g = c.Drawbar[k];
                if (g != 0) {
                    y += g * SineTable.Sin(phase[k]);
                }

                phase[k] += increment[k];
                if (phase[k] >= 1) {
                    phase[k] -= 1;
                }
            }

            if (percussion > Adsr.Silence) {
                y += percussion * SineTable.Sin(percussionPhase) * 0.25f;
                percussion *= c.PercussionCoefficient;
            }

            y *= keys.Next(c.Keys);

            percussionPhase += percussionIncrement;
            if (percussionPhase >= 1) {
                percussionPhase -= 1;
            }

            if (click > Adsr.Silence) {
                y += click * (float)clickState.Tick(clickBand, PolyBlep.White(ref noise), SvfOutput.BandPass);
                click *= clickCoefficient;
            }

            output[i] += y * fade;
            if ((keys.IsIdle && click <= Adsr.Silence) || (Stolen && (fade -= c.StealStep) <= 0)) {
                Kill();
                return;
            }
        }
    }
}
