using Anode.Dsp.Dynamics;
using Anode.Dsp.Filters;
using Anode.Dsp.Metering;
using Anode.Dsp.Spectral;

namespace Anode.Dsp.Mastering;

/// <summary>
///     The bands of the Mastering Assistant's spectrum and curves: ISO 266 third-octave centres, 25 Hz … 20 kHz (the
///     contract's <c>MasteringBands</c>; a test checks they agree). Real data, not a stub.
/// </summary>
public static class MasteringBands {
    public static IReadOnlyList<float> CentresHz { get; } = [
        25, 31.5f, 40, 50, 63, 80, 100, 125, 160, 200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600, 2000, 2500, 3150,
        4000, 5000, 6300, 8000, 10000, 12500, 16000, 20000
    ];

    public static int Count => CentresHz.Count;

    /// <summary>The band's exact centre: 1 kHz × 2^(n/3), which the ISO values round.</summary>
    public static double ExactHz(int band) => 1_000 * Math.Pow(2, (band - 16) / 3.0);
}

/// <summary>The Mastering Assistant's characters, in the contract's <c>MasteringCharacter</c> order.</summary>
public enum MasteringCharacterKind {
    Clean,
    Valve,
    Punch,
    Transparent
}

/// <summary>
///     What the analysis measured (the contract's <c>MasteringAnalysis</c> without its range): BS.1770 integrated
///     loudness (LUFS), the loudest short-term loudness, the loudness range (LU), true peak (dBTP), crest factor (dB),
///     width (0 mono … 1), and the smoothed spectrum per band of <see cref="MasteringBands" /> (dB, relative to its
///     mean).
/// </summary>
public sealed record MasteringMeasurement(
    double IntegratedLufs,
    double MaxShortTermLufs,
    double LoudnessRange,
    double TruePeakDbtp,
    double CrestFactorDb,
    double Width,
    float[] SpectrumDb
);

/// <summary>
///     The Mastering Assistant's analysis (docs/12-phase-11.md, workstream E): BS.1770 loudness and true peak from
///     Phase 4's meters, a smoothed spectrum, crest factor and width, over an offline render of the master before the
///     assistant.
/// </summary>
/// <remarks>
///     <list type="bullet">
///         <item>
///             Loudness by <see cref="LoudnessMeter" />: integrated (gated), the loudest short-term value (once 3 s
///             have passed, or of the whole render when it is shorter), and the loudness range by EBU Tech 3342: the
///             short-term values every 100 ms gated at −70 LUFS and 20 LU below their mean, the 95th percentile less the
///             10th.
///         </item>
///         <item>
///             True peak by the 4× interpolation of <see cref="TruePeak" /> (the limiters' detector), the crest factor
///             as the true peak over the RMS of both channels, the width as the side's energy over the mid's (clamped
///             to 0 … 1).
///         </item>
///         <item>
///             The spectrum: the mid's power averaged over Hann frames of 16,384 samples at 48 kHz (half overlapping),
///             summed into each band (its centre × 2<sup>±1/6</sup>), in dB, smoothed 1-2-1 across the bands, less the
///             mean of the bands.
///         </item>
///     </list>
/// </remarks>
public static class MasteringAnalyzer {
    /// <summary>What silence measures: the floor of every level.</summary>
    const double FloorDb = -120;

    /// <summary>Measures a stereo render (<paramref name="left" />, <paramref name="right" />, equal lengths).</summary>
    public static MasteringMeasurement Analyse(ReadOnlySpan<float> left, ReadOnlySpan<float> right, float sampleRate) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        var frames = Math.Min(left.Length, right.Length);
        left = left[..frames];
        right = right[..frames];
        var (integrated, maxShortTerm, range) = Loudness(left, right, sampleRate);
        var peak = Math.Max(Peak(left), Peak(right));
        double square = 0, mid = 0, side = 0;
        for (var i = 0; i < frames; i++) {
            double l = left[i];
            double r = right[i];
            square += l * l + r * r;
            mid += (l + r) * (l + r);
            side += (l - r) * (l - r);
        }

        var rms = frames == 0 ? 0 : Math.Sqrt(square / (2.0 * frames));
        var truePeak = ToDb(peak);
        var crest = rms > 1e-9 ? truePeak - ToDb(rms) : 0;
        var width = mid > 1e-12 ? Math.Clamp(side / mid, 0, 1) : side > 1e-12 ? 1 : 0;
        return new(integrated, maxShortTerm, range, truePeak, crest, width, Spectrum(left, right, sampleRate));
    }

    /// <summary>
    ///     The reference curve of <paramref name="character" /> per band (dB, relative to its mean): a typical
    ///     master's third-octave balance (flat from 40 to 150 Hz, −2.5 dB an octave to 2 kHz, −3.5 to 12 kHz, steeper
    ///     above; −6 dB an octave below 40 Hz), warmer for Valve (+1.5 dB around 200 Hz, −1.5 dB above 6 kHz), fuller
    ///     in the low end and forward for Punch (+2 dB around 70 Hz, +1 dB around 3 kHz). Transparent has Clean's curve
    ///     (the designer applies it at half weight).
    /// </summary>
    public static float[] Curve(MasteringCharacterKind character) {
        var curve = new double[MasteringBands.Count];
        for (var b = 0; b < curve.Length; b++) {
            var f = MasteringBands.ExactHz(b);
            var db = Base(f)
                + character switch {
                    MasteringCharacterKind.Valve => Bell(f, 200, 1.5, 1.5) - 1.5 * Shelf(f, 6_000),
                    MasteringCharacterKind.Punch => Bell(f, 70, 1, 2) + Bell(f, 3_000, 1.5, 1),
                    _ => 0
                };
            curve[b] = db;
        }

        return Centred(Smoothed(curve));
    }

    /// <summary>
    ///     A bell of <paramref name="db" /> at <paramref name="centre" />, <paramref name="octaves" /> wide (Gaussian in
    ///     octaves).
    /// </summary>
    static double Bell(double f, double centre, double octaves, double db) {
        var x = Math.Log2(f / centre) / (octaves / 2);
        return db * Math.Exp(-x * x);
    }

    /// <summary>The curve every character starts from at <paramref name="f" /> (dB).</summary>
    static double Base(double f) {
        if (f < 40) {
            return -6 * Math.Log2(40 / f);
        }

        if (f < 150) {
            return 0;
        }

        if (f < 2_000) {
            return -2.5 * Math.Log2(f / 150);
        }

        var top = f > 12_000 ? 4 * Math.Log2(f / 12_000) : 0;
        return -2.5 * Math.Log2(2_000 / 150.0) - 3.5 * Math.Log2(f / 2_000) - top;
    }


    /// <summary>0 well below <paramref name="corner" />, 1 well above, ½ there (logistic in octaves).</summary>
    static double Shelf(double f, double corner) => 1 / (1 + Math.Pow(corner / f, 3));

    static double Peak(ReadOnlySpan<float> channel) {
        var peak = 0.0;
        foreach (var x in channel) {
            peak = Math.Max(peak, Math.Abs(x));
        }

        // The interpolated points between the samples: the samples c − 15 … c + 16 around each interval (c, c + 1).
        var kernels = TruePeak.Kernels;
        for (var c = TruePeak.Delay - 1; c + TruePeak.Delay < channel.Length; c++) {
            var window = channel.Slice(c - (TruePeak.Delay - 1), TruePeak.Taps);
            for (var p = 0; p < 3; p++) {
                var taps = kernels[p];
                var sum = 0.0;
                for (var j = 0; j < TruePeak.Taps; j++) {
                    sum += taps[j] * window[j];
                }

                peak = Math.Max(peak, Math.Abs(sum));
            }
        }

        return peak;
    }

    static (double Integrated, double MaxShortTerm, double Range) Loudness(
        ReadOnlySpan<float> left,
        ReadOnlySpan<float> right,
        float sampleRate
    ) {
        var meter = new LoudnessMeter(sampleRate);
        var bin = Math.Max(1, (int)Math.Round(sampleRate * 0.1));
        var shortTerms = new List<double>();
        double loudest = LoudnessMeter.FloorLufs;
        double first = LoudnessMeter.FloorLufs;
        for (var at = 0; at < left.Length; at += bin) {
            var n = Math.Min(bin, left.Length - at);
            meter.Process(left.Slice(at, n), right.Slice(at, n));
            if (n < bin) {
                break;
            }

            // Before 3 s the short-term window is not full: kept only for a render shorter than that.
            if (at + n >= 3 * sampleRate) {
                shortTerms.Add(meter.ShortTerm);
                loudest = Math.Max(loudest, meter.ShortTerm);
            } else {
                first = Math.Max(first, meter.ShortTerm);
            }
        }

        return (meter.Integrated, shortTerms.Count > 0 ? loudest : first, Range(shortTerms));
    }

    /// <summary>EBU Tech 3342's loudness range of short-term values.</summary>
    static double Range(List<double> shortTerms) {
        var gated = shortTerms.Where(static s => s > -70).ToList();
        if (gated.Count < 2) {
            return 0;
        }

        var mean = 10 * Math.Log10(gated.Average(static s => Math.Pow(10, s / 10)));
        var kept = gated.Where(s => s > mean - 20).Order().ToArray();
        if (kept.Length < 2) {
            return 0;
        }

        return Percentile(0.95) - Percentile(0.10);

        double Percentile(double p) => kept[(int)Math.Round(p * (kept.Length - 1))];
    }

    static float[] Spectrum(ReadOnlySpan<float> left, ReadOnlySpan<float> right, float sampleRate) {
        var size = 1;
        while (size < 16_384 * sampleRate / 48_000) {
            size <<= 1;
        }

        var fft = new RealFft(size);
        var frame = new float[size];
        var real = new float[fft.Bins];
        var imaginary = new float[fft.Bins];
        var power = new double[fft.Bins];
        var window = new float[size];
        for (var i = 0; i < size; i++) {
            window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / size));
        }

        var frames = 0;
        for (var start = 0; start < Math.Max(1, left.Length - size / 2); start += size / 2) {
            for (var i = 0; i < size; i++) {
                var at = start + i;
                frame[i] = at < left.Length ? 0.5f * (left[at] + right[at]) * window[i] : 0;
            }

            fft.Forward(frame, real, imaginary);
            for (var k = 0; k < fft.Bins; k++) {
                power[k] += real[k] * (double)real[k] + imaginary[k] * (double)imaginary[k];
            }

            frames++;
        }

        var binHz = sampleRate / (double)size;
        var bands = new double[MasteringBands.Count];
        var any = false;
        for (var b = 0; b < bands.Length; b++) {
            var centre = MasteringBands.ExactHz(b);
            var low = (int)Math.Ceiling(centre * Math.Pow(2, -1 / 6.0) / binHz);
            var high = (int)Math.Floor(centre * Math.Pow(2, 1 / 6.0) / binHz);
            var sum = 0.0;
            for (var k = Math.Max(1, low); k <= Math.Min(fft.Bins - 1, Math.Max(low, high)); k++) {
                sum += power[k];
            }

            any |= sum > 0;
            bands[b] = sum > 0 ? 10 * Math.Log10(sum / Math.Max(1, frames)) : FloorDb;
        }

        return any ? Centred(Smoothed(bands)) : new float[bands.Length];
    }

    /// <summary>1-2-1 across neighbouring bands (the ends with themselves).</summary>
    internal static double[] Smoothed(double[] bands) {
        var smoothed = new double[bands.Length];
        for (var b = 0; b < bands.Length; b++) {
            smoothed[b] = 0.25 * bands[Math.Max(0, b - 1)]
                + 0.5 * bands[b]
                + 0.25 * bands[Math.Min(bands.Length - 1, b + 1)];
        }

        return smoothed;
    }

    /// <summary>Less the mean of the bands.</summary>
    static float[] Centred(double[] bands) {
        var mean = bands.Average();
        return [..bands.Select(b => (float)(b - mean))];
    }

    static double ToDb(double gain) => gain > 1e-6 ? 20 * Math.Log10(gain) : FloorDb;
}

/// <summary>
///     The chain the analysis sets (the IPC's <c>EngineMasteringChain</c>): the EQ's gain per band at full strength
///     (dB), the compressor's threshold (dBFS) and ratio, the analysed integrated loudness (LUFS) and the loudness the
///     EQ and compressor add at full strength (LU).
/// </summary>
public sealed record MasteringChainSettings(
    float[] EqGainsDb,
    float CompressorThresholdDb,
    float CompressorRatio,
    float LoudnessLufs,
    float ChainGainDb
);

/// <summary>Designs the chain from a measurement and a character (workstream E): EQ matched to the curve, limited.</summary>
/// <remarks>
///     <list type="bullet">
///         <item>
///             <b>EQ</b>: the character's curve less the measured spectrum, smoothed 1-2-1 twice, within ±
///             <see cref="MaxEqDb" /> (±2 dB at the outer bands, 25, 31.5 and 20 kHz); Transparent at half weight. Nothing
///             for a silent measurement.
///         </item>
///         <item>
///             <b>Compression</b>: the chain runs on its input normalised to <see cref="MasteringAssistant.WorkingLufs" />
///             (−18 LUFS), so the threshold is 4 dB above that level's RMS (−13.3 dBFS), wherever the mix sat; the ratio
///             1.5 … 2 with the loudness range (4 … 12 LU), 0.3 more for Punch, half the way to 1 for Transparent.
///         </item>
///         <item>
///             <b>Chain gain</b>: what the EQ does to the K-weighted loudness, from the band powers (the measured
///             spectrum) times the K-weighting and the EQ's gains, and an estimate of the compressor's reduction from
///             how far the loudest short-term loudness rises over the integrated. The assistant corrects the estimate as
///             it plays (its remarks).
///         </item>
///     </list>
/// </remarks>
public static class MasteringChainDesigner {
    /// <summary>The most the EQ moves a band (dB).</summary>
    public const float MaxEqDb = 4;

    /// <summary>The compressor's threshold over the working level's RMS (dB).</summary>
    const double HeadroomDb = 4;

    public static MasteringChainSettings Design(MasteringMeasurement measurement, MasteringCharacterKind character) {
        var count = MasteringBands.Count;
        var curve = MasteringAnalyzer.Curve(character);
        var silent = measurement.IntegratedLufs <= -70 || measurement.SpectrumDb.Length != count;
        var gains = new double[count];
        if (!silent) {
            var weight = character == MasteringCharacterKind.Transparent ? 0.5 : 1;
            for (var b = 0; b < count; b++) {
                gains[b] = curve[b] - measurement.SpectrumDb[b];
            }

            gains = MasteringAnalyzer.Smoothed(MasteringAnalyzer.Smoothed(gains));
            for (var b = 0; b < count; b++) {
                var limit = b < 2 || b == count - 1 ? MaxEqDb / 2 : MaxEqDb;
                gains[b] = Math.Clamp(gains[b], -limit, limit) * weight;
            }
        }

        var range = Math.Clamp((measurement.LoudnessRange - 4) / 8, 0, 1);
        var ratio = 1.5 + 0.5 * range + (character == MasteringCharacterKind.Punch ? 0.3 : 0);
        if (character == MasteringCharacterKind.Transparent) {
            ratio = 1 + (ratio - 1) / 2;
        }

        var threshold = MasteringAssistant.WorkingLufs + LoudnessMeterOffset + HeadroomDb;
        var eqGain = silent ? 0 : EqLoudness(measurement.SpectrumDb, gains);
        var rise = Math.Max(0, measurement.MaxShortTermLufs - measurement.IntegratedLufs - 2);
        var compression = silent ? 0 : -(1 - 1 / ratio) * rise * 0.5;
        return new(
            [..gains.Select(static g => (float)g)],
            (float)threshold,
            (float)ratio,
            (float)(silent ? MasteringAssistant.WorkingLufs : measurement.IntegratedLufs),
            (float)(eqGain + compression)
        );
    }

    /// <summary>
    ///     A stereo signal's level in the compressor's detector (the summed mean square of both channels, as the
    ///     Compressor's Opto reads it) over its loudness: about 0.7 dB for a broad mix (LUFS is −0.691 + that, before
    ///     the K-weighting).
    /// </summary>
    const double LoudnessMeterOffset = 0.691;

    /// <summary>The change of the K-weighted loudness of a spectrum (band dB) under band gains (dB).</summary>
    static double EqLoudness(float[] spectrumDb, double[] gainsDb) {
        var shelf = BiquadCoefficients.Design(BiquadType.HighShelf, 48_000, 1_681.97, 0.7072, 4);
        var highPass = BiquadCoefficients.Design(BiquadType.HighPass, 48_000, 38.14, 0.5003);
        double before = 0, after = 0;
        for (var b = 0; b < spectrumDb.Length; b++) {
            var f = MasteringBands.ExactHz(b);
            var k = shelf.MagnitudeAt(f, 48_000) * highPass.MagnitudeAt(f, 48_000);
            var p = Math.Pow(10, spectrumDb[b] / 10) * k * k;
            before += p;
            after += p * Math.Pow(10, gainsDb[b] / 10);
        }

        return before > 0 ? 10 * Math.Log10(after / before) : 0;
    }
}
