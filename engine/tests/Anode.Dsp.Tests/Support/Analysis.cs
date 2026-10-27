using System.Numerics;

namespace Anode.Dsp.Tests.Support;

/// <summary>Measurements for the instrument tests (after Cathode.Testing's analysers).</summary>
public static class Analysis {
    public static double ToDb(double gain) => 20 * Math.Log10(Math.Max(gain, 1e-15));

    public static double Rms(ReadOnlySpan<float> signal) {
        if (signal.IsEmpty) {
            return 0;
        }

        double sum = 0;
        foreach (var s in signal) {
            sum += (double)s * s;
        }

        return Math.Sqrt(sum / signal.Length);
    }

    public static double Peak(ReadOnlySpan<float> signal) {
        double peak = 0;
        foreach (var s in signal) {
            peak = Math.Max(peak, Math.Abs(s));
        }

        return peak;
    }

    /// <summary>RMS of <c>a − b</c> in dBFS; identical signals give −300 dB.</summary>
    public static double NullResidualDb(ReadOnlySpan<float> a, ReadOnlySpan<float> b) {
        Assert.Equal(a.Length, b.Length);
        double sum = 0;
        for (var i = 0; i < a.Length; i++) {
            var d = (double)a[i] - b[i];
            sum += d * d;
        }

        return sum == 0 ? -300 : ToDb(Math.Sqrt(sum / a.Length));
    }

    /// <summary>|H| at <paramref name="frequency" /> of an impulse response, evaluated exactly (DTFT).</summary>
    public static double MagnitudeAt(ReadOnlySpan<float> impulseResponse, double frequency, double sampleRate) {
        var w = -2 * Math.PI * frequency / sampleRate;
        var sum = Complex.Zero;
        for (var n = 0; n < impulseResponse.Length; n++) {
            sum += impulseResponse[n] * Complex.FromPolarCoordinates(1, w * n);
        }

        return sum.Magnitude;
    }

    /// <summary>
    ///     The Hann-windowed spectrum of <paramref name="length" /> (a power of two) samples from
    ///     <paramref name="start" />.
    /// </summary>
    public static Complex[] Spectrum(ReadOnlySpan<float> signal, int start, int length) {
        var data = new Complex[length];
        for (var i = 0; i < length; i++) {
            var w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / length);
            data[i] = signal[start + i] * w;
        }

        Fft.Forward(data);
        return data;
    }

    /// <summary>
    ///     The frequency of the strongest partial by FFT, refined with the phase advance between two Hann-windowed
    ///     frames a quarter frame apart (the phase-vocoder estimate): far finer than a bin for a steady tone.
    /// </summary>
    public static double PeakFrequency(
        ReadOnlySpan<float> signal,
        double sampleRate,
        int start = 0,
        int length = 32_768
    ) {
        var hop = length / 4;
        var a = Spectrum(signal, start, length);
        var b = Spectrum(signal, start + hop, length);
        var peak = 1;
        for (var k = 2; k < length / 2; k++) {
            if (a[k].Magnitude > a[peak].Magnitude) {
                peak = k;
            }
        }

        var expected = 2 * Math.PI * peak * hop / length;
        var deviation = b[peak].Phase - a[peak].Phase - expected;
        deviation -= 2 * Math.PI * Math.Round(deviation / (2 * Math.PI));
        var bin = peak + deviation * length / (2 * Math.PI * hop);
        return bin * sampleRate / length;
    }

    public static double Cents(double frequency, double reference) => 1_200 * Math.Log2(frequency / reference);

    public static double NoteFrequency(double note) => 440 * Math.Pow(2, (note - 69) / 12);

    /// <summary>The mean frequency over a window from its rising zero crossings (linearly interpolated).</summary>
    public static double ZeroCrossingFrequency(ReadOnlySpan<float> signal, int start, int length, double sampleRate) {
        double first = -1, last = -1;
        var crossings = 0;
        for (var i = start + 1; i < start + length; i++) {
            if (signal[i - 1] < 0 && signal[i] >= 0) {
                var t = i - 1 + signal[i - 1] / (double)(signal[i - 1] - signal[i]);
                if (first < 0) {
                    first = t;
                } else {
                    crossings++;
                }

                last = t;
            }
        }

        return crossings == 0 ? 0 : crossings * sampleRate / (last - first);
    }

    /// <summary>The first index at or after <paramref name="start" /> whose magnitude exceeds <paramref name="threshold" />.</summary>
    public static int FirstAbove(ReadOnlySpan<float> signal, float threshold, int start = 0) {
        for (var i = start; i < signal.Length; i++) {
            if (Math.Abs(signal[i]) > threshold) {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>Double-precision radix-2 FFT, for analysis accuracy rather than speed.</summary>
public static class Fft {
    public static void Forward(Complex[] data) {
        var n = data.Length;
        if (n == 0 || (n & (n - 1)) != 0) {
            throw new ArgumentException("Length must be a power of two.", nameof(data));
        }

        for (int i = 1, j = 0; i < n; i++) {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) {
                j ^= bit;
            }

            j ^= bit;
            if (i < j) {
                (data[i], data[j]) = (data[j], data[i]);
            }
        }

        for (var length = 2; length <= n; length <<= 1) {
            var angle = -2 * Math.PI / length;
            for (var i = 0; i < n; i += length) {
                for (var k = 0; k < length / 2; k++) {
                    var w = Complex.FromPolarCoordinates(1, angle * k);
                    var u = data[i + k];
                    var v = data[i + k + length / 2] * w;
                    data[i + k] = u + v;
                    data[i + k + length / 2] = u - v;
                }
            }
        }
    }
}

/// <summary>Measures managed allocations made by the current thread (as Cathode.Testing's probe does).</summary>
public static class AllocationProbe {
    /// <summary>
    ///     Runs <paramref name="action" /> once to warm up, then returns the least of three measurements of the bytes
    ///     it allocates: a one-off runtime allocation shows in one, a real one in all three.
    /// </summary>
    public static long Measure(Action action) {
        action();
        var least = long.MaxValue;
        for (var i = 0; i < 3 && least > 0; i++) {
            var before = GC.GetAllocatedBytesForCurrentThread();
            action();
            least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        return least;
    }
}
