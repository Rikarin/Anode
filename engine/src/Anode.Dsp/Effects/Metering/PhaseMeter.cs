using Anode.Dsp.Metering;

namespace Anode.Dsp.Effects.Metering;

/// <summary>
///     <c>anode.phase-meter</c>: passes the audio and shows its stereo image, as a correlation and a goniometer. The
///     correlation and the balance are <see cref="StereoImage" /> over an exponential window of the Integration time; the
///     <see cref="IAudioEffect.Reading" /> is the correlation. The goniometer plots the last frames with mid up and side
///     across (left to the left), so mono is a vertical line, out of phase a horizontal one, and a channel alone a
///     diagonal.
///     <para>
///         Display (<see cref="IEffectDisplay" />): the correlation, the balance (dB, right over left), then
///         <see cref="Points" /> pairs of side and mid, newest first, spread over about a display frame's audio.
///     </para>
/// </summary>
public sealed class PhaseMeter : AudioEffect, IEffectDisplay {
    public const string Id = "anode.phase-meter";
    public const int Points = 256;

    /// <summary>The display frames a second the points are spread for (the host reads about 15).</summary>
    const int FramesPerSecond = 15;

    static readonly EffectParameters P = new();
    public static readonly int Integration = P.Add("integration", "Integration", 50, 3_000, 300, "ms");

    public static EffectDescriptor Info { get; } =
        P.Describe(Id, "Phase Meter", EffectCategories.Metering, Integration);

    readonly StereoHistory history = new();
    StereoImage image;
    double coefficient;
    int stride = 1;
    float balance;

    public PhaseMeter() : base(Info) { }

    /// <summary>The balance over the integration time, dB, right over left.</summary>
    public float Balance => Volatile.Read(ref balance);

    public int DisplaySize => 2 + 2 * Points;

    public int ReadDisplay(Span<float> values) {
        if (!Prepared) {
            return 0;
        }

        values[0] = Reading;
        values[1] = Balance;
        var end = history.Written;
        for (var p = 0; p < Points; p++) {
            var at = end - 1 - p * (long)stride;
            float l = 0, r = 0;
            if (at >= 0) {
                l = history.Left(at);
                r = history.Right(at);
            }

            values[2 + 2 * p] = (r - l) * MathF.Sqrt(0.5f);
            values[3 + 2 * p] = (r + l) * MathF.Sqrt(0.5f);
        }

        return DisplaySize;
    }

    protected override void OnPrepare(float sampleRate) {
        // Odd, so a tone at a simple fraction of the rate still shows as a line and not as a few fixed points.
        stride = (int)Math.Round(sampleRate / (FramesPerSecond * Points)) | 1;
        history.Allocate(Points * stride + 1);
    }

    protected override void OnReset() {
        image.Reset();
        history.Reset();
        Volatile.Write(ref balance, 0);
        Publish(0);
    }

    protected override void Update() => coefficient = Math.Exp(-1 / (Value(Integration) * 0.001 * SampleRate));

    protected override void Render(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        for (var i = 0; i < left.Length; i++) {
            image.Tick(coefficient, left[i], right[i]);
        }

        history.Write(left, right);
    }

    protected override void Finish() {
        Volatile.Write(ref balance, (float)image.BalanceDb);
        Publish((float)image.Correlation);
        image.FlushDenormals();
    }
}
