using Anode.Dsp.Delays;
using Anode.Dsp.Dynamics;
using Anode.Dsp.Effects;
using Anode.Dsp.Filters;
using Anode.Dsp.Metering;
using Anode.Dsp.Resampling;

namespace Anode.Dsp.Mastering;

/// <summary>
///     <c>anode.mastering-assistant</c>, the Mastering Assistant (docs/12-phase-11.md, workstream E): EQ to the
///     character's curve, gentle compression, the character's colour, stereo width and a true-peak limiter whose input
///     gain reaches the loudness target; a level-matched bypass. Its parameters are the user's settings (the contract's
///     <c>MasteringParameters</c> ids, automatable); its chain is what the analysis set (<see cref="SetChain" />).
/// </summary>
/// <remarks>
///     <para>
///         <b>The chain</b>, per sample: the input normalised to <see cref="WorkingLufs" /> (from the analysed
///         loudness), so the EQ, the compressor and the colour work at one level whatever the mix's; the EQ (30 bells of
///         a third of an octave at the bands' centres, their gains solved through the bells' overlap so the response at
///         each centre is the designer's gain times the <c>eq</c> strength); the compressor (stereo-linked, a 10 ms RMS
///         detector, 6 dB knee, attack 10 ms or 30 ms for Punch, release 150 ms, the ratio moved toward 1 by the
///         <c>dynamics</c> strength); the colour (Valve: a biased tube curve at 4× the rate, even harmonics; Punch: up to
///         +3.6 dB on transients, a fast envelope over a slow one; Clean and Transparent: none); the width (the side
///         times <c>width</c>); a band limit (a 4th-order Butterworth low-pass at 19.5 kHz, at most 0.42 of the rate: the
///         limiter's true-peak detector, as a BS.1770 meter's interpolator, is specified to 20 kHz); then the limiter's
///         input gain and a true-peak limiter (adaptive, 2 ms lookahead, 80 ms release) under the ceiling less a 0.3 dB
///         margin.
///     </para>
///     <para>
///         <b>The target.</b> The total gain before the limiter is <c>targetLufs − loudnessLufs − chainGain</c>, as the
///         IPC's chain says. The chain gain starts at the designer's estimate (at the <c>eq</c> strength) and is then
///         measured as it plays: the BS.1770 integrated loudness of the limiter's output at unit input gain over that of
///         the normalised input, since the last reset (both gated as BS.1770 gates), blended with the estimate as if it
///         had half a second of weight, and the gain follows it with a 0.25 s time constant. So the limiter's own loss,
///         the width
///         and anything the estimate missed are made up within the first seconds and the integrated loudness of a bounce
///         lands on the target; the measure is a ratio of loudnesses, not a level, so a quiet passage is not lifted.
///     </para>
///     <para>
///         Without a chain (not analysed yet) it is the limiter alone at unit gain (no band limit either). The
///         level-matched bypass plays the
///         input (delayed by the latency) at <c>targetLufs − loudnessLufs</c>, crossfaded over a control interval. The
///         output is metered: momentary, short-term and integrated loudness, the true peak since the reset, and the
///         compressor's and limiter's deepest reduction over the last buffer (the <c>mastering</c> telemetry); the
///         <see cref="IAudioEffect.Reading" /> is the limiter's.
///     </para>
/// </remarks>
public sealed class MasteringAssistant : AudioEffect, IMasteringChain {
    public const string Id = "anode.mastering-assistant";
    static readonly EffectParameters P = new();

    public static readonly int Character = P.Choice(
        "character",
        "Character",
        ["Clean", "Valve", "Punch", "Transparent"],
        0
    );

    public static readonly int TargetLufs = P.Add("targetLufs", "Loudness", -24, -6, -14, "LUFS");
    public static readonly int CeilingDbtp = P.Add("ceilingDbtp", "Ceiling", -3, 0, -1, "dBTP");
    public static readonly int Eq = P.Add("eq", "EQ", 0, 1, 1, string.Empty);
    public static readonly int Dynamics = P.Add("dynamics", "Dynamics", 0, 1, 1, string.Empty);
    public static readonly int Width = P.Add("width", "Width", 0, 2, 1, string.Empty);
    public static readonly int MatchedBypass = P.Toggle("matchedBypass", "Level-Matched Bypass", false);

    public static EffectDescriptor Info { get; } = P.Describe(
        Id,
        "Mastering Assistant",
        EffectCategories.Dynamics,
        TargetLufs,
        Width
    );

    /// <summary>The level the chain works at: its input is normalised to this loudness (LUFS).</summary>
    public const double WorkingLufs = -18;

    const double LookaheadSeconds = 0.002;
    const double ReleaseSeconds = 0.08;
    const double MarginDb = 0.3;
    const double PriorSeconds = 0.5;
    const double GainSeconds = 0.25;
    const double BellQ = 4.318;
    const double KneeDb = 6;
    const double RmsSeconds = 0.01;
    const double CompressorRelease = 0.15;
    const float ValveDrive = 1.5f;
    const float ValveBias = 0.25f;
    const double MaxPunch = 2;
    const double BandLimitHz = 19_500;

    static readonly float ValveOffset = MathF.Tanh(ValveBias);

    /// <summary>The tube curve's slope at rest, divided out so it is unity for small signals.</summary>
    static readonly float ValveSlope = ValveDrive * (1 - ValveOffset * ValveOffset);

    readonly LookaheadLimiter limiter = new() { Adaptive = true };
    readonly SvfEqState[] eqLeft = new SvfEqState[MasteringBands.Count];
    readonly SvfEqState[] eqRight = new SvfEqState[MasteringBands.Count];
    readonly SvfEqCoefficients[] bells = new SvfEqCoefficients[MasteringBands.Count];
    readonly BiquadState[] bandLeft = new BiquadState[2];
    readonly BiquadState[] bandRight = new BiquadState[2];
    readonly BiquadCoefficients[] band = new BiquadCoefficients[2];
    readonly bool[] bellOn = new bool[MasteringBands.Count];
    readonly double[] targets = new double[MasteringBands.Count];
    readonly float[] blockLeft = new float[ControlInterval];
    readonly float[] blockRight = new float[ControlInterval];
    readonly float[] inputLeft = new float[ControlInterval];
    readonly float[] inputRight = new float[ControlInterval];
    readonly float[] unitLeft = new float[ControlInterval];
    readonly float[] unitRight = new float[ControlInterval];
    readonly Oversampler4x upLeft = new();
    readonly Oversampler4x upRight = new();
    readonly DelayLine dryLeft = new();
    readonly DelayLine dryRight = new();
    readonly TruePeakMeter peak = new();
    double[] solve = [];
    LoudnessMeter input = new(48_000);
    LoudnessMeter unit = new(48_000);
    LoudnessMeter output = new(48_000);
    Chain? chain;
    Chain? applied;
    double appliedEq = -1;
    int lookahead;
    double ceiling = 1;
    double rms;
    double meanSquare;
    double gainDb;
    double compressorAttack;
    double compressorRelease;
    double threshold;
    double ratio;
    double punchFast;
    double punchSlow;
    double fastAttack;
    double fastRelease;
    double slowAttack;
    double slowRelease;
    double limiterDb;
    double limiterStep;
    double gatedSeconds;
    double deepestCompressor;
    double deepestLimiter = 1;
    int character;
    GainRamp normalise;
    GainRamp limiterGain;
    GainRamp side;
    GainRamp valve;
    GainRamp punch;
    GainRamp bypass;
    GainRamp matched;
    float momentary = LoudnessMeter.FloorLufs;
    float shortTerm = LoudnessMeter.FloorLufs;
    float integrated = LoudnessMeter.FloorLufs;
    float truePeak = FloorDb;
    float compressorReduction;
    float limiterReduction;

    public MasteringAssistant() : base(Info) { }

    public override int LatencySamples => Prepared ? limiter.Latency : 0;

    /// <summary>The output's momentary loudness (LUFS).</summary>
    public float MomentaryLufs => Volatile.Read(ref momentary);

    /// <summary>The output's short-term loudness (LUFS).</summary>
    public float ShortTermLufs => Volatile.Read(ref shortTerm);

    /// <summary>The output's integrated loudness since the reset (LUFS).</summary>
    public float IntegratedLufs => Volatile.Read(ref integrated);

    /// <summary>The output's true peak since the reset (dBTP).</summary>
    public float TruePeakDbtp => Volatile.Read(ref truePeak);

    /// <summary>The compressor's deepest gain reduction over the last buffer (dB, ≤ 0).</summary>
    public float CompressorReductionDb => Volatile.Read(ref compressorReduction);

    /// <summary>The limiter's deepest gain reduction over the last buffer (dB, ≤ 0).</summary>
    public float LimiterReductionDb => Volatile.Read(ref limiterReduction);

    /// <summary>The gain before the limiter now, from the input (dB): <c>targetLufs − loudnessLufs − chainGain</c>.</summary>
    public double TotalGainDb => chain is null ? 0 : WorkingLufs - chain.Settings.LoudnessLufs + limiterDb;

    /// <inheritdoc />
    public void SetChain(MasteringChainSettings? chain) {
        if (chain is not null && chain.EqGainsDb.Length != MasteringBands.Count) {
            throw new ArgumentException($"A chain has {MasteringBands.Count} EQ gains.", nameof(chain));
        }

        Volatile.Write(ref this.chain, chain is null ? null : new Chain(chain));
    }

    protected override void OnPrepare(float sampleRate) {
        lookahead = (int)Math.Round(LookaheadSeconds * sampleRate);
        limiter.Allocate(lookahead);
        limiter.Configure(lookahead, true);
        limiter.SetRelease(ReleaseSeconds, sampleRate);
        dryLeft.Allocate(lookahead + TruePeak.Delay + ControlInterval);
        dryRight.Allocate(lookahead + TruePeak.Delay + ControlInterval);
        input = new(sampleRate);
        unit = new(sampleRate);
        output = new(sampleRate);
        rms = Math.Exp(-1 / (RmsSeconds * sampleRate));
        compressorRelease = Math.Exp(-1 / (CompressorRelease * sampleRate));
        fastAttack = Math.Exp(-1 / (0.001 * sampleRate));
        fastRelease = Math.Exp(-1 / (0.02 * sampleRate));
        slowAttack = Math.Exp(-1 / (0.015 * sampleRate));
        slowRelease = Math.Exp(-1 / (0.12 * sampleRate));
        limiterStep = 1 - Math.Exp(-ControlInterval / (GainSeconds * sampleRate));
        solve = Inverse(Overlap(sampleRate));
        var corner = Math.Min(BandLimitHz, 0.42 * sampleRate);
        band[0] = BiquadCoefficients.Design(BiquadType.LowPass, sampleRate, corner, 0.5412);
        band[1] = BiquadCoefficients.Design(BiquadType.LowPass, sampleRate, corner, 1.3066);
    }

    /// <summary>The dB response at each band's centre of each band's bell at +1 dB: row centre, column bell.</summary>
    static double[] Overlap(double sampleRate) {
        var n = MasteringBands.Count;
        var matrix = new double[n * n];
        for (var j = 0; j < n; j++) {
            var bell = BiquadCoefficients.Design(BiquadType.Peak, sampleRate, MasteringBands.ExactHz(j), BellQ, 1);
            for (var i = 0; i < n; i++) {
                matrix[i * n + j] = 20 * Math.Log10(bell.MagnitudeAt(MasteringBands.ExactHz(i), sampleRate));
            }
        }

        return matrix;
    }

    /// <summary>The inverse of a square matrix by Gauss-Jordan elimination with partial pivoting.</summary>
    static double[] Inverse(double[] matrix) {
        var n = (int)Math.Sqrt(matrix.Length);
        var a = (double[])matrix.Clone();
        var inverse = new double[n * n];
        for (var i = 0; i < n; i++) {
            inverse[i * n + i] = 1;
        }

        for (var column = 0; column < n; column++) {
            var pivot = column;
            for (var row = column + 1; row < n; row++) {
                if (Math.Abs(a[row * n + column]) > Math.Abs(a[pivot * n + column])) {
                    pivot = row;
                }
            }

            for (var k = 0; k < n; k++) {
                (a[column * n + k], a[pivot * n + k]) = (a[pivot * n + k], a[column * n + k]);
                (inverse[column * n + k], inverse[pivot * n + k]) = (inverse[pivot * n + k], inverse[column * n + k]);
            }

            var scale = 1 / a[column * n + column];
            for (var k = 0; k < n; k++) {
                a[column * n + k] *= scale;
                inverse[column * n + k] *= scale;
            }

            for (var row = 0; row < n; row++) {
                if (row == column) {
                    continue;
                }

                var factor = a[row * n + column];
                for (var k = 0; k < n; k++) {
                    a[row * n + k] -= factor * a[column * n + k];
                    inverse[row * n + k] -= factor * inverse[column * n + k];
                }
            }
        }

        return inverse;
    }

    protected override void OnReset() {
        limiter.Reset();
        dryLeft.Reset();
        dryRight.Reset();
        upLeft.Reset();
        upRight.Reset();
        peak.Reset();
        input.Reset();
        unit.Reset();
        output.Reset();
        for (var b = 0; b < MasteringBands.Count; b++) {
            eqLeft[b].Reset();
            eqRight[b].Reset();
        }

        for (var b = 0; b < band.Length; b++) {
            bandLeft[b].Reset();
            bandRight[b].Reset();
        }

        meanSquare = 0;
        gainDb = 0;
        punchFast = 0;
        punchSlow = 0;
        gatedSeconds = 0;
        deepestCompressor = 0;
        deepestLimiter = 1;
        applied = null;
        appliedEq = -1;
        Volatile.Write(ref momentary, LoudnessMeter.FloorLufs);
        Volatile.Write(ref shortTerm, LoudnessMeter.FloorLufs);
        Volatile.Write(ref integrated, LoudnessMeter.FloorLufs);
        Volatile.Write(ref truePeak, FloorDb);
        Volatile.Write(ref compressorReduction, 0);
        Volatile.Write(ref limiterReduction, 0);
        Publish(0);
    }

    protected override void Update() {
        var current = Volatile.Read(ref chain);
        var restart = !ReferenceEquals(current, applied);
        if (restart) {
            // A new analysis: the measured chain gain starts again from its estimate.
            applied = current;
            appliedEq = -1;
            input.Reset();
            unit.Reset();
            gatedSeconds = 0;
        }

        character = Choice(Character);
        var eq = Value(Eq);
        if (current is not null && eq != appliedEq) {
            Bells(current, eq);
            appliedEq = eq;
        }

        ceiling = Decibels.ToGain(Value(CeilingDbtp) - MarginDb);
        var strength = Value(Dynamics);
        var punchy = character == (int)MasteringCharacterKind.Punch;
        compressorAttack = Math.Exp(-1 / ((punchy ? 0.03 : 0.01) * SampleRate));
        if (current is null) {
            limiterDb = 0;
            normalise.Set(1, Snapping);
        } else {
            threshold = current.Settings.CompressorThresholdDb;
            ratio = 1 + (current.Settings.CompressorRatio - 1) * strength;
            normalise.Set((float)Decibels.ToGain(WorkingLufs - current.Settings.LoudnessLufs), Snapping);
            var target = Value(TargetLufs) - WorkingLufs - ChainGain(current, eq);
            target = Math.Clamp(target, -24, 36);
            limiterDb = Snapping || restart ? target : limiterDb + (target - limiterDb) * limiterStep;
        }

        limiterGain.Set((float)Decibels.ToGain(limiterDb), Snapping);
        side.Set(current is null ? 1 : Value(Width), Snapping);
        valve.Set(current is not null && character == (int)MasteringCharacterKind.Valve ? 1 : 0, Snapping);
        punch.Set(current is not null && punchy ? 1 : 0, Snapping);
        bypass.Set(Switch(MatchedBypass) ? 1 : 0, Snapping);
        matched.Set(
            current is null ? 1 : Decibels.ToGain(Value(TargetLufs) - current.Settings.LoudnessLufs),
            Snapping
        );
    }

    /// <summary>The chain gain: the estimate at the EQ's strength, blended with what has been measured.</summary>
    double ChainGain(Chain current, double eq) {
        var estimate = current.Settings.ChainGainDb * eq;
        var measuredIn = input.Integrated;
        var measuredOut = unit.Integrated;
        if (gatedSeconds <= 0 || measuredIn <= -70 || measuredOut <= -70) {
            return estimate;
        }

        var measured = measuredOut - measuredIn;
        return (PriorSeconds * estimate + gatedSeconds * measured) / (PriorSeconds + gatedSeconds);
    }

    /// <summary>The bells' gains for the chain's EQ at <paramref name="strength" />: through the overlap's inverse.</summary>
    void Bells(Chain current, double strength) {
        var n = MasteringBands.Count;
        for (var b = 0; b < n; b++) {
            targets[b] = current.Settings.EqGainsDb[b] * strength;
        }

        for (var j = 0; j < n; j++) {
            var gain = 0.0;
            for (var i = 0; i < n; i++) {
                gain += solve[j * n + i] * targets[i];
            }

            bellOn[j] = Math.Abs(gain) > 1e-3;
            bells[j] = bellOn[j]
                ? SvfEqCoefficients.Design(SvfEqType.Bell, SampleRate, MasteringBands.ExactHz(j), BellQ, gain)
                : SvfEqCoefficients.Identity;
        }
    }

    protected override void Render(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        var n = left.Length;
        var chained = applied is not null;
        for (var i = 0; i < n; i++) {
            var l = left[i];
            var r = right[i];
            dryLeft.Write(l);
            dryRight.Write(r);
            if (chained) {
                var g = normalise.Next();
                l *= g;
                r *= g;
                inputLeft[i] = l;
                inputRight[i] = r;
                (l, r) = Equalise(l, r);
                (l, r) = Compress(l, r);
            }

            blockLeft[i] = l;
            blockRight[i] = r;
        }

        if (chained) {
            Colour(n);
        }

        var latency = limiter.Latency;
        for (var i = 0; i < n; i++) {
            var l = blockLeft[i];
            var r = blockRight[i];
            if (chained) {
                (l, r) = Punch(l, r);
                var s = side.Next();
                var mid = 0.5f * (l + r);
                var difference = 0.5f * (l - r) * s;
                l = mid + difference;
                r = mid - difference;
                for (var b = 0; b < band.Length; b++) {
                    l = (float)bandLeft[b].Tick(band[b], l);
                    r = (float)bandRight[b].Tick(band[b], r);
                }
            }

            var g = limiterGain.Next();
            l *= g;
            r *= g;
            limiter.Tick(ref l, ref r, ceiling);
            deepestLimiter = Math.Min(deepestLimiter, limiter.Gain);
            unitLeft[i] = l / g;
            unitRight[i] = r / g;
            var by = bypass.Next();
            var level = matched.Next();
            if (by > 0) {
                l += (dryLeft.Read(latency + n - 1 - i) * level - l) * by;
                r += (dryRight.Read(latency + n - 1 - i) * level - r) * by;
            }

            left[i] = l;
            right[i] = r;
        }

        if (chained) {
            input.Process(inputLeft.AsSpan(0, n), inputRight.AsSpan(0, n));
            unit.Process(unitLeft.AsSpan(0, n), unitRight.AsSpan(0, n));
            if (input.Momentary > WorkingLufs - 20) {
                gatedSeconds += n / SampleRate;
            }
        }

        output.Process(left, right);
        peak.Process(left, right);
    }

    (float, float) Equalise(float l, float r) {
        double x = l;
        double y = r;
        for (var b = 0; b < bells.Length; b++) {
            if (bellOn[b]) {
                x = eqLeft[b].Tick(bells[b], x);
                y = eqRight[b].Tick(bells[b], y);
            }
        }

        return ((float)x, (float)y);
    }

    (float, float) Compress(float l, float r) {
        meanSquare = rms * meanSquare + (1 - rms) * (l * (double)l + r * (double)r);
        var levelDb = meanSquare > 1e-12 ? 10 * Math.Log10(meanSquare) : -120;
        var target = GainComputer.Gain(levelDb, threshold, ratio, KneeDb);
        var coefficient = target < gainDb ? compressorAttack : compressorRelease;
        gainDb = coefficient * gainDb + (1 - coefficient) * target;
        deepestCompressor = Math.Min(deepestCompressor, gainDb);
        var gain = (float)Math.Pow(10, gainDb * 0.05);
        return (l * gain, r * gain);
    }

    /// <summary>Valve: the tube curve at 4× the rate, blended in by its ramp (so a change of character does not click).</summary>
    void Colour(int n) {
        if (valve.Current == 0 && character != (int)MasteringCharacterKind.Valve) {
            return;
        }

        // Both channels follow the same ramp: each from a copy of it, then it moves on.
        var ramp = valve;
        Tube(upLeft, blockLeft.AsSpan(0, n), ramp);
        Tube(upRight, blockRight.AsSpan(0, n), ramp);
        for (var i = 0; i < n; i++) {
            valve.Next();
        }
    }

    static void Tube(Oversampler4x oversampler, Span<float> block, GainRamp amount) {
        var fast = oversampler.Up(block);
        for (var i = 0; i < fast.Length; i++) {
            var x = fast[i];
            var shaped = (MathF.Tanh(ValveDrive * x + ValveBias) - ValveOffset) / ValveSlope;
            // The ramp moves once a frame: four oversampled samples to each.
            var a = (i & 3) == 0 ? amount.Next() : amount.Current;
            fast[i] = x + (shaped - x) * a;
        }

        oversampler.Down(block);
    }

    /// <summary>Punch: a fast envelope over a slow one lifts the transients, up to <see cref="MaxPunch" /> times.</summary>
    (float, float) Punch(float l, float r) {
        var amount = punch.Next();
        var x = Math.Abs(0.5 * (l + r));
        punchFast = x > punchFast ? fastAttack * punchFast + (1 - fastAttack) * x : fastRelease * punchFast;
        punchSlow = x > punchSlow ? slowAttack * punchSlow + (1 - slowAttack) * x : slowRelease * punchSlow;
        if (amount == 0) {
            return (l, r);
        }

        var lift = punchSlow > 1e-9 ? Math.Clamp(punchFast / punchSlow, 1, MaxPunch) : 1;
        var gain = (float)(1 + (Math.Pow(lift, 0.6) - 1) * amount);
        return (l * gain, r * gain);
    }

    protected override void Finish() {
        upLeft.FlushDenormals();
        upRight.FlushDenormals();
        for (var b = 0; b < MasteringBands.Count; b++) {
            eqLeft[b].FlushDenormals();
            eqRight[b].FlushDenormals();
        }

        for (var b = 0; b < band.Length; b++) {
            bandLeft[b].FlushDenormals();
            bandRight[b].FlushDenormals();
        }

        if (meanSquare < 1e-30) {
            meanSquare = 0;
        }

        Volatile.Write(ref momentary, output.Momentary);
        Volatile.Write(ref shortTerm, output.ShortTerm);
        Volatile.Write(ref integrated, output.Integrated);
        Volatile.Write(ref truePeak, Decibels.FromGain(peak.Peak));
        Volatile.Write(ref compressorReduction, (float)deepestCompressor);
        var reduction = Decibels.FromGain(deepestLimiter);
        Volatile.Write(ref limiterReduction, reduction);
        Publish(reduction);
        deepestCompressor = gainDb;
        deepestLimiter = 1;
    }

    /// <summary>A chain as set: immutable, swapped in whole.</summary>
    sealed class Chain(MasteringChainSettings settings) {
        public MasteringChainSettings Settings { get; } = settings with { EqGainsDb = [..settings.EqGainsDb] };
    }
}

/// <summary>The true peak (4×, <see cref="TruePeak" />) of a stereo stream since its reset. Allocation-free.</summary>
internal sealed class TruePeakMeter {
    readonly float[] left = new float[TruePeak.Taps];
    readonly float[] right = new float[TruePeak.Taps];
    int at;

    public double Peak { get; private set; }

    public void Reset() {
        Array.Clear(left);
        Array.Clear(right);
        at = 0;
        Peak = 0;
    }

    public void Process(ReadOnlySpan<float> l, ReadOnlySpan<float> r) {
        var kernels = TruePeak.Kernels;
        for (var i = 0; i < l.Length; i++) {
            left[at] = l[i];
            right[at] = r[i];
            at = (at + 1) % TruePeak.Taps;
            var peak = Peak;
            foreach (var channel in (ReadOnlySpan<float[]>)[left, right]) {
                peak = Math.Max(peak, Math.Abs(channel[(at + TruePeak.Delay - 1) % TruePeak.Taps]));
                for (var p = 0; p < 3; p++) {
                    var taps = kernels[p];
                    var sum = 0.0;
                    for (var j = 0; j < TruePeak.Taps; j++) {
                        var k = at + j;
                        sum += taps[j] * channel[k >= TruePeak.Taps ? k - TruePeak.Taps : k];
                    }

                    peak = Math.Max(peak, Math.Abs(sum));
                }
            }

            Peak = peak;
        }
    }
}
