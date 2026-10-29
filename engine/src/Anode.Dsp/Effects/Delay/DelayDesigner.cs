using Anode.Dsp.Delays;
using Anode.Dsp.Filters;

namespace Anode.Dsp.Effects.Delay;

/// <summary>
///     <c>anode.delay-designer</c>, after Logic's Delay Designer: up to 26 taps (A … Z) on one delay line.
///     <list type="bullet">
///         <item>
///             The mono sum of the input feeds a line of up to <see cref="MaxSeconds" />. The first
///             <c>Taps</c> taps read it, each at its own time (cubic interpolation; a change of time glides), through
///             its own low cut and high cut (12 dB/octave, off at the ends of their ranges), at its level, panned
///             (constant power) into the stereo sum. A muted tap is silent.
///         </item>
///         <item>
///             With Sync, each tap's time snaps to the nearest multiple of the Grid note value at the tempo (the
///             transport's when the engine passes it, else the <c>tempo</c> parameter).
///         </item>
///         <item>
///             Feedback sends the Feedback Tap's filtered output back into the line. Mix crossfades dry and wet.
///         </item>
///     </list>
///     <para>
///         Parameter ids: <c>taps</c>, <c>sync</c>, <c>grid</c>, <c>tempo</c>, <c>feedbackTap</c>, <c>feedback</c>,
///         <c>mix</c>; then per tap X = A … Z: <c>tap{X}Time</c> (ms), <c>tap{X}Level</c> (dB),
///         <c>tap{X}Pan</c> (−100 … 100), <c>tap{X}LowCut</c>, <c>tap{X}HighCut</c> (Hz), <c>tap{X}Mute</c>.
///     </para>
/// </summary>
public sealed class DelayDesigner : AudioEffect {
    public const string Id = "anode.delay-designer";
    public const int MaxTaps = 26;
    public const double MaxSeconds = 10;

    static readonly string[] Letters =
        [..Enumerable.Range(0, MaxTaps).Select(static i => ((char)('A' + i)).ToString())];

    static readonly EffectParameters P = new();
    public static readonly int TapCount = P.Stepped("taps", "Taps", 1, MaxTaps, 4, string.Empty, MaxTaps - 1);
    public static readonly int Sync = P.Toggle("sync", "Sync", false);
    public static readonly int Grid = P.Choice("grid", "Grid", NoteValues.Names, NoteValues.Sixteenth);
    public static readonly int Tempo = P.Add("tempo", "Tempo", 20, 300, 120, "bpm");
    public static readonly int FeedbackTap = P.Choice("feedbackTap", "Feedback Tap", Letters, 0);
    public static readonly int Feedback = P.Add("feedback", "Feedback", 0, 100, 0, "%");
    public static readonly int Mix = P.Add("mix", "Mix", 0, 100, 50, "%");

    /// <summary>Each tap's parameter indices, tap A first.</summary>
    public static readonly IReadOnlyList<TapParameters> Taps = [..Enumerable.Range(0, MaxTaps).Select(AddTap)];

    public static EffectDescriptor Info { get; } = P.Describe(
        Id,
        "Delay Designer",
        EffectCategories.Delay,
        Mix,
        Feedback,
        Taps[0].Time,
        Taps[1].Time,
        Taps[2].Time,
        Taps[3].Time,
        Taps[0].Level,
        Taps[1].Level
    );

    readonly DelayLine line = new();
    readonly DelayGlide[] time = new DelayGlide[MaxTaps];
    readonly SvfEqCoefficients[] lowCut = new SvfEqCoefficients[MaxTaps];
    readonly SvfEqCoefficients[] highCut = new SvfEqCoefficients[MaxTaps];
    readonly SvfEqState[] lowState = new SvfEqState[MaxTaps];
    readonly SvfEqState[] highState = new SvfEqState[MaxTaps];
    readonly bool[] lowOn = new bool[MaxTaps];
    readonly bool[] highOn = new bool[MaxTaps];
    readonly GainRamp[] gainLeft = new GainRamp[MaxTaps];
    readonly GainRamp[] gainRight = new GainRamp[MaxTaps];
    GainRamp mix;
    double glide;
    float feedback;
    int active;
    int feedbackTap;

    public DelayDesigner() : base(Info) { }

    protected override double ClockTempo => HostTempo(Value(Tempo));

    static TapParameters AddTap(int i) {
        var x = Letters[i];
        // A dotted rhythm by default: taps every 3/16 at 120 bpm, each a little quieter, alternating sides.
        return new(
            P.Add($"tap{x}Time", $"Tap {x} Time", 0, (float)(MaxSeconds * 1_000), 187.5f * (i + 1), "ms"),
            P.Add($"tap{x}Level", $"Tap {x} Level", -60, 6, Math.Max(-60, -3 - 3 * i), "dB"),
            P.Add($"tap{x}Pan", $"Tap {x} Pan", -100, 100, i % 2 == 0 ? -50 : 50, string.Empty),
            P.Add($"tap{x}LowCut", $"Tap {x} Low Cut", 20, 2_000, 20, "Hz"),
            P.Add($"tap{x}HighCut", $"Tap {x} High Cut", 1_000, 20_000, 20_000, "Hz"),
            P.Toggle($"tap{x}Mute", $"Tap {x} Mute", false)
        );
    }

    /// <summary>Tap <paramref name="tap" />'s delay at the current settings, in samples (at least one).</summary>
    public double DelaySamples(int tap) {
        double ms = Value(Taps[tap].Time);
        if (Switch(Sync)) {
            var grid = NoteValues.Seconds(Choice(Grid), HostTempo(Value(Tempo))) * 1_000;
            ms = Math.Round(ms / grid) * grid;
        }

        return Math.Clamp(ms * 0.001 * SampleRate, 1, MaxSeconds * SampleRate);
    }

    protected override void OnPrepare(float sampleRate) {
        line.Allocate((int)Math.Ceiling(MaxSeconds * sampleRate) + 4);
        glide = 1 - Math.Exp(-ControlInterval / (DelayGlide.GlideSeconds * sampleRate));
    }

    protected override void OnReset() {
        line.Reset();
        Array.Clear(lowState);
        Array.Clear(highState);
    }

    protected override void Update() {
        active = (int)Math.Round(Value(TapCount));
        feedbackTap = Choice(FeedbackTap);
        feedback = Value(Feedback) * 0.01f;
        for (var t = 0; t < MaxTaps; t++) {
            var p = Taps[t];
            // The read happens before the write, so a delay of D samples reads D − 1 writes back.
            time[t].Update(DelaySamples(t) - 1, glide, Snapping);
            lowOn[t] = Value(p.LowCut) > 20;
            highOn[t] = Value(p.HighCut) < 20_000;
            if (t < active) {
                lowCut[t] = SvfEqCoefficients.Design(SvfEqType.HighPass, SampleRate, Value(p.LowCut), 0.7071);
                highCut[t] = SvfEqCoefficients.Design(SvfEqType.LowPass, SampleRate, Value(p.HighCut), 0.7071);
            }

            var level = t < active && !Switch(p.Mute) ? Decibels.ToGain(Value(p.Level)) : 0;
            var angle = (Value(p.Pan) + 100) / 200 * MathF.PI / 2;
            gainLeft[t].Set(level * MathF.Cos(angle), Snapping);
            gainRight[t].Set(level * MathF.Sin(angle), Snapping);
        }

        mix.Set(Value(Mix) * 0.01f, Snapping);
    }

    protected override void Render(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        for (var i = 0; i < left.Length; i++) {
            float l = 0, r = 0, back = 0;
            for (var t = 0; t < MaxTaps; t++) {
                var d = time[t].Next();
                var gl = gainLeft[t].Next();
                var gr = gainRight[t].Next();
                if (t >= active) {
                    continue;
                }

                double y = d >= 1 ? line.ReadCubic(d) : line.ReadLinear(d);
                if (lowOn[t]) {
                    y = lowState[t].Tick(lowCut[t], y);
                }

                if (highOn[t]) {
                    y = highState[t].Tick(highCut[t], y);
                }

                var s = (float)y;
                if (t == feedbackTap) {
                    back = s;
                }

                l += s * gl;
                r += s * gr;
            }

            line.Write(0.5f * (left[i] + right[i]) + feedback * back);
            var wet = mix.Next();
            left[i] += (l - left[i]) * wet;
            right[i] += (r - right[i]) * wet;
        }
    }

    protected override void Finish() {
        for (var t = 0; t < MaxTaps; t++) {
            lowState[t].FlushDenormals();
            highState[t].FlushDenormals();
        }
    }

    /// <summary>The parameter indices of one tap.</summary>
    public sealed record TapParameters(int Time, int Level, int Pan, int LowCut, int HighCut, int Mute);
}
