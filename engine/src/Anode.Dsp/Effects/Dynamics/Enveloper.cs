using Anode.Dsp.Delays;

namespace Anode.Dsp.Effects.Dynamics;

/// <summary>
///     <c>anode.enveloper</c>, after Logic's Enveloper: a transient designer that boosts or cuts a sound's attacks and
///     its tails independently of its level.
///     <list type="bullet">
///         <item>
///             Two pairs of envelope followers on the stereo-linked level (a 10 ms mean square), in dB. The attack pair: a fast follower
///             (0.5 ms up) against one rising at the Attack time; where the fast one leads, the gap is the transient.
///             The release pair: a follower falling at the Release time against one falling in 20 ms; where the slow one
///             lingers, the gap is the tail.
///         </item>
///         <item>
///             The gain is Attack Gain × the transient gap plus Release Gain × the tail gap (±100 %: the gap itself, at
///             most ±24 dB), applied only above the Threshold.
///         </item>
///         <item>
///             Lookahead delays the audio, not the detector, so a boost lands on the very start of an attack; it is the
///             effect's latency. Output trims; Mix blends the (equally delayed) dry signal back.
///         </item>
///     </list>
///     The <see cref="IAudioEffect.Reading" /> is the gain applied at the end of the last buffer (dB).
/// </summary>
public sealed class Enveloper : AudioEffect {
    public const string Id = "anode.enveloper";
    const double MaxDb = 24;

    static readonly EffectParameters P = new();
    public static readonly int Threshold = P.Add("threshold", "Threshold", -80, 0, -50, "dB");
    public static readonly int AttackGain = P.Add("attackGain", "Attack Gain", -100, 100, 0, "%");
    public static readonly int AttackTime = P.Add("attackTime", "Attack Time", 1, 200, 20, "ms");
    public static readonly int ReleaseGain = P.Add("releaseGain", "Release Gain", -100, 100, 0, "%");
    public static readonly int ReleaseTime = P.Add("releaseTime", "Release Time", 20, 3_000, 400, "ms");
    public static readonly int Lookahead = P.Stepped("lookahead", "Lookahead", 0, 20, 0, "ms", 20);
    public static readonly int Output = P.Add("output", "Output", -24, 24, 0, "dB");
    public static readonly int Mix = P.Add("mix", "Mix", 0, 100, 100, "%");

    public static EffectDescriptor Info { get; } = P.Describe(
        Id,
        "Enveloper",
        EffectCategories.Dynamics,
        AttackGain,
        ReleaseGain,
        AttackTime,
        ReleaseTime,
        Threshold,
        Output,
        Mix
    );

    readonly DelayLine lineLeft = new();
    readonly DelayLine lineRight = new();
    GainRamp output;
    GainRamp mix;
    double fast;
    double attackFollower;
    double releaseFollower;
    double shortFollower;
    double fastUp;
    double attackUp;
    double releaseDown;
    double shortDown;
    double attackAmount;
    double releaseAmount;
    double threshold;
    double gainDb;
    double meanSquare;
    double meanStep;
    int lookahead;

    public Enveloper() : base(Info) { }

    public override int LatencySamples =>
        Prepared ? (int)Math.Round(GetParameter(Lookahead) * 0.001 * SampleRate) : 0;

    protected override void OnPrepare(float sampleRate) {
        meanStep = 1 - Math.Exp(-1 / (0.01 * sampleRate));
        var samples = (int)Math.Ceiling(0.02 * sampleRate) + 4;
        lineLeft.Allocate(samples);
        lineRight.Allocate(samples);
    }

    protected override void OnReset() {
        lineLeft.Reset();
        lineRight.Reset();
        fast = attackFollower = releaseFollower = shortFollower = -120;
        meanSquare = 0;
        gainDb = 0;
        Publish(0);
    }

    protected override void Update() {
        threshold = Value(Threshold);
        attackAmount = Value(AttackGain) * 0.01;
        releaseAmount = Value(ReleaseGain) * 0.01;
        fastUp = Coefficient(0.5);
        attackUp = Coefficient(Value(AttackTime));
        releaseDown = Coefficient(Value(ReleaseTime));
        shortDown = Coefficient(20);
        lookahead = (int)Math.Round(Value(Lookahead) * 0.001 * SampleRate);
        output.Set(Decibels.ToGain(Value(Output)), Snapping);
        mix.Set(Value(Mix) * 0.01f, Snapping);
    }

    double Coefficient(double ms) => Math.Exp(-1 / (ms * 0.001 * SampleRate));

    protected override void Render(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        for (var i = 0; i < left.Length; i++) {
            // The level: a 10 ms mean square, so the followers see the envelope rather than the waveform.
            var square = Math.Max(left[i] * left[i], right[i] * right[i]);
            meanSquare += (square - meanSquare) * meanStep;
            var level = meanSquare > 1e-12 ? 10 * Math.Log10(meanSquare) + 3 : -120;
            // Attack pair: both fall quickly, rise at their own speeds.
            fast = level > fast ? level + (fast - level) * fastUp : level + (fast - level) * shortDown;
            attackFollower = level > attackFollower
                ? level + (attackFollower - level) * attackUp
                : level + (attackFollower - level) * shortDown;
            // Release pair: both rise at once, fall at their own speeds.
            releaseFollower = level > releaseFollower ? level : level + (releaseFollower - level) * releaseDown;
            shortFollower = level > shortFollower ? level : level + (shortFollower - level) * shortDown;
            var transient = Math.Max(0, fast - attackFollower);
            var tail = Math.Max(0, releaseFollower - shortFollower);
            var target = Math.Clamp(attackAmount * transient + releaseAmount * tail, -MaxDb, MaxDb);
            // Above the threshold only, faded in over 6 dB.
            target *= Math.Clamp((releaseFollower - threshold) / 6, 0, 1);
            gainDb = target;
            var gain = (float)Math.Pow(10, gainDb * 0.05) * output.Next();
            lineLeft.Write(left[i]);
            lineRight.Write(right[i]);
            var l = lineLeft.Read(lookahead);
            var r = lineRight.Read(lookahead);
            var wet = mix.Next();
            left[i] = l + (l * gain - l) * wet;
            right[i] = r + (r * gain - r) * wet;
        }
    }

    protected override void Finish() {
        Publish((float)gainDb);
        if (meanSquare < 1e-30) {
            meanSquare = 0;
        }
    }
}
