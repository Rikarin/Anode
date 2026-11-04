using Anode.Dsp.Metering;

namespace Anode.Dsp.Effects.Metering;

/// <summary>
///     <c>anode.scope</c>: passes the audio and keeps its last seconds for an oscilloscope. Time is how much a trace
///     spans. Free shows the newest audio; Rising and Falling look back for the left channel crossing Level in that
///     direction, so a periodic signal stands still. The <see cref="IAudioEffect.Reading" /> is the louder channel's peak
///     (dBFS) over the last buffer.
///     <para>
///         Display (<see cref="IEffectDisplay" />): <see cref="TracePoints" /> left samples, then as many right. Each
///         point of a trace longer than the display is the largest sample of its slice, so a peak never falls between
///         points; a shorter one is interpolated.
///     </para>
/// </summary>
public sealed class Scope : AudioEffect, IEffectDisplay {
    public const string Id = "anode.scope";
    public const int TracePoints = 256;
    public const int Free = 0;
    public const int Rising = 1;
    public const int Falling = 2;
    const double MaxSeconds = 2;

    static readonly EffectParameters P = new();
    public static readonly int Time = P.Add("time", "Time", 1, 2_000, 20, "ms");
    public static readonly int Trigger = P.Choice("trigger", "Trigger", ["Free", "Rising", "Falling"], Rising);
    public static readonly int Level = P.Add("level", "Trigger Level", -1, 1, 0, string.Empty);

    public static EffectDescriptor Info { get; } =
        P.Describe(Id, "Oscilloscope", EffectCategories.Metering, Time, Trigger, Level);

    readonly StereoHistory history = new();
    float peak;

    public Scope() : base(Info) { }

    public int DisplaySize => 2 * TracePoints;

    public int ReadDisplay(Span<float> values) {
        if (!Prepared) {
            return 0;
        }

        var end = history.Written;
        var span = Math.Clamp((int)Math.Round(GetParameter(Time) * 0.001 * SampleRate), 2, history.Capacity / 2);
        var start = TraceStart(end, span);
        for (var p = 0; p < TracePoints; p++) {
            var from = start + (double)p * span / TracePoints;
            var to = start + (double)(p + 1) * span / TracePoints;
            if (span <= TracePoints) {
                var whole = (long)Math.Floor(from);
                var frac = (float)(from - whole);
                var next = Math.Min(whole + 1, end - 1); // the newest frame has none after it yet
                values[p] = history.Left(whole) + (history.Left(next) - history.Left(whole)) * frac;
                values[p + TracePoints] = history.Right(whole) + (history.Right(next) - history.Right(whole)) * frac;
                continue;
            }

            values[p] = 0;
            values[p + TracePoints] = 0;
            for (var at = (long)from; at < (long)to; at++) {
                values[p] = Larger(values[p], history.Left(at));
                values[p + TracePoints] = Larger(values[p + TracePoints], history.Right(at));
            }
        }

        return DisplaySize;
    }

    protected override void OnPrepare(float sampleRate) =>
        history.Allocate((int)Math.Ceiling(2 * MaxSeconds * sampleRate) + TracePoints);

    protected override void OnReset() {
        history.Reset();
        peak = 0;
        Publish(FloorDb);
    }

    protected override void Update() { }

    protected override void Render(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        var max = peak;
        for (var i = 0; i < left.Length; i++) {
            max = MathF.Max(max, MathF.Max(MathF.Abs(left[i]), MathF.Abs(right[i])));
        }

        peak = max;
        history.Write(left, right);
    }

    protected override void Finish() {
        Publish(Decibels.FromGain(peak));
        peak = 0;
    }

    static float Larger(float a, float b) => MathF.Abs(b) > MathF.Abs(a) ? b : a;

    /// <summary>
    ///     Where a trace of <paramref name="span" /> frames ending at or before <paramref name="end" /> starts: the latest
    ///     trigger with a whole trace after it, or the newest audio when there is none.
    /// </summary>
    long TraceStart(long end, int span) {
        var mode = (int)MathF.Round(GetParameter(Trigger));
        var free = Math.Max(end - span, 0);
        if (mode == Free) {
            return free;
        }

        var level = GetParameter(Level);
        var oldest = Math.Max(end - 2 * span, 1);
        for (var at = free; at >= oldest; at--) {
            var before = history.Left(at - 1);
            var after = history.Left(at);
            if (mode == Rising ? before < level && after >= level : before > level && after <= level) {
                return at;
            }
        }

        return free;
    }
}
