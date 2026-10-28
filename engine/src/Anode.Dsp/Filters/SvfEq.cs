using System.Runtime.CompilerServices;

namespace Anode.Dsp.Filters;

/// <summary>The responses of an <see cref="SvfEqCoefficients" /> band.</summary>
public enum SvfEqType {
    LowPass,
    HighPass,
    BandPass,
    Notch,
    AllPass,
    Bell,
    LowShelf,
    HighShelf
}

/// <summary>
///     A trapezoidal state-variable filter whose output mixes its input, band-pass and low-pass (Andrew Simper,
///     "Solving the continuous SVF equations using trapezoidal integration and equivalent currents", Cytomic 2013). The
///     mixes give every RBJ cookbook response (the same bilinear transforms of the same analog prototypes), but the
///     states stay those of the analog integrators, so the coefficients can move every control interval without the
///     clicks a direct-form biquad makes.
/// </summary>
public readonly record struct SvfEqCoefficients(double A1, double A2, double A3, double M0, double M1, double M2) {
    public static SvfEqCoefficients Identity { get; } = new(1, 0, 0, 1, 0, 0);

    /// <summary>
    ///     A band of <paramref name="type" /> at <paramref name="frequency" /> Hz (kept below Nyquist) with quality
    ///     <paramref name="q" />; <paramref name="gainDb" /> for the bell and the shelves.
    /// </summary>
    public static SvfEqCoefficients Design(
        SvfEqType type,
        double sampleRate,
        double frequency,
        double q,
        double gainDb = 0
    ) {
        var g = Math.Tan(Math.PI * Math.Clamp(frequency, 1.0, sampleRate * 0.49) / sampleRate);
        var k = 1 / Math.Max(q, 1e-3);
        var a = Math.Pow(10, gainDb / 40);
        double m0, m1, m2;
        switch (type) {
            case SvfEqType.LowPass:
                (m0, m1, m2) = (0, 0, 1);
                break;
            case SvfEqType.HighPass:
                (m0, m1, m2) = (1, -k, -1);
                break;
            case SvfEqType.BandPass:
                (m0, m1, m2) = (0, k, 0);
                break;
            case SvfEqType.Notch:
                (m0, m1, m2) = (1, -k, 0);
                break;
            case SvfEqType.AllPass:
                (m0, m1, m2) = (1, -2 * k, 0);
                break;
            case SvfEqType.Bell:
                k = 1 / (Math.Max(q, 1e-3) * a);
                (m0, m1, m2) = (1, k * (a * a - 1), 0);
                break;
            case SvfEqType.LowShelf:
                g /= Math.Sqrt(a);
                (m0, m1, m2) = (1, k * (a - 1), a * a - 1);
                break;
            default:
                g *= Math.Sqrt(a);
                (m0, m1, m2) = (a * a, k * (1 - a) * a, 1 - a * a);
                break;
        }

        var a1 = 1 / (1 + g * (g + k));
        var a2 = g * a1;
        return new(a1, a2, g * a2, m0, m1, m2);
    }
}

/// <summary>One channel of an <see cref="SvfEqCoefficients" /> band.</summary>
public struct SvfEqState {
    double ic1;
    double ic2;

    public void Reset() {
        ic1 = 0;
        ic2 = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double Tick(in SvfEqCoefficients c, double v0) {
        var v3 = v0 - ic2;
        var v1 = c.A1 * ic1 + c.A2 * v3;
        var v2 = ic2 + c.A2 * ic1 + c.A3 * v3;
        ic1 = 2 * v1 - ic1;
        ic2 = 2 * v2 - ic2;
        return c.M0 * v0 + c.M1 * v1 + c.M2 * v2;
    }

    public void FlushDenormals() {
        if (Math.Abs(ic1) < 1e-30) {
            ic1 = 0;
        }

        if (Math.Abs(ic2) < 1e-30) {
            ic2 = 0;
        }
    }
}

/// <summary>
///     A first-order trapezoidal low-pass, high-pass or all-pass section (Zavalishin, "The Art of VA Filter Design",
///     3.10): the phaser's stage, the damping in delay feedback.
/// </summary>
public struct OnePole {
    double s;

    /// <summary>The coefficient G = g / (1 + g) of a section at <paramref name="frequency" /> Hz.</summary>
    public static double Coefficient(double sampleRate, double frequency) {
        var g = Math.Tan(Math.PI * Math.Clamp(frequency, 1.0, sampleRate * 0.49) / sampleRate);
        return g / (1 + g);
    }

    public void Reset() => s = 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double LowPass(double coefficient, double x) {
        var v = (x - s) * coefficient;
        var y = v + s;
        s = y + v;
        return y;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double HighPass(double coefficient, double x) => x - LowPass(coefficient, x);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double AllPass(double coefficient, double x) => 2 * LowPass(coefficient, x) - x;

    public void FlushDenormals() {
        if (Math.Abs(s) < 1e-30) {
            s = 0;
        }
    }
}
