using Anode.Dsp.Filters;
using Anode.Dsp.Modulation;

namespace Anode.Dsp.Effects.Filter;

/// <summary>
///     <c>anode.auto-filter</c>, after Logic's AutoFilter: the synth's resonant multimode filter
///     (<see cref="MultimodeFilter" />: 24 dB low-pass, 12 dB low-, high- and band-pass) with its cutoff moved by an LFO
///     (sine, triangle, saw, square, sample-and-hold; in Hz or, with Sync, a cycle per note value at the host's tempo;
///     up to ±4 octaves at full depth) and by an envelope follower on the input (up to ±4 octaves either way; its own
///     attack and release). The modulation is applied at control rate. Drive, when above 0 dB, saturates the input (a
///     tanh) before the filter; Mix blends the dry signal back.
/// </summary>
public sealed class AutoFilter : AudioEffect {
    public const string Id = "anode.auto-filter";
    const double Octaves = 4;

    static readonly EffectParameters P = new();

    public static readonly int Mode = P.Choice(
        "mode",
        "Filter Mode",
        ["Low-pass 24 dB", "Low-pass 12 dB", "High-pass 12 dB", "Band-pass 12 dB"],
        0
    );

    public static readonly int Cutoff = P.Add("cutoff", "Cutoff", 20, 20_000, 1_000, "Hz");
    public static readonly int Resonance = P.Add("resonance", "Resonance", 0, 100, 30, "%");

    public static readonly int LfoShape =
        P.Choice("lfoShape", "LFO Shape", ["Sine", "Triangle", "Saw", "Square", "Sample & Hold"], 0);

    public static readonly int LfoRate = P.Add("lfoRate", "LFO Rate", 0.05f, 20, 1, "Hz");
    public static readonly int Sync = P.Toggle("sync", "Sync", false);
    public static readonly int Tempo = P.Add("tempo", "Tempo", 20, 300, 120, "bpm");
    public static readonly int Note = P.Choice("note", "LFO Note", NoteValues.Names, NoteValues.Quarter);
    public static readonly int LfoDepth = P.Add("lfoDepth", "LFO Depth", 0, 100, 50, "%");
    public static readonly int EnvelopeAmount = P.Add("envAmount", "Envelope Amount", -100, 100, 0, "%");
    public static readonly int EnvelopeAttack = P.Add("envAttack", "Envelope Attack", 0.1f, 500, 10, "ms");
    public static readonly int EnvelopeRelease = P.Add("envRelease", "Envelope Release", 5, 2_000, 200, "ms");
    public static readonly int Drive = P.Add("drive", "Drive", 0, 24, 0, "dB");
    public static readonly int Mix = P.Add("mix", "Mix", 0, 100, 100, "%");

    public static EffectDescriptor Info { get; } = P.Describe(
        Id,
        "AutoFilter",
        EffectCategories.Filter,
        Cutoff,
        Resonance,
        LfoDepth,
        LfoRate,
        EnvelopeAmount,
        Drive,
        Mix,
        Note
    );

    MultimodeCoefficients coefficients;
    MultimodeFilter left;
    MultimodeFilter right;
    Lfo lfo;
    GainRamp mix;
    double envelope;
    double attack;
    double release;
    float drive;
    bool driven;

    public AutoFilter() : base(Info) { }

    /// <summary>The cutoff at the last control point, in Hz.</summary>
    public double CurrentCutoff { get; private set; }

    protected override void OnPrepare(float sampleRate) { }

    protected override void OnReset() {
        left.Reset();
        right.Reset();
        lfo.Reset();
        envelope = 0;
    }

    protected override void Update() {
        var hz = Switch(Sync) ? 1 / NoteValues.Seconds(Choice(Note), HostTempo(Value(Tempo))) : Value(LfoRate);
        var modulation = lfo.Next((LfoShape)Choice(LfoShape), hz * ControlInterval / SampleRate);
        var octaves = Octaves
            * (Value(LfoDepth) * 0.01 * modulation
                + Value(EnvelopeAmount) * 0.01 * Math.Min(1, envelope));
        CurrentCutoff = Math.Clamp(Value(Cutoff) * Math.Pow(2, octaves), 20, 20_000);
        var mode = (FilterMode)Choice(Mode);
        coefficients = MultimodeCoefficients.Design(
            mode,
            SampleRate,
            CurrentCutoff,
            MultimodeCoefficients.Quality(mode, Value(Resonance) * 0.01)
        );
        attack = Math.Exp(-1 / (Value(EnvelopeAttack) * 0.001 * SampleRate));
        release = Math.Exp(-1 / (Value(EnvelopeRelease) * 0.001 * SampleRate));
        driven = Value(Drive) > 0;
        drive = Decibels.ToGain(Value(Drive));
        mix.Set(Value(Mix) * 0.01f, Snapping);
    }

    protected override void Render(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        for (var i = 0; i < left.Length; i++) {
            var l = left[i];
            var r = right[i];
            var level = Math.Max(Math.Abs(l), Math.Abs(r));
            envelope = level > envelope ? level + (envelope - level) * attack : level + (envelope - level) * release;
            if (driven) {
                l = MathF.Tanh(drive * l);
                r = MathF.Tanh(drive * r);
            }

            var fl = (float)this.left.Tick(coefficients, l);
            var fr = (float)this.right.Tick(coefficients, r);
            var wet = mix.Next();
            left[i] += (fl - left[i]) * wet;
            right[i] += (fr - right[i]) * wet;
        }
    }

    protected override void Finish() {
        left.FlushDenormals();
        right.FlushDenormals();
        if (envelope < 1e-30) {
            envelope = 0;
        }
    }
}
