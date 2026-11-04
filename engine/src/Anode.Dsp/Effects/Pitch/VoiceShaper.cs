namespace Anode.Dsp.Effects.Pitch;

/// <summary>
///     <c>anode.voice-shaper</c>: moves a monophonic voice's pitch and its formants apart, in real time.
///     <list type="bullet">
///         <item>
///             <b>Detection.</b> Every <see cref="Hop" /> samples, <see cref="YinDetector" /> on the mono sum, periods of
///             <see cref="MinHz" /> … <see cref="MaxHz" />.
///         </item>
///         <item>
///             <b>Target.</b> <c>Pitch</c> transposes the voice by its semitones. <c>Side Chain</c> sings at the pitch of
///             the sidechain plus those semitones, however the voice moves; while either is unvoiced, it transposes
///             as if by the last interval. The shift moves to its target with a 20 ms time constant.
///         </item>
///         <item>
///             <b>Shift.</b> TD-PSOLA, as in <see cref="PitchCorrection" />: a grain of two periods per mark,
///             overlap-added
///             a period ÷ the pitch ratio apart, so the waveform's period changes and the spectral envelope stays.
///             Reading a grain faster or slower than it is written scales that envelope alone: <c>Formant</c> sets the
///             grain's rate, so the pitch stays where the marks put it. The sum is divided by the windows' sum. At no
///             shift this is the input exactly. A grain needs a period of input after its mark and lands up to two
///             before it, so the output runs <see cref="LatencySamples" /> behind (about 38 ms).
///         </item>
///         <item>
///             <b>Level.</b> How much a shift changes a voice's level depends on how spiky the voice is, so a shifted
///             signal is matched to the dry one's power over 50 ms, within ±12 dB.
///         </item>
///     </list>
///     Mix crossfades the equally delayed dry signal and the shaped one. The <see cref="IAudioEffect.Reading" /> is the
///     detected pitch in Hz (0 while unvoiced).
///     <para>
///         Display (<see cref="IEffectDisplay" />): the detected pitch in Hz, the shift in cents, the sidechain's pitch in
///         Hz (0 without one).
///     </para>
/// </summary>
public sealed class VoiceShaper : AudioEffect, IEffectDisplay {
    public const string Id = "anode.voice-shaper";
    public const double MinHz = 80;
    public const double MaxHz = 1_000;
    public const int Hop = 256;
    public const int Transpose = 0;
    public const int FollowSidechain = 1;

    /// <summary>The widest shift a sidechain's pitch asks for, in semitones.</summary>
    const double MaxInterval = 36;

    const double ResponseSeconds = 0.02;

    /// <summary>The window of the level match, and how fast its gain moves.</summary>
    const double LevelSeconds = 0.05;

    const double LevelGainSeconds = 0.005;

    /// <summary>The widest level match, as a gain.</summary>
    const double MaxLevelGain = 4;

    /// <summary>Under this the windows' sum is too thin to divide by: the first and last grain of a note.</summary>
    const float LeastWeight = 0.25f;

    static readonly EffectParameters P = new();
    public static readonly int Semitones = P.Add("pitch", "Pitch", -24, 24, 0, "st");
    public static readonly int Formant = P.Add("formant", "Formant", -12, 12, 0, "st");
    public static readonly int Target = P.Choice("target", "Target", ["Pitch", "Side Chain"], Transpose);
    public static readonly int Mix = P.Add("mix", "Mix", 0, 100, 100, "%");

    public static EffectDescriptor Info { get; } = P.Describe(
        Id,
        "Voice Shaper",
        EffectCategories.Pitch,
        true,
        Semitones,
        Formant,
        Mix
    );

    YinDetector yin = new(48_000, MinHz, MaxHz);
    YinDetector sideYin = new(48_000, MinHz, MaxHz);
    float[] inLeft = [];
    float[] inRight = [];
    float[] mono = [];
    float[] sideMono = [];
    float[] outLeft = [];
    float[] outRight = [];
    float[] weight = [];
    GainRamp mix;
    int mask;
    int unvoicedPeriod;
    int latency;
    long now;
    double synth;
    double mark;
    double period;
    bool voiced;
    double sidePeriod;
    bool sideVoiced;
    bool following;
    double cents;
    double interval;
    double semitones;
    double ratio = 1;
    double grainRate = 1;
    double smoothing;
    double levelWindow;
    double levelGainStep;
    double dryPower;
    double wetPower;
    double levelGain = 1;
    float shownHz;
    float shownCents;
    float shownSide;

    public VoiceShaper() : base(Info) { }

    public override int LatencySamples => Prepared ? latency : 0;

    public int DisplaySize => 3;

    public int ReadDisplay(Span<float> values) {
        values[0] = Volatile.Read(ref shownHz);
        values[1] = Volatile.Read(ref shownCents);
        values[2] = Volatile.Read(ref shownSide);
        return 3;
    }

    protected override void OnPrepare(float sampleRate) {
        yin = new(sampleRate, MinHz, MaxHz);
        sideYin = new(sampleRate, MinHz, MaxHz);
        unvoicedPeriod = (int)Math.Round(0.005 * sampleRate);
        latency = 3 * yin.MaxPeriod + 2;
        smoothing = 1 - Math.Exp(-Hop / (ResponseSeconds * sampleRate));
        levelWindow = 1 - Math.Exp(-1 / (LevelSeconds * sampleRate));
        levelGainStep = 1 - Math.Exp(-1 / (LevelGainSeconds * sampleRate));
        var ring = 1;
        while (ring < Math.Max(yin.Size, latency + 3 * yin.MaxPeriod) * 2) {
            ring <<= 1;
        }

        mask = ring - 1;
        inLeft = new float[ring];
        inRight = new float[ring];
        mono = new float[ring];
        sideMono = new float[ring];
        outLeft = new float[ring];
        outRight = new float[ring];
        weight = new float[ring];
    }

    protected override void OnReset() {
        Array.Clear(inLeft);
        Array.Clear(inRight);
        Array.Clear(mono);
        Array.Clear(sideMono);
        Array.Clear(outLeft);
        Array.Clear(outRight);
        Array.Clear(weight);
        now = 0;
        synth = 0;
        mark = 0;
        period = unvoicedPeriod;
        voiced = false;
        sideVoiced = false;
        cents = double.NaN;
        interval = 0;
        ratio = 1;
        dryPower = 0;
        wetPower = 0;
        levelGain = 1;
        Volatile.Write(ref shownHz, 0);
        Volatile.Write(ref shownCents, 0);
        Volatile.Write(ref shownSide, 0);
        Publish(0);
    }

    protected override void Update() {
        semitones = Value(Semitones);
        grainRate = Math.Pow(2, Value(Formant) / 12);
        following = Choice(Target) == FollowSidechain;
        mix.Set(Value(Mix) * 0.01f, Snapping);
    }

    protected override void Render(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        var side = !sidechainLeft.IsEmpty;
        for (var i = 0; i < left.Length; i++) {
            var at = (int)(now & mask);
            inLeft[at] = left[i];
            inRight[at] = right[i];
            mono[at] = 0.5f * (left[i] + right[i]);
            sideMono[at] = side ? 0.5f * (sidechainLeft[i] + sidechainRight[i]) : 0;
            if ((now + 1) % Hop == 0) {
                Analyse(side);
            }

            Synthesise();
            var t = (int)((now - latency) & mask);
            var w = MathF.Max(weight[t], LeastWeight);
            var wetLeft = outLeft[t] / w;
            var wetRight = outRight[t] / w;
            outLeft[t] = 0;
            outRight[t] = 0;
            weight[t] = 0;
            var match = MatchLevel(inLeft[t], inRight[t], wetLeft, wetRight);
            wetLeft *= match;
            wetRight *= match;
            var m = mix.Next();
            left[i] = inLeft[t] + (wetLeft - inLeft[t]) * m;
            right[i] = inRight[t] + (wetRight - inRight[t]) * m;
            now++;
        }
    }

    protected override void Finish() {
        Publish(Volatile.Read(ref shownHz));
        Volatile.Write(ref shownCents, double.IsNaN(cents) ? 0 : (float)cents);
        if (dryPower < 1e-30) {
            dryPower = 0;
        }

        if (wetPower < 1e-30) {
            wetPower = 0;
        }
    }

    /// <summary>The gain that brings the shaped signal to the dry one's level; 1 while nothing is shifted.</summary>
    float MatchLevel(float dryLeft, float dryRight, float wetLeft, float wetRight) {
        dryPower += levelWindow * ((double)dryLeft * dryLeft + (double)dryRight * dryRight - dryPower);
        wetPower += levelWindow * ((double)wetLeft * wetLeft + (double)wetRight * wetRight - wetPower);
        var target = 1.0;
        if ((ratio != 1 || grainRate != 1) && wetPower > 1e-12) {
            target = Math.Clamp(Math.Sqrt(dryPower / wetPower), 1 / MaxLevelGain, MaxLevelGain);
        }

        levelGain += levelGainStep * (target - levelGain);
        if (Math.Abs(levelGain - 1) < 1e-7 && target == 1) {
            levelGain = 1;
        }

        return (float)levelGain;
    }

    void Analyse(bool side) {
        voiced = yin.Detect(mono, mask, now, out var found);
        if (voiced) {
            period = found;
        }

        sideVoiced = side && sideYin.Detect(sideMono, mask, now, out sidePeriod);
        Volatile.Write(ref shownHz, voiced ? (float)(SampleRate / period) : 0);
        Volatile.Write(ref shownSide, sideVoiced ? (float)(SampleRate / sidePeriod) : 0);
        if (following && voiced && sideVoiced) {
            interval = Math.Clamp(12 * Math.Log2(period / sidePeriod), -MaxInterval, MaxInterval);
        }

        var target = ((following ? interval : 0) + semitones) * 100;
        cents = double.IsNaN(cents) ? target : cents + (target - cents) * smoothing;
        ratio = Math.Pow(2, cents / 1_200);
    }

    /// <summary>Overlap-adds the grains whose input is complete.</summary>
    void Synthesise() {
        var p = voiced ? period : unvoicedPeriod;
        var read = now - latency;
        var span = p / grainRate;
        while (synth + p + 1 <= now) {
            while (mark + p <= synth) {
                mark += p;
            }

            if (ratio == 1) {
                mark = synth; // unshifted, the output stays in step with the delayed dry signal
            }

            // A grain's output sample j from its centre reads the input j × the grain rate from the mark, so the grain
            // covers a period of input either side whatever the rate, in span output samples.
            var first = (long)Math.Floor(synth - span) + 1;
            var last = (long)Math.Ceiling(synth + span) - 1;
            for (var to = Math.Max(first, read + 1); to <= last; to++) {
                var j = to - synth;
                var w = (float)(0.5 + 0.5 * Math.Cos(Math.PI * j / span));
                var from = mark + j * grainRate;
                var whole = (long)Math.Floor(from);
                var frac = (float)(from - whole);
                var a = (int)(whole & mask);
                var b = (int)((whole + 1) & mask);
                var o = (int)(to & mask);
                outLeft[o] += w * (inLeft[a] + (inLeft[b] - inLeft[a]) * frac);
                outRight[o] += w * (inRight[a] + (inRight[b] - inRight[a]) * frac);
                weight[o] += w;
            }

            synth += p / ratio;
        }
    }
}
