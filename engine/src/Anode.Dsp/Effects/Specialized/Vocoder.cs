using Anode.Dsp.Filters;
using Anode.Dsp.Oscillators;

namespace Anode.Dsp.Effects.Specialized;

/// <summary>
///     <c>anode.vocoder</c>: a channel vocoder. A bank of band-passes (two SVF sections each, log-spaced from Low to High)
///     splits the modulator; each band's level, followed at Attack and Release, sets the gain of the same band of the
///     carrier, and the carrier's bands are summed. The spectrum of one signal so shapes the other.
///     <list type="bullet">
///         <item>
///             <b>Source.</b> <c>Synth</c>: the input modulates a sawtooth at Note. <c>Side Chain Carrier</c>: the input
///             modulates the sidechain (a voice on this track, the keys sent to it). <c>Side Chain Voice</c>: the
///             sidechain modulates the input (a synth on this track, the voice sent to it). A source that needs a
///             sidechain leaves the audio as it is until one is connected.
///         </item>
///         <item>
///             <b>Formant</b> moves the carrier's bank against the modulator's, so a voice sounds larger or smaller at the
///             same pitch. <b>Sharpness</b> narrows or widens the bands from their spacing.
///         </item>
///         <item>
///             <b>Sibilance</b> adds the modulator above High straight to the output: consonants carry little a pitched
///             carrier can shape.
///         </item>
///     </list>
///     A band's follower reads a sine's mean rectified level, scaled so that a tone of amplitude A in a band passes a
///     carrier tone of amplitude C at A·C. Level trims the result; Mix blends the input back. The
///     <see cref="IAudioEffect.Reading" /> is the output's peak (dBFS) over the last buffer.
///     <para>
///         Display (<see cref="IEffectDisplay" />): each band's level, dBFS, lowest first.
///     </para>
/// </summary>
public sealed class Vocoder : AudioEffect, IEffectDisplay {
    public const string Id = "anode.vocoder";
    public const int MaxBands = 32;
    public const int Synth = 0;
    public const int SidechainCarrier = 1;
    public const int SidechainVoice = 2;

    /// <summary>A sine's peak over its mean rectified level.</summary>
    const double RectifiedToPeak = Math.PI / 2;

    static readonly EffectParameters P = new();

    public static readonly int Source = P.Choice(
        "source",
        "Source",
        ["Synth", "Side Chain Carrier", "Side Chain Voice"],
        Synth
    );

    public static readonly int Bands = P.Stepped("bands", "Bands", 4, MaxBands, 20, string.Empty, MaxBands - 4);
    public static readonly int Low = P.Add("low", "Low", 50, 1_000, 100, "Hz");
    public static readonly int High = P.Add("high", "High", 1_000, 16_000, 8_000, "Hz");
    public static readonly int Formant = P.Add("formant", "Formant", -12, 12, 0, "st");
    public static readonly int Sharpness = P.Add("sharpness", "Sharpness", 0.5f, 4, 1, string.Empty);
    public static readonly int Attack = P.Add("attack", "Attack", 0.5f, 100, 4, "ms");
    public static readonly int Release = P.Add("release", "Release", 5, 1_000, 40, "ms");
    public static readonly int Note = P.Stepped("note", "Note", 24, 84, 48, string.Empty, 60);
    public static readonly int Sibilance = P.Add("sibilance", "Sibilance", 0, 100, 25, "%");
    public static readonly int Level = P.Add("level", "Level", -24, 24, 0, "dB");
    public static readonly int Mix = P.Add("mix", "Mix", 0, 100, 100, "%");

    public static EffectDescriptor Info { get; } = P.Describe(
        Id,
        "Vocoder",
        EffectCategories.Specialized,
        true,
        Source,
        Bands,
        Formant,
        Sharpness,
        Note,
        Mix
    );

    readonly SvfCoefficients[] analysis = new SvfCoefficients[MaxBands];
    readonly SvfCoefficients[] synthesis = new SvfCoefficients[MaxBands];
    readonly SvfState[] analysisStates = new SvfState[MaxBands * 2];
    readonly SvfState[] leftStates = new SvfState[MaxBands * 2];
    readonly SvfState[] rightStates = new SvfState[MaxBands * 2];
    readonly double[] envelopes = new double[MaxBands];
    readonly float[] published = new float[MaxBands];
    SvfCoefficients sibilance;
    SvfState sibilanceState;
    GainRamp sibilanceLevel;
    GainRamp level;
    GainRamp mix;
    int source;
    int bands;
    double attack;
    double release;
    double phase;
    double increment;
    float peak;

    public Vocoder() : base(Info) { }

    public int DisplaySize => MaxBands;

    public int ReadDisplay(Span<float> values) {
        var count = Volatile.Read(ref bands);
        for (var band = 0; band < count; band++) {
            values[band] = Volatile.Read(ref published[band]);
        }

        return count;
    }

    protected override void OnPrepare(float sampleRate) { }

    protected override void OnReset() {
        Array.Clear(analysisStates);
        Array.Clear(leftStates);
        Array.Clear(rightStates);
        Array.Clear(envelopes);
        Array.Fill(published, FloorDb);
        sibilanceState.Reset();
        phase = 0;
        peak = 0;
        Publish(FloorDb);
    }

    protected override void Update() {
        source = Choice(Source);
        var count = Choice(Bands);
        if (count != bands) {
            for (var band = count; band < bands; band++) {
                envelopes[band] = 0;
                Volatile.Write(ref published[band], FloorDb);
            }

            Volatile.Write(ref bands, count);
        }

        var low = Math.Min(Value(Low), SampleRate * 0.4);
        var high = Math.Clamp(Value(High), low * 2, SampleRate * 0.45);
        var ratio = Math.Pow(high / low, 1.0 / (count - 1));
        var q = Value(Sharpness) / (Math.Sqrt(ratio) - 1 / Math.Sqrt(ratio));
        var shift = Math.Pow(2, Value(Formant) / 12);
        for (var band = 0; band < count; band++) {
            var centre = low * Math.Pow(ratio, band);
            analysis[band] = SvfCoefficients.Design(SampleRate, centre, q);
            synthesis[band] = SvfCoefficients.Design(SampleRate, Math.Min(centre * shift, SampleRate * 0.45), q);
        }

        sibilance = SvfCoefficients.Design(SampleRate, high, 0.70710678118654752);
        attack = Math.Exp(-1 / (Value(Attack) * 0.001 * SampleRate));
        release = Math.Exp(-1 / (Value(Release) * 0.001 * SampleRate));
        increment = 440 * Math.Pow(2, (Value(Note) - 69) / 12) / SampleRate;
        sibilanceLevel.Set(Value(Sibilance) * 0.01f, Snapping);
        level.Set(Decibels.ToGain(Value(Level)), Snapping);
        mix.Set(Value(Mix) * 0.01f, Snapping);
    }

    protected override void Render(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        var noSidechain = sidechainLeft.IsEmpty;
        if (source != Synth && noSidechain) {
            return; // nothing to work with yet: the audio stays as it is
        }

        var max = peak;
        for (var i = 0; i < left.Length; i++) {
            var inLeft = left[i];
            var inRight = right[i];
            double modulator;
            double carrierLeft;
            double carrierRight;
            switch (source) {
                case Synth:
                    modulator = 0.5 * (inLeft + (double)inRight);
                    carrierLeft = PolyBlep.Saw(phase, increment);
                    carrierRight = carrierLeft;
                    phase += increment;
                    if (phase >= 1) {
                        phase -= 1;
                    }

                    break;
                case SidechainCarrier:
                    modulator = 0.5 * (inLeft + (double)inRight);
                    carrierLeft = sidechainLeft[i];
                    carrierRight = sidechainRight[i];
                    break;
                default:
                    modulator = 0.5 * (sidechainLeft[i] + (double)sidechainRight[i]);
                    carrierLeft = inLeft;
                    carrierRight = inRight;
                    break;
            }

            double wetLeft = 0;
            double wetRight = 0;
            for (var band = 0; band < bands; band++) {
                var a = analysis[band];
                var x = analysisStates[2 * band].Tick(a, modulator, SvfOutput.BandPass);
                x = analysisStates[2 * band + 1].Tick(a, x, SvfOutput.BandPass);
                var rectified = Math.Abs(x);
                var envelope = envelopes[band];
                envelope += (rectified > envelope ? 1 - attack : 1 - release) * (rectified - envelope);
                envelopes[band] = envelope;
                var s = synthesis[band];
                var l = leftStates[2 * band].Tick(s, carrierLeft, SvfOutput.BandPass);
                l = leftStates[2 * band + 1].Tick(s, l, SvfOutput.BandPass);
                var r = rightStates[2 * band].Tick(s, carrierRight, SvfOutput.BandPass);
                r = rightStates[2 * band + 1].Tick(s, r, SvfOutput.BandPass);
                wetLeft += l * envelope;
                wetRight += r * envelope;
            }

            var air = sibilanceState.Tick(sibilance, modulator, SvfOutput.HighPass) * sibilanceLevel.Next();
            var g = level.Next();
            var outLeft = (float)((wetLeft * RectifiedToPeak + air) * g);
            var outRight = (float)((wetRight * RectifiedToPeak + air) * g);
            var m = mix.Next();
            left[i] = inLeft + (outLeft - inLeft) * m;
            right[i] = inRight + (outRight - inRight) * m;
            max = MathF.Max(max, MathF.Max(MathF.Abs(left[i]), MathF.Abs(right[i])));
        }

        peak = max;
    }

    protected override void Finish() {
        Publish(Decibels.FromGain(peak));
        peak = 0;
        for (var band = 0; band < bands; band++) {
            Volatile.Write(ref published[band], Decibels.FromGain(envelopes[band] * RectifiedToPeak));
            if (envelopes[band] < 1e-30) {
                envelopes[band] = 0;
            }
        }

        for (var i = 0; i < bands * 2; i++) {
            analysisStates[i].FlushDenormals();
            leftStates[i].FlushDenormals();
            rightStates[i].FlushDenormals();
        }

        sibilanceState.FlushDenormals();
    }
}
