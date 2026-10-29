using Anode.Dsp.Filters;

namespace Anode.Dsp.Effects.Specialized;

/// <summary>
///     <c>anode.exciter</c>, after Logic's Exciter: adds harmonics above a frequency.
///     <list type="bullet">
///         <item>The input passes a 24 dB/octave high-pass at Frequency.</item>
///         <item>
///             What passes is driven into a shaper, Colour 1 a tanh (odd harmonics), Colour 2 an asymmetric curve
///             (even and odd, warmer), and scaled back by the drive: the high band, saturated into its harmonics.
///         </item>
///         <item>
///             A second 12 dB/octave high-pass at the frequency removes the asymmetric curve's offset and what the shaper
///             folded down, and Harmonics
///             (the <c>mix</c> parameter) adds the result to the untouched input: at 0 % the input passes exactly.
///         </item>
///     </list>
/// </summary>
public sealed class Exciter : AudioEffect {
    public const string Id = "anode.exciter";

    static readonly EffectParameters P = new();
    public static readonly int Frequency = P.Add("frequency", "Frequency", 1_000, 12_000, 4_000, "Hz");
    public static readonly int Drive = P.Add("drive", "Drive", 0, 24, 12, "dB");
    public static readonly int Colour = P.Choice("colour", "Colour", ["Colour 1", "Colour 2"], 0);
    public static readonly int Mix = P.Add("mix", "Harmonics", 0, 100, 30, "%");

    public static EffectDescriptor Info { get; } =
        P.Describe(Id, "Exciter", EffectCategories.Specialized, Mix, Frequency, Drive, Colour);

    readonly SvfState[] input = new SvfState[4];
    readonly SvfState[] output = new SvfState[2];
    SvfCoefficients filter;
    GainRamp amount;
    float drive;
    bool even;

    public Exciter() : base(Info) { }

    protected override void OnPrepare(float sampleRate) { }

    protected override void OnReset() {
        Array.Clear(input);
        Array.Clear(output);
    }

    protected override void Update() {
        filter = SvfCoefficients.Design(SampleRate, Value(Frequency), 0.70710678118654752);
        drive = Decibels.ToGain(Value(Drive)) * 2;
        even = Choice(Colour) == 1;
        amount.Set(Value(Mix) * 0.01f, Snapping);
    }

    protected override void Render(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        for (var i = 0; i < left.Length; i++) {
            var a = amount.Next();
            var l = Harmonics(left[i], 0);
            var r = Harmonics(right[i], 1);
            left[i] += a * l;
            right[i] += a * r;
        }
    }

    float Harmonics(float x, int channel) {
        var high = input[channel * 2].Tick(filter, x, SvfOutput.HighPass);
        high = input[channel * 2 + 1].Tick(filter, high, SvfOutput.HighPass);
        var driven = drive * high;
        // Scaled back by the drive, so a quiet band passes at its own level and a loud one is folded into harmonics.
        var shaped = even ? Math.Tanh(driven + 0.3 * driven * driven) : Math.Tanh(driven);
        var harmonics = shaped / drive;
        return (float)output[channel].Tick(filter, harmonics, SvfOutput.HighPass);
    }

    protected override void Finish() {
        for (var i = 0; i < input.Length; i++) {
            input[i].FlushDenormals();
        }

        output[0].FlushDenormals();
        output[1].FlushDenormals();
    }
}
