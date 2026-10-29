using Anode.Dsp.Dynamics;
using Anode.Dsp.Filters;

namespace Anode.Dsp.Effects.Dynamics;

/// <summary>
///     <c>anode.multipressor</c>, after Logic's Multipressor: four bands, each compressed above its threshold and
///     expanded below its expander threshold, then summed.
///     <list type="bullet">
///         <item>
///             Three Linkwitz–Riley crossovers (24 dB/octave, two Butterworth SVF sections each) split the signal; each
///             lower band passes the higher crossovers' all-passes (the second-order all-pass an LR4 pair sums to), so
///             with every band at 0 dB the bands sum to a flat all-pass.
///         </item>
///         <item>
///             Per band, a stereo-linked peak detector (instant attack, 30 ms release) feeds the static curve: compression above the threshold
///             (<see cref="GainComputer" />, a 6 dB knee), and downward expansion below the expander threshold by its
///             ratio, limited to the band's reduction range. The gain moves at the band's attack and release, then the
///             band's make-up gain is applied. A band that is off passes at 0 dB.
///         </item>
///         <item>
///             Mix blends the dry signal back (at 0 % it is the input, untouched); Output trims the sum.
///         </item>
///     </list>
///     The <see cref="IAudioEffect.Reading" /> is the deepest gain change of any band over the last buffer (dB, ≤ 0);
///     <see cref="BandGain" /> gives each band's.
///     <para>
///         Parameter ids: <c>cross1</c> … <c>cross3</c>, then per band n = 1 … 4: <c>band{n}On</c>,
///         <c>band{n}Threshold</c>, <c>band{n}Ratio</c>, <c>band{n}ExpThreshold</c>, <c>band{n}ExpRatio</c>,
///         <c>band{n}Reduction</c>, <c>band{n}Attack</c>, <c>band{n}Release</c>, <c>band{n}Gain</c>; then
///         <c>output</c> and <c>mix</c>.
///     </para>
/// </summary>
public sealed class Multipressor : AudioEffect {
    public const string Id = "anode.multipressor";
    public const int BandCount = 4;
    const double Knee = 6;

    static readonly EffectParameters P = new();
    public static readonly int Cross1 = P.Add("cross1", "Crossover 1", 20, 20_000, 150, "Hz");
    public static readonly int Cross2 = P.Add("cross2", "Crossover 2", 20, 20_000, 1_000, "Hz");
    public static readonly int Cross3 = P.Add("cross3", "Crossover 3", 20, 20_000, 5_000, "Hz");

    /// <summary>Each band's parameter indices, band 1 (lowest) first.</summary>
    public static readonly IReadOnlyList<BandParameters> Bands = [..Enumerable.Range(1, BandCount).Select(AddBand)];

    public static readonly int Output = P.Add("output", "Output", -24, 24, 0, "dB");
    public static readonly int Mix = P.Add("mix", "Mix", 0, 100, 100, "%");

    public static EffectDescriptor Info { get; } = P.Describe(
        Id,
        "Multipressor",
        EffectCategories.Dynamics,
        Bands[0].Threshold,
        Bands[1].Threshold,
        Bands[2].Threshold,
        Bands[3].Threshold,
        Bands[0].Gain,
        Bands[1].Gain,
        Bands[2].Gain,
        Bands[3].Gain
    );

    readonly SvfCoefficients[] cross = new SvfCoefficients[3];
    readonly SvfEqCoefficients[] allPass = new SvfEqCoefficients[3];

    // Per channel: the crossover sections [crossover, low/high, section] and the all-passes [band, crossover].
    readonly SvfState[] splitLeft = new SvfState[3 * 2 * 2];
    readonly SvfState[] splitRight = new SvfState[3 * 2 * 2];
    readonly SvfEqState[] passLeft = new SvfEqState[BandCount * 3];
    readonly SvfEqState[] passRight = new SvfEqState[BandCount * 3];
    readonly double[] gainDb = new double[BandCount];
    readonly double[] detector = new double[BandCount];
    readonly double[] deepest = new double[BandCount];
    readonly double[] threshold = new double[BandCount];
    readonly double[] ratio = new double[BandCount];
    readonly double[] expThreshold = new double[BandCount];
    readonly double[] expRatio = new double[BandCount];
    readonly double[] range = new double[BandCount];
    readonly double[] attack = new double[BandCount];
    readonly double[] release = new double[BandCount];
    readonly double[] makeup = new double[BandCount];
    readonly bool[] on = new bool[BandCount];
    readonly float[] published = new float[BandCount];
    GainRamp output;
    GainRamp mix;
    double detectorRelease;

    public Multipressor() : base(Info) { }

    /// <summary>Band <paramref name="band" />'s (0 … 3) gain change over the last buffer, dB (≤ 0 when reducing).</summary>
    public float BandGain(int band) => Volatile.Read(ref published[band]);

    static BandParameters AddBand(int n) =>
        new(
            P.Toggle($"band{n}On", $"Band {n}", true),
            P.Add($"band{n}Threshold", $"Band {n} Threshold", -60, 0, -20, "dB"),
            P.Add($"band{n}Ratio", $"Band {n} Ratio", 1, 20, 2, ":1"),
            P.Add($"band{n}ExpThreshold", $"Band {n} Expander Threshold", -90, -10, -60, "dB"),
            P.Add($"band{n}ExpRatio", $"Band {n} Expander Ratio", 1, 10, 1, ":1"),
            P.Add($"band{n}Reduction", $"Band {n} Reduction", -60, 0, -20, "dB"),
            P.Add($"band{n}Attack", $"Band {n} Attack", 0.1f, 200, 20, "ms"),
            P.Add($"band{n}Release", $"Band {n} Release", 5, 2_000, 200, "ms"),
            P.Add($"band{n}Gain", $"Band {n} Gain", -24, 24, 0, "dB")
        );

    protected override void OnPrepare(float sampleRate) => detectorRelease = Math.Exp(-1 / (0.03 * sampleRate));

    protected override void OnReset() {
        Array.Clear(splitLeft);
        Array.Clear(splitRight);
        Array.Clear(passLeft);
        Array.Clear(passRight);
        Array.Clear(gainDb);
        Array.Clear(detector);
        Array.Clear(deepest);
        Publish(0);
    }

    protected override void Update() {
        // The crossovers in order, at least a third of an octave apart.
        var f1 = Math.Min(Value(Cross1), SampleRate * 0.45 / 1.26 / 1.26);
        var f2 = Math.Max(Value(Cross2), f1 * 1.26);
        var f3 = Math.Max(Value(Cross3), f2 * 1.26);
        f2 = Math.Min(f2, SampleRate * 0.45 / 1.26);
        f3 = Math.Min(f3, SampleRate * 0.45);
        Span<double> frequencies = [f1, f2, f3];
        for (var c = 0; c < 3; c++) {
            cross[c] = SvfCoefficients.Design(SampleRate, frequencies[c], 0.70710678118654752);
            allPass[c] = SvfEqCoefficients.Design(SvfEqType.AllPass, SampleRate, frequencies[c], 0.70710678118654752);
        }

        for (var b = 0; b < BandCount; b++) {
            var band = Bands[b];
            on[b] = Switch(band.On);
            threshold[b] = Value(band.Threshold);
            ratio[b] = Value(band.Ratio);
            expThreshold[b] = Math.Min(Value(band.ExpThreshold), threshold[b]);
            expRatio[b] = Value(band.ExpRatio);
            range[b] = Value(band.Reduction);
            attack[b] = Math.Exp(-1 / (Value(band.Attack) * 0.001 * SampleRate));
            release[b] = Math.Exp(-1 / (Value(band.Release) * 0.001 * SampleRate));
            makeup[b] = Value(band.Gain);
        }

        output.Set(Decibels.ToGain(Value(Output)), Snapping);
        mix.Set(Value(Mix) * 0.01f, Snapping);
    }

    protected override void Render(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        Span<double> bl = stackalloc double[BandCount];
        Span<double> br = stackalloc double[BandCount];
        for (var i = 0; i < left.Length; i++) {
            Split(left[i], splitLeft, passLeft, bl);
            Split(right[i], splitRight, passRight, br);
            double l = 0, r = 0;
            for (var b = 0; b < BandCount; b++) {
                var gain = 1.0;
                if (on[b]) {
                    var peak = Math.Max(Math.Abs(bl[b]), Math.Abs(br[b]));
                    detector[b] = peak > detector[b] ? peak : peak + (detector[b] - peak) * detectorRelease;
                    var level = detector[b] > 1e-6 ? 20 * Math.Log10(detector[b]) : -120;
                    var target = GainComputer.Gain(level, threshold[b], ratio[b], Knee);
                    if (level < expThreshold[b] && expRatio[b] > 1) {
                        target += Math.Max(range[b], (expThreshold[b] - level) * (1 - expRatio[b]));
                    }

                    var coefficient = target < gainDb[b] ? attack[b] : release[b];
                    gainDb[b] = coefficient * gainDb[b] + (1 - coefficient) * target;
                    deepest[b] = Math.Min(deepest[b], gainDb[b]);
                    gain = Math.Pow(10, (gainDb[b] + makeup[b]) * 0.05);
                }

                l += bl[b] * gain;
                r += br[b] * gain;
            }

            var o = output.Next();
            var wet = mix.Next();
            left[i] += ((float)l * o - left[i]) * wet;
            right[i] += ((float)r * o - right[i]) * wet;
        }
    }

    protected override void Finish() {
        var overall = 0.0;
        for (var b = 0; b < BandCount; b++) {
            Volatile.Write(ref published[b], (float)deepest[b]);
            overall = Math.Min(overall, deepest[b]);
            deepest[b] = on[b] ? gainDb[b] : 0;
            if (Math.Abs(gainDb[b]) < 1e-12) {
                gainDb[b] = 0;
            }

            if (detector[b] < 1e-30) {
                detector[b] = 0;
            }
        }

        Publish((float)overall);
        for (var i = 0; i < splitLeft.Length; i++) {
            splitLeft[i].FlushDenormals();
            splitRight[i].FlushDenormals();
        }

        for (var i = 0; i < passLeft.Length; i++) {
            passLeft[i].FlushDenormals();
            passRight[i].FlushDenormals();
        }
    }

    /// <summary>One sample into the four bands, phase-aligned.</summary>
    void Split(double x, SvfState[] split, SvfEqState[] pass, Span<double> bands) {
        var rest = x;
        for (var c = 0; c < 3; c++) {
            var at = c * 4;
            var low = split[at].Tick(cross[c], rest, SvfOutput.LowPass);
            low = split[at + 1].Tick(cross[c], low, SvfOutput.LowPass);
            var high = split[at + 2].Tick(cross[c], rest, SvfOutput.HighPass);
            high = split[at + 3].Tick(cross[c], high, SvfOutput.HighPass);
            // The band below crossover c passes the higher crossovers' all-passes.
            for (var later = c + 1; later < 3; later++) {
                low = pass[c * 3 + later].Tick(allPass[later], low);
            }

            bands[c] = low;
            rest = high;
        }

        bands[3] = rest;
    }

    /// <summary>The parameter indices of one band.</summary>
    public sealed record BandParameters(
        int On,
        int Threshold,
        int Ratio,
        int ExpThreshold,
        int ExpRatio,
        int Reduction,
        int Attack,
        int Release,
        int Gain
    );
}
