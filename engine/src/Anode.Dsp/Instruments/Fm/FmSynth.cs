using Anode.Dsp.Envelopes;
using Anode.Dsp.Modulation;

namespace Anode.Dsp.Instruments.Fm;

/// <summary>
///     <c>anode.fm</c>'s parameters. Operator parameters have ids <c>op{n}{Name}</c> for operators 1 … 4 (for example
///     <c>op2Ratio</c>, <c>op4Level</c>); <see cref="FmAlgorithms" /> says how each algorithm routes them.
/// </summary>
public static class FmParameters {
    public const int Operators = 4;

    static readonly ParameterList List = new();

    public static readonly int Algorithm = List.Choice("algorithm", "Algorithm", FmAlgorithms.Names, 4, 1);
    public static readonly int Feedback = List.Add("feedback", "Feedback", 0, 100, 0, "%", 4);

    static readonly int[] Ratios =
        Each(static n => List.Add($"op{n}Ratio", $"Op {n} Ratio", 0.5f, 16, n == 4 ? 14 : 1, "×"));

    static readonly int[] Fines =
        Each(static n => List.Add($"op{n}Fine", $"Op {n} Fine", -100, 100, n == 3 ? 3 : 0, "ct"));

    static readonly int[] Levels = Each(static n =>
        List.Add(
            $"op{n}Level",
            $"Op {n} Level",
            0,
            100,
            n switch {
                1 => 100,
                2 => 45,
                3 => 70,
                _ => 25
            },
            "%",
            n == 2 ? 2 : n == 4 ? 3 : null
        )
    );

    static readonly int[] Attacks = Each(static n => List.Add($"op{n}Attack", $"Op {n} Attack", 0, 10_000, 1, "ms"));

    static readonly int[] Decays = Each(static n =>
        List.Add($"op{n}Decay", $"Op {n} Decay", 1, 20_000, n == 4 ? 300 : 2_500, "ms")
    );

    static readonly int[] Sustains =
        Each(static n => List.Add($"op{n}Sustain", $"Op {n} Sustain", 0, 100, n == 4 ? 0 : 30, "%"));

    static readonly int[] Releases =
        Each(static n => List.Add($"op{n}Release", $"Op {n} Release", 1, 10_000, 400, "ms"));

    static readonly int[] Velocities = Each(static n =>
        List.Add($"op{n}Velocity", $"Op {n} Velocity Sensitivity", 0, 100, n == 1 ? 60 : 80, "%")
    );

    public static readonly int VibratoRate = List.Add("vibratoRate", "Vibrato Rate", 0.1f, 12, 5.5f, "Hz");
    public static readonly int VibratoDepth = List.Add("vibratoDepth", "Vibrato Depth (Mod Wheel)", 0, 100, 30, "ct");
    public static readonly int BendRange = List.Stepped("bendRange", "Pitch Bend Range", 0, 24, 2, "st", 24);
    public static readonly int Volume = List.Add("volume", "Volume", -60, 6, -6, "dB", 5);

    public static IReadOnlyList<InstrumentParameter> All => List.All;

    /// <summary>The index of operator <paramref name="op" />'s (1 … 4) ratio.</summary>
    public static int Ratio(int op) => Ratios[op - 1];

    public static int Fine(int op) => Fines[op - 1];
    public static int Level(int op) => Levels[op - 1];
    public static int Attack(int op) => Attacks[op - 1];
    public static int Decay(int op) => Decays[op - 1];
    public static int Sustain(int op) => Sustains[op - 1];
    public static int Release(int op) => Releases[op - 1];
    public static int Velocity(int op) => Velocities[op - 1];

    static int[] Each(Func<int, int> add) {
        var indices = new int[Operators];
        for (var n = 1; n <= Operators; n++) {
            indices[n - 1] = add(n);
        }

        return indices;
    }
}

/// <summary>
///     The eight 4-operator algorithms (after the 4-operator Yamaha FM synths). Operator 4 has the feedback; a
///     modulator always has a higher number than what it modulates, so a sample computes operators 4, 3, 2, 1 in order.
/// </summary>
public static class FmAlgorithms {
    /// <summary>The algorithm names, as the <c>algorithm</c> parameter's values.</summary>
    public static IReadOnlyList<string> Names { get; } = [
        "1: 4→3→2→1", "2: (3+4)→2→1", "3: (3→2)+4→1", "4: (4→3)+2→1", "5: 2→1, 4→3", "6: 4→1, 4→2, 4→3",
        "7: 4→3, 2, 1", "8: 1, 2, 3, 4"
    ];

    /// <summary>
    ///     Per algorithm, per operator (1 … 4 at 0 … 3), a bit mask of the operators modulating it (bit <c>k</c>:
    ///     operator <c>k + 1</c>).
    /// </summary>
    internal static int[][] Modulators { get; } = [
        [0b0010, 0b0100, 0b1000, 0], [0b0010, 0b1100, 0, 0], [0b1010, 0b0100, 0, 0], [0b0110, 0, 0b1000, 0],
        [0b0010, 0, 0b1000, 0], [0b1000, 0b1000, 0b1000, 0], [0, 0, 0b1000, 0], [0, 0, 0, 0]
    ];

    /// <summary>Per algorithm, a bit mask of the carriers (the operators heard).</summary>
    internal static int[] Carriers { get; } = [0b0001, 0b0001, 0b0001, 0b0001, 0b0101, 0b0111, 0b0111, 0b1111];

    /// <summary>
    ///     Algorithm <paramref name="algorithm" />'s (0 … 7) routing: for each operator (1 … 4 at 0 … 3) the operators
    ///     modulating it, and the carriers.
    /// </summary>
    public static (IReadOnlyList<int>[] Modulators, IReadOnlyList<int> Carriers) Routing(int algorithm) {
        var modulators = new IReadOnlyList<int>[FmParameters.Operators];
        for (var op = 0; op < FmParameters.Operators; op++) {
            modulators[op] = Bits(Modulators[algorithm][op]);
        }

        return (modulators, Bits(Carriers[algorithm]));
    }

    static int[] Bits(int mask) =>
        [..Enumerable.Range(0, 4).Where(k => (mask & (1 << k)) != 0).Select(static k => k + 1)];
}

/// <summary>
///     <c>anode.fm</c>: a 4-operator FM (phase modulation) synthesizer, 16 voices, 8 algorithms. Each operator is a sine
///     at a ratio of the note's frequency (0.5 … 16, with a fine tune) through its own ADSR, its output scaled by its
///     level and its velocity sensitivity. A modulator's output moves its targets' phase by up to 2 cycles at level
///     100 % (the level squared); the carriers are summed and divided by their count. Operator 4 feeds back on itself
///     (the mean of its last two outputs, up to 0.3 cycles). Vibrato comes in with the mod wheel.
/// </summary>
public sealed class FmSynth : IInstrument, IPolyphonic {
    public const string Id = "anode.fm";
    public const int Polyphony = 16;

    readonly Engine engine = new();

    public static InstrumentDescriptor Info { get; } = new(Id, "FM Synth", "Anode", "1.0.0", FmParameters.All, []);

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

    sealed class Engine : PolyInstrument<FmVoice> {
        readonly FmControl control;
        readonly ParameterSmoother smoothed = new(FmParameters.All);
        Lfo lfo;

        public Engine() : this(new()) { }

        Engine(FmControl control) : base(Info, FmSynth.Polyphony, 4, i => new(control), FmParameters.Volume) {
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
            lfo.Reset();
            Snap();
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
        }

        protected override void ControlTick() {
            if (ActiveVoices == 0) {
                smoothed.Snap(Parameters);
            } else {
                smoothed.Tick(Parameters);
            }

            var vibrato = lfo.Next(LfoShape.Sine, smoothed[FmParameters.VibratoRate] * ControlInterval / SampleRate);
            Compute(vibrato);
        }

        void Snap() {
            smoothed.Snap(Parameters);
            if (Prepared) {
                Compute(0);
            }
        }

        void Compute(float vibrato) {
            var s = smoothed;
            var c = control;
            var algorithm = Math.Clamp((int)s[FmParameters.Algorithm], 0, 7);
            c.Carriers = FmAlgorithms.Carriers[algorithm];
            for (var op = 0; op < FmParameters.Operators; op++) {
                var n = op + 1;
                c.Modulators[op] = FmAlgorithms.Modulators[algorithm][op];
                c.Ratio[op] = s[FmParameters.Ratio(n)] * Math.Pow(2, s[FmParameters.Fine(n)] / 1_200);
                var level = s[FmParameters.Level(n)] / 100f;
                c.Level[op] = level * level;
                c.Velocity[op] = s[FmParameters.Velocity(n)] / 100f;
                c.Envelope[op] = AdsrSettings.From(
                    s[FmParameters.Attack(n)] * 0.001,
                    0,
                    s[FmParameters.Decay(n)] * 0.001,
                    s[FmParameters.Sustain(n)] / 100.0,
                    s[FmParameters.Release(n)] * 0.001,
                    c.SampleRate
                );
            }

            var carriers = System.Numerics.BitOperations.PopCount((uint)c.Carriers);
            c.CarrierGain = 1f / carriers;
            c.Feedback = s[FmParameters.Feedback] / 100f * 0.3f;
            c.PitchModulation = Bend * s[FmParameters.BendRange]
                + vibrato * ModWheel * s[FmParameters.VibratoDepth] / 100;
        }
    }
}

/// <summary>The control-rate values every <see cref="FmVoice" /> reads.</summary>
internal sealed class FmControl {
    public double[] Ratio { get; } = new double[FmParameters.Operators];
    public float[] Level { get; } = new float[FmParameters.Operators];
    public float[] Velocity { get; } = new float[FmParameters.Operators];
    public AdsrSettings[] Envelope { get; } = new AdsrSettings[FmParameters.Operators];
    public int[] Modulators { get; } = new int[FmParameters.Operators];
    public int Carriers = 1;
    public float CarrierGain = 1;
    public float Feedback;
    public double PitchModulation;
    public double SampleRate;
    public float StealStep;
}

/// <summary>One voice of <see cref="FmSynth" />: four operators with their envelopes.</summary>
internal sealed class FmVoice(FmControl control) : Voice {
    /// <summary>A modulator's reach at level 100 %, in cycles.</summary>
    const float Depth = 2;

    readonly double[] phase = new double[FmParameters.Operators];
    readonly double[] increment = new double[FmParameters.Operators];
    readonly float[] gain = new float[FmParameters.Operators];
    readonly float[] output = new float[FmParameters.Operators];
    readonly Adsr[] envelope = new Adsr[FmParameters.Operators];
    float feedback1;
    float feedback2;
    float fade = 1;
    int velocity;
    int untilTick;

    public void Start(int note, int velocity) {
        Begin(note);
        for (var op = 0; op < FmParameters.Operators; op++) {
            phase[op] = 0;
            envelope[op].Reset();
            output[op] = 0;
        }

        feedback1 = 0;
        feedback2 = 0;
        fade = 1;
        untilTick = 0;
        Trigger(velocity);
    }

    public void Retrigger(int note, int velocity) {
        Begin(note);
        Trigger(velocity);
    }

    void Trigger(int v) {
        velocity = v;
        for (var op = 0; op < FmParameters.Operators; op++) {
            envelope[op].Trigger();
        }
    }

    protected override void OnRelease() {
        for (var op = 0; op < FmParameters.Operators; op++) {
            envelope[op].Release();
        }
    }

    protected override void OnSteal() { }

    protected override void OnKill() {
        for (var op = 0; op < FmParameters.Operators; op++) {
            envelope[op].Reset();
        }

        fade = 1;
    }

    void Tick() {
        var c = control;
        var frequency = Pitch.Frequency(Note + c.PitchModulation) / c.SampleRate;
        for (var op = 0; op < FmParameters.Operators; op++) {
            increment[op] = Math.Min(frequency * c.Ratio[op], 0.49);
            gain[op] = c.Level[op] * Pitch.Velocity(velocity, c.Velocity[op]);
        }
    }

    public void Render(Span<float> left, Span<float> right) {
        var c = control;
        var done = 0;
        while (done < left.Length && Active) {
            if (untilTick == 0) {
                Tick();
                untilTick = PolyInstrument<FmVoice>.ControlInterval;
            }

            var count = Math.Min(untilTick, left.Length - done);
            for (var i = done; i < done + count; i++) {
                // Operator 4, with its feedback, then 3, 2 and 1, each modulated by the outputs of the ones above.
                var e4 = envelope[3].Next(c.Envelope[3]);
                var o4 = SineTable.Sin(phase[3] + c.Feedback * 0.5f * (feedback1 + feedback2)) * e4 * gain[3];
                feedback2 = feedback1;
                feedback1 = o4;
                output[3] = o4;
                for (var op = 2; op >= 0; op--) {
                    var mask = c.Modulators[op];
                    float m = 0;
                    if (mask != 0) {
                        for (var k = op + 1; k < FmParameters.Operators; k++) {
                            if ((mask & (1 << k)) != 0) {
                                m += output[k];
                            }
                        }
                    }

                    var e = envelope[op].Next(c.Envelope[op]);
                    output[op] = SineTable.Sin(phase[op] + m * Depth) * e * gain[op];
                }

                float y = 0;
                var carrying = false;
                for (var op = 0; op < FmParameters.Operators; op++) {
                    phase[op] += increment[op];
                    if (phase[op] >= 1) {
                        phase[op] -= 1;
                    }

                    if ((c.Carriers & (1 << op)) != 0) {
                        y += output[op];
                        carrying |= !envelope[op].IsIdle;
                    }
                }

                y *= c.CarrierGain * fade;
                left[i] += y;
                right[i] += y;
                if (!carrying || (Stolen && (fade -= c.StealStep) <= 0)) {
                    Kill();
                    return;
                }
            }

            untilTick -= count;
            done += count;
        }
    }
}
