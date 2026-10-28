namespace Anode.Model.Audio;

/// <summary>
///     A true-peak brickwall limiter for whole files, in place and without delay (the output lines up with the input
///     and keeps its length). One gain, linked over every channel, follows the true-peak envelope (see
///     <see cref="TruePeak" />): it reaches the gain a peak needs by the time the peak arrives, ramping down smoothly
///     over a 2 ms lookahead, holds for 1 ms after it and recovers with an 80 ms release. Gain changes move the
///     inter-sample peaks a little, so the pass repeats while anything is still over the ceiling, and a last uniform
///     safety gain settles what remains: afterwards <see cref="TruePeak.Measure" /> is never above the ceiling.
///     Material whose true peak is already at or below the ceiling is left untouched.
/// </summary>
public static class Limiter {
    const double LookaheadSeconds = 0.001;
    const double ReleaseSeconds = 0.08;
    const int MaxPasses = 4;

    /// <summary>
    ///     Each pass aims this much below the ceiling (as a gain), to absorb what the gain change itself adds.
    /// </summary>
    const double PassMargin = 0.997;

    /// <summary>
    ///     Limits <paramref name="channels" /> (planar, equal lengths) to <paramref name="ceilingDb" /> dBTP.
    /// </summary>
    public static void Apply(float[][] channels, int sampleRate, double ceilingDb) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        if (channels.Length == 0 || channels[0].Length == 0) {
            return;
        }

        var ceiling = Math.Pow(10, ceilingDb / 20);
        var frames = channels[0].Length;
        var envelope = new float[frames];
        var gain = new float[frames];
        for (var pass = 0; pass < MaxPasses; pass++) {
            Array.Clear(envelope);
            TruePeak.Envelope(channels, envelope);
            if (Max(envelope) <= ceiling) {
                return;
            }

            ComputeGain(envelope, ceiling * PassMargin, sampleRate, gain);
            foreach (var channel in channels) {
                for (var i = 0; i < frames; i++) {
                    channel[i] *= gain[i];
                }
            }
        }

        // What the passes left over the ceiling (rarely anything) goes with one uniform gain.
        for (var peak = TruePeak.Measure(channels); peak > ceiling; peak = TruePeak.Measure(channels)) {
            Scale(channels, (float)(ceiling / peak * 0.9999));
        }
    }

    static void Scale(float[][] channels, float gain) {
        foreach (var channel in channels) {
            for (var i = 0; i < channel.Length; i++) {
                channel[i] *= gain;
            }
        }
    }

    /// <summary>
    ///     The gain for every frame: at most what each frame within the lookahead needs, released, then smoothed with a
    ///     moving average no wider than the lookahead window, so no frame gets more gain than it needs.
    /// </summary>
    static void ComputeGain(float[] envelope, double target, int sampleRate, float[] gain) {
        var frames = envelope.Length;
        var reach = Math.Max(1, (int)Math.Round(LookaheadSeconds * sampleRate));
        var needed = new float[frames];
        for (var i = 0; i < frames; i++) {
            needed[i] = envelope[i] > target ? (float)(target / envelope[i]) : 1f;
        }

        var held = SlidingMinimum(needed, reach);
        var alpha = 1 - Math.Exp(-1 / (ReleaseSeconds * sampleRate));
        var level = 1.0;
        for (var i = 0; i < frames; i++) {
            level = held[i] < level ? held[i] : level + alpha * (held[i] - level);
            held[i] = (float)level;
        }

        MovingAverage(held, reach, gain);
    }

    /// <summary>
    ///     The minimum of <paramref name="values" /> over <c>[i - reach, i + reach]</c> (clipped to the array).
    /// </summary>
    static float[] SlidingMinimum(float[] values, int reach) {
        var result = new float[values.Length];
        var queue = new int[values.Length];
        var head = 0;
        var tail = 0;
        var next = 0;
        for (var i = 0; i < values.Length; i++) {
            var last = Math.Min(values.Length - 1, i + reach);
            for (; next <= last; next++) {
                while (tail > head && values[queue[tail - 1]] >= values[next]) {
                    tail--;
                }

                queue[tail++] = next;
            }

            while (queue[head] < i - reach) {
                head++;
            }

            result[i] = values[queue[head]];
        }

        return result;
    }

    /// <summary>
    ///     The mean of <paramref name="values" /> over <c>[i - reach, i + reach]</c> (clipped to the array).
    /// </summary>
    static void MovingAverage(float[] values, int reach, float[] result) {
        var prefix = new double[values.Length + 1];
        for (var i = 0; i < values.Length; i++) {
            prefix[i + 1] = prefix[i] + values[i];
        }

        for (var i = 0; i < values.Length; i++) {
            var from = Math.Max(0, i - reach);
            var to = Math.Min(values.Length, i + reach + 1);
            var mean = (prefix[to] - prefix[from]) / (to - from);

            // The mean of values that are each at most values[i]'s window minimum cannot exceed it but for rounding.
            result[i] = Math.Min((float)mean, 1f);
        }
    }

    static float Max(float[] values) {
        var max = 0f;
        foreach (var value in values) {
            max = Math.Max(max, value);
        }

        return max;
    }
}
