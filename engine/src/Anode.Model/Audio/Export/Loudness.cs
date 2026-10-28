namespace Anode.Model.Audio;

/// <summary>
///     Integrated loudness after ITU-R BS.1770-4 with the gating of EBU R 128 (EBU Tech 3341): K-weighting (the
///     high-shelf pre-filter and the RLB high-pass, their coefficients derived for any sample rate by the bilinear
///     transform from the analogue prototypes behind the standard's 48 kHz table), 400 ms blocks every 100 ms, an
///     absolute gate at -70 LUFS and a relative gate 10 LU below the loudness of the blocks that pass it.
/// </summary>
/// <remarks>
///     Every channel weighs 1.0 (mono counts once, so a mono file reads 3 dB below the same signal in both channels of
///     a stereo one), except six channels, read as 5.1 (L, R, C, LFE, Ls, Rs): the LFE is left out and the surrounds
///     weigh 1.41.
/// </remarks>
public static class Loudness {
    const double AbsoluteGate = -70;
    const double RelativeGate = -10;

    /// <summary>
    ///     The integrated loudness in LUFS; negative infinity when every block is gated out (or none fits).
    /// </summary>
    public static double Integrated(float[][] channels, int sampleRate) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        var step = sampleRate / 10;
        var frames = channels.Length == 0 ? 0 : channels[0].Length;
        var segments = frames / step;
        if (segments < 4) {
            return double.NegativeInfinity;
        }

        // The weighted mean square of every 100 ms segment, summed over the channels; a block is four of them.
        var power = new double[segments];
        for (var c = 0; c < channels.Length; c++) {
            var weight = Weight(c, channels.Length);
            if (weight > 0) {
                AddSegmentPowers(channels[c], sampleRate, step, weight, power);
            }
        }

        var blocks = new double[segments - 3];
        for (var b = 0; b < blocks.Length; b++) {
            blocks[b] = (power[b] + power[b + 1] + power[b + 2] + power[b + 3]) / 4;
        }

        var absolute = Gated(blocks, Power(AbsoluteGate));
        if (absolute.Count == 0) {
            return double.NegativeInfinity;
        }

        var relative = Gated(blocks, Math.Max(Power(AbsoluteGate), absolute.Mean * Power(RelativeGate)));
        return relative.Count == 0 ? double.NegativeInfinity : Lufs(relative.Mean);
    }

    static double Weight(int channel, int count) =>
        count != 6
            ? 1.0
            : channel switch {
                3 => 0.0,
                4 or 5 => 1.41,
                _ => 1.0
            };

    /// <summary>The block power (weighted mean square sum) that reads <paramref name="lufs" />.</summary>
    static double Power(double lufs) => Math.Pow(10, (lufs + 0.691) / 10);

    static double Lufs(double power) => -0.691 + 10 * Math.Log10(power);

    /// <summary>
    ///     How many blocks are louder than <paramref name="threshold" /> (a power), and their mean power.
    /// </summary>
    static (int Count, double Mean) Gated(double[] blocks, double threshold) {
        var count = 0;
        var sum = 0.0;
        foreach (var block in blocks) {
            if (block > threshold) {
                count++;
                sum += block;
            }
        }

        return (count, count == 0 ? 0 : sum / count);
    }

    static void AddSegmentPowers(float[] samples, int sampleRate, int step, double weight, double[] power) {
        var shelf = Biquad.Shelf(sampleRate);
        var highPass = Biquad.HighPass(sampleRate);
        for (var s = 0; s < power.Length; s++) {
            var sum = 0.0;
            for (var i = s * step; i < (s + 1) * step; i++) {
                var y = highPass.Process(shelf.Process(samples[i]));
                sum += y * y;
            }

            power[s] += weight * sum / step;
        }
    }

    /// <summary>A direct-form I biquad, <c>a0</c> normalised to 1.</summary>
    sealed class Biquad(double b0, double b1, double b2, double a1, double a2) {
        double x1;
        double x2;
        double y1;
        double y2;

        /// <summary>The K-weighting's first stage: a high shelf of about +4 dB from about 1.7 kHz.</summary>
        public static Biquad Shelf(int sampleRate) {
            const double f0 = 1681.974450955533;
            const double gainDb = 3.999843853973347;
            const double q = 0.7071752369554196;
            var k = Math.Tan(Math.PI * f0 / sampleRate);
            var vh = Math.Pow(10, gainDb / 20);
            var vb = Math.Pow(vh, 0.4996667741545416);
            var a0 = 1 + k / q + k * k;
            return new(
                (vh + vb * k / q + k * k) / a0,
                2 * (k * k - vh) / a0,
                (vh - vb * k / q + k * k) / a0,
                2 * (k * k - 1) / a0,
                (1 - k / q + k * k) / a0
            );
        }

        /// <summary>The K-weighting's second stage: the RLB high-pass at about 38 Hz.</summary>
        public static Biquad HighPass(int sampleRate) {
            const double f0 = 38.13547087602444;
            const double q = 0.5003270373238773;
            var k = Math.Tan(Math.PI * f0 / sampleRate);
            var a0 = 1 + k / q + k * k;
            return new(1, -2, 1, 2 * (k * k - 1) / a0, (1 - k / q + k * k) / a0);
        }

        public double Process(double x) {
            var y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1;
            x1 = x;
            y2 = y1;
            y1 = y;
            return y;
        }
    }
}
