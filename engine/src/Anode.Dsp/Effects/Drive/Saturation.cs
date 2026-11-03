using Anode.Dsp.Filters;
using Anode.Dsp.Resampling;

namespace Anode.Dsp.Effects.Drive;

/// <summary>
///     <c>anode.saturation</c> (ChromaGlow-like; docs/12-phase-11.md, workstream E): oversampled saturation in four
///     models (tube, tape, transformer, modern), with drive, tone, mix and output level.
/// </summary>
/// <remarks>
///     <para>
///         Per channel: the drive (0 … 30 dB), the model's emphasis, its shaper at 4× the rate
///         (<see cref="Oversampler4x" />), the de-emphasis, a DC blocker, the tone (a tilt: shelves at 800 Hz, ±6 dB each
///         way), then the level, made up so a −12 dBFS signal keeps its level whatever the drive (the shaper's gain at
///         that level is divided out), the output and the dry/wet mix.
///     </para>
///     <list type="bullet">
///         <item>
///             <b>Tube</b>: a biased soft clip, <c>tanh(x + 0.3) − tanh(0.3)</c>, which bends the positive half sooner:
///             even harmonics as well as odd ones, the even ones first.
///         </item>
///         <item>
///             <b>Tape</b>: an algebraic soft clip, <c>x / √(1 + x²)</c>, between a pre-emphasis (+6 dB above 3 kHz)
///             and its inverse, so the highs saturate first, as a tape's do: odd harmonics, softly.
///         </item>
///         <item>
///             <b>Transformer</b>: the lows (+6 dB below 120 Hz) driven into a cubic core that saturates at ±1 with a
///             slight offset, then given back: the bass thickens, mostly odd harmonics and a little even.
///         </item>
///         <item><b>Modern</b>: a hard-kneed but smooth clip, <c>x / (1 + x⁴)^¼</c>: odd harmonics, clean below it.</item>
///     </list>
/// </remarks>
public sealed class Saturation : AudioEffect {
    public const string Id = "anode.saturation";

    public const int Tube = 0;
    public const int Tape = 1;
    public const int Transformer = 2;
    public const int Modern = 3;

    static readonly EffectParameters P = new();
    public static readonly int Model = P.Choice("model", "Model", ["Tube", "Tape", "Transformer", "Modern"], 0);
    public static readonly int Drive = P.Add("drive", "Drive", 0, 100, 30, "%");
    public static readonly int Tone = P.Add("tone", "Tone", -100, 100, 0, "%");
    public static readonly int Mix = P.Add("mix", "Mix", 0, 100, 100, "%");
    public static readonly int Output = P.Add("output", "Output", -24, 24, 0, "dB");

    public static EffectDescriptor Info { get; } =
        P.Describe(Id, "Saturation", EffectCategories.Distortion, Drive, Mix);

    /// <summary>The drive at 100 %.</summary>
    public const double MaxDriveDb = 30;

    const float TubeBias = 0.3f;
    const float TransformerOffset = 0.08f;
    const double DcCutHz = 8;
    const double TiltHz = 800;
    const double TiltDb = 6;

    /// <summary>The level whose gain the make-up divides out (−12 dBFS).</summary>
    const float ReferenceLevel = 0.25f;

    static readonly float TubeOffset = MathF.Tanh(TubeBias);

    readonly Channel left = new();
    readonly Channel right = new();
    readonly float[] dryLeft = new float[ControlInterval];
    readonly float[] dryRight = new float[ControlInterval];
    SvfEqCoefficients emphasis = SvfEqCoefficients.Identity;
    SvfEqCoefficients deEmphasis = SvfEqCoefficients.Identity;
    SvfEqCoefficients lowTilt = SvfEqCoefficients.Identity;
    SvfEqCoefficients highTilt = SvfEqCoefficients.Identity;
    double dcCut;
    int model;
    GainRamp drive;
    GainRamp level;
    GainRamp mix;

    public Saturation() : base(Info) { }

    protected override void OnPrepare(float sampleRate) => dcCut = OnePole.Coefficient(sampleRate, DcCutHz);

    protected override void OnReset() {
        left.Reset();
        right.Reset();
    }

    protected override void Update() {
        var next = Choice(Model);
        if (next != model) {
            // A model's emphasis state means nothing to another's.
            left.ResetEmphasis();
            right.ResetEmphasis();
        }

        model = next;
        (emphasis, deEmphasis) = model switch {
            Tape => (
                SvfEqCoefficients.Design(SvfEqType.HighShelf, SampleRate, 3_000, 0.7071, 6),
                SvfEqCoefficients.Design(SvfEqType.HighShelf, SampleRate, 3_000, 0.7071, -6)),
            Transformer => (
                SvfEqCoefficients.Design(SvfEqType.LowShelf, SampleRate, 120, 0.7071, 6),
                SvfEqCoefficients.Design(SvfEqType.LowShelf, SampleRate, 120, 0.7071, -6)),
            _ => (SvfEqCoefficients.Identity, SvfEqCoefficients.Identity)
        };
        var tilt = Value(Tone) / 100 * TiltDb;
        lowTilt = SvfEqCoefficients.Design(SvfEqType.LowShelf, SampleRate, TiltHz, 0.7071, -tilt);
        highTilt = SvfEqCoefficients.Design(SvfEqType.HighShelf, SampleRate, TiltHz, 0.7071, tilt);
        var gain = (float)Decibels.ToGain(Value(Drive) / 100 * MaxDriveDb);
        drive.Set(gain, Snapping);
        level.Set(MakeUp(model, gain) * Decibels.ToGain(Value(Output)), Snapping);
        mix.Set(Value(Mix) * 0.01f, Snapping);
    }

    /// <summary>The gain that brings a −12 dBFS signal driven by <paramref name="drive" /> back to its level.</summary>
    static float MakeUp(int model, float drive) {
        var x = ReferenceLevel * drive;
        var gain = 0.5f * (MathF.Abs(Shape(model, x)) + MathF.Abs(Shape(model, -x))) / ReferenceLevel;
        return gain > 1e-6f ? 1 / gain : 1;
    }

    /// <summary>The shaper of <paramref name="model" />.</summary>
    public static float Shape(int model, float x) =>
        model switch {
            Tube => MathF.Tanh(x + TubeBias) - TubeOffset,
            Tape => x / MathF.Sqrt(1 + x * x),
            Transformer => Core(x + TransformerOffset) - Core(TransformerOffset),
            _ => x / MathF.Sqrt(MathF.Sqrt(1 + x * x * x * x))
        };

    /// <summary>A cubic that saturates at ±1: <c>1.5x − 0.5x³</c> within ±1, flat beyond.</summary>
    static float Core(float x) {
        x = Math.Clamp(x, -1f, 1f);
        return 1.5f * x - 0.5f * x * x * x;
    }

    protected override void Render(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        var n = left.Length;
        left.CopyTo(dryLeft);
        right.CopyTo(dryRight);
        for (var i = 0; i < n; i++) {
            var g = drive.Next();
            left[i] = (float)this.left.Emphasis.Tick(emphasis, left[i]) * g;
            right[i] = (float)this.right.Emphasis.Tick(emphasis, right[i]) * g;
        }

        Shape(this.left.Oversampler, left);
        Shape(this.right.Oversampler, right);
        for (var i = 0; i < n; i++) {
            var l = this.left.Finish(left[i], deEmphasis, lowTilt, highTilt, dcCut);
            var r = this.right.Finish(right[i], deEmphasis, lowTilt, highTilt, dcCut);
            var gain = level.Next();
            var wet = mix.Next();
            left[i] = dryLeft[i] + ((float)l * gain - dryLeft[i]) * wet;
            right[i] = dryRight[i] + ((float)r * gain - dryRight[i]) * wet;
        }
    }

    void Shape(Oversampler4x oversampler, Span<float> channel) {
        var fast = oversampler.Up(channel);
        for (var i = 0; i < fast.Length; i++) {
            fast[i] = Shape(model, fast[i]);
        }

        oversampler.Down(channel);
    }

    protected override void Finish() {
        left.FlushDenormals();
        right.FlushDenormals();
    }

    /// <summary>One channel's state: the oversampler and the filters around the shaper.</summary>
    sealed class Channel {
        public readonly Oversampler4x Oversampler = new();
        public SvfEqState Emphasis;
        SvfEqState deEmphasis;
        SvfEqState low;
        SvfEqState high;
        OnePole dc;

        public double Finish(
            float x,
            in SvfEqCoefficients de,
            in SvfEqCoefficients lowTilt,
            in SvfEqCoefficients highTilt,
            double dcCut
        ) {
            var y = deEmphasis.Tick(de, x);
            y = dc.HighPass(dcCut, y);
            return high.Tick(highTilt, low.Tick(lowTilt, y));
        }

        public void ResetEmphasis() {
            Emphasis.Reset();
            deEmphasis.Reset();
        }

        public void Reset() {
            Oversampler.Reset();
            ResetEmphasis();
            low.Reset();
            high.Reset();
            dc.Reset();
        }

        public void FlushDenormals() {
            Oversampler.FlushDenormals();
            Emphasis.FlushDenormals();
            deEmphasis.FlushDenormals();
            low.FlushDenormals();
            high.FlushDenormals();
            dc.FlushDenormals();
        }
    }
}
