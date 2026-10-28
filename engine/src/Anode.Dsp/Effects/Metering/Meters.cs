using Anode.Dsp.Metering;

namespace Anode.Dsp.Effects.Metering;

/// <summary>
///     <c>anode.level-meter</c>: passes the audio and measures it. Peak mode holds each buffer's peak and falls at the
///     Fall rate (dB/s); RMS mode averages the square over 300 ms (a one-pole) and is calibrated so a sine reads its
///     peak. The <see cref="IAudioEffect.Reading" /> is the louder channel in dBFS; <see cref="LeftDb" /> and
///     <see cref="RightDb" /> each channel.
/// </summary>
public sealed class LevelMeter : AudioEffect {
    public const string Id = "anode.level-meter";
    const double RmsSeconds = 0.3;

    static readonly EffectParameters P = new();
    public static readonly int Mode = P.Choice("mode", "Mode", ["Peak", "RMS"], 0);
    public static readonly int Fall = P.Add("fall", "Peak Fall", 3, 60, 20, "dB/s");

    public static EffectDescriptor Info { get; } = P.Describe(Id, "Level Meter", EffectCategories.Metering);

    double rms;
    double squareLeft;
    double squareRight;
    double peakLeft;
    double peakRight;
    float left = FloorDb;
    float right = FloorDb;
    int frames;

    public LevelMeter() : base(Info) { }

    public float LeftDb => Volatile.Read(ref left);

    public float RightDb => Volatile.Read(ref right);

    protected override void OnPrepare(float sampleRate) => rms = Math.Exp(-1 / (RmsSeconds * sampleRate));

    protected override void OnReset() {
        squareLeft = 0;
        squareRight = 0;
        peakLeft = 0;
        peakRight = 0;
        frames = 0;
        Volatile.Write(ref left, FloorDb);
        Volatile.Write(ref right, FloorDb);
        Publish(FloorDb);
    }

    protected override void Update() { }

    protected override void Render(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        for (var i = 0; i < left.Length; i++) {
            double l = left[i];
            double r = right[i];
            squareLeft = rms * squareLeft + (1 - rms) * l * l;
            squareRight = rms * squareRight + (1 - rms) * r * r;
            peakLeft = Math.Max(peakLeft, Math.Abs(l));
            peakRight = Math.Max(peakRight, Math.Abs(r));
        }

        frames += left.Length;
    }

    protected override void Finish() {
        float l, r;
        if (Choice(Mode) == 1) {
            l = Decibels.FromGain(Math.Sqrt(2 * squareLeft));
            r = Decibels.FromGain(Math.Sqrt(2 * squareRight));
        } else {
            var fall = Value(Fall) * frames / SampleRate;
            l = Math.Max(Decibels.FromGain(peakLeft), Math.Max(FloorDb, left - fall));
            r = Math.Max(Decibels.FromGain(peakRight), Math.Max(FloorDb, right - fall));
        }

        peakLeft = 0;
        peakRight = 0;
        frames = 0;
        if (squareLeft < 1e-30) {
            squareLeft = 0;
        }

        if (squareRight < 1e-30) {
            squareRight = 0;
        }

        Volatile.Write(ref left, l);
        Volatile.Write(ref right, r);
        Publish(Math.Max(l, r));
    }
}

/// <summary>
///     <c>anode.loudness-meter</c>: passes the audio and measures its loudness by ITU-R BS.1770-4 / EBU R 128
///     (<see cref="LoudnessMeter" />): momentary (400 ms), short-term (3 s) and integrated (gated, since the last reset)
///     loudness in LUFS. The <see cref="IAudioEffect.Reading" /> is the one the Reading parameter picks; all three are
///     properties too.
/// </summary>
public sealed class LoudnessMeterEffect : AudioEffect {
    public const string Id = "anode.loudness-meter";

    static readonly EffectParameters P = new();

    public static readonly int Shows =
        P.Choice("reading", "Reading", ["Momentary", "Short-term", "Integrated"], 0);

    public static EffectDescriptor Info { get; } =
        P.Describe(Id, "Loudness Meter", EffectCategories.Metering);

    LoudnessMeter meter = new(48_000);
    float momentary = LoudnessMeter.FloorLufs;
    float shortTerm = LoudnessMeter.FloorLufs;
    float integrated = LoudnessMeter.FloorLufs;

    public LoudnessMeterEffect() : base(Info) { }

    public float Momentary => Volatile.Read(ref momentary);

    public float ShortTerm => Volatile.Read(ref shortTerm);

    public float Integrated => Volatile.Read(ref integrated);

    protected override void OnPrepare(float sampleRate) => meter = new(sampleRate);

    protected override void OnReset() {
        meter.Reset();
        Finish();
    }

    protected override void Update() { }

    protected override void Render(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) =>
        meter.Process(left, right);

    protected override void Finish() {
        Volatile.Write(ref momentary, meter.Momentary);
        Volatile.Write(ref shortTerm, meter.ShortTerm);
        Volatile.Write(ref integrated, meter.Integrated);
        Publish(
            Choice(Shows) switch {
                0 => meter.Momentary,
                1 => meter.ShortTerm,
                _ => meter.Integrated
            }
        );
    }
}

/// <summary>
///     <c>anode.multimeter</c>, a light MultiMeter: passes the audio and measures the stereo image. The correlation
///     <c>E[LR] / √(E[L²] E[R²])</c> (+1 mono, 0 unrelated, −1 out of phase; 0 in silence) and the balance (dB, right
///     over left) over an exponential window of the Integration time. The <see cref="IAudioEffect.Reading" /> is the
///     correlation; <see cref="Balance" />, <see cref="PeakLeftDb" /> and <see cref="PeakRightDb" /> are properties.
/// </summary>
public sealed class MultiMeter : AudioEffect {
    public const string Id = "anode.multimeter";

    static readonly EffectParameters P = new();
    public static readonly int Integration = P.Add("integration", "Integration", 50, 3_000, 300, "ms");

    public static EffectDescriptor Info { get; } = P.Describe(Id, "MultiMeter", EffectCategories.Metering);

    double coefficient;
    double product;
    double squareLeft;
    double squareRight;
    double peakLeft;
    double peakRight;
    float balance;
    float leftPeak = FloorDb;
    float rightPeak = FloorDb;

    public MultiMeter() : base(Info) { }

    public float Balance => Volatile.Read(ref balance);

    public float PeakLeftDb => Volatile.Read(ref leftPeak);

    public float PeakRightDb => Volatile.Read(ref rightPeak);

    protected override void OnPrepare(float sampleRate) { }

    protected override void OnReset() {
        product = 0;
        squareLeft = 0;
        squareRight = 0;
        peakLeft = 0;
        peakRight = 0;
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
        var c = coefficient;
        for (var i = 0; i < left.Length; i++) {
            double l = left[i];
            double r = right[i];
            product = c * product + (1 - c) * l * r;
            squareLeft = c * squareLeft + (1 - c) * l * l;
            squareRight = c * squareRight + (1 - c) * r * r;
            peakLeft = Math.Max(peakLeft, Math.Abs(l));
            peakRight = Math.Max(peakRight, Math.Abs(r));
        }
    }

    protected override void Finish() {
        var energy = Math.Sqrt(squareLeft * squareRight);
        var correlation = energy > 1e-12 ? Math.Clamp(product / energy, -1, 1) : 0;
        var b = squareLeft > 1e-12 && squareRight > 1e-12 ? 10 * Math.Log10(squareRight / squareLeft) : 0;
        Volatile.Write(ref balance, (float)b);
        Volatile.Write(ref leftPeak, Decibels.FromGain(peakLeft));
        Volatile.Write(ref rightPeak, Decibels.FromGain(peakRight));
        peakLeft = 0;
        peakRight = 0;
        if (energy < 1e-30) {
            product = 0;
            squareLeft = 0;
            squareRight = 0;
        }

        Publish((float)correlation);
    }
}
