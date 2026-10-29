using Anode.Dsp.Filters;
using Anode.Dsp.Metering;
using Anode.Dsp.Spectral;

namespace Anode.Dsp.Effects.Eq;

/// <summary>
///     <c>anode.linear-eq</c>, after Logic's Linear Phase EQ: the Channel EQ's eight bands (same parameter ids, so a
///     setting moves between the two) as one linear-phase FIR.
///     <list type="bullet">
///         <item>
///             The magnitude is the product of the bands' RBJ responses (<see cref="BiquadCoefficients" />; the cuts as
///             Butterworth cascades) sampled on <see cref="DesignSize" />/2 + 1 bins, with the output gain.
///         </item>
///         <item>
///             As a zero-phase spectrum, its inverse FFT is symmetric; shifted by half the size and windowed (Blackman)
///             it is a causal FIR symmetric about tap <see cref="DesignSize" />/2, so every frequency is delayed alike:
///             the latency, <see cref="LatencySamples" />, is that half size (2,048 samples at 48 kHz).
///         </item>
///         <item>
///             The FIR runs through a <see cref="PartitionedConvolver" /> per channel (128-sample partitions).
///             While a parameter moves, the FIR is redesigned at most every four control intervals (128 samples)
///             and swapped in.
///         </item>
///     </list>
/// </summary>
public sealed class LinearPhaseEq : AudioEffect {
    public const string Id = "anode.linear-eq";

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

    public static EffectDescriptor Info { get; } = P.Describe(
        Id,
        "Linear Phase EQ",
        EffectCategories.Eq,
        LowShelfGain,
        Peak1Gain,
        Peak2Gain,
        Peak3Gain,
        Peak4Gain,
        HighShelfGain,
        HighPassFrequency,
        LowPassFrequency
    );

    const int Partition = 128;
    const int RedesignIntervals = 4;

    readonly PartitionedConvolver convolverLeft = new();
    readonly PartitionedConvolver convolverRight = new();
    readonly SpectrumAnalyzer analyzer = new();
    readonly float[] designed = new float[Info.Parameters.Count];
    RealFft fft = new(4);
    double[] cos1 = [];
    double[] cos2 = [];
    double[] power = [];
    float[] real = [];
    float[] imaginary = [];
    float[] shape = [];
    float[] window = [];
    float[] taps = [];
    int sinceDesign;
    bool dirty;

    public LinearPhaseEq() : base(Info) { }

    /// <summary>The design FFT's size: 4,096 up to 48 kHz, doubled per doubling of the rate.</summary>
    public int DesignSize { get; private set; }

    public override int LatencySamples => Prepared ? DesignSize / 2 : 0;

    /// <summary>The FIR in use (<see cref="DesignSize" /> taps, symmetric about the middle; tap 0 is zero).</summary>
    public ReadOnlySpan<float> Taps => taps;

    /// <summary>Control thread: the output spectrum, as <see cref="SpectrumAnalyzer.Read" /> gives it.</summary>
    public void ReadSpectrum(Span<float> bandsDb) => analyzer.Read(bandsDb, SampleRate);

    protected override void OnPrepare(float sampleRate) {
        var size = 4_096;
        while (size * 48_000.0 < sampleRate * 4_096 * 0.75) {
            size <<= 1;
        }

        DesignSize = size;
        fft = new(size);
        var bins = size / 2 + 1;
        cos1 = new double[bins];
        cos2 = new double[bins];
        power = new double[bins];
        real = new float[bins];
        imaginary = new float[bins];
        shape = new float[size];
        window = new float[size];
        taps = new float[size];
        for (var k = 0; k < bins; k++) {
            var w = 2 * Math.PI * k / size;
            cos1[k] = Math.Cos(w);
            cos2[k] = Math.Cos(2 * w);
        }

        for (var n = 0; n < size; n++) {
            var x = 2 * Math.PI * n / size;
            window[n] = (float)(0.42 - 0.5 * Math.Cos(x) + 0.08 * Math.Cos(2 * x));
        }

        convolverLeft.Allocate(Partition, size);
        convolverRight.Allocate(Partition, size);
    }

    protected override void OnReset() {
        convolverLeft.Reset();
        convolverRight.Reset();
        analyzer.Reset();
        sinceDesign = 0;
        dirty = true;
    }

    protected override void Update() {
        for (var i = 0; i < designed.Length; i++) {
            if (Value(i) != designed[i]) {
                dirty = true;
                break;
            }
        }

        sinceDesign++;
        if (dirty && (Snapping || sinceDesign >= RedesignIntervals)) {
            Design();
        }
    }

    protected override void Render(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        for (var i = 0; i < left.Length; i++) {
            left[i] = convolverLeft.Tick(left[i]);
            right[i] = convolverRight.Tick(right[i]);
        }

        analyzer.Write(left, right);
    }

    /// <summary>The FIR for the current values, into both convolvers.</summary>
    void Design() {
        for (var i = 0; i < designed.Length; i++) {
            designed[i] = Value(i);
        }

        dirty = false;
        sinceDesign = 0;
        var gain = Decibels.ToGain((double)Value(Output));
        power.AsSpan().Fill(gain * gain);
        if (Switch(HighPassOn)) {
            foreach (var q in Butterworth[Choice(HighPassSlope)]) {
                Apply(BiquadCoefficients.Design(BiquadType.HighPass, SampleRate, Value(HighPassFrequency), q));
            }
        }

        if (Switch(LowPassOn)) {
            foreach (var q in Butterworth[Choice(LowPassSlope)]) {
                Apply(BiquadCoefficients.Design(BiquadType.LowPass, SampleRate, Value(LowPassFrequency), q));
            }
        }

        Band(LowShelfOn, BiquadType.LowShelf, LowShelfFrequency, LowShelfQ, LowShelfGain);
        Band(Peak1On, BiquadType.Peak, Peak1Frequency, Peak1Q, Peak1Gain);
        Band(Peak2On, BiquadType.Peak, Peak2Frequency, Peak2Q, Peak2Gain);
        Band(Peak3On, BiquadType.Peak, Peak3Frequency, Peak3Q, Peak3Gain);
        Band(Peak4On, BiquadType.Peak, Peak4Frequency, Peak4Q, Peak4Gain);
        Band(HighShelfOn, BiquadType.HighShelf, HighShelfFrequency, HighShelfQ, HighShelfGain);
        for (var k = 0; k < power.Length; k++) {
            real[k] = (float)Math.Sqrt(power[k]);
            imaginary[k] = 0;
        }

        // Zero phase → a response symmetric about 0; centred on half the size and windowed.
        fft.Inverse(real, imaginary, shape);
        var size = DesignSize;
        var half = size / 2;
        for (var n = 0; n < size; n++) {
            taps[n] = shape[(n - half + size) % size] * window[n];
        }

        taps[0] = 0;
        // The inverse transform's rounding leaves the two halves a few ulps apart; mirror them exactly.
        for (var j = 1; j < half; j++) {
            var mean = 0.5f * (taps[half + j] + taps[half - j]);
            taps[half + j] = mean;
            taps[half - j] = mean;
        }

        convolverLeft.SetFilter(taps);
        convolverRight.SetFilter(taps);
    }

    void Band(int on, BiquadType type, int frequency, int q, int gain) {
        if (Switch(on) && Value(gain) != 0) {
            Apply(BiquadCoefficients.Design(type, SampleRate, Value(frequency), Value(q), Value(gain)));
        }
    }

    /// <summary>Multiplies the power response by a biquad's |H|², a ratio of cosine polynomials.</summary>
    void Apply(in BiquadCoefficients c) {
        var n0 = c.B0 * c.B0 + c.B1 * c.B1 + c.B2 * c.B2;
        var n1 = 2 * (c.B0 * c.B1 + c.B1 * c.B2);
        var n2 = 2 * c.B0 * c.B2;
        var d0 = 1 + c.A1 * c.A1 + c.A2 * c.A2;
        var d1 = 2 * (c.A1 + c.A1 * c.A2);
        var d2 = 2 * c.A2;
        for (var k = 0; k < power.Length; k++) {
            var num = n0 + n1 * cos1[k] + n2 * cos2[k];
            var den = d0 + d1 * cos1[k] + d2 * cos2[k];
            power[k] *= Math.Max(num, 0) / Math.Max(den, 1e-30);
        }
    }
}
