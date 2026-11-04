using Anode.Dsp.Filters;

namespace Anode.Dsp.Effects.Utility;

/// <summary>
///     <c>anode.frequency-spread</c>: widens a signal by sending neighbouring bands of it to opposite sides. Bands
///     band-passes of the mid (SVF, each a band wide, log-spaced from Low to High) are summed with alternating signs; that
///     sum is added to the left channel and taken from the right at Width. Only the side changes, so the mono sum of the
///     output is the mono sum of the input, whatever the settings, and below Low and above High nothing moves (bass stays
///     central). At 100 % a band's centre is 6 dB up on one side and gone from the other, so the peak can rise by 6 dB:
///     Output trims it. The <see cref="IAudioEffect.Reading" /> is the output's peak (dBFS) over the last buffer.
/// </summary>
public sealed class FrequencySpread : AudioEffect {
    public const string Id = "anode.frequency-spread";
    public const int MaxBands = 16;

    static readonly EffectParameters P = new();
    public static readonly int Bands = P.Stepped("bands", "Bands", 2, MaxBands, 8, string.Empty, MaxBands - 2);
    public static readonly int Low = P.Add("low", "Low", 50, 5_000, 300, "Hz");
    public static readonly int High = P.Add("high", "High", 500, 20_000, 8_000, "Hz");
    public static readonly int Width = P.Add("width", "Width", 0, 100, 50, "%");
    public static readonly int Output = P.Add("output", "Output", -24, 12, 0, "dB");

    public static EffectDescriptor Info { get; } =
        P.Describe(Id, "Frequency Spread", EffectCategories.Utility, Width, Bands, Low, High, Output);

    readonly SvfCoefficients[] filters = new SvfCoefficients[MaxBands];
    readonly SvfState[] states = new SvfState[MaxBands];
    GainRamp width;
    GainRamp output;
    int bands;
    float peak;

    public FrequencySpread() : base(Info) { }

    protected override void OnPrepare(float sampleRate) { }

    protected override void OnReset() {
        foreach (ref var state in states.AsSpan()) {
            state.Reset();
        }

        peak = 0;
        Publish(FloorDb);
    }

    protected override void Update() {
        var count = Choice(Bands);
        for (var b = count; b < bands; b++) {
            states[b].Reset();
        }

        bands = count;
        var low = Math.Min(Value(Low), SampleRate * 0.45);
        var high = Math.Clamp(Value(High), low * 1.5, SampleRate * 0.45);
        var ratio = Math.Pow(high / low, 1.0 / count);
        var q = 1 / (Math.Sqrt(ratio) - 1 / Math.Sqrt(ratio));
        for (var b = 0; b < count; b++) {
            filters[b] = SvfCoefficients.Design(SampleRate, low * Math.Pow(ratio, b + 0.5), q);
        }

        width.Set(Value(Width) * 0.01f, Snapping);
        output.Set(Decibels.ToGain(Value(Output)), Snapping);
    }

    protected override void Render(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        var max = peak;
        for (var i = 0; i < left.Length; i++) {
            double mid = 0.5f * (left[i] + right[i]);
            double side = 0;
            for (var b = 0; b < bands; b++) {
                var band = states[b].Tick(filters[b], mid, SvfOutput.BandPass);
                side += b % 2 == 0 ? band : -band;
            }

            var spread = (float)side * width.Next();
            var g = output.Next();
            left[i] = (left[i] + spread) * g;
            right[i] = (right[i] - spread) * g;
            max = MathF.Max(max, MathF.Max(MathF.Abs(left[i]), MathF.Abs(right[i])));
        }

        peak = max;
    }

    protected override void Finish() {
        Publish(Decibels.FromGain(peak));
        peak = 0;
        for (var b = 0; b < bands; b++) {
            states[b].FlushDenormals();
        }
    }
}
