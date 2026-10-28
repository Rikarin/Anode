using Anode.Dsp.Effects;
using System.Runtime.CompilerServices;

namespace Anode.Dsp.Delays;

/// <summary>
///     A circular delay line of floats (a power-of-two length, so wrapping is a mask). Write a sample, then read it
///     back <c>delay</c> samples later: a delay of 0 is the sample just written. Fractional reads interpolate with a
///     cubic Hermite (after Cathode's <c>FractionalDelayLine</c>), which is exact at integer delays and needs one sample
///     newer than the one read, so it clamps the delay to at least 1. Allocation-free after <see cref="Allocate" />.
/// </summary>
public sealed class DelayLine {
    float[] buffer = new float[4];
    int mask = 3;
    int write;

    /// <summary>The longest delay a read can reach, in samples.</summary>
    public int Capacity => buffer.Length - 4;

    /// <summary>Control thread: room for delays up to <paramref name="maxDelay" /> samples; clears the line.</summary>
    public void Allocate(int maxDelay) {
        var length = 4;
        while (length < maxDelay + 4) {
            length <<= 1;
        }

        buffer = new float[length];
        mask = length - 1;
        write = 0;
    }

    public void Reset() {
        Array.Clear(buffer);
        write = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(float value) {
        write = (write + 1) & mask;
        buffer[write] = MathF.Abs(value) < 1e-25f ? 0f : value;
    }

    /// <summary>The sample written <paramref name="delay" /> writes ago (0: the last one).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Read(int delay) => buffer[(write - delay) & mask];

    /// <summary>A fractional delay, linearly interpolated (cheap; dulls the highs a little between samples).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float ReadLinear(float delay) {
        delay = Math.Clamp(delay, 0, Capacity);
        var whole = (int)delay;
        var frac = delay - whole;
        var a = buffer[(write - whole) & mask];
        var b = buffer[(write - whole - 1) & mask];
        return a + (b - a) * frac;
    }

    /// <summary>A fractional delay by cubic Hermite interpolation, at least 1 sample.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float ReadCubic(float delay) {
        delay = Math.Clamp(delay, 1, Capacity);
        var whole = (int)delay;
        var frac = delay - whole;
        var y0 = buffer[(write - whole + 1) & mask];
        var y1 = buffer[(write - whole) & mask];
        var y2 = buffer[(write - whole - 1) & mask];
        var y3 = buffer[(write - whole - 2) & mask];
        var c1 = 0.5f * (y2 - y0);
        var c2 = y0 - 2.5f * y1 + 2f * y2 - 0.5f * y3;
        var c3 = 0.5f * (y3 - y0) + 1.5f * (y1 - y2);
        return ((c3 * frac + c2) * frac + c1) * frac + y1;
    }
}

/// <summary>
///     Delay times (in samples) that glide to new values instead of jumping: a one-pole toward the target at each
///     control point (<see cref="GlideSeconds" />), then a per-sample ramp between control points. A delay line read at a
///     gliding time bends the pitch, as tape does, rather than clicking.
/// </summary>
internal struct DelayGlide {
    public const double GlideSeconds = 0.05;

    double current;
    GainRamp ramp;

    public void Update(double target, double coefficient, bool jump) {
        current = jump ? target : current + (target - current) * coefficient;
        if (Math.Abs(target - current) < 1e-3) {
            current = target;
        }

        ramp.Set((float)current, jump);
    }

    public float Next() => ramp.Next();
}
