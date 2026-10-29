using Anode.Dsp.Dynamics;

namespace Anode.Dsp.Effects.Dynamics;

/// <summary>
///     <c>anode.limiter</c>: a stereo-linked lookahead brickwall limiter (<see cref="LookaheadLimiter" />). Input gain,
///     an output ceiling, lookahead (0 … 10 ms in 0.1 ms steps), release, and true-peak detection, which keeps the
///     inter-sample peaks under the ceiling too (adds <see cref="TruePeak.Delay" /> samples). Latency is the lookahead,
///     rounded to samples, plus that. The <see cref="IAudioEffect.Reading" /> is the deepest gain reduction (dB, ≤ 0)
///     over the last buffer.
/// </summary>
public sealed class Limiter : AudioEffect {
    public const string Id = "anode.limiter";
    public const double MaxLookaheadMs = 10;

    static readonly EffectParameters P = new();
    public static readonly int InputGain = P.Add("gain", "Gain", 0, 24, 0, "dB");
    public static readonly int Ceiling = P.Add("ceiling", "Output Ceiling", -12, 0, -1, "dB");
    public static readonly int Lookahead = P.Stepped("lookahead", "Lookahead", 0, 10, 1.5f, "ms", 100);
    public static readonly int Release = P.Add("release", "Release", 1, 1_000, 50, "ms");
    public static readonly int TruePeakDetection = P.Toggle("truePeak", "True Peak Detection", true);

    public static EffectDescriptor Info { get; } = P.Describe(Id, "Limiter", EffectCategories.Dynamics, InputGain, Ceiling, Release, Lookahead);

    readonly LookaheadLimiter limiter = new();
    GainRamp gain;
    double ceiling = 1;
    double deepest = 1;

    public Limiter() : base(Info) { }

    public override int LatencySamples =>
        Prepared ? Latency(SampleRate, GetParameter(Lookahead), GetParameter(TruePeakDetection) >= 0.5f) : 0;

    /// <summary>The delay of a lookahead of <paramref name="milliseconds" />, with true-peak detection or not.</summary>
    internal static int Latency(float sampleRate, float milliseconds, bool truePeak) {
        var samples = LookaheadSamples(sampleRate, milliseconds);
        return truePeak ? samples + TruePeak.Delay : samples;
    }

    static int LookaheadSamples(float sampleRate, float milliseconds) =>
        (int)Math.Round(milliseconds * 0.001 * sampleRate);

    protected override void OnPrepare(float sampleRate) =>
        limiter.Allocate((int)Math.Ceiling(MaxLookaheadMs * 0.001 * sampleRate));

    protected override void OnReset() {
        limiter.Reset();
        deepest = 1;
        Publish(0);
    }

    protected override void Update() {
        limiter.Configure(LookaheadSamples(SampleRate, Value(Lookahead)), Switch(TruePeakDetection));
        limiter.SetRelease(Value(Release) * 0.001, SampleRate);
        gain.Set(Decibels.ToGain(Value(InputGain)), Snapping);
        ceiling = Decibels.ToGain((double)Value(Ceiling));
    }

    protected override void Render(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        for (var i = 0; i < left.Length; i++) {
            var g = gain.Next();
            var l = left[i] * g;
            var r = right[i] * g;
            limiter.Tick(ref l, ref r, ceiling);
            deepest = Math.Min(deepest, limiter.Gain);
            left[i] = l;
            right[i] = r;
        }
    }

    protected override void Finish() {
        Publish(Decibels.FromGain(deepest));
        deepest = 1;
    }
}

/// <summary>
///     <c>anode.adaptive-limiter</c>, after Logic's Adaptive Limiter: a limiter that rounds peaks rather than flattening
///     them. A soft knee starts <see cref="LookaheadLimiter.KneeDb" /> dB under the ceiling, and the release adapts to
///     the program: brief peaks let go quickly, sustained loudness stays down and recovers slowly (10× the release).
///     Gain, output ceiling, lookahead, release, and the mode: Optimal Lookahead (sample peaks) or No Overs (true peaks,
///     <see cref="TruePeak.Delay" /> more samples of latency). The <see cref="IAudioEffect.Reading" /> is the deepest gain
///     reduction (dB, ≤ 0) over the last buffer.
/// </summary>
public sealed class AdaptiveLimiter : AudioEffect {
    public const string Id = "anode.adaptive-limiter";

    static readonly EffectParameters P = new();
    public static readonly int InputGain = P.Add("gain", "Gain", 0, 24, 0, "dB");
    public static readonly int Ceiling = P.Add("ceiling", "Out Ceiling", -12, 0, -0.5f, "dB");
    public static readonly int Lookahead = P.Stepped("lookahead", "Lookahead", 0, 10, 5, "ms", 100);
    public static readonly int Release = P.Add("release", "Release", 10, 1_000, 60, "ms");
    public static readonly int Mode = P.Choice("mode", "Mode", ["Optimal Lookahead", "No Overs"], 0);

    public static EffectDescriptor Info { get; } =
        P.Describe(Id, "Adaptive Limiter", EffectCategories.Dynamics, InputGain, Ceiling, Release, Lookahead);

    readonly LookaheadLimiter limiter = new() { Adaptive = true };
    GainRamp gain;
    double ceiling = 1;
    double deepest = 1;

    public AdaptiveLimiter() : base(Info) { }

    public override int LatencySamples =>
        Prepared ? Limiter.Latency(SampleRate, GetParameter(Lookahead), GetParameter(Mode) >= 0.5f) : 0;

    protected override void OnPrepare(float sampleRate) =>
        limiter.Allocate((int)Math.Ceiling(Limiter.MaxLookaheadMs * 0.001 * sampleRate));

    protected override void OnReset() {
        limiter.Reset();
        deepest = 1;
        Publish(0);
    }

    protected override void Update() {
        limiter.Configure((int)Math.Round(Value(Lookahead) * 0.001 * SampleRate), Choice(Mode) == 1);
        limiter.SetRelease(Value(Release) * 0.001, SampleRate);
        gain.Set(Decibels.ToGain(Value(InputGain)), Snapping);
        ceiling = Decibels.ToGain((double)Value(Ceiling));
    }

    protected override void Render(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        for (var i = 0; i < left.Length; i++) {
            var g = gain.Next();
            var l = left[i] * g;
            var r = right[i] * g;
            limiter.Tick(ref l, ref r, ceiling);
            deepest = Math.Min(deepest, limiter.Gain);
            left[i] = l;
            right[i] = r;
        }
    }

    protected override void Finish() {
        Publish(Decibels.FromGain(deepest));
        deepest = 1;
    }
}
