using Anode.Dsp.Metering;
using System.Numerics;

namespace Anode.Dsp.Analysis;

/// <summary>
///     The onset envelope transients and beats share: the spectral flux of a Hann-windowed STFT (about 21 ms frames,
///     5 ms hop), on log-compressed magnitudes, half-wave rectified, against the previous frame's spectrum widened by one
///     bin (SuperFlux's maximum filter, which keeps vibrato from reading as onsets). Frame <c>k</c> is centred on file
///     frame <c>k · Hop</c>. <see cref="Low" /> is the same flux below 200 Hz (kicks, for downbeats).
/// </summary>
sealed class OnsetEnvelope {
    // log(1 + γ·|X|), |X| scaled so a full-scale sine peaks at 1.
    const double Compression = 1_000;
    const double LowBandHz = 200;

    OnsetEnvelope(int hop, int size, float[] flux, float[] low) {
        Hop = hop;
        Size = size;
        Flux = flux;
        Low = low;
    }

    /// <summary>File frames between envelope frames.</summary>
    public int Hop { get; }

    /// <summary>The STFT frame length (a power of two).</summary>
    public int Size { get; }

    public float[] Flux { get; }
    public float[] Low { get; }

    /// <summary>The envelope's hop for <paramref name="sampleRate" />: 5 ms.</summary>
    public static int HopFor(int sampleRate) => Math.Max(1, (int)Math.Round(sampleRate / 200.0));

    /// <summary>The STFT frame length for <paramref name="sampleRate" />: the power of two at or above 20 ms.</summary>
    public static int SizeFor(int sampleRate) =>
        (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(64, sampleRate / 50));

    public static OnsetEnvelope Compute(ReadOnlySpan<float> mono, int sampleRate) {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        var hop = HopFor(sampleRate);
        var size = SizeFor(sampleRate);
        var half = size / 2;
        var frames = mono.Length == 0 ? 0 : mono.Length / hop + 1;
        var flux = new float[frames];
        var low = new float[frames];
        if (frames == 0) {
            return new(hop, size, flux, low);
        }

        var window = new double[size];
        double windowSum = 0;
        for (var i = 0; i < size; i++) {
            window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / size);
            windowSum += window[i];
        }

        var gain = Compression * 2 / windowSum;
        var lowBins = Math.Clamp((int)Math.Ceiling(LowBandHz * size / sampleRate), 1, half);
        var re = new double[size];
        var im = new double[size];
        var previous = new double[half + 1];
        var current = new double[half + 1];
        var pending = new double[half + 1];

        // Two real frames per complex FFT: frame k in the real part, k + 1 in the imaginary part.
        for (var k = 0; k < frames; k += 2) {
            Fill(mono, k, hop, size, window, re);
            if (k + 1 < frames) {
                Fill(mono, k + 1, hop, size, window, im);
            } else {
                Array.Clear(im);
            }

            SplitFft.Forward(re, im);
            for (var m = 0; m <= half; m++) {
                var mirror = (size - m) & (size - 1);
                double zr = re[m], zi = im[m], cr = re[mirror], ci = im[mirror];
                double ar = 0.5 * (zr + cr), ai = 0.5 * (zi - ci);
                double br = 0.5 * (zi + ci), bi = 0.5 * (cr - zr);
                current[m] = Math.Log(1 + gain * Math.Sqrt(ar * ar + ai * ai));
                pending[m] = Math.Log(1 + gain * Math.Sqrt(br * br + bi * bi));
            }

            Flux1(k, current, previous, half, lowBins, flux, low);
            if (k + 1 < frames) {
                Flux1(k + 1, pending, current, half, lowBins, flux, low);
                (previous, pending) = (pending, previous);
            } else {
                (previous, current) = (current, previous);
            }
        }

        return new(hop, size, flux, low);
    }

    static void Fill(ReadOnlySpan<float> mono, int frame, int hop, int size, double[] window, double[] target) {
        var start = (long)frame * hop - size / 2;
        for (var i = 0; i < size; i++) {
            var n = start + i;
            target[i] = n >= 0 && n < mono.Length ? mono[(int)n] * window[i] : 0;
        }
    }

    static void Flux1(int k, double[] spectrum, double[] previous, int half, int lowBins, float[] flux, float[] low) {
        double sum = 0, lowSum = 0;
        for (var m = 0; m <= half; m++) {
            var reference = previous[m];
            if (m > 0 && previous[m - 1] > reference) {
                reference = previous[m - 1];
            }

            if (m < half && previous[m + 1] > reference) {
                reference = previous[m + 1];
            }

            var rise = spectrum[m] - reference;
            if (rise > 0) {
                sum += rise;
                if (m < lowBins) {
                    lowSum += rise;
                }
            }
        }

        flux[k] = (float)sum;
        low[k] = (float)lowSum;
    }
}
