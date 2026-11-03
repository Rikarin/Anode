using Anode.Dsp.Metering;

namespace Anode.Dsp.Tests.Support;

/// <summary>
///     Program material for the Mastering Assistant's tests: twelve seconds of a mix at 120 bpm in four shapes (quiet,
///     loud, bass-heavy, bright), deterministic, each brought to its own loudness.
/// </summary>
public static class Masters {
    public const int Rate = 48_000;
    public const int Seconds = 12;
    const int Beat = Rate / 2;

    public enum Kind {
        Quiet,
        Loud,
        BassHeavy,
        Bright
    }

    /// <summary>The material of <paramref name="kind" /> (stereo, at its loudness: −30, −8, −16 and −20 LUFS).</summary>
    public static (float[] Left, float[] Right) Make(Kind kind) {
        var frames = Seconds * Rate;
        var left = new float[frames];
        var right = new float[frames];
        var random = 0x2545F491u;

        float Noise() {
            random ^= random << 13;
            random ^= random >> 17;
            random ^= random << 5;
            return random * (2f / uint.MaxValue) - 1;
        }

        var (kick, snare, hats, bass, chords, lead) = kind switch {
            Kind.Quiet => (0.2f, 0.15f, 0.05f, 0.2f, 0.6f, 0f),
            Kind.Loud => (1f, 0.8f, 0.3f, 0.7f, 0.5f, 0.3f),
            Kind.BassHeavy => (1f, 0.3f, 0.05f, 1f, 0.2f, 0f),
            _ => (0.3f, 0.5f, 0.8f, 0.15f, 0.3f, 0.6f)
        };
        int[] roots = [36, 41, 43, 38];
        for (var bar = 0; bar < Seconds / 2; bar++) {
            var root = roots[bar % roots.Length];
            for (var beat = 0; beat < 4; beat++) {
                var start = (bar * 4 + beat) * Beat;
                Add(
                    left,
                    right,
                    start,
                    Rate / 3,
                    kick,
                    static (t, i) => MathF.Sin(2 * MathF.PI * (50 + 80 * MathF.Exp(-t * 30)) * t) * MathF.Exp(-t * 8)
                );
                if ((beat & 1) == 1) {
                    Add(
                        left,
                        right,
                        start,
                        Rate / 4,
                        snare,
                        (t, _) => (0.7f * Noise() + 0.3f * MathF.Sin(2 * MathF.PI * 190 * t)) * MathF.Exp(-t * 18)
                    );
                }

                for (var h = 0; h < 4; h++) {
                    var previous = 0f;
                    Add(
                        left,
                        right,
                        start + h * Beat / 4,
                        Rate / 16,
                        hats,
                        (t, _) => {
                            var white = Noise();
                            var high = white - previous; // a first difference: the highs
                            previous = white;
                            return high * 0.5f * MathF.Exp(-t * 60);
                        },
                        0.3f
                    );
                }

                var bassHz = 440 * MathF.Pow(2, (root - 69) / 12f);
                Add(
                    left,
                    right,
                    start,
                    Beat - 200,
                    bass,
                    (t, _) => {
                        var phase = bassHz * t % 1;
                        return (MathF.Sin(2 * MathF.PI * bassHz * t) + 0.3f * (2 * phase - 1))
                            * MathF.Min(1, t * 200)
                            * MathF.Exp(-t * 2);
                    }
                );
                if (lead > 0 && beat % 2 == 0) {
                    var leadHz = 440 * MathF.Pow(2, (root + 36 + beat * 2 - 69) / 12f);
                    Add(
                        left,
                        right,
                        start,
                        Beat,
                        lead,
                        (t, _) => {
                            var phase = leadHz * t % 1;
                            return (2 * phase - 1) * 0.5f * MathF.Min(1, t * 100) * MathF.Exp(-t * 3);
                        },
                        -0.4f
                    );
                }
            }

            foreach (var interval in (int[])[12, 16, 19, 24]) {
                var hz = 440 * MathF.Pow(2, (root + interval - 69) / 12f);
                Add(
                    left,
                    right,
                    bar * 4 * Beat,
                    4 * Beat,
                    chords * 0.4f,
                    (t, _) =>
                        (MathF.Sin(2 * MathF.PI * hz * t)
                            + 0.3f * MathF.Sin(4 * MathF.PI * hz * t)
                            + 0.1f * MathF.Sin(6 * MathF.PI * hz * t))
                        * MathF.Min(1, t * 20)
                        * MathF.Exp(-t * 0.8f),
                    interval % 2 == 0 ? 0.5f : -0.5f
                );
            }
        }

        if (kind == Kind.Loud) {
            // A mix already pushed: saturated, then up to just under full scale.
            for (var i = 0; i < frames; i++) {
                left[i] = MathF.Tanh(3 * left[i]);
                right[i] = MathF.Tanh(3 * right[i]);
            }
        }

        var target = kind switch {
            Kind.Quiet => -30,
            Kind.Loud => -8,
            Kind.BassHeavy => -16,
            _ => -20
        };
        var gain = (float)Math.Pow(10, (target - Integrated(left, right)) / 20);
        for (var i = 0; i < frames; i++) {
            left[i] *= gain;
            right[i] *= gain;
        }

        return (left, right);
    }

    /// <summary>The BS.1770 integrated loudness (LUFS).</summary>
    public static double Integrated(float[] left, float[] right) {
        var meter = new LoudnessMeter(Rate);
        meter.Process(left, right);
        return meter.Integrated;
    }

    /// <summary>A sound of <paramref name="length" /> frames from <paramref name="start" />, panned (−1 … 1).</summary>
    static void Add(
        float[] left,
        float[] right,
        int start,
        int length,
        float level,
        Func<float, int, float> sound,
        float pan = 0
    ) {
        if (level == 0) {
            return;
        }

        var l = level * MathF.Sqrt(0.5f * (1 - pan));
        var r = level * MathF.Sqrt(0.5f * (1 + pan));
        for (var i = 0; i < length && start + i < left.Length; i++) {
            var x = sound(i / (float)Rate, i);
            left[start + i] += l * x;
            right[start + i] += r * x;
        }
    }
}
