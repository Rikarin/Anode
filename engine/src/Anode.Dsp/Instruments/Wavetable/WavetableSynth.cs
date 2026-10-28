using Anode.Dsp.Envelopes;
using Anode.Dsp.Filters;
using Anode.Dsp.Oscillators;
using Anode.Dsp.Transport;

namespace Anode.Dsp.Instruments.Wavetable;

/// <summary>The modulation matrix's sources.</summary>
public enum WavetableSource {
    Off,
    Envelope2,
    Envelope3,
    Lfo1,
    Lfo2,
    Velocity,
    ModWheel,
    Key,
    PitchBend
}

/// <summary>The modulation matrix's destinations.</summary>
public enum WavetableDestination {
    Off,
    Osc1Position,
    Osc2Position,
    Osc1Pitch,
    Osc2Pitch,
    Pitch,
    Cutoff,
    Resonance,
    Osc1Level,
    Osc2Level,
    SubLevel,
    Amp,
    Pan
}

/// <summary>
///     <c>anode.wavetable</c>'s parameters. Oscillators are <c>osc{n}…</c> (1, 2), envelopes <c>env{n}…</c> (1: amp,
///     2: filter, 3: modulation), LFOs <c>lfo{n}…</c> (1, 2) and the modulation matrix's slots
///     <c>mod{n}Source</c>, <c>mod{n}Destination</c>, <c>mod{n}Amount</c> (1 … 6).
/// </summary>
public static class WavetableParameters {
    public const int Slots = 6;

    static readonly ParameterList List = new();

    /// <summary>The wavetable banks' names (<see cref="Wavetables" />).</summary>
    public static IReadOnlyList<string> TableNames { get; } = ["Basic", "Harmonic", "Formant", "Sync", "FM"];

    static readonly string[] Shapes = ["Sine", "Triangle", "Saw", "Square", "Sample & Hold"];

    /// <summary>The LFO sync values: off (the rate in Hz), else a note length.</summary>
    static readonly string[] Syncs = ["Off", "1/1", "1/2", "1/4", "1/8", "1/16", "1/4T", "1/8T", "1/16T", "1/4.", "1/8."];

    static readonly string[] Sources =
        ["Off", "Env 2", "Env 3", "LFO 1", "LFO 2", "Velocity", "Mod Wheel", "Key", "Pitch Bend"];

    static readonly string[] Destinations = [
        "Off", "Osc 1 Position", "Osc 2 Position", "Osc 1 Pitch", "Osc 2 Pitch", "Pitch", "Cutoff", "Resonance",
        "Osc 1 Level", "Osc 2 Level", "Sub Level", "Amp", "Pan"
    ];

    static readonly string[] Modes = ["Low-pass 24 dB", "Low-pass 12 dB", "High-pass 12 dB", "Band-pass 12 dB"];

    /// <summary>Each sync value's length in quarter notes (0: off).</summary>
    internal static readonly double[] SyncQuarters = [0, 4, 2, 1, 0.5, 0.25, 2 / 3.0, 1 / 3.0, 1 / 6.0, 1.5, 0.75];

    public static readonly int Osc1Table = List.Choice("osc1Table", "Osc 1 Wavetable", TableNames, 0);
    public static readonly int Osc1Position = List.Add("osc1Position", "Osc 1 Position", 0, 100, 20, "%", 1);
    public static readonly int Osc1Octave = List.Stepped("osc1Octave", "Osc 1 Octave", -3, 3, 0, "oct", 6);
    public static readonly int Osc1Semitone = List.Stepped("osc1Semitone", "Osc 1 Semitone", -12, 12, 0, "st", 24);
    public static readonly int Osc1Fine = List.Add("osc1Fine", "Osc 1 Fine", -100, 100, 0, "ct");
    public static readonly int Osc1Level = List.Add("osc1Level", "Osc 1 Level", 0, 100, 80, "%");
    public static readonly int Osc2Table = List.Choice("osc2Table", "Osc 2 Wavetable", TableNames, 1);
    public static readonly int Osc2Position = List.Add("osc2Position", "Osc 2 Position", 0, 100, 40, "%", 2);
    public static readonly int Osc2Octave = List.Stepped("osc2Octave", "Osc 2 Octave", -3, 3, 0, "oct", 6);
    public static readonly int Osc2Semitone = List.Stepped("osc2Semitone", "Osc 2 Semitone", -12, 12, 0, "st", 24);
    public static readonly int Osc2Fine = List.Add("osc2Fine", "Osc 2 Fine", -100, 100, 7, "ct");
    public static readonly int Osc2Level = List.Add("osc2Level", "Osc 2 Level", 0, 100, 50, "%", 7);
    public static readonly int SubLevel = List.Add("subLevel", "Sub Level", 0, 100, 0, "%");
    public static readonly int SubShape = List.Choice("subShape", "Sub Shape", ["Sine", "Square"], 0);
    public static readonly int NoiseLevel = List.Add("noiseLevel", "Noise Level", 0, 100, 0, "%");
    public static readonly int FilterMode = List.Choice("filterMode", "Filter Mode", Modes, 0);
    public static readonly int Cutoff = List.Add("cutoff", "Cutoff", 20, 20_000, 3_000, "Hz", 3);
    public static readonly int Resonance = List.Add("resonance", "Resonance", 0, 100, 20, "%", 4);
    public static readonly int KeyTracking = List.Add("keyTracking", "Key Tracking", 0, 100, 50, "%");
    public static readonly int FilterEnvAmount =
        List.Add("filterEnvAmount", "Filter Env Amount", -100, 100, 30, "%", 5);

    static readonly int[] EnvelopeIndices = Envelopes();

    public static readonly int Lfo1Shape = List.Choice("lfo1Shape", "LFO 1 Shape", Shapes, 1);
    public static readonly int Lfo1Rate = List.Add("lfo1Rate", "LFO 1 Rate", 0.05f, 20, 0.5f, "Hz", 8);
    public static readonly int Lfo1Sync = List.Choice("lfo1Sync", "LFO 1 Sync", Syncs, 0);
    public static readonly int Lfo2Shape = List.Choice("lfo2Shape", "LFO 2 Shape", Shapes, 0);
    public static readonly int Lfo2Rate = List.Add("lfo2Rate", "LFO 2 Rate", 0.05f, 20, 5, "Hz");
    public static readonly int Lfo2Sync = List.Choice("lfo2Sync", "LFO 2 Sync", Syncs, 0);

    static readonly int[] SlotIndices = Matrix();

    public static readonly int BendRange = List.Stepped("bendRange", "Pitch Bend Range", 0, 24, 2, "st", 24);
    public static readonly int VelocitySensitivity =
        List.Add("velocitySensitivity", "Velocity Sensitivity", 0, 100, 70, "%");
    public static readonly int Volume = List.Add("volume", "Volume", -60, 6, -6, "dB");

    public static IReadOnlyList<InstrumentParameter> All => List.All;

    /// <summary>Envelope <paramref name="n" />'s (1 … 3) attack; its decay, sustain and release follow it.</summary>
    public static int EnvelopeAttack(int n) => EnvelopeIndices[n - 1];

    /// <summary>Slot <paramref name="n" />'s (1 … 6) source; its destination and amount follow it.</summary>
    public static int SlotSource(int n) => SlotIndices[n - 1];

    static int[] Envelopes() {
        var indices = new int[3];
        for (var n = 1; n <= 3; n++) {
            indices[n - 1] = List.Add($"env{n}Attack", $"Env {n} Attack", 0, 10_000, n == 1 ? 5 : 2, "ms", n == 1 ? 6 : null);
            List.Add($"env{n}Decay", $"Env {n} Decay", 1, 10_000, n == 3 ? 1_500 : 600, "ms");
            List.Add($"env{n}Sustain", $"Env {n} Sustain", 0, 100, n == 1 ? 80 : n == 2 ? 30 : 0, "%");
            List.Add($"env{n}Release", $"Env {n} Release", 1, 10_000, 400, "ms");
        }

        return indices;
    }

    static int[] Matrix() {
        // The default patch: Env 3 sweeps oscillator 1's table, LFO 1 drifts oscillator 2's, velocity opens the filter.
        (WavetableSource, WavetableDestination, float)[] defaults = [
            (WavetableSource.Envelope3, WavetableDestination.Osc1Position, 40),
            (WavetableSource.Lfo1, WavetableDestination.Osc2Position, 25),
            (WavetableSource.Velocity, WavetableDestination.Cutoff, 20)
        ];
        var indices = new int[Slots];
        for (var n = 1; n <= Slots; n++) {
            var (source, destination, amount) = n <= defaults.Length ? defaults[n - 1] : default;
            indices[n - 1] = List.Choice($"mod{n}Source", $"Mod {n} Source", Sources, (int)source);
            List.Choice($"mod{n}Destination", $"Mod {n} Destination", Destinations, (int)destination);
            List.Add($"mod{n}Amount", $"Mod {n} Amount", -100, 100, amount, "%");
        }

        return indices;
    }
}

/// <summary>
///     <c>anode.wavetable</c>: a 16-voice wavetable synthesizer. Per voice: two wavetable oscillators (a bank of
///     <see cref="Wavetables" />, a position across its frames, octave, semitone and fine tune, a level), a sub
///     oscillator an octave below oscillator 1 and noise, into a multimode filter with key tracking; three ADSRs (amp,
///     filter, modulation); two LFOs (five shapes, free in Hz or synced to the transport); and a six-slot modulation
///     matrix.
/// </summary>
/// <remarks>
///     <para>
///         The matrix's sources are Env 2, Env 3 and the LFOs (per voice), the velocity, the mod wheel, the key (−1 at
///         C-1 … 1 at G9: (note − 60)/60) and the pitch bend; at ±100 % a destination moves by: a position ±1 (the
///         whole table), a pitch ±12 st, the cutoff ±6 octaves, the resonance ±100 %, a level ±1, the amp by ±100 %
///         (never below 0) and the pan ±1. Modulation is evaluated per voice every 16 samples.
///     </para>
///     <para>
///         The LFOs are per voice and restart with each note. A synced LFO, while the transport plays, takes its phase
///         from the transport (the quarter-note position over the sync length), so every voice and every pass of a cycle
///         is in step; stopped, it runs at the synced rate from the note. This is what <see cref="SetTransport" /> is
///         for.
///     </para>
/// </remarks>
public sealed class WavetableSynth : IInstrument, IPolyphonic {
    public const string Id = "anode.wavetable";
    public const int Polyphony = 16;

    readonly Engine engine = new();

    public static InstrumentDescriptor Info { get; } =
        new(Id, "Wavetable Synth", "Anode", "1.0.0", WavetableParameters.All, []);

    public InstrumentDescriptor Descriptor => Info;

    public int MaxVoices => Polyphony;

    public int ActiveVoices => engine.ActiveVoices;

    public bool IsSounding(int note) => engine.IsSounding(note);

    public void Prepare(float sampleRate, int maxFrames) => engine.Prepare(sampleRate, maxFrames);

    public void SetParameter(int index, float value) => engine.SetParameter(index, value);

    public float GetParameter(int index) => engine.GetParameter(index);

    public void SetTransport(in TransportContext transport) => engine.SetTransport(transport);

    public void Process(ReadOnlySpan<MidiEvent> events, Span<float> left, Span<float> right) =>
        engine.Process(events, left, right);

    public void Reset() => engine.Reset();

    public bool LoadFile(string path) => false;

    public byte[]? SaveState() => engine.SaveState();

    public bool LoadState(ReadOnlySpan<byte> state) => engine.LoadState(state);

    public void Dispose() => engine.Dispose();

    sealed class Engine : PolyInstrument<WavetableVoice> {
        readonly WavetableControl control;
        readonly ParameterSmoother smoothed = new(WavetableParameters.All);
        TransportContext transport;
        long rendered;

        public Engine() : this(new()) { }

        Engine(WavetableControl control) : base(Info, WavetableSynth.Polyphony, 4, i => new(control, i), WavetableParameters.Volume) {
            this.control = control;
        }

        public override void Prepare(float sampleRate, int maxFrames) {
            Wavetables.Ensure();
            control.SampleRate = sampleRate;
            control.StealStep = (float)(1 / (StealFadeSeconds * sampleRate));
            smoothed.Prepare(sampleRate, ControlInterval);
            transport = TransportContext.Stopped(sampleRate, maxFrames);
            base.Prepare(sampleRate, maxFrames);
        }

        public void SetTransport(in TransportContext context) {
            transport = context;
            rendered = 0;
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

        protected override void OnReset() => Snap();

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

            rendered += left.Length;
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
            var rate = c.SampleRate;
            var tickRate = rate / ControlInterval;
            c.Table1 = (int)s[WavetableParameters.Osc1Table];
            c.Table2 = (int)s[WavetableParameters.Osc2Table];
            c.Position1 = s[WavetableParameters.Osc1Position] / 100;
            c.Position2 = s[WavetableParameters.Osc2Position] / 100;
            c.Tune1 = s[WavetableParameters.Osc1Octave] * 12 + s[WavetableParameters.Osc1Semitone]
                + s[WavetableParameters.Osc1Fine] / 100;
            c.Tune2 = s[WavetableParameters.Osc2Octave] * 12 + s[WavetableParameters.Osc2Semitone]
                + s[WavetableParameters.Osc2Fine] / 100;
            c.Level1 = s[WavetableParameters.Osc1Level] / 100;
            c.Level2 = s[WavetableParameters.Osc2Level] / 100;
            c.SubLevel = s[WavetableParameters.SubLevel] / 100;
            c.SubSquare = s[WavetableParameters.SubShape] >= 0.5f;
            c.NoiseLevel = s[WavetableParameters.NoiseLevel] / 100f;
            c.Mode = (FilterMode)(int)s[WavetableParameters.FilterMode];
            c.Cutoff = s[WavetableParameters.Cutoff];
            c.Resonance = s[WavetableParameters.Resonance] / 100;
            c.KeyTracking = s[WavetableParameters.KeyTracking] / 100;
            c.FilterEnvelope = s[WavetableParameters.FilterEnvAmount] / 100 * 6;
            for (var n = 0; n < 3; n++) {
                var at = WavetableParameters.EnvelopeAttack(n + 1);
                // The amp envelope runs per sample; the others at control rate.
                c.Envelopes[n] = AdsrSettings.From(
                    s[at] * 0.001,
                    0,
                    s[at + 1] * 0.001,
                    s[at + 2] / 100.0,
                    s[at + 3] * 0.001,
                    n == 0 ? rate : tickRate
                );
            }

            var playing = transport.Playing;
            var quarter = transport.Quarter + rendered * transport.QuartersPerSample;
            for (var n = 0; n < 2; n++) {
                var shapeIndex = n == 0 ? WavetableParameters.Lfo1Shape : WavetableParameters.Lfo2Shape;
                c.LfoShape[n] = (int)s[shapeIndex];
                var sync = WavetableParameters.SyncQuarters[(int)s[shapeIndex + 2]];
                var hz = sync > 0 ? transport.Bpm / 60 / sync : s[shapeIndex + 1];
                c.LfoIncrement[n] = hz / tickRate;
                c.LfoSynced[n] = sync > 0 && playing;
                c.LfoPhase[n] = sync > 0 ? quarter / sync - Math.Floor(quarter / sync) : 0;
            }

            for (var n = 0; n < WavetableParameters.Slots; n++) {
                var at = WavetableParameters.SlotSource(n + 1);
                c.Source[n] = (WavetableSource)(int)s[at];
                c.Destination[n] = (WavetableDestination)(int)s[at + 1];
                c.Amount[n] = s[at + 2] / 100;
            }

            c.ModWheel = ModWheel;
            c.Bend = Bend;
            c.PitchBend = Bend * s[WavetableParameters.BendRange];
            c.VelocitySensitivity = s[WavetableParameters.VelocitySensitivity] / 100f;
        }
    }
}

/// <summary>The control-rate values every <see cref="WavetableVoice" /> reads.</summary>
internal sealed class WavetableControl {
    public readonly AdsrSettings[] Envelopes = new AdsrSettings[3];
    public readonly int[] LfoShape = new int[2];
    public readonly double[] LfoIncrement = new double[2];
    public readonly double[] LfoPhase = new double[2];
    public readonly bool[] LfoSynced = new bool[2];
    public readonly WavetableSource[] Source = new WavetableSource[WavetableParameters.Slots];
    public readonly WavetableDestination[] Destination = new WavetableDestination[WavetableParameters.Slots];
    public readonly double[] Amount = new double[WavetableParameters.Slots];
    public double SampleRate;
    public int Table1;
    public int Table2;
    public double Position1;
    public double Position2;
    public double Tune1;
    public double Tune2;
    public double Level1;
    public double Level2;
    public double SubLevel;
    public bool SubSquare;
    public float NoiseLevel;
    public FilterMode Mode;
    public double Cutoff;
    public double Resonance;
    public double KeyTracking;
    public double FilterEnvelope;
    public float ModWheel;
    public float Bend;
    public double PitchBend;
    public float VelocitySensitivity;
    public float StealStep;
}

/// <summary>
///     One voice of <see cref="WavetableSynth" />. Modulation, pitch, filter coefficients and levels are updated every
///     16 samples counted from the voice's start.
/// </summary>
internal sealed class WavetableVoice(WavetableControl control, int index) : Voice {
    readonly double[] lfoPhase = new double[2];
    readonly float[] lfoHeld = new float[2];
    Adsr amp;
    Adsr filterEnvelope;
    Adsr modEnvelope;
    MultimodeFilter filter;
    MultimodeCoefficients coefficients;
    double phase1;
    double phase2;
    double phaseSub;
    double increment1;
    double increment2;
    double incrementSub;
    double position1;
    double position2;
    int level1;
    int level2;
    float gain1;
    float gain2;
    float gainSub;
    float gainLeft;
    float gainRight;
    float gain;
    float gainStep;
    float velocity;
    float velocityGain;
    float fade = 1;
    int untilTick;
    bool fresh;
    uint noise;
    uint random;

    public void Start(int note, int v) {
        Begin(note);
        phase1 = 0;
        phase2 = 0.25;
        phaseSub = 0;
        lfoPhase[0] = 0;
        lfoPhase[1] = 0;
        noise = (0x9E3779B9u * (uint)(index + 1)) | 1;
        random = (0x85EBCA6Bu * (uint)(index + 1)) | 1;
        lfoHeld[0] = PolyBlep.White(ref random);
        lfoHeld[1] = PolyBlep.White(ref random);
        amp.Reset();
        filterEnvelope.Reset();
        modEnvelope.Reset();
        filter.Reset();
        fade = 1;
        fresh = true;
        untilTick = 0;
        Trigger(v);
    }

    public void Retrigger(int note, int v) {
        Begin(note);
        Trigger(v);
    }

    void Trigger(int v) {
        velocity = v / 127f;
        velocityGain = Pitch.Velocity(v, control.VelocitySensitivity);
        amp.Trigger();
        filterEnvelope.Trigger();
        modEnvelope.Trigger();
    }

    protected override void OnRelease() {
        amp.Release();
        filterEnvelope.Release();
        modEnvelope.Release();
    }

    protected override void OnSteal() { }

    protected override void OnKill() {
        amp.Reset();
        filterEnvelope.Reset();
        modEnvelope.Reset();
        filter.Reset();
        fade = 1;
    }

    float Lfo(int n) {
        var c = control;
        var p = c.LfoSynced[n] ? c.LfoPhase[n] : lfoPhase[n];
        var value = c.LfoShape[n] switch {
            0 => SineTable.Sin(p),
            1 => (float)(p < 0.25 ? 4 * p : p < 0.75 ? 2 - 4 * p : 4 * p - 4),
            2 => (float)(2 * p - 1),
            3 => p < 0.5 ? 1f : -1f,
            _ => lfoHeld[n]
        };
        lfoPhase[n] += c.LfoIncrement[n];
        if (lfoPhase[n] >= 1) {
            lfoPhase[n] -= Math.Floor(lfoPhase[n]);
            lfoHeld[n] = PolyBlep.White(ref random);
        }

        return value;
    }

    void Tick() {
        var c = control;
        filter.FlushDenormals();
        var env2 = filterEnvelope.Next(c.Envelopes[1]);
        var env3 = modEnvelope.Next(c.Envelopes[2]);
        var lfo1 = Lfo(0);
        var lfo2 = Lfo(1);

        double pos1 = 0, pos2 = 0, pitch1 = 0, pitch2 = 0, cutoff = 0, resonance = 0, level1Mod = 0, level2Mod = 0;
        double subMod = 0, ampMod = 0, pan = 0;
        for (var n = 0; n < WavetableParameters.Slots; n++) {
            var destination = c.Destination[n];
            if (destination == WavetableDestination.Off || c.Amount[n] == 0) {
                continue;
            }

            double source = c.Source[n] switch {
                WavetableSource.Envelope2 => env2,
                WavetableSource.Envelope3 => env3,
                WavetableSource.Lfo1 => lfo1,
                WavetableSource.Lfo2 => lfo2,
                WavetableSource.Velocity => velocity,
                WavetableSource.ModWheel => c.ModWheel,
                WavetableSource.Key => (Note - 60) / 60.0,
                WavetableSource.PitchBend => c.Bend,
                _ => 0
            };
            var x = source * c.Amount[n];
            switch (destination) {
                case WavetableDestination.Osc1Position: pos1 += x; break;
                case WavetableDestination.Osc2Position: pos2 += x; break;
                case WavetableDestination.Osc1Pitch: pitch1 += 12 * x; break;
                case WavetableDestination.Osc2Pitch: pitch2 += 12 * x; break;
                case WavetableDestination.Pitch:
                    pitch1 += 12 * x;
                    pitch2 += 12 * x;
                    break;
                case WavetableDestination.Cutoff: cutoff += 6 * x; break;
                case WavetableDestination.Resonance: resonance += x; break;
                case WavetableDestination.Osc1Level: level1Mod += x; break;
                case WavetableDestination.Osc2Level: level2Mod += x; break;
                case WavetableDestination.SubLevel: subMod += x; break;
                case WavetableDestination.Amp: ampMod += x; break;
                case WavetableDestination.Pan: pan += x; break;
            }
        }

        var rate = c.SampleRate;
        var note = Note + c.PitchBend;
        increment1 = Math.Min(Pitch.Frequency(note + c.Tune1 + pitch1) / rate, 0.49);
        increment2 = Math.Min(Pitch.Frequency(note + c.Tune2 + pitch2) / rate, 0.49);
        incrementSub = Math.Min(Pitch.Frequency(note + c.Tune1 + pitch1 - 12) / rate, 0.49);
        level1 = Wavetables.Level(increment1);
        level2 = Wavetables.Level(increment2);
        position1 = Math.Clamp(c.Position1 + pos1, 0, 1);
        position2 = Math.Clamp(c.Position2 + pos2, 0, 1);
        gain1 = (float)Math.Clamp(c.Level1 + level1Mod, 0, 1) * 0.5f;
        gain2 = (float)Math.Clamp(c.Level2 + level2Mod, 0, 1) * 0.5f;
        gainSub = (float)Math.Clamp(c.SubLevel + subMod, 0, 1) * 0.5f;

        var octaves = c.KeyTracking * (Note - 60) / 12 + c.FilterEnvelope * env2 + cutoff;
        var mode = c.Mode;
        coefficients = MultimodeCoefficients.Design(
            mode,
            rate,
            c.Cutoff * Math.Pow(2, octaves),
            MultimodeCoefficients.Quality(mode, Math.Clamp(c.Resonance + resonance, 0, 1))
        );
        (gainLeft, gainRight) = Pitch.Pan(pan);

        var target = velocityGain * (float)Math.Max(0, 1 + ampMod);
        if (fresh) {
            gain = target;
            gainStep = 0;
            fresh = false;
        } else {
            gainStep = (target - gain) / PolyInstrument<WavetableVoice>.ControlInterval;
        }
    }

    public void Render(Span<float> left, Span<float> right) {
        var c = control;
        var done = 0;
        while (done < left.Length && Active) {
            if (untilTick == 0) {
                Tick();
                untilTick = PolyInstrument<WavetableVoice>.ControlInterval;
            }

            var count = Math.Min(untilTick, left.Length - done);
            for (var i = done; i < done + count; i++) {
                double x = 0;
                if (gain1 != 0) {
                    x += gain1 * Wavetables.Read(c.Table1, position1, phase1, level1);
                }

                if (gain2 != 0) {
                    x += gain2 * Wavetables.Read(c.Table2, position2, phase2, level2);
                }

                if (gainSub != 0) {
                    x += gainSub * (c.SubSquare ? PolyBlep.Pulse(phaseSub, incrementSub, 0.5) : SineTable.Sin(phaseSub));
                }

                if (c.NoiseLevel != 0) {
                    x += c.NoiseLevel * 0.5f * PolyBlep.White(ref noise);
                }

                phase1 += increment1;
                if (phase1 >= 1) {
                    phase1 -= 1;
                }

                phase2 += increment2;
                if (phase2 >= 1) {
                    phase2 -= 1;
                }

                phaseSub += incrementSub;
                if (phaseSub >= 1) {
                    phaseSub -= 1;
                }

                var y = (float)filter.Tick(coefficients, x) * amp.Next(c.Envelopes[0]) * gain * fade;
                gain += gainStep;
                left[i] += y * gainLeft;
                right[i] += y * gainRight;
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
