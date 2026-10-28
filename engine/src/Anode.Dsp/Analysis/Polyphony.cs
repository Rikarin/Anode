using Anode.Dsp.Metering;

namespace Anode.Dsp.Analysis;

/// <summary>
///     Whether a file sounds polyphonic (<c>FileAnalysis.Polyphonic</c>: Flex Pitch then works less well), from its pitch
///     curve. Deterministic.
/// </summary>
/// <remarks>
///     Of the hops whose 20 ms around them is loud (above −50 dBFS and within 40 dB of the loudest), a monophonic,
///     pitched file has most voiced with a confident voicing. It is polyphonic when under 60 % of them are voiced, or
///     their mean voicing probability is under 0.5. A chord can still read as voiced, at a common sub-fundamental the
///     notes share (C2 for a C major triad) or at one of its notes: so, every 40 ms, a voiced hop counts against it when
///     its pitch's first three harmonics hold under 5 % of its spectrum's energy, or when over 30 % of that energy lies
///     away from every harmonic (within 3 %) up to the 20th; over 30 % of such hops make the file polyphonic. Chords,
///     noise
///     and drums are all "polyphonic" by this measure.
/// </remarks>
public static class Polyphony {
    const double WindowSeconds = 0.02;
    const double LoudRms = 3.16e-3; // −50 dBFS
    const double RelativeRms = 0.01; // −40 dB

    /// <summary>Whether <paramref name="mono" />, whose pitch curve is <paramref name="track" />, sounds polyphonic.</summary>
    public static bool IsPolyphonic(ReadOnlySpan<float> mono, int sampleRate, PitchTrack track) {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        var hops = track.Cents.Length;
        if (hops == 0) {
            return false;
        }

        var half = Math.Max(1, (int)Math.Round(WindowSeconds * sampleRate / 2));
        var rms = new double[hops];
        double loudest = 0;
        for (var k = 0; k < hops; k++) {
            var centre = (long)k * track.Hop;
            double sum = 0;
            for (var n = centre - half; n < centre + half; n++) {
                if (n >= 0 && n < mono.Length) {
                    sum += (double)mono[(int)n] * mono[(int)n];
                }
            }

            rms[k] = Math.Sqrt(sum / (2 * half));
            loudest = Math.Max(loudest, rms[k]);
        }

        var floor = Math.Max(LoudRms, loudest * RelativeRms);
        int loud = 0, voiced = 0;
        double voicing = 0;
        for (var k = 0; k < hops; k++) {
            if (rms[k] < floor) {
                continue;
            }

            loud++;
            voicing += track.Voicing[k];
            if (!float.IsNaN(track.Cents[k])) {
                voiced++;
            }
        }

        if (loud == 0) {
            return false;
        }

        if (voiced < 0.6 * loud || voicing < 0.5 * loud) {
            return true;
        }

        // Every 40 ms: does the voiced hop's pitch account for its spectrum?
        var size = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(256, sampleRate / 12));
        var re = new double[size];
        var im = new double[size];
        var step = Math.Max(1, (int)Math.Round(0.04 * sampleRate / track.Hop));
        int checkedHops = 0, unexplained = 0;
        for (var k = 0; k < hops; k += step) {
            if (rms[k] < floor || float.IsNaN(track.Cents[k])) {
                continue;
            }

            checkedHops++;
            var f0 = 440 * Math.Pow(2, (track.Cents[k] - 6_900) / 1_200);
            var (low, all) = HarmonicShare(mono, (long)k * track.Hop, f0, sampleRate, re, im);
            if (low < 0.05 || all < 0.7) {
                unexplained++;
            }
        }

        return unexplained > 0.3 * checkedHops;
    }

    /// <summary>
    ///     The shares of the spectrum around <paramref name="centre" /> within 3 % of the first three harmonics of
    ///     <paramref name="f0" />, and of its first twenty.
    /// </summary>
    static (double Low, double All) HarmonicShare(
        ReadOnlySpan<float> mono,
        long centre,
        double f0,
        int sampleRate,
        double[] re,
        double[] im
    ) {
        var size = re.Length;
        for (var i = 0; i < size; i++) {
            var n = centre - size / 2 + i;
            var w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / size);
            re[i] = n >= 0 && n < mono.Length ? mono[(int)n] * w : 0;
            im[i] = 0;
        }

        SplitFft.Forward(re, im);
        double total = 0, low = 0, all = 0;
        var binHz = (double)sampleRate / size;
        for (var m = 1; m < size / 2; m++) {
            var power = re[m] * re[m] + im[m] * im[m];
            total += power;
            var f = m * binHz;
            var h = Math.Max(1, Math.Round(f / f0));
            if (h <= 20 && Math.Abs(f - h * f0) <= Math.Max(2 * binHz, 0.03 * h * f0)) {
                all += power;
                if (h <= 3) {
                    low += power;
                }
            }
        }

        return total > 0 ? (low / total, all / total) : (0, 0);
    }
}
