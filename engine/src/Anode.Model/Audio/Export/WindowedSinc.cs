namespace Anode.Model.Audio;

/// <summary>Kaiser-windowed sinc kernels, shared by the resampler and the true-peak interpolator.</summary>
static class WindowedSinc {
    /// <summary>
    ///     The Kaiser window's β for a stopband attenuation of <paramref name="attenuationDb" /> (Kaiser's formula).
    /// </summary>
    public static double Beta(double attenuationDb) =>
        attenuationDb > 50
            ? 0.1102 * (attenuationDb - 8.7)
            : attenuationDb >= 21
                ? 0.5842 * Math.Pow(attenuationDb - 21, 0.4) + 0.07886 * (attenuationDb - 21)
                : 0;

    /// <summary>
    ///     The kernel's half-length in input samples for a transition band of <paramref name="transition" /> cycles per
    ///     input sample and a stopband of <paramref name="attenuationDb" /> (Kaiser's length estimate, halved and
    ///     rounded up).
    /// </summary>
    public static int HalfLength(double attenuationDb, double transition) {
        var length = (attenuationDb - 7.95) / (2.285 * 2 * Math.PI * transition);
        return (int)Math.Ceiling(length / 2) + 1;
    }

    /// <summary>
    ///     A low-pass kernel at <paramref name="x" /> input samples from its centre: cutoff <paramref name="cutoff" />
    ///     cycles per input sample (unity gain at DC), Kaiser window of half-width <paramref name="half" />.
    /// </summary>
    public static double Kernel(double x, double cutoff, int half, double beta) {
        if (Math.Abs(x) >= half) {
            return 0;
        }

        var u = 2 * cutoff * x;
        var sinc = Math.Abs(u) < 1e-12 ? 1 : Math.Sin(Math.PI * u) / (Math.PI * u);
        var r = x / half;
        var window = BesselI0(beta * Math.Sqrt(1 - r * r)) / BesselI0(beta);
        return 2 * cutoff * sinc * window;
    }

    /// <summary>The zeroth-order modified Bessel function of the first kind, by its power series.</summary>
    static double BesselI0(double x) {
        var sum = 1.0;
        var term = 1.0;
        var half = x / 2;
        for (var k = 1; k < 64; k++) {
            term *= half / k;
            var square = term * term;
            sum += square;
            if (square < sum * 1e-17) {
                break;
            }
        }

        return sum;
    }
}
