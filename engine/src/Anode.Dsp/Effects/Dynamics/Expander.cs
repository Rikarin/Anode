using Anode.Dsp.Dynamics;

namespace Anode.Dsp.Effects.Dynamics;

/// <summary>
///     <c>anode.expander</c>: a downward expander. Below the threshold each dB of level falls by Ratio dB, with a knee of
///     Knee dB (<see cref="GainComputer.Expansion" />), so quiet material sinks without the on and off of a gate; Range
///     limits how far. A stereo-linked peak detector (instant attack, 30 ms release) drives it, the gain opening at Attack
///     and closing at Release. Make-up trims the result; Mix blends the dry signal back. The
///     <see cref="IAudioEffect.Reading" /> is the deepest gain change (dB, ≤ 0) over the last buffer.
/// </summary>
public sealed class Expander : AudioEffect {
    public const string Id = "anode.expander";
    const double DetectorRelease = 0.03;

    static readonly EffectParameters P = new();
    public static readonly int Threshold = P.Add("threshold", "Threshold", -80, 0, -40, "dB");
    public static readonly int Ratio = P.Add("ratio", "Ratio", 1, 8, 2, ":1");
    public static readonly int Knee = P.Add("knee", "Knee", 0, 24, 6, "dB");
    public static readonly int Range = P.Add("range", "Range", -80, 0, -40, "dB");
    public static readonly int Attack = P.Add("attack", "Attack", 0.1f, 100, 2, "ms");
    public static readonly int Release = P.Add("release", "Release", 5, 2_000, 150, "ms");
    public static readonly int Makeup = P.Add("makeup", "Make-up", -24, 24, 0, "dB");
    public static readonly int Mix = P.Add("mix", "Mix", 0, 100, 100, "%");

    public static EffectDescriptor Info { get; } =
        P.Describe(Id, "Expander", EffectCategories.Dynamics, Threshold, Ratio, Range, Attack, Release, Makeup);

    GainRamp makeup;
    GainRamp mix;
    double detector;
    double detectorRelease;
    double gainDb;
    double deepest;
    double threshold;
    double ratio;
    double knee;
    double range;
    double attack;
    double release;

    public Expander() : base(Info) { }

    protected override void OnPrepare(float sampleRate) =>
        detectorRelease = Math.Exp(-1 / (DetectorRelease * sampleRate));

    protected override void OnReset() {
        detector = 0;
        gainDb = 0;
        deepest = 0;
        Publish(0);
    }

    protected override void Update() {
        threshold = Value(Threshold);
        ratio = Value(Ratio);
        knee = Value(Knee);
        range = Value(Range);
        attack = Math.Exp(-1 / (Value(Attack) * 0.001 * SampleRate));
        release = Math.Exp(-1 / (Value(Release) * 0.001 * SampleRate));
        makeup.Set(Decibels.ToGain(Value(Makeup)), Snapping);
        mix.Set(Value(Mix) * 0.01f, Snapping);
    }

    protected override void Render(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        for (var i = 0; i < left.Length; i++) {
            double peak = MathF.Max(MathF.Abs(left[i]), MathF.Abs(right[i]));
            detector = peak > detector ? peak : peak + (detector - peak) * detectorRelease;
            var level = detector > 1e-6 ? 20 * Math.Log10(detector) : -120;
            var target = Math.Max(GainComputer.Expansion(level, threshold, ratio, knee), range);
            var coefficient = target > gainDb ? attack : release;
            gainDb = coefficient * gainDb + (1 - coefficient) * target;
            deepest = Math.Min(deepest, gainDb);
            var gain = (float)Math.Pow(10, gainDb * 0.05) * makeup.Next();
            var wet = mix.Next();
            left[i] += (left[i] * gain - left[i]) * wet;
            right[i] += (right[i] * gain - right[i]) * wet;
        }
    }

    protected override void Finish() {
        Publish((float)deepest);
        deepest = gainDb;
        if (detector < 1e-30) {
            detector = 0;
        }
    }
}
