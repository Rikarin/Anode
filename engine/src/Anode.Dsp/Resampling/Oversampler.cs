namespace Anode.Dsp.Resampling;

/// <summary>
///     Polyphase IIR half-band filter design (ported from Cathode.Dsp) after Laurent de Soras' HIIR
///     (http://ldesoras.free.fr/prod.html): two
///     parallel chains of first-order allpass sections whose average is an elliptic half-band low-pass. Near-zero
///     latency (non-linear phase), and cheap: each coefficient costs one multiply per sample at the low rate.
/// </summary>
public static class HalfBand {
    /// <summary>
    ///     Coefficients for a filter reaching <paramref name="attenuationDb" /> in the stopband with the given
    ///     <paramref name="transition" /> bandwidth (fraction of the high sample rate, 0..0.5).
    /// </summary>
    public static double[] Design(double attenuationDb, double transition) {
        var (k, q) = TransitionParameters(transition);
        var attenuation = Math.Pow(10, -attenuationDb / 10);
        var a = attenuation / (1 - attenuation);
        var order = (int)Math.Ceiling(Math.Log(a * a / 16) / Math.Log(q));
        if ((order & 1) == 0) {
            order++;
        }

        order = Math.Max(order, 3);
        var count = (order - 1) / 2;
        var coefficients = new double[count];
        for (var i = 0; i < count; i++) {
            coefficients[i] = Coefficient(i, k, q, order);
        }

        return coefficients;
    }

    static (double K, double Q) TransitionParameters(double transition) {
        var k = Math.Pow(Math.Tan((1 - transition * 2) * Math.PI / 4), 2);
        var root = Math.Pow(1 - k * k, 0.25);
        var e = 0.5 * (1 - root) / (1 + root);
        var e4 = Math.Pow(e, 4);
        var q = e * (1 + e4 * (2 + e4 * (15 + 150 * e4)));
        return (k, q);
    }

    static double Coefficient(int index, double k, double q, int order) {
        var c = index + 1;
        var numerator = SeriesNumerator(q, order, c) * Math.Pow(q, 0.25);
        var denominator = SeriesDenominator(q, order, c) + 0.5;
        var ww = numerator / denominator;
        var wwsq = ww * ww;
        var x = Math.Sqrt((1 - wwsq * k) * (1 - wwsq / k)) / (1 + wwsq);
        return (1 - x) / (1 + x);
    }

    static double SeriesNumerator(double q, int order, int c) {
        double sum = 0, term;
        var sign = 1;
        var i = 0;
        do {
            term = Math.Pow(q, i * (i + 1)) * Math.Sin((i * 2 + 1) * c * Math.PI / order) * sign;
            sum += term;
            sign = -sign;
            i++;
        } while (Math.Abs(term) > 1e-100);

        return sum;
    }

    static double SeriesDenominator(double q, int order, int c) {
        double sum = 0, term;
        var sign = -1;
        var i = 1;
        do {
            term = Math.Pow(q, i * i) * Math.Cos(i * 2 * c * Math.PI / order) * sign;
            sum += term;
            sign = -sign;
            i++;
        } while (Math.Abs(term) > 1e-100);

        return sum;
    }
}

/// <summary>One allpass chain: y[n] = a (x[n] − y[n−1]) + x[n−1] per section.</summary>
internal sealed class AllpassChain(float[] coefficients) {
    readonly float[] x = new float[coefficients.Length];
    readonly float[] y = new float[coefficients.Length];

    public float Process(float input) {
        for (var i = 0; i < coefficients.Length; i++) {
            var output = coefficients[i] * (input - y[i]) + x[i];
            x[i] = input;
            y[i] = output;
            input = output;
        }

        return input;
    }

    public void Reset() {
        Array.Clear(x);
        Array.Clear(y);
    }

    public void FlushDenormals() {
        for (var i = 0; i < x.Length; i++) {
            if (MathF.Abs(x[i]) < 1e-20f) {
                x[i] = 0;
            }

            if (MathF.Abs(y[i]) < 1e-20f) {
                y[i] = 0;
            }
        }
    }
}

/// <summary>A 2× upsampler and downsampler pair from one half-band design (two allpass branches each).</summary>
internal sealed class HalfBandStage {
    readonly AllpassChain upEven;
    readonly AllpassChain upOdd;
    readonly AllpassChain downEven;
    readonly AllpassChain downOdd;

    public HalfBandStage(double[] coefficients) {
        var even = new float[(coefficients.Length + 1) / 2];
        var odd = new float[coefficients.Length / 2];
        for (var i = 0; i < coefficients.Length; i++) {
            if (i % 2 == 0) {
                even[i / 2] = (float)coefficients[i];
            } else {
                odd[i / 2] = (float)coefficients[i];
            }
        }

        upEven = new(even);
        upOdd = new(odd);
        downEven = new(even);
        downOdd = new(odd);
    }

    /// <summary>Writes 2 × <paramref name="input" />.Length samples to <paramref name="output" />.</summary>
    public void Up(ReadOnlySpan<float> input, Span<float> output) {
        for (var i = 0; i < input.Length; i++) {
            output[2 * i] = upEven.Process(input[i]);
            output[2 * i + 1] = upOdd.Process(input[i]);
        }
    }

    /// <summary>Reads 2 × <paramref name="output" />.Length samples from <paramref name="input" />.</summary>
    public void Down(ReadOnlySpan<float> input, Span<float> output) {
        for (var i = 0; i < output.Length; i++) {
            output[i] = 0.5f * (downEven.Process(input[2 * i + 1]) + downOdd.Process(input[2 * i]));
        }
    }

    public void Reset() {
        upEven.Reset();
        upOdd.Reset();
        downEven.Reset();
        downOdd.Reset();
    }

    public void FlushDenormals() {
        upEven.FlushDenormals();
        upOdd.FlushDenormals();
        downEven.FlushDenormals();
        downOdd.FlushDenormals();
    }
}

/// <summary>
///     4× oversampling for one channel, as two cascaded 2× half-band stages (Cathode's <c>Oversampler4x</c>, 100 dB
///     stopband; the first stage's transition starts near 20 kHz at 48 kHz, the second only rejects the images of an
///     already band-limited signal). Process a block of up to <see cref="Effects.AudioEffect.ControlInterval" /> frames
///     with <see cref="Up" />, run the nonlinearity on the returned span, then <see cref="Down" />. The filters are
///     minimum-phase IIR: a little phase shift near the top of the band and no latency to report.
///     Allocation-free after construction.
/// </summary>
public sealed class Oversampler4x {
    public const double AttenuationDb = 100;
    public const double FirstTransition = 0.04;
    public const double SecondTransition = 0.26;

    static readonly double[] FirstStage = HalfBand.Design(AttenuationDb, FirstTransition);
    static readonly double[] SecondStage = HalfBand.Design(AttenuationDb, SecondTransition);

    readonly HalfBandStage first = new(FirstStage);
    readonly HalfBandStage second = new(SecondStage);
    readonly float[] twice;
    readonly float[] four;

    public Oversampler4x(int maxBlock = Effects.AudioEffect.ControlInterval) {
        twice = new float[maxBlock * 2];
        four = new float[maxBlock * 4];
    }

    /// <summary>Upsamples <paramref name="input" /> and returns the 4× buffer to process in place.</summary>
    public Span<float> Up(ReadOnlySpan<float> input) {
        var two = twice.AsSpan(0, input.Length * 2);
        var quad = four.AsSpan(0, input.Length * 4);
        first.Up(input, two);
        second.Up(two, quad);
        return quad;
    }

    /// <summary>Downsamples the 4× buffer returned by <see cref="Up" /> into <paramref name="output" />.</summary>
    public void Down(Span<float> output) {
        var two = twice.AsSpan(0, output.Length * 2);
        second.Down(four.AsSpan(0, output.Length * 4), two);
        first.Down(two, output);
    }

    public void Reset() {
        first.Reset();
        second.Reset();
    }

    public void FlushDenormals() {
        first.FlushDenormals();
        second.FlushDenormals();
    }
}
