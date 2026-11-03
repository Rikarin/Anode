using Anode.Dsp.Metering;
using System.Numerics;

namespace Anode.Dsp.Analysis.Chords;

/// <summary>
///     The harmonic chromagram audio Chord ID and key detection hear (ADR-0014): pitch-class energies per STFT frame,
///     with the bass register's apart.
/// </summary>
/// <remarks>
///     <list type="number">
///         <item>
///             An STFT of Hann frames of about 340 ms (16,384 at 48 kHz; the power of two at or above 0.32 s), hop a
///             quarter of that; magnitudes up to 4 kHz.
///         </item>
///         <item>
///             Percussive energy suppressed (Fitzgerald's median filtering): each magnitude kept by the soft mask
///             H² ÷ (H² + P²), H its median over 7 frames, P over 17 bins.
///         </item>
///         <item>
///             The tuning: the magnitude-weighted circular mean of the spectral peaks' (80 Hz–2 kHz, parabolically
///             interpolated) offsets from equal temperament, over the whole input.
///         </item>
///         <item>
///             Semitone bins E1…C7 on that tuning, each FFT bin shared between its two nearest semitones (linearly).
///         </item>
///         <item>
///             Overtones removed from the bottom up: a semitone's magnitude, times 0.5, 0.25, 0.125 and 0.06, comes off
///             its 2nd to 5th harmonics' semitones (an octave, a twelfth, two octaves, two octaves and a third).
///         </item>
///         <item>
///             The chroma: the semitones summed by pitch class. The bass: the lowest semitone up to C5 whose peak (the
///             largest bin in it, overtones removed alike) is at least 30 % of the largest one's there.
///         </item>
///     </list>
/// </remarks>
sealed class Chromagram {
    const int LowestPitch = 28;
    const int HighestPitch = 96;
    const int BassHighest = 72;
    const double BassShare = 0.3;
    const double MaxHz = 4_000;
    const int TimeMedian = 7;
    const int FrequencyMedian = 17;

    readonly double[][] chroma;
    readonly double[][] bass;
    readonly double loudest;

    Chromagram(int hop, double[][] chroma, double[][] bass, double tuningCents) {
        Hop = hop;
        this.chroma = chroma;
        this.bass = bass;
        TuningCents = tuningCents;
        loudest = chroma.Select(static c => c.Sum()).DefaultIfEmpty(0).Max();
    }

    /// <summary>Input frames between chroma frames; frame k is centred on input frame k · Hop.</summary>
    public int Hop { get; }

    /// <summary>The tuning found, in cents from A = 440 Hz (−50…50).</summary>
    public double TuningCents { get; }

    public int Count => chroma.Length;

    /// <summary>Every frame's chroma summed (key detection).</summary>
    public double[] Total() {
        var total = new double[12];
        foreach (var frame in chroma) {
            for (var pc = 0; pc < 12; pc++) {
                total[pc] += frame[pc];
            }
        }

        return total;
    }

    /// <summary>
    ///     The chroma of input frames <paramref name="start" />…<paramref name="end" />: the mean of the frames centred in
    ///     it (the nearest when none is), its bass (<see cref="BassOf" />), silent when under −60 dB of the loudest frame.
    /// </summary>
    public ChordFrame Frame(long start, long end) {
        // Frames centred at least a hop (a quarter window) after the start and half a window before the end, so the next
        // beat's attack does not leak in; else those centred inside.
        var first = (int)Math.Clamp((start + 2L * Hop - 1) / Hop, 0, Math.Max(0, Count - 1));
        var last = (int)Math.Clamp((end - 1 - 2L * Hop) / Hop, 0, Math.Max(0, Count - 1));
        if (last < first) {
            first = (int)Math.Clamp((start + Hop - 1) / Hop, 0, Math.Max(0, Count - 1));
            last = (int)Math.Clamp((end - 1) / Hop, first, Math.Max(0, Count - 1));
        }

        var c = new double[12];
        var b = new double[12];
        if (Count == 0) {
            return new(c, -1, true);
        }

        for (var k = first; k <= last; k++) {
            for (var pc = 0; pc < 12; pc++) {
                c[pc] += chroma[k][pc] / (last - first + 1);
                b[pc] += bass[k][pc] / (last - first + 1);
            }
        }

        var silent = c.Sum() <= loudest * 1e-3;
        // Broadband noise lifts every pitch class alike: its floor (the median class; a chord has five tones at most)
        // comes off, or it would favour the templates with more tones.
        Floor(c);
        Floor(b);
        return new(c, silent ? -1 : BassOf(b), silent);
    }

    static void Floor(double[] classes) {
        var sorted = (double[])classes.Clone();
        Array.Sort(sorted);
        var floor = 0.5 * (sorted[5] + sorted[6]);
        for (var pc = 0; pc < 12; pc++) {
            classes[pc] = Math.Max(0, classes[pc] - floor);
        }
    }

    /// <summary>The bass's pitch class: the strongest of the low register's chroma (−1 when it holds nothing).</summary>
    public static int BassOf(double[] bassChroma) {
        var best = 0;
        for (var pc = 1; pc < 12; pc++) {
            if (bassChroma[pc] > bassChroma[best]) {
                best = pc;
            }
        }

        return bassChroma[best] > 0 ? best : -1;
    }

    public static Chromagram Compute(ReadOnlySpan<float> mono, float sampleRate) {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1f);
        var size = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(256, sampleRate * 0.32));
        var hop = size / 4;
        var bins = Math.Min(size / 2, (int)(MaxHz * size / sampleRate) + 1);
        var frames = mono.Length == 0 ? 0 : (mono.Length - 1) / hop + 1;
        var magnitude = Spectrogram(mono, size, hop, frames, bins);
        Harmonic(magnitude, frames, bins);
        var tuning = Tuning(magnitude, frames, bins, sampleRate, size);

        // Each FFT bin's share of its two nearest semitones.
        var map = new (int Pitch, double Weight)[bins * 2];
        for (var k = 0; k < bins; k++) {
            var hz = k * (double)sampleRate / size;
            if (hz <= 0) {
                continue;
            }

            var pitch = 69 + 12 * Math.Log2(hz / 440) - tuning / 100;
            var below = (int)Math.Floor(pitch);
            var fraction = pitch - below;
            map[2 * k] = (below, 1 - fraction);
            map[2 * k + 1] = (below + 1, fraction);
        }

        var chroma = new double[frames][];
        var bass = new double[frames][];
        var semitones = new double[HighestPitch + 1];
        var peaks = new double[HighestPitch + 1];
        for (var t = 0; t < frames; t++) {
            Array.Clear(semitones);
            Array.Clear(peaks);
            var row = magnitude[t];
            for (var k = 0; k < bins; k++) {
                foreach (var (pitch, weight) in (ReadOnlySpan<(int, double)>)[map[2 * k], map[2 * k + 1]]) {
                    if (pitch is >= LowestPitch and <= HighestPitch) {
                        semitones[pitch] += weight * row[k];
                        peaks[pitch] = Math.Max(peaks[pitch], weight * row[k]);
                    }
                }
            }

            RemoveOvertones(semitones);
            RemoveOvertones(peaks);
            var c = chroma[t] = new double[12];
            var b = bass[t] = new double[12];
            for (var p = LowestPitch; p <= HighestPitch; p++) {
                c[p % 12] += semitones[p];
            }

            // The bass: the lowest semitone whose peak is a good part of the loudest's (peaks, not sums: noise spreads
            // over the more bins of a higher semitone, a tone does not).
            var loudest = 0.0;
            for (var p = LowestPitch; p <= BassHighest; p++) {
                loudest = Math.Max(loudest, peaks[p]);
            }

            for (var p = LowestPitch; p <= BassHighest && loudest > 0; p++) {
                if (peaks[p] >= BassShare * loudest) {
                    b[p % 12] = peaks[p];
                    break;
                }
            }
        }

        return new(hop, chroma, bass, tuning);
    }

    /// <summary>Hann-windowed magnitudes, frame k centred on input frame k · hop, bins 0 … <paramref name="bins" /> − 1.</summary>
    static double[][] Spectrogram(ReadOnlySpan<float> mono, int size, int hop, int frames, int bins) {
        var window = new double[size];
        for (var i = 0; i < size; i++) {
            window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / size);
        }

        var gain = 2 / window.Sum();
        var re = new double[size];
        var im = new double[size];
        var magnitude = new double[frames][];
        for (var t = 0; t < frames; t++) {
            var start = (long)t * hop - size / 2;
            for (var i = 0; i < size; i++) {
                var at = start + i;
                re[i] = at >= 0 && at < mono.Length ? mono[(int)at] * window[i] : 0;
            }

            Array.Clear(im);
            SplitFft.Forward(re, im);
            var row = magnitude[t] = new double[bins];
            for (var k = 0; k < bins; k++) {
                row[k] = Math.Sqrt(re[k] * re[k] + im[k] * im[k]) * gain;
            }
        }

        return magnitude;
    }

    /// <summary>Percussive energy suppressed in place: each magnitude times H² ÷ (H² + P²).</summary>
    static void Harmonic(double[][] magnitude, int frames, int bins) {
        var horizontal = new double[frames][];
        var buffer = new double[Math.Max(TimeMedian, FrequencyMedian)];
        for (var t = 0; t < frames; t++) {
            horizontal[t] = new double[bins];
            for (var k = 0; k < bins; k++) {
                for (var j = 0; j < TimeMedian; j++) {
                    var u = t + j - TimeMedian / 2;
                    buffer[j] = u >= 0 && u < frames ? magnitude[u][k] : 0;
                }

                horizontal[t][k] = Median(buffer, TimeMedian);
            }
        }

        var percussive = new double[bins];
        for (var t = 0; t < frames; t++) {
            var row = magnitude[t];
            for (var k = 0; k < bins; k++) {
                for (var j = 0; j < FrequencyMedian; j++) {
                    var u = k + j - FrequencyMedian / 2;
                    buffer[j] = u >= 0 && u < bins ? row[u] : 0;
                }

                percussive[k] = Median(buffer, FrequencyMedian);
            }

            for (var k = 0; k < bins; k++) {
                var h = horizontal[t][k] * horizontal[t][k];
                var p = percussive[k] * percussive[k];
                row[k] = h + p <= 0 ? 0 : row[k] * h / (h + p);
            }
        }
    }

    /// <summary>The median of the first <paramref name="count" /> values (sorted in place; an odd count).</summary>
    static double Median(double[] values, int count) {
        for (var j = 1; j < count; j++) {
            var v = values[j];
            var i = j;
            while (i > 0 && values[i - 1] > v) {
                values[i] = values[i - 1];
                i--;
            }

            values[i] = v;
        }

        return values[count / 2];
    }

    /// <summary>The tuning in cents: the weighted circular mean of the peaks' offsets from the nearest semitone.</summary>
    static double Tuning(double[][] magnitude, int frames, int bins, float sampleRate, int size) {
        double x = 0, y = 0;
        var low = Math.Max(2, (int)(80.0 * size / sampleRate));
        var high = Math.Min(bins - 2, (int)(2_000.0 * size / sampleRate));
        for (var t = 0; t < frames; t++) {
            var row = magnitude[t];
            var floor = row.Max() * 0.05;
            for (var k = low; k <= high; k++) {
                if (row[k] <= floor || row[k] < row[k - 1] || row[k] <= row[k + 1]) {
                    continue;
                }

                // Parabolic interpolation on log magnitudes.
                double a = Math.Log(row[k - 1] + 1e-12), b = Math.Log(row[k]), c = Math.Log(row[k + 1] + 1e-12);
                var offset = 0.5 * (a - c) / (a - 2 * b + c);
                var hz = (k + Math.Clamp(offset, -0.5, 0.5)) * sampleRate / size;
                var semitones = 12 * Math.Log2(hz / 440);
                var angle = 2 * Math.PI * (semitones - Math.Round(semitones));
                x += row[k] * Math.Cos(angle);
                y += row[k] * Math.Sin(angle);
            }
        }

        return x == 0 && y == 0 ? 0 : Math.Atan2(y, x) / (2 * Math.PI) * 100;
    }

    /// <summary>A semitone's overtones taken off the semitones above it, from the lowest up.</summary>
    static void RemoveOvertones(double[] semitones) {
        ReadOnlySpan<(int Interval, double Share)> overtones = [(12, 0.5), (19, 0.25), (24, 0.125), (28, 0.06)];
        for (var p = LowestPitch; p <= HighestPitch; p++) {
            var a = semitones[p];
            if (a <= 0) {
                continue;
            }

            foreach (var (interval, share) in overtones) {
                var q = p + interval;
                if (q <= HighestPitch) {
                    semitones[q] = Math.Max(0, semitones[q] - a * share);
                }
            }
        }
    }
}
