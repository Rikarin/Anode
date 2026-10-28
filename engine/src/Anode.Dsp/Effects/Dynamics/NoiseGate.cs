using Anode.Dsp.Filters;

namespace Anode.Dsp.Effects.Dynamics;

/// <summary>
///     <c>anode.noise-gate</c>: a stereo-linked gate after Logic's Noise Gate. The detector follows the peak level of
///     the input, or of the sidechain when Sidechain is External and the host provides one, through a high and a low
///     cut (off at their extremes). The gate opens when the level rises above the threshold, stays open for the hold
///     time after it falls back below the threshold plus the hysteresis (≤ 0 dB), then closes to the reduction. The
///     gain moves toward open with the attack and toward closed with the release (time constants). The
///     <see cref="IAudioEffect.Reading" /> is the gate's gain (dB, ≤ 0) at the end of the last buffer.
/// </summary>
public sealed class NoiseGate : AudioEffect {
    public const string Id = "anode.noise-gate";

    static readonly EffectParameters P = new();
    public static readonly int Threshold = P.Add("threshold", "Threshold", -100, 0, -50, "dB");
    public static readonly int Hysteresis = P.Add("hysteresis", "Hysteresis", -20, 0, -3, "dB");
    public static readonly int Reduction = P.Add("reduction", "Reduction", -100, 0, -100, "dB");
    public static readonly int Attack = P.Add("attack", "Attack", 0.01f, 200, 0.5f, "ms");
    public static readonly int Hold = P.Add("hold", "Hold", 0, 2_000, 20, "ms");
    public static readonly int Release = P.Add("release", "Release", 1, 4_000, 100, "ms");
    public static readonly int Sidechain = P.Choice("sidechain", "Sidechain", ["Internal", "External"], 0);
    public static readonly int HighCut = P.Add("highCut", "Sidechain High Cut", 200, 20_000, 20_000, "Hz");
    public static readonly int LowCut = P.Add("lowCut", "Sidechain Low Cut", 20, 4_000, 20, "Hz");

    public static EffectDescriptor Info { get; } =
        P.Describe(Id, "Noise Gate", EffectCategories.Dynamics, true);

    /// <summary>How fast the detector's peak follower falls.</summary>
    const double DetectorSeconds = 0.005;

    BiquadCoefficients lowCut;
    BiquadCoefficients highCut;
    BiquadState lowLeft;
    BiquadState lowRight;
    BiquadState highLeft;
    BiquadState highRight;
    bool lowCutOn;
    bool highCutOn;
    double detectorFall;
    double envelope;
    double gain;
    double floor;
    double attack;
    double release;
    double open;
    double close;
    int holdSamples;
    int holding;
    bool isOpen;
    bool external;

    public NoiseGate() : base(Info) { }

    protected override void OnPrepare(float sampleRate) => detectorFall = Math.Exp(-1 / (DetectorSeconds * sampleRate));

    protected override void OnReset() {
        lowLeft.Reset();
        lowRight.Reset();
        highLeft.Reset();
        highRight.Reset();
        envelope = 0;
        isOpen = false;
        holding = 0;
        gain = -1; // taken as the closed gain at the first update
        Publish(0);
    }

    protected override void Update() {
        open = Decibels.ToGain((double)Value(Threshold));
        close = Decibels.ToGain((double)(Value(Threshold) + Value(Hysteresis)));
        floor = Value(Reduction) <= -100 ? 0 : Decibels.ToGain((double)Value(Reduction));
        if (gain < 0) {
            gain = floor;
        }

        attack = Math.Exp(-1 / (Value(Attack) * 0.001 * SampleRate));
        release = Math.Exp(-1 / (Value(Release) * 0.001 * SampleRate));
        holdSamples = (int)(Value(Hold) * 0.001 * SampleRate);
        external = Choice(Sidechain) == 1;
        lowCutOn = Value(LowCut) > 20;
        highCutOn = Value(HighCut) < 20_000;
        lowCut = BiquadCoefficients.Design(BiquadType.HighPass, SampleRate, Value(LowCut), 0.7071);
        highCut = BiquadCoefficients.Design(BiquadType.LowPass, SampleRate, Value(HighCut), 0.7071);
    }

    protected override void Render(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        var side = external && !sidechainLeft.IsEmpty;
        for (var i = 0; i < left.Length; i++) {
            double dl = side ? sidechainLeft[i] : left[i];
            double dr = side ? sidechainRight[i] : right[i];
            if (lowCutOn) {
                dl = lowLeft.Tick(lowCut, dl);
                dr = lowRight.Tick(lowCut, dr);
            }

            if (highCutOn) {
                dl = highLeft.Tick(highCut, dl);
                dr = highRight.Tick(highCut, dr);
            }

            var peak = Math.Max(Math.Abs(dl), Math.Abs(dr));
            envelope = peak > envelope ? peak : envelope * detectorFall;
            if (envelope >= open) {
                isOpen = true;
                holding = holdSamples;
            } else if (envelope < close) {
                if (holding > 0) {
                    holding--;
                } else {
                    isOpen = false;
                }
            }

            var target = isOpen ? 1 : floor;
            gain = target > gain ? target + (gain - target) * attack : target + (gain - target) * release;
            left[i] *= (float)gain;
            right[i] *= (float)gain;
        }
    }

    protected override void Finish() {
        Publish(Decibels.FromGain(gain));
        lowLeft.FlushDenormals();
        lowRight.FlushDenormals();
        highLeft.FlushDenormals();
        highRight.FlushDenormals();
        if (envelope < 1e-30) {
            envelope = 0;
        }
    }
}

/// <summary>
///     <c>anode.deesser</c>: a de-esser after Logic's DeEsser 2. The detector hears the mid signal through a 12 dB/octave
///     high-pass at the frequency, follows its peak (0.5 ms attack, the release), and reduces by as much as the level
///     passes the threshold, at most the range. Split mode turns down only the band above the frequency (a high shelf
///     there, its gain the reduction, recomputed per sample; at 0 dB it is the input exactly); Wide mode turns down the
///     whole signal. Listen outputs the band the detector hears. The <see cref="IAudioEffect.Reading" /> is the deepest
///     reduction (dB, ≤ 0) over the last buffer.
/// </summary>
public sealed class DeEsser : AudioEffect {
    public const string Id = "anode.deesser";

    static readonly EffectParameters P = new();
    public static readonly int Frequency = P.Add("frequency", "Frequency", 2_000, 16_000, 6_000, "Hz");
    public static readonly int Threshold = P.Add("threshold", "Threshold", -60, 0, -30, "dB");
    public static readonly int Range = P.Add("range", "Range", 0, 24, 9, "dB");
    public static readonly int Release = P.Add("release", "Release", 5, 500, 60, "ms");
    public static readonly int Mode = P.Choice("mode", "Mode", ["Split", "Wide"], 0);
    public static readonly int Listen = P.Toggle("listen", "Listen to Detector", false);

    public static EffectDescriptor Info { get; } = P.Describe(Id, "DeEsser", EffectCategories.Dynamics);

    const double AttackSeconds = 0.0005;

    SvfEqCoefficients band;
    SvfEqState detector;
    double warp;
    SvfEqState left;
    SvfEqState right;
    double envelope;
    double attack;
    double release;
    double threshold;
    double range;
    double deepest;
    bool split;
    bool listen;

    public DeEsser() : base(Info) { }

    protected override void OnPrepare(float sampleRate) => attack = Math.Exp(-1 / (AttackSeconds * sampleRate));

    protected override void OnReset() {
        detector.Reset();
        left.Reset();
        right.Reset();
        envelope = 0;
        deepest = 0;
        Publish(0);
    }

    protected override void Update() {
        band = SvfEqCoefficients.Design(SvfEqType.HighPass, SampleRate, Value(Frequency), 0.7071);
        warp = Math.Tan(Math.PI * Math.Min(Value(Frequency), SampleRate * 0.49) / SampleRate);
        release = Math.Exp(-1 / (Value(Release) * 0.001 * SampleRate));
        threshold = Value(Threshold);
        range = Value(Range);
        split = Choice(Mode) == 0;
        listen = Switch(Listen);
    }

    protected override void Render(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        for (var i = 0; i < left.Length; i++) {
            var high = Math.Abs(detector.Tick(band, 0.5 * (left[i] + right[i])));
            envelope = high > envelope ? high + (envelope - high) * attack : high + (envelope - high) * release;
            var levelDb = envelope > 1e-6 ? 20 * Math.Log10(envelope) : -120;
            var reduction = -Math.Clamp(levelDb - threshold, 0, range);
            deepest = Math.Min(deepest, reduction);
            var gain = reduction < 0 ? Math.Pow(10, reduction * 0.05) : 1;
            if (listen) {
                left[i] = (float)this.left.Tick(band, left[i]);
                right[i] = (float)this.right.Tick(band, right[i]);
            } else if (split) {
                var shelf = Shelf(gain);
                left[i] = (float)this.left.Tick(shelf, left[i]);
                right[i] = (float)this.right.Tick(shelf, right[i]);
            } else {
                left[i] *= (float)gain;
                right[i] *= (float)gain;
            }
        }
    }

    /// <summary>The high shelf at the frequency for a gain (≤ 1): <see cref="SvfEqCoefficients" />, inlined per sample.</summary>
    SvfEqCoefficients Shelf(double gain) {
        const double k = 1 / 0.7071;
        var a = Math.Sqrt(gain); // the shelf's A = 10^(dB/40)
        var g = warp * Math.Sqrt(a);
        var a1 = 1 / (1 + g * (g + k));
        var a2 = g * a1;
        return new(a1, a2, g * a2, a * a, k * (1 - a) * a, 1 - a * a);
    }

    protected override void Finish() {
        Publish((float)deepest);
        deepest = 0;
        detector.FlushDenormals();
        left.FlushDenormals();
        right.FlushDenormals();
        if (envelope < 1e-30) {
            envelope = 0;
        }
    }
}
