using Anode.Dsp.Envelopes;
using Anode.Dsp.Filters;
using Anode.Dsp.IO;
using Anode.Dsp.Oscillators;

namespace Anode.Dsp.Instruments.Alchemy;

/// <summary>How <see cref="SampleAlchemy" /> resynthesises its sample.</summary>
public enum AlchemyMode {
    Additive,
    Granular,
    Spectral
}

/// <summary><c>anode.sample-alchemy</c>'s parameters (envelopes <c>env{n}…</c>, 1: amp, 2: filter).</summary>
public static class SampleAlchemyParameters {
    static readonly ParameterList List = new();
    static readonly string[] Modes = ["Low-pass 24 dB", "Low-pass 12 dB", "High-pass 12 dB", "Band-pass 12 dB"];

    public static readonly int Mode = List.Choice("mode", "Mode", ["Additive", "Granular", "Spectral"], 0, 0);
    public static readonly int RootNote = List.Stepped("rootNote", "Root Note", 0, 127, 60, string.Empty, 127);
    public static readonly int Position = List.Add("position", "Start Position", 0, 100, 0, "%", 1);
    public static readonly int Loop = List.Toggle("loop", "Loop", true);
    public static readonly int LoopStart = List.Add("loopStart", "Loop Start", 0, 100, 30, "%");
    public static readonly int LoopEnd = List.Add("loopEnd", "Loop End", 0, 100, 80, "%");
    public static readonly int Speed = List.Add("speed", "Speed", 0, 200, 100, "%", 2);
    public static readonly int Partials = List.Stepped("partials", "Partials", 1, 64, 32, string.Empty, 63, 3);
    public static readonly int PartialSpread = List.Add("partialSpread", "Partial Spread", -100, 100, 0, "%", 4);
    public static readonly int GrainSize = List.Add("grainSize", "Grain Size", 10, 500, 80, "ms", 5);
    public static readonly int GrainDensity = List.Add("grainDensity", "Grain Density", 1, 100, 30, "/s", 6);
    public static readonly int GrainSpray = List.Add("grainSpray", "Grain Spray", 0, 500, 20, "ms");
    public static readonly int GrainPitch = List.Add("grainPitch", "Grain Pitch Spread", 0, 1_200, 0, "ct");
    public static readonly int Noise = List.Add("noise", "Noise", 0, 100, 20, "%");
    public static readonly int FilterMode = List.Choice("filterMode", "Filter Mode", Modes, 0);
    public static readonly int Cutoff = List.Add("cutoff", "Cutoff", 20, 20_000, 20_000, "Hz", 7);
    public static readonly int Resonance = List.Add("resonance", "Resonance", 0, 100, 0, "%");
    public static readonly int KeyTracking = List.Add("keyTracking", "Key Tracking", 0, 100, 0, "%");

    public static readonly int FilterEnvAmount =
        List.Add("filterEnvAmount", "Filter Env Amount", -100, 100, 0, "%");

    static readonly int[] EnvelopeIndices = Envelopes();

    public static readonly int Tune = List.Add("tune", "Tune", -100, 100, 0, "ct");
    public static readonly int BendRange = List.Stepped("bendRange", "Pitch Bend Range", 0, 24, 2, "st", 24);

    public static readonly int VelocitySensitivity =
        List.Add("velocitySensitivity", "Velocity Sensitivity", 0, 100, 70, "%");

    public static readonly int Volume = List.Add("volume", "Volume", -60, 12, 0, "dB");

    public static IReadOnlyList<InstrumentParameter> All => List.All;

    /// <summary>Envelope <paramref name="n" />'s (1, 2) attack; its decay, sustain and release follow it.</summary>
    public static int EnvelopeAttack(int n) => EnvelopeIndices[n - 1];

    static int[] Envelopes() {
        var indices = new int[2];
        for (var n = 1; n <= 2; n++) {
            indices[n - 1] = List.Add(
                $"env{n}Attack",
                $"Env {n} Attack",
                0,
                10_000,
                n == 1 ? 5 : 2,
                "ms",
                n == 1 ? 8 : null
            );
            List.Add($"env{n}Decay", $"Env {n} Decay", 1, 10_000, 600, "ms");
            List.Add($"env{n}Sustain", $"Env {n} Sustain", 0, 100, n == 1 ? 100 : 30, "%");
            List.Add($"env{n}Release", $"Env {n} Release", 1, 10_000, n == 1 ? 300 : 400, "ms", n == 1 ? 9 : null);
        }

        return indices;
    }
}

/// <summary>
///     <c>anode.sample-alchemy</c>: a 16-voice resynthesizer of one sample (a <c>.wav</c>, <c>.aif</c> or <c>.aiff</c>,
///     or the generated default: a sung "ah" on C4), analysed when it loads (<see cref="AlchemySource" />) and played
///     back, tuned to each key's pitch, in one of three modes, then a multimode filter with its envelope and the amp
///     ADSR.
/// </summary>
/// <remarks>
///     <para>
///         <b>The playhead.</b> Every mode reads the sample at a playhead that starts at Start Position and moves at
///         Speed (100 % the sample's own time, 0 frozen); with Loop it jumps from Loop End back to Loop Start (the
///         partials glide over the jump), without it the note ends with the sample (a 10 ms fade). Keys play their own
///         pitch: the sample's detected pitch is moved to the key's (the root note is the pitch assumed of a sample with
///         none, and what a file's <c>smpl</c> or <c>INST</c> note sets).
///     </para>
///     <para>
///         <b>Additive.</b> The tracked partials (Partials of them) at the playhead's frame, each a sine at its frequency
///         over the sample's pitch times the key's, its amplitude glided per 16-sample tick; Partial Spread stretches
///         partial k by k<sup>0.15·spread</sup> (inharmonic up, squeezed down; the first stays).
///     </para>
///     <para>
///         <b>Granular.</b> Hann grains of Grain Size, Grain Density a second (±20 % apart, seeded by the key), each from
///         the playhead plus a random Grain Spray, read at the key's pitch over the sample's plus a random Grain Pitch
///         Spread, up to <see cref="MaxGrains" /> at once, summed at 1/√overlap. A grain starts within a period of the
///         sample's pitch of where it would, where the sample's phase meets a steady tone's at the key's pitch, so the
///         grains of a pitched sample add up in phase (pitch-synchronous) instead of combing at the grain rate.
///     </para>
///     <para>
///         <b>Spectral.</b> The key's harmonics (Partials of them), each at the smoothed spectral envelope's level at its
///         frequency (so the formants stay where the sample has them), and noise through twelve band-passes at the
///         envelope's levels (a third of the band's strongest component); Noise blends from the partials to the noise.
///     </para>
/// </remarks>
public sealed class SampleAlchemy : IInstrument, IPolyphonic {
    public const string Id = "anode.sample-alchemy";
    public const int Polyphony = 16;

    /// <summary>The most grains a voice plays at once.</summary>
    public const int MaxGrains = 24;

    readonly Engine engine = new();

    public static InstrumentDescriptor Info { get; } =
        new(Id, "Sample Alchemy", "Anode", "1.0.0", SampleAlchemyParameters.All, [".wav", ".aif", ".aiff"]);

    public InstrumentDescriptor Descriptor => Info;

    public int MaxVoices => Polyphony;

    public int ActiveVoices => engine.ActiveVoices;

    /// <summary>The file loaded, or null (the default sample).</summary>
    public string? File => engine.Path;

    /// <summary>The pitch every key's is measured against: the loaded sample's (Hz).</summary>
    public double SourcePitch => engine.Control.Source.RootHz;

    /// <summary>The generated default sample at <paramref name="sampleRate" /> (mono), as it plays before any file loads.</summary>
    public static float[] DefaultSample(int sampleRate) => AlchemySource.DefaultSample(sampleRate);

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

    sealed class Engine : PolyInstrument<AlchemyVoice> {
        readonly ParameterSmoother smoothed = new(SampleAlchemyParameters.All);

        public Engine() : this(new()) { }

        Engine(AlchemyControl control) : base(
            Info,
            SampleAlchemy.Polyphony,
            4,
            _ => new(control),
            SampleAlchemyParameters.Volume
        ) {
            Control = control;
        }

        public AlchemyControl Control { get; }

        public string? Path { get; private set; }

        public override void Prepare(float sampleRate, int maxFrames) {
            Control.SampleRate = sampleRate;
            Control.StealStep = (float)(1 / (StealFadeSeconds * sampleRate));
            smoothed.Prepare(sampleRate, ControlInterval);
            base.Prepare(sampleRate, maxFrames);
        }

        public override bool LoadFile(string path) {
            AlchemySource source;
            WavData data;
            try {
                path = System.IO.Path.GetFullPath(path);
                var extension = System.IO.Path.GetExtension(path);
                data = extension.Equals(".aif", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".aiff", StringComparison.OrdinalIgnoreCase)
                        ? AiffReader.Read(path)
                        : WavReader.Read(path);
                if (data.Frames == 0) {
                    return false;
                }

                source = AlchemySource.Analyse(data.Channels, data.SampleRate, data.RootNote);
            } catch (Exception e) when (e is IOException
                                            or InvalidDataException
                                            or UnauthorizedAccessException
                                            or ArgumentException
                                            or NotSupportedException) {
                return false;
            }

            if (data.RootNote >= 0) {
                Parameters.Set(SampleAlchemyParameters.RootNote, data.RootNote);
            }

            Control.Source = source;
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
                Control.Source = AlchemySource.Default;
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
            var tickRate = rate / ControlInterval;
            c.Mode = (AlchemyMode)(int)s[SampleAlchemyParameters.Mode];
            c.Position = s[SampleAlchemyParameters.Position] / 100.0;
            c.Loop = s[SampleAlchemyParameters.Loop] >= 0.5f;
            c.LoopStart = s[SampleAlchemyParameters.LoopStart] / 100.0;
            c.LoopEnd = s[SampleAlchemyParameters.LoopEnd] / 100.0;
            c.Speed = s[SampleAlchemyParameters.Speed] / 100.0;
            c.Partials = (int)s[SampleAlchemyParameters.Partials];
            c.Spread = s[SampleAlchemyParameters.PartialSpread] / 100.0 * 0.15;
            c.GrainSeconds = s[SampleAlchemyParameters.GrainSize] * 0.001;
            c.GrainDensity = s[SampleAlchemyParameters.GrainDensity];
            c.SpraySeconds = s[SampleAlchemyParameters.GrainSpray] * 0.001;
            c.GrainCents = s[SampleAlchemyParameters.GrainPitch];
            c.Noise = s[SampleAlchemyParameters.Noise] / 100.0;
            c.FilterMode = (FilterMode)(int)s[SampleAlchemyParameters.FilterMode];
            c.Cutoff = s[SampleAlchemyParameters.Cutoff];
            c.Resonance = s[SampleAlchemyParameters.Resonance] / 100.0;
            c.KeyTracking = s[SampleAlchemyParameters.KeyTracking] / 100.0;
            c.FilterEnvelope = s[SampleAlchemyParameters.FilterEnvAmount] / 100.0 * 6;
            for (var n = 0; n < 2; n++) {
                var at = SampleAlchemyParameters.EnvelopeAttack(n + 1);
                c.Envelopes[n] = AdsrSettings.From(
                    s[at] * 0.001,
                    0,
                    s[at + 1] * 0.001,
                    s[at + 2] / 100.0,
                    s[at + 3] * 0.001,
                    n == 0 ? rate : tickRate
                );
            }

            c.Pitch = s[SampleAlchemyParameters.Tune] / 100.0 + Bend * s[SampleAlchemyParameters.BendRange];
            c.VelocitySensitivity = s[SampleAlchemyParameters.VelocitySensitivity] / 100f;
        }
    }
}

/// <summary>The control-rate values every <see cref="AlchemyVoice" /> reads.</summary>
internal sealed class AlchemyControl {
    public AlchemySource Source = AlchemySource.Default;
    public AdsrSettings[] Envelopes { get; } = new AdsrSettings[2];
    public double SampleRate;
    public AlchemyMode Mode;
    public double Position;
    public bool Loop;
    public double LoopStart;
    public double LoopEnd;
    public double Speed;
    public int Partials;
    public double Spread;
    public double GrainSeconds;
    public double GrainDensity;
    public double SpraySeconds;
    public double GrainCents;
    public double Noise;
    public FilterMode FilterMode;
    public double Cutoff;
    public double Resonance;
    public double KeyTracking;
    public double FilterEnvelope;

    /// <summary>The tune and the bend, in semitones.</summary>
    public double Pitch;

    public float VelocitySensitivity;
    public float StealStep;
}

/// <summary>One voice of <see cref="SampleAlchemy" />: an oscillator bank, a grain cloud and a noise bank.</summary>
internal sealed class AlchemyVoice(AlchemyControl control) : Voice {
    const int Interval = 16;
    const int Partials = AlchemySource.Partials;
    const int NoiseBands = AlchemySource.Bands / 2;

    /// <summary>The voice's level for the sample at full scale.</summary>
    const double OutputGain = 2;

    /// <summary>The quality of the noise bands (each about two-thirds of an octave wide).</summary>
    const double NoiseQ = 2.1;

    /// <summary>The partials' glide toward each frame's amplitudes (s).</summary>
    const double GlideSeconds = 0.003;

    /// <summary>The fade when the sample ends without a loop (s).</summary>
    const double EndSeconds = 0.01;

    readonly double[] real = new double[Partials];
    readonly double[] imaginary = new double[Partials];
    readonly double[] cosines = new double[Partials];
    readonly double[] sines = new double[Partials];
    readonly double[] amplitudes = new double[Partials];
    readonly double[] targets = new double[Partials];
    readonly double[] steps = new double[Partials];
    readonly double[] grainPosition = new double[SampleAlchemy.MaxGrains];
    readonly double[] grainIncrement = new double[SampleAlchemy.MaxGrains];
    readonly int[] grainAge = new int[SampleAlchemy.MaxGrains];
    readonly int[] grainLength = new int[SampleAlchemy.MaxGrains];
    readonly SvfState[] noiseFilters = new SvfState[NoiseBands];
    readonly SvfCoefficients[] noiseCoefficients = new SvfCoefficients[NoiseBands];
    readonly double[] noiseGains = new double[NoiseBands];
    readonly double[] noiseSteps = new double[NoiseBands];
    readonly double[] noiseTargets = new double[NoiseBands];
    AlchemySource source = AlchemySource.Default;
    Adsr amp;
    Adsr filterEnvelope;
    MultimodeFilter filter;
    MultimodeCoefficients coefficients;
    AlchemyMode mode;
    double playhead;
    double keyHz;
    double grainGain;
    double untilGrain;
    long elapsed;
    double end;
    double glide;
    float velocityGain;
    float fade = 1;
    int untilTick;
    int partials;
    bool ending;
    bool fresh;
    uint random;
    uint noise;

    public void Start(int note, int velocity) {
        Begin(note);
        var c = control;
        source = c.Source;
        mode = c.Mode;
        playhead = c.Position * source.Seconds;
        end = 1;
        ending = false;
        fresh = true;
        Array.Clear(real);
        Array.Clear(imaginary);
        Array.Clear(amplitudes);
        Array.Clear(grainLength);
        Array.Clear(noiseGains);
        for (var k = 0; k < Partials; k++) {
            real[k] = 1; // a sine from phase zero: the output is the imaginary part
        }

        for (var b = 0; b < NoiseBands; b++) {
            noiseFilters[b].Reset();
            noiseCoefficients[b] = SvfCoefficients.Design(c.SampleRate, AlchemySource.BandHz(2 * b + 1), NoiseQ);
        }

        random = (uint)(note * 2_654_435_761u) | 1;
        noise = random ^ 0x5BD1E995u;
        untilGrain = 0;
        elapsed = 0;
        velocityGain = Pitch.Velocity(velocity, c.VelocitySensitivity);
        glide = 1 - Math.Exp(-Interval / (GlideSeconds * c.SampleRate));
        amp.Reset();
        filterEnvelope.Reset();
        filter.Reset();
        amp.Trigger();
        filterEnvelope.Trigger();
        fade = 1;
        untilTick = 0;
    }

    protected override void OnRelease() {
        amp.Release();
        filterEnvelope.Release();
    }

    protected override void OnSteal() { }

    protected override void OnKill() {
        amp.Reset();
        filterEnvelope.Reset();
        filter.Reset();
        fade = 1;
    }

    /// <summary>Moves the playhead over a tick, looping or ending at the sample's end.</summary>
    void Advance(double seconds) {
        var c = control;
        var length = source.Seconds;
        playhead += seconds * c.Speed;
        var loopStart = c.LoopStart * length;
        var loopEnd = c.LoopEnd * length;
        if (c.Loop && loopEnd - loopStart > 0.01) {
            while (playhead >= loopEnd) {
                playhead -= loopEnd - loopStart;
            }
        } else if (playhead >= length && !ending) {
            ending = true;
        }
    }

    void Tick() {
        var c = control;
        filter.FlushDenormals();
        for (var b = 0; b < NoiseBands; b++) {
            noiseFilters[b].FlushDenormals();
        }

        var env2 = filterEnvelope.Next(c.Envelopes[1]);
        keyHz = Pitch.Frequency(Note + c.Pitch);
        partials = Math.Clamp(c.Partials, 1, Partials);
        switch (mode) {
            case AlchemyMode.Additive:
                Additive();
                break;
            case AlchemyMode.Spectral:
                Spectral();
                break;
            case AlchemyMode.Granular:
                break;
        }

        var octaves = c.KeyTracking * (Note - 60) / 12 + c.FilterEnvelope * env2;
        coefficients = MultimodeCoefficients.Design(
            c.FilterMode,
            c.SampleRate,
            c.Cutoff * Math.Pow(2, octaves),
            MultimodeCoefficients.Quality(c.FilterMode, c.Resonance)
        );
        Advance(Interval / c.SampleRate);
        fresh = false;
    }

    /// <summary>The frame at the playhead and the next, and how far between them.</summary>
    (int Frame, int Next, double Fraction) Frame() {
        var position = Math.Clamp(playhead / AlchemySource.HopSeconds, 0, source.FrameCount - 1);
        var frame = (int)position;
        return (frame, Math.Min(frame + 1, source.FrameCount - 1), position - frame);
    }

    void Additive() {
        var c = control;
        var (frame, next, t) = Frame();
        var limit = 0.45 * c.SampleRate;
        for (var k = 0; k < partials; k++) {
            var a = source.Amplitudes[frame * Partials + k];
            var b = source.Amplitudes[next * Partials + k];
            var ratio = source.Ratios[frame * Partials + k]
                + (source.Ratios[next * Partials + k] - source.Ratios[frame * Partials + k]) * t;
            var hz = keyHz * ratio * (c.Spread == 0 ? 1 : Math.Pow(k + 1, c.Spread));
            Target(k, hz < limit ? a + (b - a) * t : 0);
            Rotate(k, hz);
        }

        for (var k = partials; k < Partials; k++) {
            Target(k, 0);
        }
    }

    void Spectral() {
        var c = control;
        var (frame, next, t) = Frame();
        var limit = 0.45 * c.SampleRate;
        var tonal = 1 - c.Noise;
        for (var k = 0; k < partials; k++) {
            var hz = keyHz * (k + 1);
            Target(k, hz < limit ? tonal * Envelope(frame, next, t, hz) : 0);
            Rotate(k, hz);
        }

        for (var k = partials; k < Partials; k++) {
            Target(k, 0);
        }

        // White noise (RMS 1/√3) through a band-pass of quality Q at f has a noise bandwidth of πf/(2Q): each band is
        // scaled to a third of the envelope's level there (the envelope is its band's strongest component's amplitude).
        for (var b = 0; b < NoiseBands; b++) {
            var hz = AlchemySource.BandHz(2 * b + 1);
            var level = Envelope(frame, next, t, hz) / 3;
            var gain = level / (Math.Sqrt(1 / 3.0) * Math.Sqrt(Math.PI * hz / (2 * NoiseQ) / (c.SampleRate / 2)));
            var target = hz < limit ? c.Noise * gain : 0;
            if (fresh) {
                noiseGains[b] = target;
                noiseSteps[b] = 0;
            } else {
                noiseTargets[b] = noiseGains[b] + (target - noiseGains[b]) * glide;
                noiseSteps[b] = (noiseTargets[b] - noiseGains[b]) / Interval;
            }
        }
    }

    /// <summary>The spectral envelope at <paramref name="hz" />, between the frames and between the bands (in log frequency).</summary>
    double Envelope(int frame, int next, double t, double hz) {
        var band = Math.Log(hz / 60.0) / Math.Log(15_000 / 60.0) * (AlchemySource.Bands - 1);
        band = Math.Clamp(band, 0, AlchemySource.Bands - 1);
        var low = (int)band;
        var high = Math.Min(low + 1, AlchemySource.Bands - 1);
        var u = band - low;
        var e = source.Envelope;
        var a = e[frame * AlchemySource.Bands + low]
            + (e[frame * AlchemySource.Bands + high] - e[frame * AlchemySource.Bands + low]) * u;
        var b = e[next * AlchemySource.Bands + low]
            + (e[next * AlchemySource.Bands + high] - e[next * AlchemySource.Bands + low]) * u;
        return a + (b - a) * t;
    }

    void Target(int k, double value) {
        if (fresh) {
            amplitudes[k] = value;
            targets[k] = value;
            steps[k] = 0;
            return;
        }

        targets[k] = amplitudes[k] + (value - amplitudes[k]) * glide;
        steps[k] = (targets[k] - amplitudes[k]) / Interval;
    }

    void Rotate(int k, double hz) {
        var phase = hz / control.SampleRate;
        var cos = SineTable.Sin(phase + 0.25);
        var sin = SineTable.Sin(phase);
        var norm = 1 / Math.Sqrt(cos * (double)cos + sin * (double)sin);
        cosines[k] = cos * norm;
        sines[k] = sin * norm;
    }

    /// <summary>Starts a grain at the playhead (plus spray), at the key's pitch (plus its spread).</summary>
    void Spawn() {
        var c = control;
        var slot = -1;
        for (var g = 0; g < SampleAlchemy.MaxGrains; g++) {
            if (grainLength[g] == 0) {
                slot = g;
                break;
            }
        }

        var seconds = Math.Max(0.005, c.GrainSeconds);
        var overlap = Math.Max(1, c.GrainDensity * seconds);
        grainGain = 1 / Math.Sqrt(overlap);
        untilGrain += c.SampleRate / Math.Max(1, c.GrainDensity) * (1 + 0.2 * PolyBlep.White(ref random));
        if (slot < 0) {
            return;
        }

        var start = (playhead + c.SpraySeconds * PolyBlep.White(ref random)) * source.Rate;
        var cents = c.GrainCents * PolyBlep.White(ref random);
        // Pitch-synchronous: the grain starts where the sample's phase meets the phase a steady tone at the key's pitch
        // has now, so the grains of a pitched sample add up in phase (a spread pitch takes them apart again).
        var period = source.Rate / source.RootHz;
        var mismatch = keyHz * elapsed / c.SampleRate - start / period;
        start += (mismatch - Math.Round(mismatch)) * period;
        grainPosition[slot] = start >= 0 ? start : start + period * Math.Ceiling(-start / period);
        grainIncrement[slot] = keyHz / source.RootHz * source.Rate / c.SampleRate * Math.Pow(2, cents / 1_200);
        grainAge[slot] = 0;
        grainLength[slot] = (int)(seconds * c.SampleRate);
    }

    double Grains() {
        var mono = source.Mono;
        var sum = 0.0;
        for (var g = 0; g < SampleAlchemy.MaxGrains; g++) {
            var length = grainLength[g];
            if (length == 0) {
                continue;
            }

            var window = SineTable.Sin(0.5 * grainAge[g] / length);
            sum += window * window * Read(mono, grainPosition[g]);
            grainPosition[g] += grainIncrement[g];
            if (++grainAge[g] >= length) {
                grainLength[g] = 0;
            }
        }

        return sum * grainGain;
    }

    static double Read(float[] samples, double position) {
        var whole = (int)position;
        var t = position - whole;
        double At(int i) => (uint)i < (uint)samples.Length ? samples[i] : 0;
        var y0 = At(whole - 1);
        var y1 = At(whole);
        var y2 = At(whole + 1);
        var y3 = At(whole + 2);
        var c1 = 0.5 * (y2 - y0);
        var c2 = y0 - 2.5 * y1 + 2 * y2 - 0.5 * y3;
        var c3 = 0.5 * (y3 - y0) + 1.5 * (y1 - y2);
        return ((c3 * t + c2) * t + c1) * t + y1;
    }

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
                double x = 0;
                if (mode == AlchemyMode.Granular) {
                    if (!ending && --untilGrain <= 0) {
                        Spawn();
                    }

                    x = Grains();
                } else {
                    for (var k = 0; k < Partials; k++) {
                        var a = amplitudes[k];
                        if (a == 0 && steps[k] == 0) {
                            continue;
                        }

                        var re = real[k];
                        var im = imaginary[k];
                        real[k] = cosines[k] * re - sines[k] * im;
                        imaginary[k] = sines[k] * re + cosines[k] * im;
                        x += a * imaginary[k];
                        amplitudes[k] = a + steps[k];
                    }

                    if (mode == AlchemyMode.Spectral && c.Noise > 0) {
                        var white = PolyBlep.White(ref noise);
                        for (var b = 0; b < NoiseBands; b++) {
                            x += noiseGains[b] * noiseFilters[b].Tick(noiseCoefficients[b], white, SvfOutput.BandPass);
                            noiseGains[b] += noiseSteps[b];
                        }
                    }
                }

                elapsed++;
                if (ending) {
                    end -= 1 / (EndSeconds * c.SampleRate);
                    if (end <= 0) {
                        Kill();
                        return;
                    }
                }

                var y = (float)(filter.Tick(coefficients, x) * end * OutputGain)
                    * amp.Next(c.Envelopes[0])
                    * velocityGain
                    * fade;
                left[i] += y;
                right[i] += y;
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
