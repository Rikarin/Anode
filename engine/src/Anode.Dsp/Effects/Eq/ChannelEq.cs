using Anode.Dsp.Filters;
using Anode.Dsp.Metering;

namespace Anode.Dsp.Effects.Eq;

/// <summary>
///     <c>anode.channel-eq</c>, after Logic's Channel EQ: eight bands in series. A high-pass and a low-pass (Butterworth,
///     12, 24, 36 or 48 dB/octave), a low and a high shelf, and four bells, then the output gain. Every band is a
///     trapezoidal SVF (<see cref="SvfEqCoefficients" />), so the bands sweep without clicks; their frequency, gain and
///     quality are smoothed at control rate. A bell or shelf at 0 dB is skipped.
///     <para>
///         The <see cref="IAudioEffect.Reading" /> is the output's peak (dBFS) over the last buffer; the spectrum the
///         editor draws behind the curve comes from <see cref="ReadSpectrum" />.
///     </para>
/// </summary>
public sealed class ChannelEq : AudioEffect {
    public const string Id = "anode.channel-eq";

    /// <summary>The qualities of the Butterworth sections, per order (2, 4, 6, 8).</summary>
    static readonly double[][] Butterworth = [
        [0.70710678118654752],
        [0.54119610014619698, 1.3065629648763766],
        [0.51763809020504152, 0.70710678118654752, 1.9318516525781366],
        [0.50979557910415917, 0.60134488693504529, 0.89997622313641570, 2.5629154477415055]
    ];

    static readonly string[] Slopes = ["12 dB/oct", "24 dB/oct", "36 dB/oct", "48 dB/oct"];
    static readonly EffectParameters P = new();
    public static readonly int HighPassOn = P.Toggle("hpfOn", "Low Cut", false);
    public static readonly int HighPassFrequency = P.Add("hpfFreq", "Low Cut Frequency", 10, 20_000, 30, "Hz");
    public static readonly int HighPassSlope = P.Choice("hpfSlope", "Low Cut Slope", Slopes, 1);
    public static readonly int LowShelfOn = P.Toggle("lowShelfOn", "Low Shelf", true);
    public static readonly int LowShelfFrequency = P.Add("lowShelfFreq", "Low Shelf Frequency", 10, 20_000, 80, "Hz");
    public static readonly int LowShelfGain = P.Add("lowShelfGain", "Low Shelf Gain", -24, 24, 0, "dB");
    public static readonly int LowShelfQ = P.Add("lowShelfQ", "Low Shelf Q", 0.1f, 2, 0.71f, string.Empty);
    public static readonly int Peak1On = P.Toggle("peak1On", "Peak 1", true);
    public static readonly int Peak1Frequency = P.Add("peak1Freq", "Peak 1 Frequency", 10, 20_000, 200, "Hz");
    public static readonly int Peak1Gain = P.Add("peak1Gain", "Peak 1 Gain", -24, 24, 0, "dB");
    public static readonly int Peak1Q = P.Add("peak1Q", "Peak 1 Q", 0.1f, 20, 1, string.Empty);
    public static readonly int Peak2On = P.Toggle("peak2On", "Peak 2", true);
    public static readonly int Peak2Frequency = P.Add("peak2Freq", "Peak 2 Frequency", 10, 20_000, 600, "Hz");
    public static readonly int Peak2Gain = P.Add("peak2Gain", "Peak 2 Gain", -24, 24, 0, "dB");
    public static readonly int Peak2Q = P.Add("peak2Q", "Peak 2 Q", 0.1f, 20, 1, string.Empty);
    public static readonly int Peak3On = P.Toggle("peak3On", "Peak 3", true);
    public static readonly int Peak3Frequency = P.Add("peak3Freq", "Peak 3 Frequency", 10, 20_000, 2_000, "Hz");
    public static readonly int Peak3Gain = P.Add("peak3Gain", "Peak 3 Gain", -24, 24, 0, "dB");
    public static readonly int Peak3Q = P.Add("peak3Q", "Peak 3 Q", 0.1f, 20, 1, string.Empty);
    public static readonly int Peak4On = P.Toggle("peak4On", "Peak 4", true);
    public static readonly int Peak4Frequency = P.Add("peak4Freq", "Peak 4 Frequency", 10, 20_000, 6_000, "Hz");
    public static readonly int Peak4Gain = P.Add("peak4Gain", "Peak 4 Gain", -24, 24, 0, "dB");
    public static readonly int Peak4Q = P.Add("peak4Q", "Peak 4 Q", 0.1f, 20, 1, string.Empty);
    public static readonly int HighShelfOn = P.Toggle("highShelfOn", "High Shelf", true);

    public static readonly int HighShelfFrequency =
        P.Add("highShelfFreq", "High Shelf Frequency", 10, 20_000, 10_000, "Hz");

    public static readonly int HighShelfGain = P.Add("highShelfGain", "High Shelf Gain", -24, 24, 0, "dB");
    public static readonly int HighShelfQ = P.Add("highShelfQ", "High Shelf Q", 0.1f, 2, 0.71f, string.Empty);
    public static readonly int LowPassOn = P.Toggle("lpfOn", "High Cut", false);
    public static readonly int LowPassFrequency = P.Add("lpfFreq", "High Cut Frequency", 10, 20_000, 18_000, "Hz");
    public static readonly int LowPassSlope = P.Choice("lpfSlope", "High Cut Slope", Slopes, 1);
    public static readonly int Output = P.Add("output", "Output Gain", -24, 24, 0, "dB");

    public static EffectDescriptor Info { get; } = P.Describe(Id, "Channel EQ", EffectCategories.Eq);

    /// <summary>The cut filters' sections (up to four each), then the shelves and bells.</summary>
    const int Sections = 4 + 4 + 6;

    readonly SvfEqCoefficients[] coefficients = new SvfEqCoefficients[Sections];
    readonly bool[] active = new bool[Sections];
    readonly SvfEqState[] left = new SvfEqState[Sections];
    readonly SvfEqState[] right = new SvfEqState[Sections];
    readonly SpectrumAnalyzer analyzer = new();
    GainRamp output;
    float peak;

    public ChannelEq() : base(Info) { }

    /// <summary>Control thread: the output spectrum, as <see cref="SpectrumAnalyzer.Read" /> gives it.</summary>
    public void ReadSpectrum(Span<float> bandsDb) => analyzer.Read(bandsDb, SampleRate);

    protected override void OnPrepare(float sampleRate) { }

    protected override void OnReset() {
        for (var i = 0; i < Sections; i++) {
            left[i].Reset();
            right[i].Reset();
        }

        analyzer.Reset();
        peak = 0;
        Publish(FloorDb);
    }

    protected override void Update() {
        Cut(0, SvfEqType.HighPass, HighPassOn, HighPassFrequency, HighPassSlope);
        Cut(4, SvfEqType.LowPass, LowPassOn, LowPassFrequency, LowPassSlope);
        Band(8, SvfEqType.LowShelf, LowShelfOn, LowShelfFrequency, LowShelfGain, LowShelfQ);
        Band(9, SvfEqType.Bell, Peak1On, Peak1Frequency, Peak1Gain, Peak1Q);
        Band(10, SvfEqType.Bell, Peak2On, Peak2Frequency, Peak2Gain, Peak2Q);
        Band(11, SvfEqType.Bell, Peak3On, Peak3Frequency, Peak3Gain, Peak3Q);
        Band(12, SvfEqType.Bell, Peak4On, Peak4Frequency, Peak4Gain, Peak4Q);
        Band(13, SvfEqType.HighShelf, HighShelfOn, HighShelfFrequency, HighShelfGain, HighShelfQ);
        output.Set(Decibels.ToGain(Value(Output)), Snapping);
    }

    void Cut(int first, SvfEqType type, int on, int frequency, int slope) {
        var qualities = Butterworth[Choice(slope)];
        for (var i = 0; i < 4; i++) {
            var use = Switch(on) && i < qualities.Length;
            if (use) {
                coefficients[first + i] = SvfEqCoefficients.Design(type, SampleRate, Value(frequency), qualities[i]);
            } else if (active[first + i]) {
                left[first + i].Reset();
                right[first + i].Reset();
            }

            active[first + i] = use;
        }
    }

    void Band(int index, SvfEqType type, int on, int frequency, int gain, int q) {
        var use = Switch(on) && Value(gain) != 0;
        if (use) {
            coefficients[index] = SvfEqCoefficients.Design(type, SampleRate, Value(frequency), Value(q), Value(gain));
        } else if (active[index]) {
            left[index].Reset();
            right[index].Reset();
        }

        active[index] = use;
    }

    protected override void Render(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        for (var s = 0; s < Sections; s++) {
            if (!active[s]) {
                continue;
            }

            ref readonly var c = ref coefficients[s];
            ref var l = ref this.left[s];
            ref var r = ref this.right[s];
            for (var i = 0; i < left.Length; i++) {
                left[i] = (float)l.Tick(c, left[i]);
                right[i] = (float)r.Tick(c, right[i]);
            }
        }

        var max = peak;
        for (var i = 0; i < left.Length; i++) {
            var g = output.Next();
            left[i] *= g;
            right[i] *= g;
            max = MathF.Max(max, MathF.Max(MathF.Abs(left[i]), MathF.Abs(right[i])));
        }

        peak = max;
        analyzer.Write(left, right);
    }

    protected override void Finish() {
        Publish(Decibels.FromGain(peak));
        peak = 0;
        for (var s = 0; s < Sections; s++) {
            left[s].FlushDenormals();
            right[s].FlushDenormals();
        }
    }
}
