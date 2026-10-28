using Anode.Dsp.Effects;

namespace Anode.Dsp.Tests.Support;

/// <summary>Runs the built-in effects for the tests.</summary>
public static class Fx {
    public const float Rate = Play.Rate;

    /// <summary>A new, prepared instance of <paramref name="id" /> with <paramref name="parameters" /> set.</summary>
    public static IAudioEffect Create(string id, params (int Index, float Value)[] parameters) {
        var effect = BuiltinEffects.Create(id) ?? throw new InvalidOperationException(id);
        effect.Prepare(Rate, 1_024);
        foreach (var (index, value) in parameters) {
            effect.SetParameter(index, value);
        }

        return effect;
    }

    /// <summary>
    ///     Processes copies of <paramref name="left" /> and <paramref name="right" /> in buffers of
    ///     <paramref name="block" /> frames (or the sizes <paramref name="blocks" /> cycles through), with an optional
    ///     sidechain.
    /// </summary>
    public static (float[] Left, float[] Right) Run(
        IAudioEffect effect,
        float[] left,
        float[]? right = null,
        int block = Play.Block,
        int[]? blocks = null,
        float[]? sidechainLeft = null,
        float[]? sidechainRight = null
    ) {
        var l = (float[])left.Clone();
        var r = (float[])(right ?? left).Clone();
        var cycle = 0;
        for (var at = 0; at < l.Length;) {
            var count = Math.Min(blocks is null ? block : blocks[cycle++ % blocks.Length], l.Length - at);
            effect.Process(
                l.AsSpan(at, count),
                r.AsSpan(at, count),
                sidechainLeft is null ? default : sidechainLeft.AsSpan(at, count),
                (sidechainRight ?? sidechainLeft) is { } sideRight ? sideRight.AsSpan(at, count) : default
            );
            at += count;
        }

        return (l, r);
    }

    public static float[] Impulse(int frames, int at = 0, float height = 1) {
        var x = new float[frames];
        x[at] = height;
        return x;
    }

    /// <summary>
    ///     A second and a half of deterministic program material: plucked chords, noise-burst drum hits with a low
    ///     thump, and a sine sweep under them; the right channel differs from the left.
    /// </summary>
    public static (float[] Left, float[] Right) Music(int frames = 72_000) {
        var left = new float[frames];
        var right = new float[frames];
        var notes = new[] { 130.81, 164.81, 196.0, 261.63, 329.63 };
        for (var n = 0; n < notes.Length; n++) {
            var pluck = TestWav.Pluck(notes[n], (int)Rate, frames / (double)Rate + 0.01, n + 1);
            for (var i = 0; i < frames; i++) {
                var start = n * 1_500;
                if (i >= start) {
                    left[i] += 0.25f * pluck[i - start] * (n % 2 == 0 ? 1 : 0.6f);
                    right[i] += 0.25f * pluck[i - start] * (n % 2 == 0 ? 0.6f : 1);
                }
            }
        }

        var random = 0x1234567u;
        for (var hit = 0; hit * 12_000 < frames; hit++) {
            for (var i = 0; i < 6_000 && hit * 12_000 + i < frames; i++) {
                var at = hit * 12_000 + i;
                var envelope = MathF.Exp(-i / 900f);
                random ^= random << 13;
                random ^= random >> 17;
                random ^= random << 5;
                var noise = random * (2f / uint.MaxValue) - 1;
                var thump = MathF.Sin(2 * MathF.PI * 55 * i / Rate) * MathF.Exp(-i / 2_500f);
                var hitLevel = hit % 2 == 0 ? 0.5f : 0.3f;
                left[at] += hitLevel * (0.6f * noise * envelope + thump);
                right[at] += hitLevel * (0.5f * noise * envelope + thump);
            }
        }

        double phase = 0;
        for (var i = 0; i < frames; i++) {
            var f = 100 * Math.Pow(80, i / (double)frames);
            phase += f / Rate;
            var s = 0.08f * (float)Math.Sin(2 * Math.PI * phase);
            left[i] += s;
            right[i] -= s;
        }

        return (left, right);
    }

    /// <summary>
    ///     The true peak by 4× oversampling: a windowed-sinc interpolator (64 taps either side, Blackman-Harris),
    ///     far finer than a meter's.
    /// </summary>
    public static double TruePeak(ReadOnlySpan<float> signal) {
        const int half = 64;
        double peak = 0;
        var kernels = new double[3][];
        for (var p = 1; p <= 3; p++) {
            var k = new double[2 * half];
            var f = p / 4.0;
            for (var j = 0; j < 2 * half; j++) {
                var t = j - (half - 1) - f;
                var sinc = Math.Sin(Math.PI * t) / (Math.PI * t);
                var x = (t + half) / (2.0 * half);
                var w = 0.35875 - 0.48829 * Math.Cos(2 * Math.PI * x) + 0.14128 * Math.Cos(4 * Math.PI * x)
                    - 0.01168 * Math.Cos(6 * Math.PI * x);
                k[j] = sinc * w;
            }

            kernels[p - 1] = k;
        }

        for (var c = 0; c < signal.Length; c++) {
            peak = Math.Max(peak, Math.Abs(signal[c]));
            for (var p = 0; p < 3; p++) {
                double sum = 0;
                for (var j = 0; j < 2 * half; j++) {
                    var at = c + j - (half - 1);
                    if (at >= 0 && at < signal.Length) {
                        sum += kernels[p][j] * signal[at];
                    }
                }

                peak = Math.Max(peak, Math.Abs(sum));
            }
        }

        return peak;
    }
}
