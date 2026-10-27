using System.Runtime.CompilerServices;

namespace Anode.Dsp.Filters;

/// <summary>The responses of a <see cref="MultimodeFilter" />.</summary>
public enum FilterMode {
    /// <summary>24 dB/octave: two cascaded stages tuned as a 4th-order Butterworth, resonance on the second.</summary>
    LowPass24,
    LowPass12,
    HighPass12,
    BandPass12
}

/// <summary>
///     The coefficients of a <see cref="MultimodeFilter" />: two SVF stages (the second unused by the 12 dB modes).
///     Without resonance every mode is 3 dB down at the cutoff (the band-pass peaks there at 0 dB); resonance raises
///     the quality of the resonant stage exponentially up to <see cref="MaxQ" />.
/// </summary>
public readonly record struct MultimodeCoefficients(FilterMode Mode, SvfCoefficients First, SvfCoefficients Second) {
    public const double MaxQ = 20;

    /// <summary>The Butterworth qualities: one 2nd-order stage, then the two stages of a 4th-order one.</summary>
    const double ButterworthQ = 0.70710678118654752;

    const double FourthOrderQ1 = 0.54119610014619698;
    const double FourthOrderQ2 = 1.3065629648763766;

    /// <summary>The quality of the resonant stage for a resonance in 0 … 1.</summary>
    public static double Quality(FilterMode mode, double resonance) {
        var q = mode == FilterMode.LowPass24 ? FourthOrderQ2 : ButterworthQ;
        return q * Math.Pow(MaxQ / q, Math.Clamp(resonance, 0, 1));
    }

    public static MultimodeCoefficients Design(FilterMode mode, double sampleRate, double cutoff, double quality) =>
        mode == FilterMode.LowPass24
            ? new(
                mode,
                SvfCoefficients.Design(sampleRate, cutoff, FourthOrderQ1),
                SvfCoefficients.Design(sampleRate, cutoff, quality)
            )
            : new(mode, SvfCoefficients.Design(sampleRate, cutoff, quality), default);
}

/// <summary>One channel of a multimode filter: up to two SVF stages.</summary>
public struct MultimodeFilter {
    SvfState first;
    SvfState second;

    public void Reset() {
        first.Reset();
        second.Reset();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double Tick(in MultimodeCoefficients c, double input) =>
        c.Mode switch {
            FilterMode.LowPass24 => second.Tick(
                c.Second,
                first.Tick(c.First, input, SvfOutput.LowPass),
                SvfOutput.LowPass
            ),
            FilterMode.LowPass12 => first.Tick(c.First, input, SvfOutput.LowPass),
            FilterMode.HighPass12 => first.Tick(c.First, input, SvfOutput.HighPass),
            _ => first.Tick(c.First, input, SvfOutput.BandPass)
        };

    public void FlushDenormals() {
        first.FlushDenormals();
        second.FlushDenormals();
    }
}
