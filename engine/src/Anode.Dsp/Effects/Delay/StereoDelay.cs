using Anode.Dsp.Delays;
using Anode.Dsp.Filters;

namespace Anode.Dsp.Effects.Delay;

/// <summary>
///     <c>anode.stereo-delay</c>, after Logic's Stereo Delay: a delay per channel, each set in milliseconds or, with Sync,
///     as a note value at the host's tempo (the <c>tempo</c> parameter, which the engine sets from the project). Each
///     channel feeds back into itself and, by the crossfeed, into the other. The repeats pass a low cut and a high cut
///     (12 dB/octave, off at the ends of their ranges) on their way out and around. Delay times are capped at
///     <see cref="MaxSeconds" />. Changing a time glides the delay (a pitch bend) rather than clicking.
/// </summary>
public sealed class StereoDelay : AudioEffect {
    public const string Id = "anode.stereo-delay";
    public const double MaxSeconds = 4;

    static readonly EffectParameters P = new();
    public static readonly int Tempo = P.Add("tempo", "Tempo", 20, 300, 120, "bpm");
    public static readonly int Sync = P.Toggle("sync", "Sync", true);
    public static readonly int LeftNote = P.Choice("leftNote", "Left Note", NoteValues.Names, NoteValues.Eighth);
    public static readonly int RightNote = P.Choice("rightNote", "Right Note", NoteValues.Names, NoteValues.Quarter);
    public static readonly int LeftTime = P.Add("leftTime", "Left Delay", 1, 4_000, 250, "ms");
    public static readonly int RightTime = P.Add("rightTime", "Right Delay", 1, 4_000, 500, "ms");
    public static readonly int LeftFeedback = P.Add("leftFeedback", "Left Feedback", 0, 100, 30, "%");
    public static readonly int RightFeedback = P.Add("rightFeedback", "Right Feedback", 0, 100, 30, "%");
    public static readonly int Crossfeed = P.Add("crossfeed", "Crossfeed", 0, 100, 0, "%");
    public static readonly int LowCut = P.Add("lowCut", "Low Cut", 20, 2_000, 20, "Hz");
    public static readonly int HighCut = P.Add("highCut", "High Cut", 1_000, 20_000, 20_000, "Hz");
    public static readonly int Mix = P.Add("mix", "Mix", 0, 100, 30, "%");

    public static EffectDescriptor Info { get; } = P.Describe(Id, "Stereo Delay", EffectCategories.Delay);

    readonly DelayLine lineLeft = new();
    readonly DelayLine lineRight = new();
    DelayGlide timeLeft;
    DelayGlide timeRight;
    SvfEqCoefficients lowCut;
    SvfEqCoefficients highCut;
    SvfEqState lowLeft;
    SvfEqState lowRight;
    SvfEqState highLeft;
    SvfEqState highRight;
    GainRamp mix;
    double glide;
    float feedbackLeft;
    float feedbackRight;
    float cross;
    bool lowCutOn;
    bool highCutOn;

    public StereoDelay() : base(Info) { }

    /// <summary>The delay of channel <paramref name="right" /> at the current settings, in samples.</summary>
    public double DelaySamples(bool right) {
        var note = right ? RightNote : LeftNote;
        var time = right ? RightTime : LeftTime;
        var seconds = Switch(Sync) ? NoteValues.Seconds(Choice(note), Value(Tempo)) : Value(time) * 0.001;
        return Math.Clamp(seconds, 0.001, MaxSeconds) * SampleRate;
    }

    protected override void OnPrepare(float sampleRate) {
        var samples = (int)Math.Ceiling(MaxSeconds * sampleRate) + 4;
        lineLeft.Allocate(samples);
        lineRight.Allocate(samples);
        glide = 1 - Math.Exp(-ControlInterval / (DelayGlide.GlideSeconds * sampleRate));
    }

    protected override void OnReset() {
        lineLeft.Reset();
        lineRight.Reset();
        lowLeft.Reset();
        lowRight.Reset();
        highLeft.Reset();
        highRight.Reset();
    }

    protected override void Update() {
        // The read happens before the write, so a delay of D samples reads D − 1 writes back.
        timeLeft.Update(DelaySamples(false) - 1, glide, Snapping);
        timeRight.Update(DelaySamples(true) - 1, glide, Snapping);
        feedbackLeft = Value(LeftFeedback) * 0.01f;
        feedbackRight = Value(RightFeedback) * 0.01f;
        cross = Value(Crossfeed) * 0.01f;
        lowCutOn = Value(LowCut) > 20;
        highCutOn = Value(HighCut) < 20_000;
        lowCut = SvfEqCoefficients.Design(SvfEqType.HighPass, SampleRate, Value(LowCut), 0.7071);
        highCut = SvfEqCoefficients.Design(SvfEqType.LowPass, SampleRate, Value(HighCut), 0.7071);
        mix.Set(Value(Mix) * 0.01f, Snapping);
    }

    protected override void Render(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        for (var i = 0; i < left.Length; i++) {
            double yl = lineLeft.ReadCubic(timeLeft.Next());
            double yr = lineRight.ReadCubic(timeRight.Next());
            if (lowCutOn) {
                yl = lowLeft.Tick(lowCut, yl);
                yr = lowRight.Tick(lowCut, yr);
            }

            if (highCutOn) {
                yl = highLeft.Tick(highCut, yl);
                yr = highRight.Tick(highCut, yr);
            }

            var l = (float)yl;
            var r = (float)yr;
            lineLeft.Write(left[i] + feedbackLeft * (l + (r - l) * cross));
            lineRight.Write(right[i] + feedbackRight * (r + (l - r) * cross));
            var wet = mix.Next();
            left[i] += (l - left[i]) * wet;
            right[i] += (r - right[i]) * wet;
        }
    }

    protected override void Finish() {
        lowLeft.FlushDenormals();
        lowRight.FlushDenormals();
        highLeft.FlushDenormals();
        highRight.FlushDenormals();
    }
}
