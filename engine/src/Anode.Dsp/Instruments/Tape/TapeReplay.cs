using Anode.Dsp.Envelopes;
using Anode.Dsp.Filters;
using Anode.Dsp.Oscillators;

namespace Anode.Dsp.Instruments.Tape;

/// <summary><c>anode.tape-replay</c>'s parameters.</summary>
public static class TapeReplayParameters {
    static readonly ParameterList List = new();

    public static readonly int Speed = List.Add("speed", "Tape Speed", 70, 130, 100, "%", 3);
    public static readonly int Tune = List.Add("tune", "Tune", -100, 100, 0, "ct");
    public static readonly int Wow = List.Add("wow", "Wow", 0, 100, 25, "%", 1);
    public static readonly int Flutter = List.Add("flutter", "Flutter", 0, 100, 25, "%", 2);
    public static readonly int MotorStart = List.Add("motorStart", "Start Motor Time", 0, 250, 15, "ms", 4);
    public static readonly int Attack = List.Add("attack", "Attack", 0, 1_000, 5, "ms");
    public static readonly int Release = List.Add("release", "Release", 5, 2_000, 150, "ms", 5);
    public static readonly int Tone = List.Add("tone", "Tone", 500, 20_000, 10_000, "Hz", 6);
    public static readonly int Hiss = List.Add("hiss", "Tape Hiss", 0, 100, 10, "%");

    public static readonly int VelocitySensitivity =
        List.Add("velocitySensitivity", "Velocity Sensitivity", 0, 100, 0, "%");

    public static readonly int BendRange = List.Stepped("bendRange", "Pitch Bend Range", 0, 12, 2, "st", 12);
    public static readonly int Volume = List.Add("volume", "Volume", -60, 6, -6, "dB", 7);

    public static IReadOnlyList<InstrumentParameter> All => List.All;
}

/// <summary>
///     <c>anode.tape-replay</c>, the tape-replay instrument (Mellotron-style): each key plays its own recording of a
///     <see cref="TapeSet" /> from its start, for at most the tape's length (<see cref="TapeSet.MaxSeconds" />, 8 s at
///     normal speed): a held note stops when its tape runs out. It loads a tape set (<c>.antape</c>: the content
///     builder's strings, flutes and choir) and has a generated one (<see cref="TapeSet.Default" />) until it does.
/// </summary>
/// <remarks>
///     <para>
///         Each voice reads its key's tape at the set's rate times the tape speed (70 … 130 %, which moves the pitch and
///         shortens or lengthens the tape's time), the tune and the bend, and the transport's irregularities: wow (a
///         0.7 Hz drift with a slower wander, up to ±0.45 % at 100 %) and flutter (9.5 and 15 Hz, up to ±0.15 %), each
///         voice with its own phases (seeded by its key) from its start. The start motor brings the tape from rest to
///         speed linearly over its time, so a slow motor swoops up to pitch; the tape's time is then 8 s plus half the
///         motor time. Keys outside the set play its nearest key an octave (or more) away, transposed by reading faster
///         or slower. Interpolation is cubic Hermite.
///     </para>
///     <para>
///         The level follows an attack and release (the pressure pad on and off the tape), the velocity (by its
///         sensitivity, 0 as the original), and the last 5 ms of the tape fade out. Tape hiss, while a tape moves, and a
///         12 dB/octave tone low-pass complete it. Voices update on their own 16-sample grid from their start, so a
///         note sounds the same wherever it falls.
///     </para>
/// </remarks>
public sealed class TapeReplay : IInstrument, IPolyphonic {
    public const string Id = "anode.tape-replay";

    /// <summary>One voice a key of the classic instrument's 35, plus a few for keys played again before they end.</summary>
    public const int Polyphony = 40;

    readonly Engine engine = new();

    public static InstrumentDescriptor Info { get; } =
        new(Id, "Tape Replay", "Anode", "1.0.0", TapeReplayParameters.All, [TapeSet.Extension]);

    public InstrumentDescriptor Descriptor => Info;

    public int MaxVoices => Polyphony;

    public int ActiveVoices => engine.ActiveVoices;

    /// <summary>The tape set it plays (the default until it loads one).</summary>
    public TapeSet Tapes => engine.Control.Tapes;

    /// <summary>The tape set file loaded, or null (the default set).</summary>
    public string? File => engine.Path;

    public bool IsSounding(int note) => engine.IsSounding(note);

    public void Prepare(float sampleRate, int maxFrames) => engine.Prepare(sampleRate, maxFrames);

    public void SetParameter(int index, float value) => engine.SetParameter(index, value);

    public float GetParameter(int index) => engine.GetParameter(index);

    public void Process(ReadOnlySpan<MidiEvent> events, Span<float> left, Span<float> right) =>
        engine.Process(events, left, right);

    public void Reset() => engine.Reset();

    public bool LoadFile(string path) => engine.LoadFile(path);

    public byte[]? SaveState() => engine.SaveState();

    public bool LoadState(ReadOnlySpan<byte> state) => engine.LoadState(state);

    public void Dispose() => engine.Dispose();

    sealed class Engine : PolyInstrument<TapeVoice> {
        readonly ParameterSmoother smoothed = new(TapeReplayParameters.All);

        public Engine() : this(new()) { }

        Engine(TapeControl control) : base(
            Info,
            TapeReplay.Polyphony,
            4,
            _ => new(control),
            TapeReplayParameters.Volume
        ) {
            Control = control;
        }

        public TapeControl Control { get; }

        public string? Path { get; private set; }

        public override void Prepare(float sampleRate, int maxFrames) {
            Control.SampleRate = sampleRate;
            Control.StealStep = (float)(1 / (StealFadeSeconds * sampleRate));
            smoothed.Prepare(sampleRate, ControlInterval);
            base.Prepare(sampleRate, maxFrames);
        }

        public override bool LoadFile(string path) {
            TapeSet tapes;
            try {
                path = System.IO.Path.GetFullPath(path);
                tapes = TapeSet.Load(path);
            } catch (Exception e) when (e is IOException
                                            or InvalidDataException
                                            or UnauthorizedAccessException
                                            or ArgumentException
                                            or NotSupportedException) {
                return false;
            }

            Control.Tapes = tapes;
            Path = path;
            Reset();
            return true;
        }

        public override byte[]? SaveState() => InstrumentState.Write(Id, Path, Info.Parameters, Parameters);

        public override bool LoadState(ReadOnlySpan<byte> state) {
            if (!InstrumentState.TryRead(state, Id, out var file, out var values)) {
                return false;
            }

            var loaded = true;
            if (file is null) {
                Control.Tapes = TapeSet.Default;
                Path = null;
            } else if (file != Path) {
                loaded = LoadFile(file);
            }

            InstrumentState.Apply(values, Parameters);
            Snap();
            return loaded;
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

            // A key played again starts its tape again: the old one fades as a stolen voice does.
            foreach (var voice in Voices) {
                if (voice.Active && !voice.Stolen && voice.Note == note) {
                    voice.Steal();
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

            Compute();
        }

        void Compute() {
            var s = smoothed;
            var c = Control;
            var rate = c.SampleRate;
            c.Speed = s[TapeReplayParameters.Speed] / 100.0;
            c.Tune = s[TapeReplayParameters.Tune] / 100.0;
            c.Wow = s[TapeReplayParameters.Wow] / 100.0 * 0.0045;
            c.Flutter = s[TapeReplayParameters.Flutter] / 100.0 * 0.0015;
            c.MotorSamples = s[TapeReplayParameters.MotorStart] * 0.001 * rate;
            c.Envelope = AdsrSettings.From(
                s[TapeReplayParameters.Attack] * 0.001,
                0,
                1,
                1,
                s[TapeReplayParameters.Release] * 0.001,
                rate
            );
            c.Filter = MultimodeCoefficients.Design(
                FilterMode.LowPass12,
                rate,
                s[TapeReplayParameters.Tone],
                MultimodeCoefficients.Quality(FilterMode.LowPass12, 0)
            );
            c.Hiss = s[TapeReplayParameters.Hiss] / 100f * 0.006f;
            c.VelocitySensitivity = s[TapeReplayParameters.VelocitySensitivity] / 100f;
            c.PitchBend = Bend * s[TapeReplayParameters.BendRange];
        }
    }
}

/// <summary>The control-rate values every <see cref="TapeVoice" /> reads.</summary>
internal sealed class TapeControl {
    public TapeSet Tapes = TapeSet.Default;
    public double SampleRate;
    public double Speed = 1;
    public double Tune;
    public double Wow;
    public double Flutter;
    public double MotorSamples;
    public AdsrSettings Envelope;
    public MultimodeCoefficients Filter;
    public float Hiss;
    public float VelocitySensitivity;
    public double PitchBend;
    public float StealStep;
}

/// <summary>One key's tape of <see cref="TapeReplay" />, read from its start.</summary>
internal sealed class TapeVoice(TapeControl control) : Voice {
    const int Interval = 16;

    /// <summary>The fade at the end of a tape, in seconds.</summary>
    const double EndFadeSeconds = 0.005;

    TapeSet tapes = TapeSet.Default;
    Adsr amp;
    MultimodeFilter filter;
    int key;
    double transpose;
    double position;
    double increment;
    double motor;
    double wowPhase;
    double wanderPhase;
    double flutterPhase;
    double flutterPhase2;
    double end;
    double fadeLength;
    float velocityGain;
    float fade = 1;
    int untilTick;
    uint noise;

    public void Start(int note, int velocity) {
        Begin(note);
        tapes = control.Tapes;
        // A key outside the set plays the nearest key of the set octaves away, read faster or slower.
        key = note - tapes.LowKey;
        transpose = 1;
        while (key < 0) {
            key += 12;
            transpose *= 0.5;
        }

        while (key >= tapes.KeyCount) {
            key -= 12;
            transpose *= 2;
        }

        key = Math.Clamp(key, 0, tapes.KeyCount - 1);
        position = 0;
        motor = 0;
        end = tapes.Frames(key);
        fadeLength = EndFadeSeconds * tapes.SampleRate;
        // Each tape wobbles in its own way: phases seeded by the key.
        var seed = (uint)(note * 2_654_435_761u) | 1;
        wowPhase = (seed & 0xFFFF) / 65_536.0;
        wanderPhase = ((seed >> 8) & 0xFFFF) / 65_536.0;
        flutterPhase = ((seed >> 16) & 0xFFFF) / 65_536.0;
        flutterPhase2 = (seed >> 24) / 256.0;
        noise = seed;
        velocityGain = Pitch.Velocity(velocity, control.VelocitySensitivity);
        amp.Reset();
        amp.Trigger();
        filter.Reset();
        fade = 1;
        untilTick = 0;
    }

    protected override void OnRelease() => amp.Release();

    protected override void OnSteal() { }

    protected override void OnKill() {
        amp.Reset();
        filter.Reset();
        fade = 1;
    }

    void Tick() {
        var c = control;
        filter.FlushDenormals();
        var seconds = Interval / c.SampleRate;
        wowPhase = Wrap(wowPhase + 0.7 * seconds);
        wanderPhase = Wrap(wanderPhase + 0.23 * seconds);
        flutterPhase = Wrap(flutterPhase + 9.5 * seconds);
        flutterPhase2 = Wrap(flutterPhase2 + 15 * seconds);
        var wobble = c.Wow * (0.7 * SineTable.Sin(wowPhase) + 0.3 * SineTable.Sin(wanderPhase))
            + c.Flutter * (0.75 * SineTable.Sin(flutterPhase) + 0.25 * SineTable.Sin(flutterPhase2));
        increment = tapes.SampleRate
            / c.SampleRate
            * transpose
            * c.Speed
            * Math.Pow(2, (c.Tune + c.PitchBend) / 12)
            * (1 + wobble);
    }

    static double Wrap(double phase) => phase >= 1 ? phase - 1 : phase;

    public void Render(Span<float> left, Span<float> right) {
        var c = control;
        var done = 0;
        while (done < left.Length && Active) {
            if (untilTick == 0) {
                Tick();
                untilTick = Interval;
            }

            var count = Math.Min(untilTick, left.Length - done);
            for (var i = done; i < done + count; i++) {
                if (position >= end) {
                    Kill();
                    return;
                }

                var x = tapes.Read(key, position) + c.Hiss * PolyBlep.White(ref noise);
                var tail = Math.Min(1, (end - position) / fadeLength);
                var y = (float)(filter.Tick(c.Filter, x) * tail) * amp.Next(c.Envelope) * velocityGain * fade;
                left[i] += y;
                right[i] += y;
                // The motor brings the tape up to speed over its time.
                motor = c.MotorSamples < 1 ? 1 : Math.Min(1, motor + 1 / c.MotorSamples);
                position += increment * motor;
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
