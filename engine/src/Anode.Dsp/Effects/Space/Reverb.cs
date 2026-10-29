using Anode.Dsp.Delays;
using Anode.Dsp.Filters;

namespace Anode.Dsp.Effects.Space;

/// <summary>
///     <c>anode.reverb</c>: an algorithmic reverb with plate, room and hall types, after Logic's ChromaVerb.
///     <para>
///         The tail is an eight-line feedback delay network (Jot and Chaigne 1991): the lines feed back through an
///         orthogonal 8×8 Hadamard matrix, each through an absorption filter that sets the decay exactly. A line of
///         <c>d</c> samples has the gain <c>10^(−3d / (T60 fs))</c>, so every path through the network loses 60 dB in
///         the Decay time; the filter is a one-pole low-pass (Jot's first-order absorption) whose pole makes the decay
///         at Nyquist <c>1 − 0.85 · Damping</c> times as long as at DC. The input passes the predelay and four Schroeder
///         allpass diffusers per channel first. The types differ in their line lengths (plate and room short and dense,
///         hall long), their diffusion, their early reflections (room and hall: six taps per channel off the predelayed
///         input) and how much the line lengths are modulated (which smears the modes: plate and hall more, room
///         hardly). Size scales the line lengths from 30 % to 100 %.
///     </para>
///     <para>
///         The wet signal then passes a low and a high cut (12 dB/octave, off at the ends of their ranges) and a
///         mid/side width; Mix crossfades dry and wet.
///     </para>
/// </summary>
public sealed class Reverb : AudioEffect {
    public const string Id = "anode.reverb";
    public const int Plate = 0;
    public const int Room = 1;
    public const int Hall = 2;
    const int Lines = 8;
    const int Diffusers = 4;
    const int Taps = 6;
    const double MaxPredelaySeconds = 0.25;
    const float WetGain = 0.35f;

    /// <summary>Line lengths at full size, in ms, per type (mutually prime in samples once rounded, near enough).</summary>
    static readonly double[][] LineMs = [
        [11.3, 13.7, 17.1, 19.9, 23.3, 27.1, 31.7, 35.9],
        [7.3, 9.1, 11.3, 13.1, 15.7, 18.3, 21.1, 24.7],
        [29.3, 37.1, 43.9, 51.7, 61.3, 71.9, 83.3, 97.1]
    ];

    static readonly double[] DiffuserMs = [4.77, 3.59, 12.73, 9.31];
    static readonly double[] Diffusion = [0.75, 0.6, 0.7];
    static readonly double[] ModulationMs = [0.25, 0.04, 0.35];

    static readonly double[][] EarlyMs = [
        [],
        [3.1, 5.3, 7.7, 11.2, 13.9, 17.3],
        [7.1, 13.3, 19.7, 27.1, 34.3, 41.9]
    ];

    static readonly float[] EarlyGains = [0.6f, 0.5f, 0.42f, 0.35f, 0.28f, 0.22f];

    static readonly EffectParameters P = new();
    public static readonly int Type = P.Choice("type", "Type", ["Plate", "Room", "Hall"], Hall);
    public static readonly int Size = P.Add("size", "Size", 0, 100, 70, "%");
    public static readonly int Decay = P.Add("decay", "Decay", 0.1f, 20, 2.2f, "s");
    public static readonly int Damping = P.Add("damping", "Damping", 0, 100, 40, "%");
    public static readonly int Predelay = P.Add("predelay", "Predelay", 0, 250, 15, "ms");
    public static readonly int Width = P.Add("width", "Width", 0, 200, 100, "%");
    public static readonly int LowCut = P.Add("lowCut", "Low Cut", 20, 2_000, 20, "Hz");
    public static readonly int HighCut = P.Add("highCut", "High Cut", 1_000, 20_000, 20_000, "Hz");
    public static readonly int Mix = P.Add("mix", "Mix", 0, 100, 25, "%");

    public static EffectDescriptor Info { get; } = P.Describe(Id, "Reverb", EffectCategories.Reverb, Mix, Decay, Size, Predelay, Damping, Width, HighCut, LowCut);

    readonly DelayLine[] lines = new DelayLine[Lines];
    readonly DelayLine[] diffusersLeft = new DelayLine[Diffusers];
    readonly DelayLine[] diffusersRight = new DelayLine[Diffusers];
    readonly int[] diffuserLengths = new int[Diffusers];
    readonly DelayLine predelayLeft = new();
    readonly DelayLine predelayRight = new();
    readonly float[] lengths = new float[Lines];
    readonly float[] modulation = new float[Lines];
    readonly float[] modulationStep = new float[Lines];
    readonly double[] modulationPhase = new double[Lines];
    readonly double[] lineGain = new double[Lines];
    readonly double[] pole = new double[Lines];
    readonly double[] damped = new double[Lines];
    readonly double[] state = new double[Lines];
    readonly int[] earlyLeft = new int[Taps];
    readonly int[] earlyRight = new int[Taps];
    SvfEqCoefficients lowCut;
    SvfEqCoefficients highCut;
    SvfEqState lowLeft;
    SvfEqState lowRight;
    SvfEqState highLeft;
    SvfEqState highRight;
    DelayGlide predelay;
    GainRamp mix;
    double glide;
    float diffusion;
    float width;
    float modulationDepth;
    int type;
    bool lowCutOn;
    bool highCutOn;

    public Reverb() : base(Info) {
        for (var i = 0; i < Lines; i++) {
            lines[i] = new();
        }

        for (var i = 0; i < Diffusers; i++) {
            diffusersLeft[i] = new();
            diffusersRight[i] = new();
        }
    }

    protected override void OnPrepare(float sampleRate) {
        for (var i = 0; i < Lines; i++) {
            var longest = Math.Max(LineMs[Plate][i], Math.Max(LineMs[Room][i], LineMs[Hall][i]));
            lines[i].Allocate((int)Math.Ceiling((longest + 1) * 0.001 * sampleRate) + 4);
        }

        for (var i = 0; i < Diffusers; i++) {
            diffuserLengths[i] = Math.Max(1, (int)Math.Round(DiffuserMs[i] * 0.001 * sampleRate));
            diffusersLeft[i].Allocate(diffuserLengths[i] + 1);
            diffusersRight[i].Allocate(diffuserLengths[i] + 2);
        }

        var predelaySamples = (int)Math.Ceiling((MaxPredelaySeconds + 0.05) * sampleRate) + 4;
        predelayLeft.Allocate(predelaySamples);
        predelayRight.Allocate(predelaySamples);
        glide = 1 - Math.Exp(-ControlInterval / (0.05 * sampleRate));
    }

    protected override void OnReset() {
        foreach (var line in lines) {
            line.Reset();
        }

        for (var i = 0; i < Diffusers; i++) {
            diffusersLeft[i].Reset();
            diffusersRight[i].Reset();
        }

        predelayLeft.Reset();
        predelayRight.Reset();
        Array.Clear(state);
        Array.Clear(damped);
        Array.Clear(modulation);
        Array.Clear(modulationStep);
        for (var i = 0; i < Lines; i++) {
            modulationPhase[i] = i / (double)Lines;
        }

        lowLeft.Reset();
        lowRight.Reset();
        highLeft.Reset();
        highRight.Reset();
    }

    protected override void Update() {
        type = Choice(Type);
        var scale = 0.3 + 0.7 * Value(Size) * 0.01;
        var decay = Value(Decay);
        var ratio = 1 - 0.85 * Value(Damping) * 0.01;
        modulationDepth = (float)(ModulationMs[type] * 0.001 * SampleRate);
        for (var i = 0; i < Lines; i++) {
            var length = Math.Round(LineMs[type][i] * scale * 0.001 * SampleRate);
            lengths[i] = (float)length;
            // Jot's absorption filter: gain g for the length at DC, a pole for the ratio of decays at Nyquist.
            var g = Math.Pow(10, -3 * length / (decay * SampleRate));
            lineGain[i] = g;
            pole[i] = Math.Log(10) / 4 * Math.Log10(g) * (1 - 1 / (ratio * ratio));
            // The modulation, a sine per line at its own rate (0.5 … 1.2 Hz), stepped linearly between control points.
            modulationPhase[i] += ControlInterval * (0.5 + 0.1 * i) / SampleRate;
            if (modulationPhase[i] >= 1) {
                modulationPhase[i] -= 1;
            }

            var next = modulationDepth * (float)Math.Sin(2 * Math.PI * modulationPhase[i]);
            if (Snapping) {
                modulation[i] = next;
            }

            modulationStep[i] = (next - modulation[i]) / ControlInterval;
        }

        for (var t = 0; t < Taps; t++) {
            var early = EarlyMs[type];
            earlyLeft[t] = early.Length == 0 ? 0 : (int)(early[t] * scale * 0.001 * SampleRate);
            earlyRight[t] = early.Length == 0 ? 0 : (int)(early[t] * scale * 1.07 * 0.001 * SampleRate);
        }

        diffusion = (float)Diffusion[type];
        predelay.Update(Value(Predelay) * 0.001 * SampleRate, glide, Snapping);
        width = Value(Width) * 0.01f;
        lowCutOn = Value(LowCut) > 20;
        highCutOn = Value(HighCut) < 20_000;
        lowCut = SvfEqCoefficients.Design(SvfEqType.HighPass, SampleRate, Value(LowCut), 0.7071);
        highCut = SvfEqCoefficients.Design(SvfEqType.LowPass, SampleRate, Value(HighCut), 0.7071);
        mix.Set(Value(Mix) * 0.01f, Snapping);
    }

    protected override void Render(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        Span<double> mixed = stackalloc double[Lines];
        var early = EarlyMs[type].Length > 0;
        for (var i = 0; i < left.Length; i++) {
            predelayLeft.Write(left[i]);
            predelayRight.Write(right[i]);
            var delay = predelay.Next();
            var inLeft = predelayLeft.ReadCubic(delay + 1);
            var inRight = predelayRight.ReadCubic(delay + 1);
            float earlyL = 0, earlyR = 0;
            if (early) {
                for (var t = 0; t < Taps; t++) {
                    earlyL += EarlyGains[t] * predelayLeft.ReadCubic(delay + 1 + earlyLeft[t]);
                    earlyR += EarlyGains[t] * predelayRight.ReadCubic(delay + 1 + earlyRight[t]);
                }
            }

            for (var d = 0; d < Diffusers; d++) {
                inLeft = Allpass(diffusersLeft[d], diffuserLengths[d], inLeft);
                inRight = Allpass(diffusersRight[d], diffuserLengths[d] + 1, inRight);
            }

            for (var n = 0; n < Lines; n++) {
                modulation[n] += modulationStep[n];
                var read = lines[n].ReadCubic(lengths[n] + modulation[n] - 1);
                // The absorption filter: y = g (1 − p) x + p y.
                state[n] = lineGain[n] * (1 - pole[n]) * read + pole[n] * state[n];
                damped[n] = state[n];
                mixed[n] = state[n];
            }

            Hadamard(mixed);
            for (var n = 0; n < Lines; n++) {
                var input = (n & 1) == 0 ? inLeft : inRight;
                lines[n].Write((float)(mixed[n] + ((n & 2) == 0 ? input : -input)));
            }

            var wetLeft = (damped[0] + damped[2] + damped[4] + damped[6]) * 0.5 + earlyL * 0.5;
            var wetRight = (damped[1] + damped[3] + damped[5] + damped[7]) * 0.5 + earlyR * 0.5;
            if (lowCutOn) {
                wetLeft = lowLeft.Tick(lowCut, wetLeft);
                wetRight = lowRight.Tick(lowCut, wetRight);
            }

            if (highCutOn) {
                wetLeft = highLeft.Tick(highCut, wetLeft);
                wetRight = highRight.Tick(highCut, wetRight);
            }

            var mid = 0.5 * (wetLeft + wetRight);
            var side = 0.5 * (wetLeft - wetRight) * width;
            var wet = mix.Next();
            left[i] += ((float)(mid + side) * WetGain - left[i]) * wet;
            right[i] += ((float)(mid - side) * WetGain - right[i]) * wet;
        }
    }

    /// <summary>A Schroeder allpass of <paramref name="length" /> samples with the diffusion as its coefficient.</summary>
    float Allpass(DelayLine line, int length, float x) {
        var delayed = line.Read(length - 1);
        var v = x + diffusion * delayed;
        line.Write(v);
        return delayed - diffusion * v;
    }

    /// <summary>The 8-point fast Walsh–Hadamard transform, scaled to be orthogonal.</summary>
    static void Hadamard(Span<double> x) {
        for (var h = 1; h < Lines; h <<= 1) {
            for (var i = 0; i < Lines; i += h << 1) {
                for (var j = i; j < i + h; j++) {
                    var a = x[j];
                    var b = x[j + h];
                    x[j] = a + b;
                    x[j + h] = a - b;
                }
            }
        }

        const double scale = 0.35355339059327373; // 1 / √8
        for (var i = 0; i < Lines; i++) {
            x[i] *= scale;
        }
    }

    protected override void Finish() {
        for (var n = 0; n < Lines; n++) {
            if (Math.Abs(state[n]) < 1e-30) {
                state[n] = 0;
            }
        }

        lowLeft.FlushDenormals();
        lowRight.FlushDenormals();
        highLeft.FlushDenormals();
        highRight.FlushDenormals();
    }
}
