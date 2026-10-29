using Anode.Dsp.Dynamics;

namespace Anode.Dsp.Effects.Dynamics;

/// <summary>
///     <c>anode.compressor</c>: a stereo-linked feed-forward compressor (<see cref="GainComputer" />, soft knee) with a
///     smooth, branching attack/release detector on the gain in dB (Giannoulis et al. 2012, §2.3), after Logic's
///     Compressor. Three characters:
///     <list type="bullet">
///         <item><b>VCA</b>: peak level, the clean reference: the static curve is exactly the gain computer's.</item>
///         <item>
///             <b>FET</b>: peak level, and a gain cell that colours as it works: odd harmonics that grow with the gain
///             reduction (the input less a cubic, <c>x − a x³</c>, with <c>a</c> rising to 0.1 at 20 dB of
///             reduction).
///         </item>
///         <item>
///             <b>Opto</b>: RMS level (10 ms, calibrated so a sine reads its peak), a knee at least 12 dB wide, and a
///             program-dependent release: the deeper the reduction, the slower it recovers (up to 3×), like a
///             photocell.
///         </item>
///     </list>
///     The attack and release are time constants (63 %). Auto Gain adds back half the reduction a 0 dBFS level gets, on
///     top of Make Up. The detector hears the input, or the sidechain when Sidechain is External and the host provides
///     one, through an optional filter (high-, low- or band-pass). Mix blends the dry signal for parallel compression.
///     The <see cref="IAudioEffect.Reading" /> is the deepest gain reduction (dB, ≤ 0) over the last buffer.
/// </summary>
public sealed class Compressor : AudioEffect {
    public const string Id = "anode.compressor";

    public const int Vca = 0;
    public const int Fet = 1;
    public const int Opto = 2;

    static readonly EffectParameters P = new();
    public static readonly int Character = P.Choice("character", "Circuit Type", ["VCA", "FET", "Opto"], Vca);
    public static readonly int Threshold = P.Add("threshold", "Threshold", -60, 0, -20, "dB");
    public static readonly int Ratio = P.Add("ratio", "Ratio", 1, 30, 4, ":1");
    public static readonly int Knee = P.Add("knee", "Knee", 0, 24, 6, "dB");
    public static readonly int Attack = P.Add("attack", "Attack", 0.05f, 200, 10, "ms");
    public static readonly int Release = P.Add("release", "Release", 5, 3_000, 150, "ms");
    public static readonly int Makeup = P.Add("makeup", "Make Up", -12, 30, 0, "dB");
    public static readonly int AutoGain = P.Toggle("autoGain", "Auto Gain", false);
    public static readonly int Sidechain = P.Choice("sidechain", "Sidechain", ["Internal", "External"], 0);

    public static readonly int SidechainMode =
        P.Choice("sidechainFilter", "Sidechain Filter", ["Off", "High-pass", "Low-pass", "Band-pass"], 0);

    public static readonly int SidechainFrequency =
        P.Add("sidechainFreq", "Sidechain Frequency", 20, 20_000, 100, "Hz");

    public static readonly int Mix = P.Add("mix", "Mix", 0, 100, 100, "%");

    public static EffectDescriptor Info { get; } =
        P.Describe(
            Id,
            "Compressor",
            EffectCategories.Dynamics,
            true,
            Threshold,
            Ratio,
            Attack,
            Release,
            Makeup,
            Knee,
            Mix
        );

    /// <summary>The FET's cubic colour at 20 dB of reduction or more.</summary>
    const float FetColour = 0.1f;

    /// <summary>The Opto detector's RMS window.</summary>
    const double RmsSeconds = 0.01;

    DetectorFilter filter;
    GainRamp makeup;
    GainRamp mix;
    double attack;
    double release;
    double rms;
    double meanSquare;
    double gainDb;
    double threshold;
    double ratio;
    double knee;
    double deepest;
    int character;
    bool external;

    public Compressor() : base(Info) { }

    protected override void OnPrepare(float sampleRate) => rms = Math.Exp(-1 / (RmsSeconds * sampleRate));

    protected override void OnReset() {
        filter.Reset();
        meanSquare = 0;
        gainDb = 0;
        deepest = 0;
        Publish(0);
    }

    protected override void Update() {
        character = Choice(Character);
        threshold = Value(Threshold);
        ratio = Value(Ratio);
        knee = character == Opto ? Math.Max(12, Value(Knee)) : Value(Knee);
        attack = Math.Exp(-1 / (Value(Attack) * 0.001 * SampleRate));
        release = Math.Exp(-1 / (Value(Release) * 0.001 * SampleRate));
        external = Choice(Sidechain) == 1;
        filter.Set((SidechainFilter)Choice(SidechainMode), SampleRate, Value(SidechainFrequency));
        var auto = Switch(AutoGain) ? -GainComputer.Gain(0, threshold, ratio, knee) / 2 : 0;
        makeup.Set((float)Decibels.ToGain(Value(Makeup) + auto), Snapping);
        mix.Set(Value(Mix) * 0.01f, Snapping);
    }

    protected override void Render(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        var side = external && !sidechainLeft.IsEmpty;
        for (var i = 0; i < left.Length; i++) {
            var dl = side ? sidechainLeft[i] : left[i];
            var dr = side ? sidechainRight[i] : right[i];
            filter.Tick(ref dl, ref dr);
            double levelDb;
            if (character == Opto) {
                meanSquare = rms * meanSquare + (1 - rms) * (dl * dl + dr * dr);
                levelDb = meanSquare > 1e-12 ? 10 * Math.Log10(meanSquare) : -120;
            } else {
                var peak = Math.Max(Math.Abs(dl), Math.Abs(dr));
                levelDb = peak > 1e-6 ? 20 * Math.Log10(peak) : -120;
            }

            var target = GainComputer.Gain(levelDb, threshold, ratio, knee);
            if (target < gainDb) {
                gainDb = attack * gainDb + (1 - attack) * target;
            } else {
                // The photocell remembers: after deep reduction it lets go more slowly.
                var r = character == Opto ? Math.Pow(release, 1 / (1 + Math.Min(2, -gainDb / 12))) : release;
                gainDb = r * gainDb + (1 - r) * target;
            }

            deepest = Math.Min(deepest, gainDb);
            var gain = (float)Math.Pow(10, gainDb * 0.05) * makeup.Next();
            var wet = mix.Next();
            var l = left[i];
            var rr = right[i];
            if (character == Fet) {
                // The pinched JFET bends the signal across it: a cubic that grows with the reduction.
                var amount = FetColour * (float)Math.Min(1, -gainDb / 20);
                l = Math.Clamp(l, -1.5f, 1.5f);
                rr = Math.Clamp(rr, -1.5f, 1.5f);
                l -= amount * l * l * l;
                rr -= amount * rr * rr * rr;
            }

            l *= gain;
            rr *= gain;

            left[i] += (l - left[i]) * wet;
            right[i] += (rr - right[i]) * wet;
        }
    }

    protected override void Finish() {
        Publish((float)deepest);
        deepest = gainDb;
        filter.FlushDenormals();
        if (Math.Abs(gainDb) < 1e-12) {
            gainDb = 0;
        }
    }
}
