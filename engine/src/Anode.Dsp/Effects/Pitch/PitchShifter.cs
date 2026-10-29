using Anode.Dsp.Delays;

namespace Anode.Dsp.Effects.Pitch;

/// <summary>
///     <c>anode.pitch-shifter</c>, after Logic's Pitch Shifter: a real-time shift of up to two octaves either way, with
///     no latency.
///     <list type="bullet">
///         <item>
///             Two read heads travel along a delay line at the speed that bends the pitch by the ratio (a delay falling
///             by r − 1 samples per sample plays r times faster), over about one Window each, half a travel apart. Each
///             fades in and out over its travel (sin²), and the two are summed normalised by their weights.
///         </item>
///         <item>
///             When a head's travel ends it restarts about half a window from the other head, at the offset (within a
///             quarter window, at most 12.5 ms either way) where the waveform best matches the other head's: the
///             normalised cross-correlation of the mono sum over a short segment (searched coarse, every fourth lag,
///             then fine around the best). So the two heads are in phase while
///             they crossfade, and a steady tone comes out at exactly the shifted pitch rather than warbling around it
///             (the synchronised overlap-add idea applied to the rotating-head shifter).
///         </item>
///         <item>At a ratio of exactly 1 the wet signal is the input. Mix crossfades dry and wet.</item>
///     </list>
/// </summary>
public sealed class PitchShifter : AudioEffect {
    public const string Id = "anode.pitch-shifter";

    static readonly EffectParameters P = new();
    public static readonly int Semitones = P.Stepped("semitones", "Semitones", -24, 24, 0, "st", 48);
    public static readonly int Cents = P.Add("cents", "Cents", -100, 100, 0, "ct");
    public static readonly int Window = P.Add("window", "Window", 10, 100, 40, "ms");
    public static readonly int Mix = P.Add("mix", "Mix", 0, 100, 100, "%");

    public static EffectDescriptor Info { get; } =
        P.Describe(Id, "Pitch Shifter", EffectCategories.Pitch, Semitones, Cents, Mix, Window);

    readonly DelayLine lineLeft = new();
    readonly DelayLine lineRight = new();
    readonly DelayLine lineMono = new();
    readonly double[] delay = new double[2];
    readonly double[] start = new double[2];
    GainRamp mix;
    double ratio = 1;
    double window;
    int direction;

    public PitchShifter() : base(Info) { }

    /// <summary>The pitch ratio at the current settings.</summary>
    public double Ratio => Math.Pow(2, (Value(Semitones) + Value(Cents) / 100.0) / 12);

    protected override void OnPrepare(float sampleRate) {
        var samples = (int)Math.Ceiling(0.13 * sampleRate) + 8;
        lineLeft.Allocate(samples);
        lineRight.Allocate(samples);
        lineMono.Allocate(samples);
    }

    protected override void OnReset() {
        lineLeft.Reset();
        lineRight.Reset();
        lineMono.Reset();
        direction = 0;
    }

    protected override void Update() {
        ratio = Ratio;
        window = Value(Window) * 0.001 * SampleRate;
        var next = ratio > 1 ? 1 : ratio < 1 ? -1 : 0;
        if (next != direction) {
            // The heads start over, half a travel apart, whenever the direction of travel changes.
            direction = next;
            for (var h = 0; h < 2; h++) {
                start[h] = direction > 0 ? window + 1 : 1;
                delay[h] = start[h] - direction * h * window / 2;
            }
        }

        mix.Set(Value(Mix) * 0.01f, Snapping);
    }

    protected override void Render(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        for (var i = 0; i < left.Length; i++) {
            lineLeft.Write(left[i]);
            lineRight.Write(right[i]);
            lineMono.Write(0.5f * (left[i] + right[i]));
            var wet = mix.Next();
            if (direction == 0) {
                continue;
            }

            float l = 0, r = 0, total = 0;
            for (var h = 0; h < 2; h++) {
                delay[h] += 1 - ratio;
                var progress = Progress(h);
                if (progress >= 1 || progress < 0) {
                    Restart(h);
                    progress = Progress(h);
                }

                var w = (float)Math.Sin(Math.PI * progress);
                w *= w;
                var d = (float)delay[h];
                l += w * lineLeft.ReadCubic(d);
                r += w * lineRight.ReadCubic(d);
                total += w;
            }

            if (total > 1e-3f) {
                l /= total;
                r /= total;
            }

            left[i] += (l - left[i]) * wet;
            right[i] += (r - right[i]) * wet;
        }
    }

    /// <summary>How far head <paramref name="h" /> is through its travel, 0 … 1.</summary>
    double Progress(int h) =>
        direction > 0
            ? (start[h] - delay[h]) / Math.Max(start[h] - 1, 1)
            : (delay[h] - start[h]) / Math.Max(window + 1 - start[h], 1);

    /// <summary>Restarts head <paramref name="h" /> about half a window from the other, where the waveforms match.</summary>
    void Restart(int h) {
        var other = delay[1 - h];
        var nominal = window / 2;
        var reach = (int)Math.Min(window / 4, 0.0125 * SampleRate);
        var segment = (int)Math.Min(256, window / 4);
        var anchor = (int)Math.Round(other);
        // Coarse to fine: every fourth lag on every other sample of the segment, then each lag around the best.
        var best = (int)nominal;
        var bestScore = double.MinValue;
        for (var lag = (int)nominal - reach; lag <= (int)nominal + reach; lag += 4) {
            Score(lag, 2, ref best, ref bestScore);
        }

        var coarse = best;
        bestScore = double.MinValue;
        for (var lag = coarse - 3; lag <= coarse + 3; lag++) {
            Score(lag, 1, ref best, ref bestScore);
        }

        // The fractional part of the other head's delay carries over, so the match is exact between samples too.
        start[h] = Math.Clamp(other + direction * best, 1, lineMono.Capacity - 4);
        delay[h] = start[h];
        return;

        void Score(int lag, int stride, ref int bestLag, ref double score) {
            var candidate = anchor + direction * lag;
            if (candidate < 1 || candidate + segment > lineMono.Capacity) {
                return;
            }

            double sum = 0, energy = 1e-12;
            for (var k = 0; k < segment; k += stride) {
                var a = lineMono.Read(anchor + k);
                var b = lineMono.Read(candidate + k);
                sum += a * b;
                energy += b * b;
            }

            var value = sum / Math.Sqrt(energy);
            if (value > score) {
                score = value;
                bestLag = lag;
            }
        }
    }
}
