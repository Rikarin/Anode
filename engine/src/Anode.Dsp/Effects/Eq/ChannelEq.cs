using Anode.Dsp.Dynamics;
using Anode.Dsp.Filters;
using Anode.Dsp.Metering;

namespace Anode.Dsp.Effects.Eq;

/// <summary>
///     <c>anode.channel-eq</c>, after Logic's Channel EQ: eight bands in series. A high-pass and a low-pass (Butterworth,
///     12, 24, 36 or 48 dB/octave), a low and a high shelf, and four bells, then the output gain. Every band is a
///     trapezoidal SVF (<see cref="SvfEqCoefficients" />), so the bands sweep without clicks; their frequency, gain and
///     quality are smoothed at control rate. A bell or shelf at 0 dB is skipped.
///     <para>
///         <b>Placement.</b> Each band works on both channels (Stereo), or on the mid or the side alone: the signal is
///         taken to mid and side around that band and back, so a band on the side leaves the mono sum as it was.
///     </para>
///     <para>
///         <b>Dynamic bands.</b> A bell or shelf can move with its own band's level. A detector hears the input through a
///         filter at the band (a band-pass at its frequency and Q; a shelf's low- or high-pass), in the band's placement.
///         Over Threshold the gain changes as a compressor's would at Ratio (a 6 dB knee), by at most Range: negative
///         cuts, positive boosts. That moves at Attack and Release on top of the band's own gain, and the band is
///         redesigned at control rate (the trapezoidal SVF takes it without clicks).
///     </para>
///     <para>
///         The <see cref="IAudioEffect.Reading" /> is the output's peak (dBFS) over the last buffer; the spectrum the
///         editor draws behind the curve comes from <see cref="ReadSpectrum" />, and is the display
///         (<see cref="IEffectDisplay" />): each of the <see cref="DynamicBands" /> gain bands' dynamic gain in dB, low
///         shelf first, then <see cref="SpectrumAnalyzer.DisplayBands" /> levels in dBFS.
///     </para>
///     <para>
///         Placement and dynamics are parameters after the original ones, so a state saved without them loads as it
///         played: parameter ids <c>{band}Place</c> on all eight bands, <c>{band}Dyn</c>, <c>{band}DynThreshold</c>,
///         <c>{band}DynRatio</c>, <c>{band}DynRange</c> on the six gain bands, and <c>dynAttack</c>, <c>dynRelease</c>.
///     </para>
/// </summary>
public sealed class ChannelEq : AudioEffect, IEffectDisplay {
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
    public const int Stereo = 0;
    public const int Mid = 1;
    public const int Side = 2;
    static readonly string[] Places = ["Stereo", "Mid", "Side"];
    public static readonly int HighPassPlace = P.Choice("hpfPlace", "Low Cut Placement", Places, Stereo);
    public static readonly int LowShelfPlace = P.Choice("lowShelfPlace", "Low Shelf Placement", Places, Stereo);
    public static readonly int Peak1Place = P.Choice("peak1Place", "Peak 1 Placement", Places, Stereo);
    public static readonly int Peak2Place = P.Choice("peak2Place", "Peak 2 Placement", Places, Stereo);
    public static readonly int Peak3Place = P.Choice("peak3Place", "Peak 3 Placement", Places, Stereo);
    public static readonly int Peak4Place = P.Choice("peak4Place", "Peak 4 Placement", Places, Stereo);
    public static readonly int HighShelfPlace = P.Choice("highShelfPlace", "High Shelf Placement", Places, Stereo);
    public static readonly int LowPassPlace = P.Choice("lpfPlace", "High Cut Placement", Places, Stereo);

    /// <summary>The gain bands' dynamics, low shelf first.</summary>
    public static readonly IReadOnlyList<DynamicParameters> Dynamics = [
        AddDynamics("lowShelf", "Low Shelf"), AddDynamics("peak1", "Peak 1"), AddDynamics("peak2", "Peak 2"),
        AddDynamics("peak3", "Peak 3"), AddDynamics("peak4", "Peak 4"), AddDynamics("highShelf", "High Shelf")
    ];

    public static readonly int DynamicAttack = P.Add("dynAttack", "Dynamic Attack", 0.5f, 200, 10, "ms");
    public static readonly int DynamicRelease = P.Add("dynRelease", "Dynamic Release", 5, 2_000, 150, "ms");

    public static EffectDescriptor Info { get; } = P.Describe(
        Id,
        "Channel EQ",
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

    /// <summary>The cut filters' sections (up to four each), then the shelves and bells.</summary>
    const int Sections = 4 + 4 + 6;

    /// <summary>The bands that can be dynamic: the bells and shelves.</summary>
    public const int DynamicBands = 6;

    /// <summary>Where the gain bands start in the sections.</summary>
    const int FirstGainBand = 8;

    /// <summary>A dynamic band's knee, as the Multipressor's, dB.</summary>
    const double DynamicKnee = 6;

    /// <summary>How fast a detector's peak falls: fast enough to follow the band, slow enough not to ripple.</summary>
    const double DetectorSeconds = 0.03;

    readonly SvfEqCoefficients[] coefficients = new SvfEqCoefficients[Sections];
    readonly bool[] active = new bool[Sections];
    readonly SvfEqState[] left = new SvfEqState[Sections];
    readonly SvfEqState[] right = new SvfEqState[Sections];
    readonly int[] placement = new int[Sections];
    readonly SpectrumAnalyzer analyzer = new();
    readonly SvfCoefficients[] detectors = new SvfCoefficients[DynamicBands];
    readonly SvfOutput[] detectorOutputs = new SvfOutput[DynamicBands];
    readonly SvfState[] detectLeft = new SvfState[DynamicBands];
    readonly SvfState[] detectRight = new SvfState[DynamicBands];
    readonly double[] envelopes = new double[DynamicBands];
    readonly double[] dynamicDb = new double[DynamicBands];
    readonly bool[] dynamic = new bool[DynamicBands];
    readonly float[] publishedDb = new float[DynamicBands];
    GainRamp output;
    double envelopeRelease;
    double dynamicAttack;
    double dynamicRelease;
    bool anyDynamic;
    float peak;

    public ChannelEq() : base(Info) { }

    /// <summary>What dynamics add to gain band <paramref name="band" /> now, dB (low shelf first).</summary>
    public float DynamicGain(int band) => Volatile.Read(ref publishedDb[band]);

    /// <summary>Control thread: the output spectrum, as <see cref="SpectrumAnalyzer.Read" /> gives it.</summary>
    public void ReadSpectrum(Span<float> bandsDb) => analyzer.Read(bandsDb, SampleRate);

    public int DisplaySize => DynamicBands + SpectrumAnalyzer.DisplayBands;

    public int ReadDisplay(Span<float> values) {
        if (!Prepared) {
            return 0;
        }

        for (var band = 0; band < DynamicBands; band++) {
            values[band] = DynamicGain(band);
        }

        ReadSpectrum(values.Slice(DynamicBands, SpectrumAnalyzer.DisplayBands));
        return DisplaySize;
    }

    static DynamicParameters AddDynamics(string id, string name) =>
        new(
            P.Toggle($"{id}Dyn", $"{name} Dynamic", false),
            P.Add($"{id}DynThreshold", $"{name} Threshold", -60, 0, -24, "dB"),
            P.Add($"{id}DynRatio", $"{name} Ratio", 1, 10, 2, ":1"),
            P.Add($"{id}DynRange", $"{name} Range", -24, 24, -6, "dB")
        );

    protected override void OnPrepare(float sampleRate) =>
        envelopeRelease = Math.Exp(-1 / (DetectorSeconds * sampleRate));

    protected override void OnReset() {
        for (var i = 0; i < Sections; i++) {
            left[i].Reset();
            right[i].Reset();
        }

        for (var band = 0; band < DynamicBands; band++) {
            detectLeft[band].Reset();
            detectRight[band].Reset();
            envelopes[band] = 0;
            dynamicDb[band] = 0;
            Volatile.Write(ref publishedDb[band], 0);
        }

        analyzer.Reset();
        peak = 0;
        Publish(FloorDb);
    }

    protected override void Update() {
        dynamicAttack = Math.Exp(-ControlInterval / (Value(DynamicAttack) * 0.001 * SampleRate));
        dynamicRelease = Math.Exp(-ControlInterval / (Value(DynamicRelease) * 0.001 * SampleRate));
        Cut(0, SvfEqType.HighPass, HighPassOn, HighPassFrequency, HighPassSlope, HighPassPlace);
        Cut(4, SvfEqType.LowPass, LowPassOn, LowPassFrequency, LowPassSlope, LowPassPlace);
        Band(8, SvfEqType.LowShelf, LowShelfOn, LowShelfFrequency, LowShelfGain, LowShelfQ, LowShelfPlace);
        Band(9, SvfEqType.Bell, Peak1On, Peak1Frequency, Peak1Gain, Peak1Q, Peak1Place);
        Band(10, SvfEqType.Bell, Peak2On, Peak2Frequency, Peak2Gain, Peak2Q, Peak2Place);
        Band(11, SvfEqType.Bell, Peak3On, Peak3Frequency, Peak3Gain, Peak3Q, Peak3Place);
        Band(12, SvfEqType.Bell, Peak4On, Peak4Frequency, Peak4Gain, Peak4Q, Peak4Place);
        Band(13, SvfEqType.HighShelf, HighShelfOn, HighShelfFrequency, HighShelfGain, HighShelfQ, HighShelfPlace);
        anyDynamic = Array.IndexOf(dynamic, true) >= 0;
        output.Set(Decibels.ToGain(Value(Output)), Snapping);
    }

    void Cut(int first, SvfEqType type, int on, int frequency, int slope, int place) {
        var qualities = Butterworth[Choice(slope)];
        for (var i = 0; i < 4; i++) {
            var use = Switch(on) && i < qualities.Length;
            Place(first + i, use, place);
            if (use) {
                coefficients[first + i] = SvfEqCoefficients.Design(type, SampleRate, Value(frequency), qualities[i]);
            } else if (active[first + i]) {
                left[first + i].Reset();
                right[first + i].Reset();
            }

            active[first + i] = use;
        }
    }

    void Band(int index, SvfEqType type, int on, int frequency, int gain, int q, int place) {
        var dynamicBand = index - FirstGainBand;
        var moves = Switch(on) && Switch(Dynamics[dynamicBand].On);
        var extra = Follow(dynamicBand, moves, type, Value(frequency), Value(q));
        var use = Switch(on) && (Value(gain) != 0 || moves);
        Place(index, use, place);
        if (use) {
            coefficients[index] = SvfEqCoefficients.Design(
                type,
                SampleRate,
                Value(frequency),
                Value(q),
                Value(gain) + extra
            );
        } else if (active[index]) {
            left[index].Reset();
            right[index].Reset();
        }

        active[index] = use;
    }

    /// <summary>A band moved between stereo, mid and side: its filter starts over on the signal it now hears.</summary>
    void Place(int index, bool use, int place) {
        var now = Choice(place);
        if (use && active[index] && placement[index] != now) {
            left[index].Reset();
            right[index].Reset();
        }

        placement[index] = now;
    }

    /// <summary>A control step of a dynamic band's gain, dB: its target from the detector, reached at attack and release.</summary>
    double Follow(int band, bool moves, SvfEqType type, double frequency, double q) {
        if (!moves) {
            if (dynamic[band]) {
                detectLeft[band].Reset();
                detectRight[band].Reset();
                envelopes[band] = 0;
            }

            dynamic[band] = false;
            dynamicDb[band] = 0;
            return 0;
        }

        dynamic[band] = true;
        detectors[band] =
            SvfCoefficients.Design(SampleRate, frequency, type == SvfEqType.Bell ? q : 0.70710678118654752);
        detectorOutputs[band] = type switch {
            SvfEqType.LowShelf => SvfOutput.LowPass,
            SvfEqType.HighShelf => SvfOutput.HighPass,
            _ => SvfOutput.BandPass
        };
        var settings = Dynamics[band];
        var level = envelopes[band] > 1e-6 ? 20 * Math.Log10(envelopes[band]) : -120;
        var amount = -GainComputer.Gain(level, Value(settings.Threshold), Value(settings.Ratio), DynamicKnee);
        double range = Value(settings.Range);
        var target = Math.Sign(range) * Math.Min(Math.Abs(range), amount);
        var coefficient = Math.Abs(target) > Math.Abs(dynamicDb[band]) ? dynamicAttack : dynamicRelease;
        dynamicDb[band] = target + (dynamicDb[band] - target) * coefficient;
        return dynamicDb[band];
    }

    /// <summary>The detectors hear the input, each in its band's placement.</summary>
    void Detect(ReadOnlySpan<float> left, ReadOnlySpan<float> right) {
        for (var band = 0; band < DynamicBands; band++) {
            if (!dynamic[band]) {
                continue;
            }

            ref readonly var c = ref detectors[band];
            var kind = detectorOutputs[band];
            var place = placement[FirstGainBand + band];
            ref var l = ref detectLeft[band];
            ref var r = ref detectRight[band];
            var envelope = envelopes[band];
            for (var i = 0; i < left.Length; i++) {
                double level;
                if (place == Stereo) {
                    var a = Math.Abs(l.Tick(c, left[i], kind));
                    var b = Math.Abs(r.Tick(c, right[i], kind));
                    level = Math.Max(a, b);
                } else {
                    var signal = 0.5 * (place == Mid ? left[i] + (double)right[i] : left[i] - (double)right[i]);
                    level = Math.Abs(l.Tick(c, signal, kind));
                }

                envelope = level > envelope ? level : level + (envelope - level) * envelopeRelease;
            }

            envelopes[band] = envelope;
        }
    }

    protected override void Render(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        if (anyDynamic) {
            Detect(left, right);
        }

        for (var s = 0; s < Sections; s++) {
            if (!active[s]) {
                continue;
            }

            ref readonly var c = ref coefficients[s];
            ref var l = ref this.left[s];
            ref var r = ref this.right[s];
            var place = placement[s];
            if (place == Stereo) {
                for (var i = 0; i < left.Length; i++) {
                    left[i] = (float)l.Tick(c, left[i]);
                    right[i] = (float)r.Tick(c, right[i]);
                }
            } else {
                for (var i = 0; i < left.Length; i++) {
                    var mid = 0.5 * (left[i] + (double)right[i]);
                    var side = 0.5 * (left[i] - (double)right[i]);
                    if (place == Mid) {
                        mid = l.Tick(c, mid);
                    } else {
                        side = l.Tick(c, side);
                    }

                    left[i] = (float)(mid + side);
                    right[i] = (float)(mid - side);
                }
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

        for (var band = 0; band < DynamicBands; band++) {
            Volatile.Write(ref publishedDb[band], (float)dynamicDb[band]);
            detectLeft[band].FlushDenormals();
            detectRight[band].FlushDenormals();
            if (envelopes[band] < 1e-30) {
                envelopes[band] = 0;
            }
        }
    }

    /// <summary>The parameter indices of one gain band's dynamics.</summary>
    public sealed record DynamicParameters(int On, int Threshold, int Ratio, int Range);
}
