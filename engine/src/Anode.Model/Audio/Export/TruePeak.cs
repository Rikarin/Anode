namespace Anode.Model.Audio;

/// <summary>
///     True peak after ITU-R BS.1770-4 Annex 2 and EBU Tech 3341: the signal is oversampled four times and the largest
///     magnitude over every channel is the true peak. Instead of the Annex's 48-tap interpolator this uses a 128-tap
///     Kaiser-windowed sinc (32 taps per phase, cutoff at the input's Nyquist frequency, about 80 dB of stopband),
///     which reads closer to the continuous signal; the original samples are part of the oversampled signal, so the
///     true peak is never below the sample peak.
/// </summary>
/// <remarks>
///     Points between samples are interpolated only where the whole kernel lies inside the file (from the 16th frame
///     to the 16th-last), so a file that starts or ends abruptly does not read the ringing of an imagined edge to
///     silence (up to 0.7 dB on Tech 3341's high-frequency sines); the samples themselves are measured everywhere.
/// </remarks>
public static class TruePeak {
    public const int Oversampling = 4;

    /// <summary>Input samples on each side of the interpolated point.</summary>
    const int Half = 16;

    const double StopbandDb = 80;

    /// <summary>The kernel for each interpolated phase (1/4, 2/4, 3/4 of a sample), <c>2 · Half</c> taps.</summary>
    static readonly float[][] Phases = BuildPhases();

    /// <summary>The true peak, linear (1 is full scale).</summary>
    public static double Measure(float[][] channels) {
        var peak = 0.0;
        foreach (var channel in channels) {
            peak = Math.Max(peak, Scan(channel, null));
        }

        return peak;
    }

    /// <summary>The true peak in dBTP; negative infinity for silence.</summary>
    public static double MeasureDb(float[][] channels) {
        var peak = Measure(channels);
        return peak > 0 ? 20 * Math.Log10(peak) : double.NegativeInfinity;
    }

    /// <summary>
    ///     Raises <paramref name="envelope" />'s every frame to the largest true magnitude of any channel between that
    ///     frame and the next (the frame itself and its three interpolated points).
    /// </summary>
    internal static void Envelope(float[][] channels, float[] envelope) {
        foreach (var channel in channels) {
            Scan(channel, envelope);
        }
    }

    static double Scan(float[] samples, float[]? envelope) {
        var peak = 0.0;
        for (var i = 0; i < samples.Length; i++) {
            double local = Math.Abs(samples[i]);

            // The points between sample i and i + 1 use samples i - Half + 1 to i + Half.
            if (i >= Half - 1 && i + Half < samples.Length) {
                var window = samples.AsSpan(i - Half + 1, 2 * Half);
                foreach (var phase in Phases) {
                    local = Math.Max(local, Math.Abs(Dot(window, phase)));
                }
            }

            if (envelope is not null && local > envelope[i]) {
                envelope[i] = (float)local;
            }

            peak = Math.Max(peak, local);
        }

        return peak;
    }

    static double Dot(ReadOnlySpan<float> window, float[] kernel) {
        var sum = 0f;
        for (var j = 0; j < kernel.Length; j++) {
            sum += window[j] * kernel[j];
        }

        return sum;
    }

    static float[][] BuildPhases() {
        var beta = WindowedSinc.Beta(StopbandDb);
        var phases = new float[Oversampling - 1][];
        for (var k = 1; k < Oversampling; k++) {
            var row = new float[2 * Half];
            var fraction = (double)k / Oversampling;
            for (var j = 0; j < row.Length; j++) {
                row[j] = (float)WindowedSinc.Kernel(j - Half + 1 - fraction, 0.5, Half, beta);
            }

            phases[k - 1] = row;
        }

        return phases;
    }
}
