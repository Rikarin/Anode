namespace Anode.Dsp.Dynamics;

/// <summary>
///     A 4× true-peak estimator (as ITU-R BS.1770 meters it): windowed-sinc interpolation at the three quarter points
///     between two samples, from <see cref="Taps" /> samples around them (Kaiser window, β = 8). It needs
///     <see cref="Delay" /> samples after the interval it estimates.
/// </summary>
public static class TruePeak {
    public const int Taps = 32;
    public const int Delay = Taps / 2;

    /// <summary>The taps for the fractions ¼, ½ and ¾, each for samples <c>c − 15 … c + 16</c> around the interval (c, c+1).</summary>
    public static float[][] Kernels { get; } = Design();

    static float[][] Design() {
        const double beta = 8;
        var kernels = new float[3][];
        for (var p = 1; p <= 3; p++) {
            var fraction = p / 4.0;
            var taps = new float[Taps];
            double sum = 0;
            for (var j = 0; j < Taps; j++) {
                // The sample at c + j − (Delay − 1), at a distance of (j − (Delay − 1) − fraction) from the point.
                var t = j - (Delay - 1) - fraction;
                var sinc = Math.Abs(t) < 1e-12 ? 1 : Math.Sin(Math.PI * t) / (Math.PI * t);
                var x = t / Delay;
                var window = Math.Abs(x) >= 1 ? 0 : BesselI0(beta * Math.Sqrt(1 - x * x)) / BesselI0(beta);
                taps[j] = (float)(sinc * window);
                sum += sinc * window;
            }

            for (var j = 0; j < Taps; j++) {
                taps[j] = (float)(taps[j] / sum);
            }

            kernels[p - 1] = taps;
        }

        return kernels;
    }

    static double BesselI0(double x) {
        double sum = 1, term = 1;
        for (var k = 1; k < 50; k++) {
            term *= x * x / (4.0 * k * k);
            sum += term;
            if (term < 1e-15 * sum) {
                break;
            }
        }

        return sum;
    }
}

/// <summary>
///     The engine of <c>anode.limiter</c> and <c>anode.adaptive-limiter</c>: a stereo-linked lookahead peak limiter
///     whose output never passes the ceiling.
///     <para>
///         Per sample, the detector takes the peak of the interval ahead (with <see cref="TruePeak" />, the
///         interpolated peaks between the samples too) and the gain that would bring it to the ceiling. A running
///         minimum over the lookahead window holds that gain, a release lets it recover, and a moving average as long
///         as the lookahead smooths it; the audio is delayed so the smoothed gain has come down to the target by the
///         time the peak arrives (the average of a window that lies entirely within the hold is at most the target).
///         The latency is the lookahead plus <see cref="TruePeak.Delay" /> when true-peak detection is on.
///     </para>
///     <para>
///         The release recovers toward a target that is 1 for the plain limiter; the adaptive one recovers fast toward
///         a slow average of the gain (so brief peaks let go quickly, a sustained loud passage stays down) and the
///         average itself returns slowly. A soft knee (adaptive) starts reducing <see cref="KneeDb" /> below the
///         ceiling and approaches it exponentially, which rounds peaks rather than flattening them.
///     </para>
/// </summary>
public sealed class LookaheadLimiter {
    /// <summary>The detector aims this far below the ceiling, to absorb the interpolation's own error.</summary>
    public const double MarginDb = 0.1;

    public const double KneeDb = 3;

    readonly float[][] history = [new float[TruePeak.Taps], new float[TruePeak.Taps]];
    float[] delayLeft = [];
    float[] delayRight = [];
    double[] minValues = [];
    long[] minIndices = [];
    double[] box = [];
    int delayMask;
    int delayWrite;
    int historyAt;
    int minHead;
    int minCount;
    int boxAt;
    double boxSum;
    long index;
    int window;
    int lookahead;
    bool truePeak;
    double release;
    double fastRelease;
    double slowRelease;
    double average = 1;
    double previous = 1;

    /// <summary>The smoothed gain of the last sample (≤ 1).</summary>
    public double Gain { get; private set; } = 1;

    /// <summary>The delay of the audio, in samples.</summary>
    public int Latency => lookahead + (truePeak ? TruePeak.Delay : 0);

    public bool Adaptive { get; set; }

    /// <summary>Control thread: room for a lookahead of up to <paramref name="maxLookahead" /> samples.</summary>
    public void Allocate(int maxLookahead) {
        var size = 1;
        while (size < maxLookahead + TruePeak.Delay + 2) {
            size <<= 1;
        }

        delayLeft = new float[size];
        delayRight = new float[size];
        delayMask = size - 1;
        minValues = new double[maxLookahead + 3];
        minIndices = new long[maxLookahead + 3];
        box = new double[maxLookahead + 2];
        Configure(Math.Min(lookahead, maxLookahead), truePeak);
        Reset();
    }

    /// <summary>
    ///     Sets the lookahead (samples, at least 0) and the detector; a change clears the history, since it moves the
    ///     delay.
    /// </summary>
    public void Configure(int lookaheadSamples, bool useTruePeak) {
        lookaheadSamples = Math.Clamp(lookaheadSamples, 0, box.Length - 2);
        if (lookaheadSamples == lookahead && useTruePeak == truePeak && window == lookahead + 1) {
            return;
        }

        lookahead = lookaheadSamples;
        truePeak = useTruePeak;
        window = lookahead + 1;
        Reset();
    }

    /// <summary>The release time (s) at <paramref name="sampleRate" />; the adaptive limiter's slow stage is 10× it.</summary>
    public void SetRelease(double seconds, double sampleRate) {
        release = Math.Exp(-1 / (Math.Max(seconds, 1e-4) * sampleRate));
        fastRelease = release;
        slowRelease = Math.Exp(-1 / (Math.Max(seconds, 1e-4) * 10 * sampleRate));
    }

    public void Reset() {
        Array.Clear(delayLeft);
        Array.Clear(delayRight);
        Array.Clear(history[0]);
        Array.Clear(history[1]);
        Array.Fill(box, 1.0);
        boxSum = window;
        boxAt = 0;
        minHead = 0;
        minCount = 0;
        delayWrite = 0;
        historyAt = 0;
        index = 0;
        average = 1;
        previous = 1;
        Gain = 1;
    }

    /// <summary>One stereo sample in, the delayed and limited one out, under <paramref name="ceiling" /> (a gain).</summary>
    public void Tick(ref float left, ref float right, double ceiling) {
        delayWrite = (delayWrite + 1) & delayMask;
        delayLeft[delayWrite] = left;
        delayRight[delayWrite] = right;
        double peak;
        if (truePeak) {
            history[0][historyAt] = left;
            history[1][historyAt] = right;
            historyAt = (historyAt + 1) % TruePeak.Taps;
            peak = IntervalPeak(history[0]);
            peak = Math.Max(peak, IntervalPeak(history[1]));
        } else {
            peak = Math.Max(Math.Abs(left), Math.Abs(right));
        }

        var target = Target(peak, ceiling * MarginGain);
        var held = HoldMinimum(target);
        double recovered;
        if (Adaptive) {
            average = held + (average - held) * slowRelease;
            var goal = Math.Max(average, held);
            recovered = previous < goal
                ? goal - (goal - previous) * fastRelease
                : 1 - (1 - previous) * slowRelease;
        } else {
            recovered = 1 - (1 - previous) * release;
        }

        var r = Math.Min(held, recovered);
        previous = r;
        boxSum += r - box[boxAt];
        box[boxAt] = r;
        boxAt = boxAt + 1 == window ? 0 : boxAt + 1;
        var gain = Math.Min(1, boxSum / window);
        Gain = gain;
        var at = (delayWrite - Latency) & delayMask;
        var limit = (float)ceiling;
        left = Math.Clamp((float)(delayLeft[at] * gain), -limit, limit);
        right = Math.Clamp((float)(delayRight[at] * gain), -limit, limit);
    }

    static readonly double MarginGain = Math.Pow(10, -MarginDb / 20);

    double Target(double peak, double ceiling) {
        if (!Adaptive) {
            return peak > ceiling ? ceiling / peak : 1;
        }

        // The soft knee: from KneeDb below the ceiling the output approaches it as c − w + w (1 − e^(−u/w)).
        var knee = ceiling * KneeGain;
        if (peak <= knee) {
            return 1;
        }

        var u = 20 * Math.Log10(peak / knee);
        var output = KneeDb * (1 - Math.Exp(-u / KneeDb));
        return Math.Pow(10, (output - u) / 20);
    }

    static readonly double KneeGain = Math.Pow(10, -KneeDb / 20);

    /// <summary>The peak of the interval between the samples <see cref="TruePeak.Delay" /> − 1 and <see cref="TruePeak.Delay" /> ago, ends included.</summary>
    double IntervalPeak(float[] samples) {
        // historyAt is the oldest sample: tap j reads the sample at c + j − (Delay − 1) for the interval (c, c + 1).
        var start = historyAt;
        var c = samples[(start + TruePeak.Delay - 1) % TruePeak.Taps];
        var next = samples[(start + TruePeak.Delay) % TruePeak.Taps];
        double peak = Math.Max(Math.Abs(c), Math.Abs(next));
        var kernels = TruePeak.Kernels;
        for (var p = 0; p < 3; p++) {
            var taps = kernels[p];
            double sum = 0;
            for (var j = 0; j < TruePeak.Taps; j++) {
                var k = start + j;
                sum += taps[j] * samples[k >= TruePeak.Taps ? k - TruePeak.Taps : k];
            }

            peak = Math.Max(peak, Math.Abs(sum));
        }

        return peak;
    }

    /// <summary>The minimum of the last <c>window + 1</c> targets (a monotonic queue).</summary>
    double HoldMinimum(double value) {
        var capacity = minValues.Length;
        while (minCount > 0 && minValues[(minHead + minCount - 1) % capacity] >= value) {
            minCount--;
        }

        var tail = (minHead + minCount) % capacity;
        minValues[tail] = value;
        minIndices[tail] = index;
        minCount++;
        while (minIndices[minHead] < index - window) {
            minHead = (minHead + 1) % capacity;
            minCount--;
        }

        index++;
        return minValues[minHead];
    }
}
