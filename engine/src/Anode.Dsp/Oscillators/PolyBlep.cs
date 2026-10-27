using System.Runtime.CompilerServices;

namespace Anode.Dsp.Oscillators;

/// <summary>The waveforms <see cref="PolyBlep" /> renders.</summary>
public enum Waveform {
    Saw,
    Pulse,
    Triangle,
    Sine,
    Noise
}

/// <summary>
///     Band-limited oscillator waveforms from a phase in [0, 1) and a per-sample phase increment: the saw and pulse
///     with polynomial band-limited steps (PolyBLEP; Välimäki and Huovilainen, 2007), the triangle with their integral
///     (PolyBLAMP; Esqueda, Välimäki and Bilbao, 2016) at its corners. Every waveform spans −1 … 1 without DC.
/// </summary>
public static class PolyBlep {
    /// <summary>A rising ramp with its step down at the wrap.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Saw(double phase, double increment) => (float)(2 * phase - 1 - Step(phase, increment));

    /// <summary>A pulse high for <paramref name="width" /> (0 … 1) of the cycle, its DC removed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Pulse(double phase, double increment, double width) {
        var naive = phase < width ? 1.0 : -1.0;
        var down = phase - width;
        if (down < 0) {
            down += 1;
        }

        return (float)(naive + Step(phase, increment) - Step(down, increment) - (2 * width - 1));
    }

    /// <summary>A triangle at −1 on the wrap and +1 half a cycle later.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Triangle(double phase, double increment) {
        var naive = 1 - 4 * Math.Abs(phase - 0.5);
        var half = phase - 0.5;
        if (half < 0) {
            half += 1;
        }

        // The slope turns by +8 per cycle at the wrap and by −8 at the half.
        return (float)(naive + 8 * increment * (Ramp(phase, increment) - Ramp(half, increment)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sine(double phase) => (float)Math.Sin(2 * Math.PI * phase);

    /// <summary>One waveform; <see cref="Waveform.Noise" /> draws from <paramref name="noise" />.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Render(Waveform waveform, double phase, double increment, double width, ref uint noise) =>
        waveform switch {
            Waveform.Saw => Saw(phase, increment),
            Waveform.Pulse => Pulse(phase, increment, width),
            Waveform.Triangle => Triangle(phase, increment),
            Waveform.Sine => Sine(phase),
            _ => White(ref noise)
        };

    /// <summary>White noise in −1 … 1 from a xorshift32 generator (the state must not be 0).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float White(ref uint state) {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state * (2f / uint.MaxValue) - 1;
    }

    /// <summary>
    ///     The residual of a band-limited step of height 2 at phase 0, for a sample at phase <paramref name="t" /> with
    ///     increment <paramref name="dt" />.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Step(double t, double dt) {
        if (t < dt) {
            t /= dt;
            return t + t - t * t - 1;
        }

        if (t > 1 - dt) {
            t = (t - 1) / dt;
            return t * t + t + t + 1;
        }

        return 0;
    }

    /// <summary>
    ///     The residual, in samples, of a band-limited corner (the slope turning by 1 per sample) at phase 0: the
    ///     integral of the unit-step residual.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Ramp(double t, double dt) {
        if (t < dt) {
            var d = t / dt;
            return 1.0 / 6 + d * (-0.5 + d * (0.5 - d / 6));
        }

        if (t > 1 - dt) {
            var d = (t - 1) / dt + 1;
            return d * d * d / 6;
        }

        return 0;
    }
}
