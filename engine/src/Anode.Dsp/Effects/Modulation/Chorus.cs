using Anode.Dsp.Delays;

namespace Anode.Dsp.Effects.Modulation;

/// <summary>
///     The modulation effects' oscillator: a phase in cycles, advanced once per control interval, with the left
///     channel's value and the right channel's (<c>spread</c> degrees on) ramped per sample between control points.
/// </summary>
internal struct StereoLfo {
    double phase;
    GainRamp left;
    GainRamp right;

    public void Reset() => phase = 0;

    /// <summary>Advances by one control interval at <paramref name="hz" /> and sets the next values.</summary>
    public void Update(double hz, double sampleRate, double spreadDegrees, bool triangle, bool jump) {
        phase += hz * AudioEffect.ControlInterval / sampleRate;
        phase -= Math.Floor(phase);
        left.Set(Shape(phase, triangle), jump);
        right.Set(Shape(phase + spreadDegrees / 360, triangle), jump);
    }

    public float NextLeft() => left.Next();

    public float NextRight() => right.Next();

    static float Shape(double p, bool triangle) {
        p -= Math.Floor(p);
        if (!triangle) {
            return (float)Math.Sin(2 * Math.PI * p);
        }

        return (float)(p < 0.25 ? 4 * p : p < 0.75 ? 2 - 4 * p : 4 * p - 4);
    }
}

/// <summary>
///     <c>anode.chorus</c>: a stereo chorus. Each channel reads its own delay line at the delay ± the depth (up to ±4
///     ms) swung by a sine, the right channel's LFO Spread degrees on from the left's; feedback returns some of the
///     chorused signal into the line. Mix crossfades dry and wet (50 % is the classic chorus).
/// </summary>
public sealed class Chorus : AudioEffect {
    public const string Id = "anode.chorus";
    const double MaxSwingMs = 4;

    static readonly EffectParameters P = new();
    public static readonly int Rate = P.Add("rate", "Rate", 0.05f, 10, 0.8f, "Hz");
    public static readonly int Depth = P.Add("depth", "Depth", 0, 100, 40, "%");
    public static readonly int Delay = P.Add("delay", "Delay", 5, 40, 12, "ms");
    public static readonly int Feedback = P.Add("feedback", "Feedback", 0, 90, 0, "%");
    public static readonly int Spread = P.Add("spread", "Stereo Spread", 0, 180, 90, "°");
    public static readonly int Mix = P.Add("mix", "Mix", 0, 100, 50, "%");

    public static EffectDescriptor Info { get; } = P.Describe(Id, "Chorus", EffectCategories.Modulation);

    readonly DelayLine lineLeft = new();
    readonly DelayLine lineRight = new();
    StereoLfo lfo;
    GainRamp delay;
    GainRamp swing;
    GainRamp mix;
    float feedback;

    public Chorus() : base(Info) { }

    protected override void OnPrepare(float sampleRate) {
        var samples = (int)Math.Ceiling((40 + MaxSwingMs + 1) * 0.001 * sampleRate);
        lineLeft.Allocate(samples);
        lineRight.Allocate(samples);
    }

    protected override void OnReset() {
        lineLeft.Reset();
        lineRight.Reset();
        lfo.Reset();
    }

    protected override void Update() {
        lfo.Update(Value(Rate), SampleRate, Value(Spread), false, Snapping);
        delay.Set(Value(Delay) * 0.001f * SampleRate, Snapping);
        swing.Set((float)(Value(Depth) * 0.01 * MaxSwingMs * 0.001 * SampleRate), Snapping);
        mix.Set(Value(Mix) * 0.01f, Snapping);
        feedback = Value(Feedback) * 0.01f;
    }

    protected override void Render(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        for (var i = 0; i < left.Length; i++) {
            var d = delay.Next();
            var s = swing.Next();
            // Read before the write: a delay of D samples is D − 1 writes back.
            var l = lineLeft.ReadCubic(d + s * lfo.NextLeft() - 1);
            var r = lineRight.ReadCubic(d + s * lfo.NextRight() - 1);
            lineLeft.Write(left[i] + feedback * l);
            lineRight.Write(right[i] + feedback * r);
            var wet = mix.Next();
            left[i] += (l - left[i]) * wet;
            right[i] += (r - right[i]) * wet;
        }
    }
}

/// <summary>
///     <c>anode.flanger</c>: a stereo flanger. A short delay (the Delay, swept by a triangle down to 5 % of it and up to
///     195 % at full depth) with feedback (negative inverts it, for the hollow flange), the right channel's LFO Spread
///     degrees on. Mix 50 % gives the deepest notches.
/// </summary>
public sealed class Flanger : AudioEffect {
    public const string Id = "anode.flanger";

    static readonly EffectParameters P = new();
    public static readonly int Rate = P.Add("rate", "Rate", 0.02f, 10, 0.25f, "Hz");
    public static readonly int Depth = P.Add("depth", "Depth", 0, 100, 70, "%");
    public static readonly int Delay = P.Add("delay", "Delay", 0.5f, 10, 2, "ms");
    public static readonly int Feedback = P.Add("feedback", "Feedback", -95, 95, 50, "%");
    public static readonly int Spread = P.Add("spread", "Stereo Spread", 0, 180, 90, "°");
    public static readonly int Mix = P.Add("mix", "Mix", 0, 100, 50, "%");

    public static EffectDescriptor Info { get; } = P.Describe(Id, "Flanger", EffectCategories.Modulation);

    readonly DelayLine lineLeft = new();
    readonly DelayLine lineRight = new();
    StereoLfo lfo;
    GainRamp delay;
    GainRamp depth;
    GainRamp mix;
    float feedback;

    public Flanger() : base(Info) { }

    protected override void OnPrepare(float sampleRate) {
        var samples = (int)Math.Ceiling(21 * 0.001 * sampleRate) + 4;
        lineLeft.Allocate(samples);
        lineRight.Allocate(samples);
    }

    protected override void OnReset() {
        lineLeft.Reset();
        lineRight.Reset();
        lfo.Reset();
    }

    protected override void Update() {
        lfo.Update(Value(Rate), SampleRate, Value(Spread), true, Snapping);
        delay.Set(Value(Delay) * 0.001f * SampleRate, Snapping);
        depth.Set(Value(Depth) * 0.0095f, Snapping);
        mix.Set(Value(Mix) * 0.01f, Snapping);
        feedback = Value(Feedback) * 0.01f;
    }

    protected override void Render(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        for (var i = 0; i < left.Length; i++) {
            var d = delay.Next();
            var k = depth.Next();
            var l = lineLeft.ReadCubic(d * (1 + k * lfo.NextLeft()) - 1);
            var r = lineRight.ReadCubic(d * (1 + k * lfo.NextRight()) - 1);
            lineLeft.Write(left[i] + feedback * l);
            lineRight.Write(right[i] + feedback * r);
            var wet = mix.Next();
            left[i] += (l - left[i]) * wet;
            right[i] += (r - right[i]) * wet;
        }
    }
}
