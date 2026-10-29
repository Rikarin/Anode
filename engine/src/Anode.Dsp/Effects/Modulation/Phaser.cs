using Anode.Dsp.Filters;

namespace Anode.Dsp.Effects.Modulation;

/// <summary>
///     <c>anode.phaser</c>: a stereo phaser of 2 to 12 first-order allpass stages (trapezoidal, <see cref="OnePole" />)
///     whose break frequency a sine sweeps around the centre, up to ±3 octaves at full depth; feedback from the last
///     stage to the first sharpens the notches (negative inverts it). The right channel's LFO is Spread degrees on.
///     Mix 50 % gives the deepest notches.
/// </summary>
public sealed class Phaser : AudioEffect {
    public const string Id = "anode.phaser";
    const int MaxStages = 12;

    static readonly EffectParameters P = new();
    public static readonly int Rate = P.Add("rate", "Rate", 0.02f, 10, 0.5f, "Hz");
    public static readonly int Depth = P.Add("depth", "Depth", 0, 100, 70, "%");
    public static readonly int Center = P.Add("center", "Center", 100, 8_000, 800, "Hz");
    public static readonly int Stages = P.Choice("stages", "Stages", ["2", "4", "6", "8", "10", "12"], 1);
    public static readonly int Feedback = P.Add("feedback", "Feedback", -95, 95, 30, "%");
    public static readonly int Spread = P.Add("spread", "Stereo Spread", 0, 180, 90, "°");
    public static readonly int Mix = P.Add("mix", "Mix", 0, 100, 50, "%");

    public static EffectDescriptor Info { get; } =
        P.Describe(Id, "Phaser", EffectCategories.Modulation, Rate, Depth, Center, Feedback, Mix, Spread);

    readonly OnePole[] left = new OnePole[MaxStages];
    readonly OnePole[] right = new OnePole[MaxStages];
    double phase;
    GainRamp coefficientLeft;
    GainRamp coefficientRight;
    GainRamp mix;
    float feedback;
    float lastLeft;
    float lastRight;
    int stages;

    public Phaser() : base(Info) { }

    protected override void OnPrepare(float sampleRate) { }

    protected override void OnReset() {
        for (var i = 0; i < MaxStages; i++) {
            left[i].Reset();
            right[i].Reset();
        }

        phase = 0;
        lastLeft = 0;
        lastRight = 0;
    }

    protected override void Update() {
        phase += Value(Rate) * ControlInterval / SampleRate;
        phase -= Math.Floor(phase);
        var octaves = 3 * Value(Depth) * 0.01;
        var spread = Value(Spread) / 360;
        var l = Value(Center) * Math.Pow(2, octaves * Math.Sin(2 * Math.PI * phase));
        var r = Value(Center) * Math.Pow(2, octaves * Math.Sin(2 * Math.PI * (phase + spread)));
        coefficientLeft.Set((float)OnePole.Coefficient(SampleRate, l), Snapping);
        coefficientRight.Set((float)OnePole.Coefficient(SampleRate, r), Snapping);
        stages = 2 * (Choice(Stages) + 1);
        feedback = Value(Feedback) * 0.01f;
        mix.Set(Value(Mix) * 0.01f, Snapping);
    }

    protected override void Render(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        for (var i = 0; i < left.Length; i++) {
            double gl = coefficientLeft.Next();
            double gr = coefficientRight.Next();
            double l = left[i] + feedback * lastLeft;
            double r = right[i] + feedback * lastRight;
            for (var s = 0; s < stages; s++) {
                l = this.left[s].AllPass(gl, l);
                r = this.right[s].AllPass(gr, r);
            }

            lastLeft = (float)l;
            lastRight = (float)r;
            var wet = mix.Next();
            left[i] += (lastLeft - left[i]) * wet;
            right[i] += (lastRight - right[i]) * wet;
        }
    }

    protected override void Finish() {
        for (var i = 0; i < MaxStages; i++) {
            left[i].FlushDenormals();
            right[i].FlushDenormals();
        }
    }
}

/// <summary>
///     <c>anode.tremolo</c>: amplitude modulation. The gain swings between 1 and 1 − Depth with a sine, a triangle or a
///     square (its edges softened, a tanh of a sine, so it does not click), at a rate in Hz or, with Sync, one cycle
///     per note value at the host's tempo. The right channel is Phase degrees on (180° makes it an auto-pan).
/// </summary>
public sealed class Tremolo : AudioEffect {
    public const string Id = "anode.tremolo";

    static readonly EffectParameters P = new();
    public static readonly int Rate = P.Add("rate", "Rate", 0.1f, 20, 4, "Hz");
    public static readonly int Sync = P.Toggle("sync", "Sync", false);
    public static readonly int Tempo = P.Add("tempo", "Tempo", 20, 300, 120, "bpm");
    public static readonly int Note = P.Choice("note", "Note", NoteValues.Names, NoteValues.Eighth);
    public static readonly int Depth = P.Add("depth", "Depth", 0, 100, 50, "%");
    public static readonly int Shape = P.Choice("shape", "Shape", ["Sine", "Triangle", "Square"], 0);
    public static readonly int Phase = P.Add("phase", "Stereo Phase", 0, 180, 0, "°");

    public static EffectDescriptor Info { get; } =
        P.Describe(Id, "Tremolo", EffectCategories.Modulation, Rate, Depth, Note, Phase, Shape);

    double phase;
    double step;
    double offset;
    GainRamp depth;
    int shape;

    public Tremolo() : base(Info) { }

    /// <summary>The modulation rate at the current settings, in Hz.</summary>
    public double RateHz => Switch(Sync) ? 1 / NoteValues.Seconds(Choice(Note), HostTempo(Value(Tempo))) : Value(Rate);

    protected override void OnPrepare(float sampleRate) { }

    protected override void OnReset() => phase = 0;

    protected override void Update() {
        step = RateHz / SampleRate;
        offset = Value(Phase) / 360;
        shape = Choice(Shape);
        depth.Set(Value(Depth) * 0.01f, Snapping);
    }

    protected override void Render(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        for (var i = 0; i < left.Length; i++) {
            var d = depth.Next();
            left[i] *= 1 - d * (1 - Wave(phase)) * 0.5f;
            right[i] *= 1 - d * (1 - Wave(phase + offset)) * 0.5f;
            phase += step;
            if (phase >= 1) {
                phase -= 1;
            }
        }
    }

    const float SquareSharpness = 8;
    static readonly float SquareNormal = MathF.Tanh(SquareSharpness);

    float Wave(double p) {
        p -= Math.Floor(p);
        return shape switch {
            0 => (float)Math.Sin(2 * Math.PI * p),
            1 => (float)(p < 0.25 ? 4 * p : p < 0.75 ? 2 - 4 * p : 4 * p - 4),
            _ => MathF.Tanh(SquareSharpness * (float)Math.Sin(2 * Math.PI * p)) / SquareNormal
        };
    }
}
