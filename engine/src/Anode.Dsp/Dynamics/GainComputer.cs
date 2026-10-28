using Anode.Dsp.Filters;

namespace Anode.Dsp.Dynamics;

/// <summary>
///     The static curve of a feed-forward compressor (Giannoulis, Massberg and Reiss, "Digital Dynamic Range
///     Compressor Design — A Tutorial and Analysis", JAES 2012, eq. 4): below the knee the level passes, above it rises
///     by 1/ratio, and within a knee of <c>width</c> dB centred on the threshold a quadratic joins the two.
/// </summary>
public static class GainComputer {
    /// <summary>The gain (dB, ≤ 0) for a level of <paramref name="levelDb" />.</summary>
    public static double Gain(double levelDb, double thresholdDb, double ratio, double kneeDb) {
        var over = levelDb - thresholdDb;
        var slope = 1 / Math.Max(ratio, 1) - 1;
        if (2 * over < -kneeDb) {
            return 0;
        }

        if (kneeDb > 0 && 2 * Math.Abs(over) <= kneeDb) {
            var x = over + kneeDb / 2;
            return slope * x * x / (2 * kneeDb);
        }

        return slope * over;
    }
}

/// <summary>The filter a dynamics processor's detector can hear its signal through.</summary>
public enum SidechainFilter {
    Off,
    HighPass,
    LowPass,
    BandPass
}

/// <summary>A stereo detector filter (an RBJ biquad per channel), off by default.</summary>
public struct DetectorFilter {
    BiquadCoefficients coefficients;
    BiquadState left;
    BiquadState right;
    SidechainFilter mode;

    public void Set(SidechainFilter filter, double sampleRate, double frequency) {
        if (filter != mode) {
            left.Reset();
            right.Reset();
        }

        mode = filter;
        coefficients = filter switch {
            SidechainFilter.HighPass => BiquadCoefficients.Design(BiquadType.HighPass, sampleRate, frequency, 0.7071),
            SidechainFilter.LowPass => BiquadCoefficients.Design(BiquadType.LowPass, sampleRate, frequency, 0.7071),
            SidechainFilter.BandPass => BiquadCoefficients.Design(BiquadType.BandPass, sampleRate, frequency, 1),
            _ => BiquadCoefficients.Identity
        };
    }

    public readonly bool Active => mode != SidechainFilter.Off;

    public void Reset() {
        left.Reset();
        right.Reset();
    }

    public void Tick(ref float l, ref float r) {
        if (mode == SidechainFilter.Off) {
            return;
        }

        l = (float)left.Tick(coefficients, l);
        r = (float)right.Tick(coefficients, r);
    }

    public void FlushDenormals() {
        left.FlushDenormals();
        right.FlushDenormals();
    }
}
