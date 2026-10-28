using Anode.Dsp.Filters;
using Anode.Dsp.Resampling;

namespace Anode.Dsp.Effects.Drive;

/// <summary>
///     What the drive effects share: per channel, a first-order low cut before the drive (so the bass does not mud the
///     clipping), the shaper at 4× the rate (<see cref="Oversampler4x" />), a DC blocker, a 12 dB/octave tone low-pass,
///     then the level and the dry/wet mix.
/// </summary>
public abstract class DriveEffect : AudioEffect {
    const double PreCutHz = 60;
    const double DcCutHz = 8;

    readonly Oversampler4x upLeft = new();
    readonly Oversampler4x upRight = new();
    OnePole preLeft;
    OnePole preRight;
    OnePole dcLeft;
    OnePole dcRight;
    SvfEqState toneLeft;
    SvfEqState toneRight;
    SvfEqCoefficients tone;
    double preCut;
    double dcCut;
    GainRamp drive;
    GainRamp level;
    GainRamp mix;
    readonly float[] dryLeft = new float[ControlInterval];
    readonly float[] dryRight = new float[ControlInterval];

    protected DriveEffect(EffectDescriptor descriptor) : base(descriptor) { }

    /// <summary>The shaper, at 4× the rate.</summary>
    protected abstract float Shape(float x);

    protected void SetStage(float driveDb, float toneHz, float levelDb, float mixPercent) {
        drive.Set(Decibels.ToGain(driveDb), Snapping);
        level.Set(Decibels.ToGain(levelDb), Snapping);
        mix.Set(mixPercent * 0.01f, Snapping);
        tone = SvfEqCoefficients.Design(SvfEqType.LowPass, SampleRate, toneHz, 0.7071);
    }

    protected override void OnPrepare(float sampleRate) {
        preCut = OnePole.Coefficient(sampleRate, PreCutHz);
        dcCut = OnePole.Coefficient(sampleRate, DcCutHz);
    }

    protected override void OnReset() {
        upLeft.Reset();
        upRight.Reset();
        preLeft.Reset();
        preRight.Reset();
        dcLeft.Reset();
        dcRight.Reset();
        toneLeft.Reset();
        toneRight.Reset();
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
            left[i] = (float)preLeft.HighPass(preCut, left[i]) * g;
            right[i] = (float)preRight.HighPass(preCut, right[i]) * g;
        }

        Shape(upLeft, left);
        Shape(upRight, right);
        for (var i = 0; i < n; i++) {
            var l = toneLeft.Tick(tone, dcLeft.HighPass(dcCut, left[i]));
            var r = toneRight.Tick(tone, dcRight.HighPass(dcCut, right[i]));
            var gain = level.Next();
            var wet = mix.Next();
            left[i] = dryLeft[i] + ((float)l * gain - dryLeft[i]) * wet;
            right[i] = dryRight[i] + ((float)r * gain - dryRight[i]) * wet;
        }
    }

    void Shape(Oversampler4x oversampler, Span<float> channel) {
        var fast = oversampler.Up(channel);
        for (var i = 0; i < fast.Length; i++) {
            fast[i] = Shape(fast[i]);
        }

        oversampler.Down(channel);
    }

    protected override void Finish() {
        upLeft.FlushDenormals();
        upRight.FlushDenormals();
        preLeft.FlushDenormals();
        preRight.FlushDenormals();
        dcLeft.FlushDenormals();
        dcRight.FlushDenormals();
        toneLeft.FlushDenormals();
        toneRight.FlushDenormals();
    }
}

/// <summary>
///     <c>anode.overdrive</c>: a warm, tube-like overdrive. The shaper is an asymmetric soft clip,
///     <c>tanh(x + 0.25) − tanh(0.25)</c>, which bends the positive half sooner and so adds even harmonics as well as
///     odd ones.
/// </summary>
public sealed class Overdrive : DriveEffect {
    public const string Id = "anode.overdrive";
    const float Bias = 0.25f;
    static readonly float Offset = MathF.Tanh(Bias);

    static readonly EffectParameters P = new();
    public static readonly int Drive = P.Add("drive", "Drive", 0, 40, 12, "dB");
    public static readonly int Tone = P.Add("tone", "Tone", 500, 16_000, 4_000, "Hz");
    public static readonly int Level = P.Add("level", "Output", -36, 12, -6, "dB");
    public static readonly int Mix = P.Add("mix", "Mix", 0, 100, 100, "%");

    public static EffectDescriptor Info { get; } = P.Describe(Id, "Overdrive", EffectCategories.Distortion);

    public Overdrive() : base(Info) { }

    protected override void Update() => SetStage(Value(Drive), Value(Tone), Value(Level), Value(Mix));

    protected override float Shape(float x) => MathF.Tanh(x + Bias) - Offset;
}

/// <summary>
///     <c>anode.distortion</c>: harder distortion with three shapers: Hard (a clip at ±1), Fuzz (asymmetric: the positive
///     half saturates exponentially, the negative half is squashed sooner and lower, as a germanium transistor does)
///     and Fold (a sine wavefolder: past full scale the wave folds back on itself).
/// </summary>
public sealed class Distortion : DriveEffect {
    public const string Id = "anode.distortion";
    public const int Hard = 0;
    public const int Fuzz = 1;
    public const int Fold = 2;

    static readonly EffectParameters P = new();
    public static readonly int Drive = P.Add("drive", "Drive", 0, 60, 24, "dB");
    public static readonly int Type = P.Choice("type", "Type", ["Hard", "Fuzz", "Fold"], Hard);
    public static readonly int Tone = P.Add("tone", "Tone", 500, 16_000, 5_000, "Hz");
    public static readonly int Level = P.Add("level", "Output", -36, 12, -12, "dB");
    public static readonly int Mix = P.Add("mix", "Mix", 0, 100, 100, "%");

    public static EffectDescriptor Info { get; } = P.Describe(Id, "Distortion", EffectCategories.Distortion);

    int type;

    public Distortion() : base(Info) { }

    protected override void Update() {
        type = Choice(Type);
        SetStage(Value(Drive), Value(Tone), Value(Level), Value(Mix));
    }

    protected override float Shape(float x) =>
        type switch {
            Hard => Math.Clamp(x, -1f, 1f),
            Fuzz => x >= 0 ? 1 - MathF.Exp(-x) : -0.6f * (1 - MathF.Exp(1.8f * x)),
            _ => MathF.Sin(x)
        };
}

/// <summary>
///     <c>anode.bitcrusher</c>: lo-fi by design (no oversampling: the aliasing is the sound). Drive, then a
///     sample-and-hold at the rate divided by Downsample (fractional, so it sweeps smoothly), then a quantizer to
///     Bits bits (fractional too: the step is 2 / 2^bits, rounded to the nearest level, clipped at full scale), then
///     the level and the dry/wet mix.
/// </summary>
public sealed class Bitcrusher : AudioEffect {
    public const string Id = "anode.bitcrusher";

    static readonly EffectParameters P = new();
    public static readonly int Drive = P.Add("drive", "Drive", 0, 36, 0, "dB");
    public static readonly int Bits = P.Add("bits", "Resolution", 1, 24, 8, "bit");
    public static readonly int Downsample = P.Add("downsample", "Downsampling", 1, 64, 4, "×");
    public static readonly int Level = P.Add("level", "Output", -36, 12, 0, "dB");
    public static readonly int Mix = P.Add("mix", "Mix", 0, 100, 100, "%");

    public static EffectDescriptor Info { get; } = P.Describe(Id, "Bitcrusher", EffectCategories.Distortion);

    GainRamp drive;
    GainRamp level;
    GainRamp mix;
    float step;
    float hold;
    float phase;
    float heldLeft;
    float heldRight;

    public Bitcrusher() : base(Info) { }

    protected override void OnPrepare(float sampleRate) { }

    protected override void OnReset() {
        phase = 1;
        heldLeft = 0;
        heldRight = 0;
    }

    protected override void Update() {
        drive.Set(Decibels.ToGain(Value(Drive)), Snapping);
        level.Set(Decibels.ToGain(Value(Level)), Snapping);
        mix.Set(Value(Mix) * 0.01f, Snapping);
        step = 2 / MathF.Pow(2, Value(Bits));
        hold = 1 / Value(Downsample);
    }

    protected override void Render(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        for (var i = 0; i < left.Length; i++) {
            var g = drive.Next();
            phase += hold;
            if (phase >= 1) {
                phase -= 1;
                heldLeft = Quantize(left[i] * g);
                heldRight = Quantize(right[i] * g);
            }

            var gain = level.Next();
            var wet = mix.Next();
            left[i] += (heldLeft * gain - left[i]) * wet;
            right[i] += (heldRight * gain - right[i]) * wet;
        }
    }

    float Quantize(float x) => Math.Clamp(MathF.Round(x / step) * step, -1f, 1f);
}
