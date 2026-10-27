using System.Runtime.CompilerServices;

namespace Anode.Dsp.Filters;

/// <summary>
///     Coefficients of a trapezoidal (TPT/ZDF) state-variable filter stage (Andrew Simper, "SVF linear trap optimised
///     2", Cytomic 2013; Zavalishin, "The Art of VA Filter Design"). Its states are an analog prototype's integrators,
///     so it stays well-behaved when the cutoff moves every few samples. One stage gives a 12 dB/octave low-, high- or
///     band-pass at once.
/// </summary>
public readonly record struct SvfCoefficients(double A1, double A2, double A3, double K) {
    /// <summary>A stage at <paramref name="frequency" /> Hz (kept below Nyquist) with quality <paramref name="q" />.</summary>
    public static SvfCoefficients Design(double sampleRate, double frequency, double q) {
        var g = Math.Tan(Math.PI * Math.Clamp(frequency, 1.0, sampleRate * 0.49) / sampleRate);
        var k = 1 / Math.Max(q, 1e-3);
        var a1 = 1 / (1 + g * (g + k));
        var a2 = g * a1;
        return new(a1, a2, g * a2, k);
    }
}

/// <summary>Which output of an SVF stage to take.</summary>
public enum SvfOutput {
    LowPass,
    BandPass,
    HighPass
}

/// <summary>One channel of an <see cref="SvfCoefficients" /> stage: two trapezoidal integrators.</summary>
public struct SvfState {
    double ic1;
    double ic2;

    public void Reset() {
        ic1 = 0;
        ic2 = 0;
    }

    /// <summary>
    ///     One sample through the stage. The band-pass is scaled by <c>k</c> so it peaks at 0 dB at the cutoff, as the
    ///     RBJ constant-peak band-pass does.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double Tick(in SvfCoefficients c, double v0, SvfOutput output) {
        var v3 = v0 - ic2;
        var v1 = c.A1 * ic1 + c.A2 * v3;
        var v2 = ic2 + c.A2 * ic1 + c.A3 * v3;
        ic1 = 2 * v1 - ic1;
        ic2 = 2 * v2 - ic2;
        return output switch {
            SvfOutput.LowPass => v2,
            SvfOutput.BandPass => c.K * v1,
            _ => v0 - c.K * v1 - v2
        };
    }

    /// <summary>Zeroes state that has decayed into the subnormal range, where arithmetic gets very slow.</summary>
    public void FlushDenormals() {
        if (Math.Abs(ic1) < 1e-30) {
            ic1 = 0;
        }

        if (Math.Abs(ic2) < 1e-30) {
            ic2 = 0;
        }
    }
}
