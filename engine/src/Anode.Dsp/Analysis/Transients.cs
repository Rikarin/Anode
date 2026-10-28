namespace Anode.Dsp.Analysis;

/// <summary>
///     Transient detection: spectral flux with an adaptive threshold. Deterministic, and about 50× real time or better
///     on one core.
/// </summary>
/// <remarks>
///     The flux (<see cref="OnsetEnvelope" />, 5 ms hop) is peak-picked against a threshold of λ times its local mean
///     (100 ms before, 50 ms after) plus δ times its maximum, λ and δ falling as the sensitivity rises. Each peak is then
///     placed to the sample in the time domain: within the peak's STFT frame, the onset is where the mean power of the
///     next 2 ms most exceeds that of the previous 10 ms (long enough that a low note's zero crossings do not look like
///     onsets). A peak whose next 20 ms are under half as loud as its previous 20 ms is a note's release, not an onset.
///     Onsets closer than 20 ms keep the first.
/// </remarks>
public static class Transients {
    const double MinGapSeconds = 0.02;
    const double RiseSeconds = 0.002;
    const double BeforeSeconds = 0.01;

    /// <summary>The frames of the onsets in <paramref name="mono" />, ascending; <paramref name="sensitivity" /> 0…1.</summary>
    public static long[] Detect(ReadOnlySpan<float> mono, int sampleRate, double sensitivity = 0.5) =>
        Detect(mono, sampleRate, OnsetEnvelope.Compute(mono, sampleRate), sensitivity);

    internal static long[] Detect(
        ReadOnlySpan<float> mono,
        int sampleRate,
        OnsetEnvelope envelope,
        double sensitivity
    ) {
        var flux = envelope.Flux;
        var frames = flux.Length;
        if (frames == 0) {
            return [];
        }

        var s = Math.Clamp(sensitivity, 0, 1);
        var lambda = 1.25 + 2.5 * (1 - s);
        var delta = 0.005 + 0.15 * (1 - s) * (1 - s);
        var hop = envelope.Hop;
        var before = Math.Max(1, (int)Math.Round(0.1 * sampleRate / hop));
        var after = Math.Max(1, (int)Math.Round(0.05 * sampleRate / hop));
        var peakRadius = Math.Max(1, (int)Math.Round(0.015 * sampleRate / hop));

        var prefix = new double[frames + 1];
        double max = 0;
        for (var k = 0; k < frames; k++) {
            prefix[k + 1] = prefix[k] + flux[k];
            max = Math.Max(max, flux[k]);
        }

        if (max <= 1e-6) {
            return [];
        }

        var onsets = new List<long>();
        var minGap = (long)Math.Round(MinGapSeconds * sampleRate);
        for (var k = 0; k < frames; k++) {
            var value = flux[k];
            var from = Math.Max(0, k - before);
            var to = Math.Min(frames, k + after + 1);
            var mean = (prefix[to] - prefix[from]) / (to - from);
            if (value <= lambda * mean + delta * max || !IsPeak(flux, k, peakRadius)) {
                continue;
            }

            var onset = Refine(mono, (long)k * hop, envelope.Size, hop, sampleRate);
            if ((onsets.Count > 0 && onset - onsets[^1] < minGap) || IsFalling(mono, onset, sampleRate)) {
                continue;
            }

            onsets.Add(onset);
        }

        return [..onsets];
    }

    /// <summary>Whether the 20 ms after <paramref name="onset" /> are under half as loud as the 20 ms before it.</summary>
    static bool IsFalling(ReadOnlySpan<float> mono, long onset, int sampleRate) {
        var span = Math.Max(1, (int)Math.Round(0.02 * sampleRate));
        double before = 0, after = 0;
        for (var i = 1; i <= span; i++) {
            var n = onset - i;
            if (n >= 0 && n < mono.Length) {
                before += (double)mono[(int)n] * mono[(int)n];
            }

            n = onset + i - 1;
            if (n >= 0 && n < mono.Length) {
                after += (double)mono[(int)n] * mono[(int)n];
            }
        }

        return after < 0.5 * before;
    }

    /// <summary>Whether <c>flux[k]</c> is the largest in its neighbourhood (the first of equal values).</summary>
    static bool IsPeak(float[] flux, int k, int radius) {
        var from = Math.Max(0, k - radius);
        var to = Math.Min(flux.Length - 1, k + radius);
        for (var j = from; j <= to; j++) {
            if (flux[j] > flux[k] || (j < k && flux[j] == flux[k])) {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     The onset near STFT frame centre <paramref name="centre" />: the frame where the mean power of the next 2 ms most
    ///     exceeds that of the previous 10 ms, searched over the frame's window and one hop after it.
    /// </summary>
    static long Refine(ReadOnlySpan<float> mono, long centre, int size, int hop, int sampleRate) {
        var length = mono.Length;
        var w = Math.Max(1, (int)Math.Round(RiseSeconds * sampleRate));
        var b = Math.Max(1, (int)Math.Round(BeforeSeconds * sampleRate));
        var from = Math.Max(0, centre - size / 2);
        var to = Math.Min(length - 1, centre + size / 2 + hop);
        if (to < from) {
            return Math.Clamp(centre, 0, Math.Max(0, length - 1));
        }

        // energy[i] is the energy of frames origin … origin + i − 1, zero outside the file.
        var origin = from - b;
        var count = (int)(to + w - origin);
        var energy = new double[count + 1];
        for (var i = 0; i < count; i++) {
            var n = origin + i;
            var x = n >= 0 && n < length ? mono[(int)n] : 0f;
            energy[i + 1] = energy[i] + (double)x * x;
        }

        double loudest = 0;
        for (var t = from; t <= to; t++) {
            var i = (int)(t - origin);
            loudest = Math.Max(loudest, (energy[i + w] - energy[i]) / w);
        }

        var floor = 1e-6 * loudest + 1e-20;
        var best = from;
        var bestScore = double.NegativeInfinity;
        for (var t = from; t <= to; t++) {
            var i = (int)(t - origin);
            var score = (energy[i + w] - energy[i]) / w / ((energy[i] - energy[i - b]) / b + floor);
            if (score > bestScore) {
                bestScore = score;
                best = t;
            }
        }

        return best;
    }
}
