namespace Anode.Dsp.Metering;

/// <summary>
///     The analyzer behind an EQ's display: the audio thread writes the mono sum into a ring of the last
///     <see cref="Size" /> samples; the control thread reads it as levels in log-spaced bands, from a Hann-windowed FFT.
///     Reading while the audio thread writes can tear a frame, which a display does not mind; one reader at a time.
///     Allocation-free after construction.
/// </summary>
public sealed class SpectrumAnalyzer {
    public const int Size = 4096;
    const int Mask = Size - 1;

    readonly float[] ring = new float[Size];
    readonly double[] real = new double[Size];
    readonly double[] imaginary = new double[Size];
    readonly double[] window = new double[Size];
    int write;

    public SpectrumAnalyzer() {
        for (var i = 0; i < Size; i++) {
            window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / Size);
        }
    }

    public void Reset() {
        Array.Clear(ring);
        write = 0;
    }

    /// <summary>Audio thread: appends the mean of <paramref name="left" /> and <paramref name="right" />.</summary>
    public void Write(ReadOnlySpan<float> left, ReadOnlySpan<float> right) {
        var at = Volatile.Read(ref write);
        for (var i = 0; i < left.Length; i++) {
            ring[at] = 0.5f * (left[i] + right[i]);
            at = (at + 1) & Mask;
        }

        Volatile.Write(ref write, at);
    }

    /// <summary>
    ///     Control thread: fills <paramref name="bandsDb" /> with the peak level (dBFS of a full-scale sine) of
    ///     <c>bandsDb.Length</c> bands spaced evenly in log frequency from <paramref name="low" /> to
    ///     <paramref name="high" /> Hz.
    /// </summary>
    public void Read(Span<float> bandsDb, float sampleRate, float low = 20, float high = 20_000) {
        var start = Volatile.Read(ref write);
        for (var i = 0; i < Size; i++) {
            real[i] = ring[(start + i) & Mask] * window[i];
            imaginary[i] = 0;
        }

        SplitFft.Forward(real, imaginary);
        // A full-scale sine peaks at Size/4 in a Hann-windowed spectrum (Size/2 × the window's mean of 1/2).
        const double scale = 4.0 / Size;
        var ratio = Math.Log(high / low);
        for (var b = 0; b < bandsDb.Length; b++) {
            var from = low * Math.Exp(ratio * b / bandsDb.Length);
            var to = low * Math.Exp(ratio * (b + 1) / bandsDb.Length);
            var first = Math.Clamp((int)Math.Floor(from * Size / sampleRate), 1, Size / 2 - 1);
            var last = Math.Clamp((int)Math.Ceiling(to * Size / sampleRate), first, Size / 2 - 1);
            double peak = 0;
            for (var k = first; k <= last; k++) {
                peak = Math.Max(peak, real[k] * real[k] + imaginary[k] * imaginary[k]);
            }

            bandsDb[b] = Effects.Decibels.FromGain(Math.Sqrt(peak) * scale);
        }
    }
}

/// <summary>An in-place radix-2 complex FFT on split real and imaginary arrays (a power-of-two length).</summary>
public static class SplitFft {
    public static void Forward(Span<double> real, Span<double> imaginary) {
        var n = real.Length;
        for (int i = 1, j = 0; i < n; i++) {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) {
                j ^= bit;
            }

            j ^= bit;
            if (i < j) {
                (real[i], real[j]) = (real[j], real[i]);
                (imaginary[i], imaginary[j]) = (imaginary[j], imaginary[i]);
            }
        }

        for (var length = 2; length <= n; length <<= 1) {
            var angle = -2 * Math.PI / length;
            var stepRe = Math.Cos(angle);
            var stepIm = Math.Sin(angle);
            var half = length / 2;
            for (var i = 0; i < n; i += length) {
                double wRe = 1, wIm = 0;
                for (var k = 0; k < half; k++) {
                    var a = i + k;
                    var b = a + half;
                    var vRe = real[b] * wRe - imaginary[b] * wIm;
                    var vIm = real[b] * wIm + imaginary[b] * wRe;
                    real[b] = real[a] - vRe;
                    imaginary[b] = imaginary[a] - vIm;
                    real[a] += vRe;
                    imaginary[a] += vIm;
                    var next = wRe * stepRe - wIm * stepIm;
                    wIm = wRe * stepIm + wIm * stepRe;
                    wRe = next;
                }
            }
        }
    }
}
