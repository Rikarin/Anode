using System.Numerics;

namespace Anode.Spikes.Stretch;

/// <summary>Objective quality proxies. All take mono audio (channel 0) unless they say otherwise.</summary>
static class Analysis {
    // ---- Spectra ----

    /// <summary>In-place radix-2 complex FFT (length a power of two).</summary>
    public static void Fft(Complex[] x) {
        var n = x.Length;
        for (int i = 1, j = 0; i < n; i++) {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) {
                j ^= bit;
            }

            j ^= bit;
            if (i < j) {
                (x[i], x[j]) = (x[j], x[i]);
            }
        }

        for (var len = 2; len <= n; len <<= 1) {
            var angle = -2 * Math.PI / len;
            var wl = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (var i = 0; i < n; i += len) {
                var w = Complex.One;
                for (var k = 0; k < len / 2; k++) {
                    var u = x[i + k];
                    var v = x[i + k + len / 2] * w;
                    x[i + k] = u + v;
                    x[i + k + len / 2] = u - v;
                    w *= wl;
                }
            }
        }
    }

    /// <summary>Power spectrum (bins 0..fft/2) of a Hann-windowed segment, zero-padded to <paramref name="fftSize" />.</summary>
    public static double[] PowerSpectrum(float[] x, int start, int window, int fftSize) {
        var buffer = new Complex[fftSize];
        for (var i = 0; i < window; i++) {
            var index = start + i;
            var sample = index >= 0 && index < x.Length ? x[index] : 0;
            buffer[i] = sample * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / window));
        }

        Fft(buffer);
        var power = new double[fftSize / 2 + 1];
        for (var k = 0; k < power.Length; k++) {
            power[k] = buffer[k].Real * buffer[k].Real + buffer[k].Imaginary * buffer[k].Imaginary;
        }

        return power;
    }

    /// <summary>The strongest peak within ±<paramref name="cents" /> of <paramref name="expected" />, interpolated.</summary>
    public static double PeakNear(double[] power, int fftSize, int sampleRate, double expected, double cents) {
        var binHz = sampleRate / (double)fftSize;
        var lo = Math.Max(1, (int)(expected * Math.Pow(2, -cents / 1200) / binHz));
        var hi = Math.Min(power.Length - 2, (int)Math.Ceiling(expected * Math.Pow(2, cents / 1200) / binHz));
        var best = lo;
        for (var k = lo; k <= hi; k++) {
            if (power[k] > power[best]) {
                best = k;
            }
        }

        var a = Math.Log(power[best - 1] + 1e-30);
        var b = Math.Log(power[best] + 1e-30);
        var c = Math.Log(power[best + 1] + 1e-30);
        var denominator = a - 2 * b + c;
        var offset = denominator == 0 ? 0 : 0.5 * (a - c) / denominator;
        return (best + Math.Clamp(offset, -1, 1)) * binHz;
    }

    public static double Cents(double measured, double expected) => 1200 * Math.Log2(measured / expected);

    // ---- Pitch on steady tones ----

    /// <summary>
    ///     Pitch error (cents) of each tone, and the energy outside ±(15 Hz or 1 %) of every expected partial relative to the
    ///     total ("non-partial energy", dB; lower is cleaner). Measured on the middle of the output.
    /// </summary>
    public static (double[] ErrorCents, double NonPartialDb) Tones(
        float[] y,
        int sampleRate,
        double[] tones,
        double[] partials,
        double transpose
    ) {
        const int window = 1 << 16;
        const int fft = 1 << 18;
        var start = Math.Max(0, y.Length / 2 - window / 2);
        var power = PowerSpectrum(y, start, Math.Min(window, y.Length), fft);
        var errors = new double[tones.Length];
        for (var i = 0; i < tones.Length; i++) {
            var expected = tones[i] * transpose;
            errors[i] = Cents(PeakNear(power, fft, sampleRate, expected, 100), expected);
        }

        var binHz = sampleRate / (double)fft;
        double total = 0, inside = 0;
        for (var k = (int)(20 / binHz); k < power.Length; k++) {
            var f = k * binHz;
            total += power[k];
            foreach (var p in partials) {
                var expected = p * transpose;
                if (Math.Abs(f - expected) <= Math.Max(15, expected * 0.01)) {
                    inside += power[k];
                    break;
                }
            }
        }

        return (errors, 10 * Math.Log10(Math.Max(total - inside, 1e-30) / total));
    }

    // ---- Sweep tracking ----

    /// <summary>Pitch error (cents) along the sweep: frame by frame, against the input frequency at that content time.</summary>
    public static double[] SweepErrors(
        Rendered r,
        int sampleRate,
        (double From, double To, double Seconds) sweep,
        double transpose
    ) {
        const int window = 4096;
        const int fft = 1 << 15;
        var y = r.Audio[0];
        var errors = new List<double>();
        var k = Math.Log(sweep.To / sweep.From);
        for (var centre = window / 2; centre + window / 2 < y.Length; centre += sampleRate / 20) {
            var contentSeconds = r.Content[Math.Min(centre, r.Content.Length - 1)] / sampleRate;
            if (contentSeconds < 0.3 || contentSeconds > sweep.Seconds - 0.3) {
                continue;
            }

            var expected = sweep.From * Math.Exp(contentSeconds / sweep.Seconds * k) * transpose;
            if (expected < 30 || expected > sampleRate * 0.45) {
                continue;
            }

            var power = PowerSpectrum(y, centre - window / 2, window, fft);
            errors.Add(Cents(PeakNear(power, fft, sampleRate, expected, 300), expected));
        }

        return [..errors];
    }

    // ---- Transients ----

    /// <summary>Per-click smearing, against the same measure on the input click.</summary>
    public readonly record struct Transient(
        double Width90Ms,
        double PreEchoDb,
        double OnsetErrorMs,
        double PeakDropDb,
        double WidthChangeMs,
        double PreEchoChangeDb
    );

    /// <summary>
    ///     For each onset, within ±<paramref name="halfMs" /> around where it should sound: the shortest span holding 90 % of
    ///     the energy, the
    ///     energy that arrives more than 2 ms early (dB of the total), the time the energy reaches 10 % relative to the
    ///     expected time (input-referenced), and the peak level change.
    /// </summary>
    public static Transient[] Transients(
        float[][] input,
        Rendered output,
        long[] onsets,
        int sampleRate,
        double halfMs
    ) {
        var result = new Transient[onsets.Length];
        var half = (int)(sampleRate * halfMs / 1000);
        for (var i = 0; i < onsets.Length; i++) {
            var reference = Measure(input, onsets[i], half, sampleRate);
            var expected = output.OutputFrameOf(onsets[i]);
            var measured = Measure(output.Audio, expected, half, sampleRate);
            result[i] = new(
                measured.Width,
                measured.PreEcho,
                measured.Onset - reference.Onset,
                20 * Math.Log10(measured.Peak / reference.Peak),
                measured.Width - reference.Width,
                measured.PreEcho - reference.PreEcho
            );
        }

        return result;
    }

    static (double Width, double PreEcho, double Onset, double Peak) Measure(
        float[][] audio,
        double centre,
        int half,
        int sampleRate
    ) {
        var start = (int)Math.Round(centre) - half;
        var energy = new double[2 * half];
        var peak = 1e-12;
        for (var i = 0; i < energy.Length; i++) {
            var index = start + i;
            if (index < 0 || index >= audio[0].Length) {
                continue;
            }

            foreach (var channel in audio) {
                energy[i] += channel[index] * (double)channel[index];
                peak = Math.Max(peak, Math.Abs(channel[index]));
            }
        }

        var cumulative = new double[energy.Length + 1];
        for (var i = 0; i < energy.Length; i++) {
            cumulative[i + 1] = cumulative[i] + energy[i];
        }

        var total = Math.Max(cumulative[^1], 1e-30);
        var width = energy.Length;
        var hi = 0;
        for (var lo = 0; lo < energy.Length; lo++) {
            while (hi < energy.Length && cumulative[hi] - cumulative[lo] < 0.9 * total) {
                hi++;
            }

            if (cumulative[hi] - cumulative[lo] >= 0.9 * total) {
                width = Math.Min(width, hi - lo);
            }
        }

        var early = cumulative[half - sampleRate / 500] / total;
        var onset = 0;
        while (onset < energy.Length && cumulative[onset] < 0.1 * total) {
            onset++;
        }

        var ms = 1000.0 / sampleRate;
        return (width * ms, 10 * Math.Log10(Math.Max(early, 1e-12)),
            (onset - half - (centre - Math.Round(centre))) * ms, peak);
    }

    // ---- Level and spectral balance ----

    public static double RmsDb(float[][] audio) {
        double sum = 0;
        long n = 0;
        foreach (var channel in audio) {
            foreach (var v in channel) {
                sum += v * (double)v;
            }

            n += channel.Length;
        }

        return 10 * Math.Log10(Math.Max(sum / Math.Max(n, 1), 1e-30));
    }

    /// <summary>Long-term average power per third-octave band (dB), 50 Hz to 16 kHz, channel 0 (Welch, 8192 Hann).</summary>
    public static double[] ThirdOctaves(float[] x, int sampleRate) {
        const int window = 8192;
        var average = new double[window / 2 + 1];
        var frames = 0;
        for (var start = 0; start + window <= x.Length; start += window / 2, frames++) {
            var power = PowerSpectrum(x, start, window, window);
            for (var k = 0; k < average.Length; k++) {
                average[k] += power[k];
            }
        }

        var bands = new List<double>();
        var binHz = sampleRate / (double)window;
        for (var b = -13; b <= 12; b++) {
            var centre = 1000 * Math.Pow(2, b / 3.0);
            double sum = 0;
            for (var k = (int)Math.Ceiling(centre * Math.Pow(2, -1 / 6.0) / binHz);
                 k * binHz < centre * Math.Pow(2, 1 / 6.0);
                 k++) {
                sum += average[k];
            }

            bands.Add(10 * Math.Log10(sum / Math.Max(frames, 1) + 1e-30));
        }

        return [..bands];
    }

    /// <summary>
    ///     Spectral-balance change: the mean absolute third-octave difference (dB) after removing the overall level
    ///     change, over the bands within 50 dB of the input's loudest.
    /// </summary>
    public static double SpectralDeviationDb(float[] input, float[] output, int sampleRate) {
        var a = ThirdOctaves(input, sampleRate);
        var b = ThirdOctaves(output, sampleRate);
        var max = a.Max();
        var used = Enumerable.Range(0, a.Length).Where(i => a[i] > max - 50).ToArray();
        var offset = used.Average(i => b[i] - a[i]);
        return used.Average(i => Math.Abs(b[i] - a[i] - offset));
    }

    // ---- Formants ----

    /// <summary>
    ///     The cepstrally smoothed long-term spectral envelope (dB, mean removed) on a 1/24-octave grid from 200 Hz to
    ///     5 kHz.
    /// </summary>
    public static double[] Envelope(float[] x, int sampleRate) {
        const int window = 4096;
        var average = new double[window / 2 + 1];
        for (var start = 0; start + window <= x.Length; start += window / 2) {
            var power = PowerSpectrum(x, start, window, window);
            for (var k = 0; k < average.Length; k++) {
                average[k] += power[k];
            }
        }

        var peak = average.Max();
        var cepstrum = new Complex[window];
        for (var k = 0; k < window; k++) {
            var p = average[k <= window / 2 ? k : window - k];
            cepstrum[k] = Math.Log(Math.Max(p, peak * 1e-8));
        }

        Fft(cepstrum);
        const int lifter = 40; // keeps detail coarser than ~1.2 kHz: removes harmonics up to an f0 of ~1 kHz
        for (var q = 0; q < window; q++) {
            var keep = q is <= lifter or >= window - lifter;
            cepstrum[q] = keep ? cepstrum[q] / window : Complex.Zero;
        }

        Fft(cepstrum); // a real, even sequence: the inverse is the forward transform
        var grid = new List<double>();
        for (var f = 200.0; f <= 5000; f *= Math.Pow(2, 1 / 24.0)) {
            var bin = f / sampleRate * window;
            var k = (int)bin;
            var t = bin - k;
            grid.Add((cepstrum[k].Real * (1 - t) + cepstrum[k + 1].Real * t) * 10 / Math.Log(10));
        }

        var mean = grid.Average();
        return [..grid.Select(v => v - mean)];
    }

    /// <summary>
    ///     The shift (semitones) that best aligns the output's envelope with the input's (0 = formants preserved), and the
    ///     mean absolute envelope difference (dB) without shifting.
    /// </summary>
    public static (double ShiftSemitones, double DeviationDb) FormantShift(
        float[] input,
        float[] output,
        int sampleRate
    ) {
        var a = Envelope(input, sampleRate);
        var b = Envelope(output, sampleRate);
        var bestShift = 0.0;
        var bestError = double.MaxValue;
        for (var offset = -30; offset <= 30; offset++) { // grid steps: two per semitone, ±15 semitones
            double error = 0;
            var count = 0;
            for (var i = 0; i < a.Length; i++) {
                var j = i + offset;
                if (j >= 0 && j < b.Length) {
                    error += (b[j] - a[i]) * (b[j] - a[i]);
                    count++;
                }
            }

            if (count > a.Length / 2 && error / count < bestError) {
                bestError = error / count;
                bestShift = offset / 2.0;
            }
        }

        var deviation = a.Zip(b, static (u, v) => Math.Abs(u - v)).Average();
        return (bestShift, deviation);
    }

    // ---- Monophonic pitch (YIN) ----

    /// <summary>YIN f0 (Hz) of a 1024-frame window starting at <paramref name="start" />, or NaN when unvoiced.</summary>
    public static double Yin(float[] x, int start, int sampleRate) {
        const int window = 1024;
        var tauMin = sampleRate / 1000;
        var tauMax = sampleRate / 70;
        if (start < 0 || start + window + tauMax >= x.Length) {
            return double.NaN;
        }

        var d = new double[tauMax + 1];
        for (var tau = 1; tau <= tauMax; tau++) {
            double sum = 0;
            for (var i = 0; i < window; i++) {
                var diff = x[start + i] - x[start + i + tau];
                sum += diff * diff;
            }

            d[tau] = sum;
        }

        double running = 0;
        var cmnd = new double[tauMax + 1];
        cmnd[0] = 1;
        for (var tau = 1; tau <= tauMax; tau++) {
            running += d[tau];
            cmnd[tau] = running > 0 ? d[tau] * tau / running : 1;
        }

        var chosen = -1;
        for (var tau = tauMin; tau < tauMax; tau++) {
            if (cmnd[tau] < 0.15) {
                while (tau + 1 < tauMax && cmnd[tau + 1] < cmnd[tau]) {
                    tau++;
                }

                chosen = tau;
                break;
            }
        }

        if (chosen < 0) {
            return double.NaN;
        }

        var a = cmnd[chosen - 1];
        var b = cmnd[chosen];
        var c = cmnd[chosen + 1];
        var denominator = a - 2 * b + c;
        var offset = denominator == 0 ? 0 : 0.5 * (a - c) / denominator;
        return sampleRate / (chosen + offset);
    }

    /// <summary>
    ///     f0 error (cents) every 10 ms where the input is steadily voiced, against the known input f0 at that content time
    ///     times the transposition.
    /// </summary>
    public static double[] F0Errors(Rendered r, int sampleRate, Func<double, double> f0, double transpose) {
        var errors = new List<double>();
        var y = r.Audio[0];
        for (var centre = 512; centre < y.Length - 2048; centre += sampleRate / 100) {
            var content = r.Content[Math.Min(centre, r.Content.Length - 1)] / sampleRate;
            var expected = f0(content) * transpose;
            if (double.IsNaN(expected)) {
                continue;
            }

            var measured = Yin(y, centre - 512, sampleRate);
            errors.Add(double.IsNaN(measured) ? 1200 : Cents(measured, expected));
        }

        return [..errors];
    }

    // ---- Statistics ----

    public static double Percentile(IEnumerable<double> values, double p) {
        var sorted = values.OrderBy(static v => v).ToArray();
        if (sorted.Length == 0) {
            return double.NaN;
        }

        var index = Math.Clamp((int)Math.Ceiling(p / 100 * sorted.Length) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }

    public static double MedianAbs(IEnumerable<double> values) => Percentile(values.Select(Math.Abs), 50);
}
