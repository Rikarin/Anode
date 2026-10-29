using Anode.Dsp.Filters;

namespace Anode.Dsp.Effects.Specialized;

/// <summary>
///     <c>anode.subbass</c>, after Logic's SubBass: a sub-harmonic an octave or two below the bass.
///     <list type="bullet">
///         <item>
///             The mono sum passes a band-pass at Frequency (its width from Bandwidth), which isolates the bass's
///             fundamental.
///         </item>
///         <item>
///             Each rising zero crossing of the band (with a little hysteresis against noise) toggles a flip-flop: a
///             square at half the band's frequency; a second flip-flop halves it again for two octaves.
///         </item>
///         <item>
///             The square, scaled by the band's envelope (so the sub follows the bass's dynamics) and low-passed twice
///             (24 dB/octave at the sub's frequency) into a near-sine, is added to both channels at Level (the
///             <c>mix</c> parameter): at 0 % the input passes exactly.
///         </item>
///     </list>
/// </summary>
public sealed class SubBass : AudioEffect {
    public const string Id = "anode.subbass";

    static readonly EffectParameters P = new();
    public static readonly int Frequency = P.Add("frequency", "Frequency", 30, 300, 90, "Hz");
    public static readonly int Bandwidth = P.Add("bandwidth", "Bandwidth", 0.2f, 2, 1, "oct");
    public static readonly int Octave = P.Choice("octave", "Octave", ["1 Octave Down", "2 Octaves Down"], 0);
    public static readonly int Mix = P.Add("mix", "Level", 0, 100, 50, "%");

    public static EffectDescriptor Info { get; } =
        P.Describe(Id, "SubBass", EffectCategories.Specialized, Mix, Frequency, Bandwidth, Octave);

    readonly SvfState[] smooth = new SvfState[2];
    SvfState band;
    SvfCoefficients bandPass;
    SvfCoefficients lowPass;
    GainRamp level;
    double envelope;
    double attack;
    double release;
    int first;
    int second;
    bool high;
    bool twoOctaves;

    public SubBass() : base(Info) { }

    protected override void OnPrepare(float sampleRate) {
        attack = Math.Exp(-1 / (0.005 * sampleRate));
        release = Math.Exp(-1 / (0.08 * sampleRate));
    }

    protected override void OnReset() {
        band.Reset();
        smooth[0].Reset();
        smooth[1].Reset();
        envelope = 0;
        first = 1;
        second = 1;
        high = false;
    }

    protected override void Update() {
        var frequency = Value(Frequency);
        var octaves = Value(Bandwidth);
        // A band-pass's Q from its width in octaves: √(2^N) / (2^N − 1).
        var q = Math.Sqrt(Math.Pow(2, octaves)) / (Math.Pow(2, octaves) - 1);
        bandPass = SvfCoefficients.Design(SampleRate, frequency, q);
        twoOctaves = Choice(Octave) == 1;
        lowPass = SvfCoefficients.Design(SampleRate, frequency / (twoOctaves ? 4 : 2) * 1.2, 0.70710678118654752);
        level.Set(Value(Mix) * 0.01f, Snapping);
    }

    protected override void Render(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        for (var i = 0; i < left.Length; i++) {
            var x = 0.5 * (left[i] + right[i]);
            var b = band.Tick(bandPass, x, SvfOutput.BandPass);
            var magnitude = Math.Abs(b);
            envelope = magnitude > envelope
                ? magnitude + (envelope - magnitude) * attack
                : magnitude + (envelope - magnitude) * release;
            // A rising crossing past a tenth of the envelope (the hysteresis) toggles the divider.
            var threshold = 0.1 * envelope;
            if (!high && b > threshold) {
                high = true;
                first = -first;
                if (first > 0) {
                    second = -second;
                }
            } else if (high && b < -threshold) {
                high = false;
            }

            var square = (twoOctaves ? second : first) * envelope;
            var sub = smooth[0].Tick(lowPass, square, SvfOutput.LowPass);
            sub = smooth[1].Tick(lowPass, sub, SvfOutput.LowPass);
            var gain = level.Next();
            var s = (float)(1.5 * sub) * gain;
            left[i] += s;
            right[i] += s;
        }
    }

    protected override void Finish() {
        band.FlushDenormals();
        smooth[0].FlushDenormals();
        smooth[1].FlushDenormals();
        if (envelope < 1e-30) {
            envelope = 0;
        }
    }
}
