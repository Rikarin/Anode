using Anode.Dsp.Filters;

namespace Anode.Dsp.Effects.Multi;

/// <summary>
///     <c>anode.phat-fx</c>, after Logic's Phat FX: a chain that fattens and moves a sound.
///     <list type="number">
///         <item>Low Boost: a low shelf at 100 Hz.</item>
///         <item>Distortion: soft (tanh), hard (clip) or fuzz (exponential), driven by Drive and levelled after.</item>
///         <item>
///             Filter: the resonant 24 dB low-pass (<see cref="MultimodeFilter" />) at Cutoff, moved by an LFO synced
///             to the transport (a sine whose cycle is the LFO note value and whose phase follows the song position, so
///             it lands on the beat; free-running at the tempo while stopped) and by an envelope follower on the input,
///             up to ±4 octaves each.
///         </item>
///         <item>Squash: a compressor whose threshold and ratio rise together with the amount.</item>
///         <item>Output trims; Mix blends the dry signal back.</item>
///     </list>
/// </summary>
public sealed class PhatFx : AudioEffect {
    public const string Id = "anode.phat-fx";
    const double Octaves = 4;

    static readonly EffectParameters P = new();
    public static readonly int LowBoost = P.Add("lowBoost", "Low Boost", 0, 12, 3, "dB");
    public static readonly int Drive = P.Add("drive", "Drive", 0, 36, 12, "dB");
    public static readonly int Type = P.Choice("type", "Distortion", ["Soft", "Hard", "Fuzz"], 0);
    public static readonly int Cutoff = P.Add("cutoff", "Cutoff", 20, 20_000, 3_000, "Hz");
    public static readonly int Resonance = P.Add("resonance", "Resonance", 0, 100, 30, "%");
    public static readonly int LfoNote = P.Choice("lfoNote", "LFO Rate", NoteValues.Names, NoteValues.Quarter);
    public static readonly int LfoDepth = P.Add("lfoDepth", "LFO Depth", 0, 100, 0, "%");
    public static readonly int EnvelopeAmount = P.Add("envAmount", "Envelope", -100, 100, 0, "%");
    public static readonly int Squash = P.Add("squash", "Squash", 0, 100, 20, "%");
    public static readonly int Tempo = P.Add("tempo", "Tempo", 20, 300, 120, "bpm");
    public static readonly int Output = P.Add("output", "Output", -24, 12, 0, "dB");
    public static readonly int Mix = P.Add("mix", "Mix", 0, 100, 100, "%");

    public static EffectDescriptor Info { get; } = P.Describe(
        Id,
        "Phat FX",
        EffectCategories.MultiEffects,
        Drive,
        Cutoff,
        Resonance,
        LfoDepth,
        Squash,
        LowBoost,
        EnvelopeAmount,
        Mix
    );

    SvfEqCoefficients shelf;
    SvfEqState shelfLeft;
    SvfEqState shelfRight;
    MultimodeCoefficients filter;
    MultimodeFilter filterLeft;
    MultimodeFilter filterRight;
    GainRamp output;
    GainRamp mix;
    double envelope;
    double attack;
    double release;
    double squashLevel;
    double squashThreshold;
    double squashSlope;
    double squashAttack;
    double squashRelease;
    float drive;
    float level;
    int type;

    public PhatFx() : base(Info) { }

    /// <summary>The filter's cutoff at the last control point, in Hz.</summary>
    public double CurrentCutoff { get; private set; }

    protected override double ClockTempo => HostTempo(Value(Tempo));

    protected override void OnPrepare(float sampleRate) {
        attack = Math.Exp(-1 / (0.005 * sampleRate));
        release = Math.Exp(-1 / (0.15 * sampleRate));
        squashAttack = Math.Exp(-1 / (0.003 * sampleRate));
        squashRelease = Math.Exp(-1 / (0.12 * sampleRate));
    }

    protected override void OnReset() {
        shelfLeft.Reset();
        shelfRight.Reset();
        filterLeft.Reset();
        filterRight.Reset();
        envelope = 0;
        squashLevel = 0;
    }

    protected override void Update() {
        shelf = SvfEqCoefficients.Design(SvfEqType.LowShelf, SampleRate, 100, 0.7071, Value(LowBoost));
        type = Choice(Type);
        drive = Decibels.ToGain(Value(Drive));
        level = 1 / MathF.Sqrt(drive);
        var beats = NoteValues.BeatsOf(Choice(LfoNote));
        var lfo = Math.Sin(2 * Math.PI * (QuarterAt(0) / beats));
        var octaves = Octaves
            * (Value(LfoDepth) * 0.01 * lfo + Value(EnvelopeAmount) * 0.01 * Math.Min(1, 2 * envelope));
        CurrentCutoff = Math.Clamp(Value(Cutoff) * Math.Pow(2, octaves), 20, 20_000);
        filter = MultimodeCoefficients.Design(
            FilterMode.LowPass24,
            SampleRate,
            CurrentCutoff,
            MultimodeCoefficients.Quality(FilterMode.LowPass24, Value(Resonance) * 0.01)
        );
        var squash = Value(Squash) * 0.01;
        squashThreshold = -30 * squash;
        squashSlope = 1 / (1 + 7 * squash) - 1;
        output.Set(Decibels.ToGain((float)(Value(Output) - squashThreshold * squashSlope * 0.5)), Snapping);
        mix.Set(Value(Mix) * 0.01f, Snapping);
    }

    protected override void Render(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        for (var i = 0; i < left.Length; i++) {
            double l = left[i];
            double r = right[i];
            var peak = Math.Max(Math.Abs(l), Math.Abs(r));
            envelope = peak > envelope ? peak + (envelope - peak) * attack : peak + (envelope - peak) * release;
            l = Shape(shelfLeft.Tick(shelf, l));
            r = Shape(shelfRight.Tick(shelf, r));
            l = filterLeft.Tick(filter, l);
            r = filterRight.Tick(filter, r);
            var p = Math.Max(Math.Abs(l), Math.Abs(r));
            squashLevel = p > squashLevel
                ? p + (squashLevel - p) * squashAttack
                : p + (squashLevel - p) * squashRelease;
            var over = (squashLevel > 1e-6 ? 20 * Math.Log10(squashLevel) : -120) - squashThreshold;
            var gain = over > 0 ? Math.Pow(10, over * squashSlope * 0.05) : 1;
            var o = output.Next() * (float)gain;
            var wet = mix.Next();
            left[i] += ((float)l * o - left[i]) * wet;
            right[i] += ((float)r * o - right[i]) * wet;
        }
    }

    double Shape(double x) {
        var d = drive * x;
        var y = type switch {
            0 => Math.Tanh(d),
            1 => Math.Clamp(d, -1, 1),
            _ => Math.Sign(d) * (1 - Math.Exp(-Math.Abs(d)))
        };
        return y * level;
    }

    protected override void Finish() {
        shelfLeft.FlushDenormals();
        shelfRight.FlushDenormals();
        filterLeft.FlushDenormals();
        filterRight.FlushDenormals();
        if (envelope < 1e-30) {
            envelope = 0;
        }

        if (squashLevel < 1e-30) {
            squashLevel = 0;
        }
    }
}
