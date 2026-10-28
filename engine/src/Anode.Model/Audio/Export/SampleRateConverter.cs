namespace Anode.Model.Audio;

/// <summary>
///     A band-limited resampler for whole files: a Kaiser-windowed sinc evaluated at the exact rational position of
///     every output sample (polyphase, so 44.1 kHz, 48 kHz, 88.2 kHz, 96 kHz and any other pair convert exactly). The
///     cutoff sits at 95.5 % of the lower Nyquist frequency, the passband reaches 91 % of it (20 kHz at 44.1 kHz) with
///     a ripple of a few millionths, and everything from the lower Nyquist frequency up is attenuated by 110 dB.
/// </summary>
/// <remarks>
///     Output sample <c>n</c> sits at input time <c>n · from / to</c>, so the first samples line up and the output is
///     not delayed. The output has <c>round(frames · to / from)</c> frames (halves round up); the signal is taken to be
///     silent outside the input.
/// </remarks>
public static class SampleRateConverter {
    const double StopbandDb = 110;
    const double CutoffFraction = 0.955;
    const double TransitionFraction = 0.09;

    /// <summary>
    ///     Above this many phases the kernel table is interpolated linearly instead of holding every phase.
    /// </summary>
    const int MaxPhases = 4096;

    /// <summary>
    ///     <paramref name="channels" /> (planar, at <paramref name="fromRate" />) at <paramref name="toRate" />; at the
    ///     same rate, copies.
    /// </summary>
    public static float[][] Convert(float[][] channels, int fromRate, int toRate) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fromRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(toRate);
        var result = new float[channels.Length][];
        if (fromRate == toRate) {
            for (var c = 0; c < channels.Length; c++) {
                result[c] = (float[])channels[c].Clone();
            }

            return result;
        }

        var filter = new Filter(fromRate, toRate);
        for (var c = 0; c < channels.Length; c++) {
            result[c] = filter.Run(channels[c]);
        }

        return result;
    }

    /// <summary>
    ///     The output length for <paramref name="frames" /> input frames: <c>round(frames · to / from)</c>.
    /// </summary>
    public static long OutputFrames(long frames, int fromRate, int toRate) =>
        (frames * toRate + fromRate / 2) / fromRate;

    /// <summary>The kernel table for one rate pair: one row per phase, plus a closing row for interpolation.</summary>
    sealed class Filter {
        readonly int fromRate;
        readonly int toRate;
        readonly long up;
        readonly long down;
        readonly int phases;
        readonly int half;
        readonly double[][] rows;

        public Filter(int fromRate, int toRate) {
            this.fromRate = fromRate;
            this.toRate = toRate;
            var divisor = Gcd(fromRate, toRate);
            up = toRate / divisor;
            down = fromRate / divisor;
            phases = (int)Math.Min(up, MaxPhases);
            var nyquist = Math.Min(fromRate, toRate) / 2.0;
            var cutoff = CutoffFraction * nyquist / fromRate;
            var transition = TransitionFraction * nyquist / fromRate;
            var beta = WindowedSinc.Beta(StopbandDb);
            half = WindowedSinc.HalfLength(StopbandDb, transition);
            rows = new double[phases + 1][];
            for (var p = 0; p <= phases; p++) {
                var row = new double[2 * half];
                var fraction = (double)p / phases;
                for (var j = 0; j < row.Length; j++) {
                    row[j] = WindowedSinc.Kernel(j - half + 1 - fraction, cutoff, half, beta);
                }

                rows[p] = row;
            }
        }

        public float[] Run(float[] input) {
            var output = new float[OutputFrames(input.Length, fromRate, toRate)];
            var padded = new float[input.Length + 2 * half + 2];
            input.CopyTo(padded, half);
            for (var n = 0L; n < output.Length; n++) {
                var position = n * down;
                var whole = position / up;
                var phase = (double)(position % up) * phases / up;
                var row = (int)phase;
                var weight = phase - row;

                // Input sample whole + j - half + 1 sits at padded index whole + j + 1.
                var start = (int)whole + 1;
                var value = Dot(padded, start, rows[row]);
                if (weight > 0) {
                    value += weight * (Dot(padded, start, rows[row + 1]) - value);
                }

                output[n] = (float)value;
            }

            return output;
        }

        static double Dot(float[] samples, int start, double[] row) {
            var sum = 0.0;
            var span = samples.AsSpan(start, row.Length);
            for (var j = 0; j < row.Length; j++) {
                sum += span[j] * row[j];
            }

            return sum;
        }

        static int Gcd(int a, int b) {
            while (b != 0) {
                (a, b) = (b, a % b);
            }

            return a;
        }
    }
}
