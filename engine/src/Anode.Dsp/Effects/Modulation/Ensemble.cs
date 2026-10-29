using Anode.Dsp.Delays;

namespace Anode.Dsp.Effects.Modulation;

/// <summary>
///     <c>anode.ensemble</c>, after Logic's Ensemble: a chorus of up to eight voices, each a delayed copy of the mono sum
///     whose delay two oscillators move.
///     <list type="bullet">
///         <item>
///             Each voice's delay is Delay plus Depth × (a sine at Rate, at a rate and phase of its own, spread around
///             it) plus Drift × a slowly wandering random line (smoothed noise, its own seed), so the voices never lock.
///         </item>
///         <item>
///             The voices are panned evenly across the stereo field by Spread (constant power) and summed at
///             1/√voices; Mix crossfades dry and wet.
///         </item>
///     </list>
///     The modulation runs at control rate, the delay reads (cubic) per sample with the delay ramped across each
///     control interval.
/// </summary>
public sealed class Ensemble : AudioEffect {
    public const string Id = "anode.ensemble";
    public const int MaxVoices = 8;

    static readonly EffectParameters P = new();
    public static readonly int Voices = P.Stepped("voices", "Voices", 2, MaxVoices, 4, string.Empty, MaxVoices - 2);
    public static readonly int Rate = P.Add("rate", "Rate", 0.05f, 5, 0.6f, "Hz");
    public static readonly int Depth = P.Add("depth", "Depth", 0, 100, 40, "%");
    public static readonly int Drift = P.Add("drift", "Drift", 0, 100, 30, "%");
    public static readonly int Delay = P.Add("delay", "Delay", 5, 40, 15, "ms");
    public static readonly int Spread = P.Add("spread", "Spread", 0, 100, 100, "%");
    public static readonly int Mix = P.Add("mix", "Mix", 0, 100, 50, "%");

    public static EffectDescriptor Info { get; } =
        P.Describe(Id, "Ensemble", EffectCategories.Modulation, Mix, Depth, Rate, Voices, Spread, Drift, Delay);

    readonly DelayLine line = new();
    readonly double[] phase = new double[MaxVoices];
    readonly double[] wander = new double[MaxVoices];
    readonly double[] wanderTarget = new double[MaxVoices];
    readonly uint[] random = new uint[MaxVoices];
    readonly int[] untilTarget = new int[MaxVoices];
    readonly GainRamp[] delay = new GainRamp[MaxVoices];
    readonly GainRamp[] gainLeft = new GainRamp[MaxVoices];
    readonly GainRamp[] gainRight = new GainRamp[MaxVoices];
    GainRamp mix;
    int voices;

    public Ensemble() : base(Info) { }

    protected override void OnPrepare(float sampleRate) =>
        line.Allocate((int)Math.Ceiling(0.06 * sampleRate) + 8);

    protected override void OnReset() {
        line.Reset();
        for (var v = 0; v < MaxVoices; v++) {
            phase[v] = v / (double)MaxVoices;
            random[v] = 0x6C8E9CF5u * (uint)(v + 1);
            wander[v] = 0;
            wanderTarget[v] = 0;
            untilTarget[v] = 0;
        }
    }

    protected override void Update() {
        voices = (int)Math.Round(Value(Voices));
        var depth = Value(Depth) * 0.01 * 0.004 * SampleRate; // up to ±4 ms
        var drift = Value(Drift) * 0.01 * 0.003 * SampleRate; // up to ±3 ms
        var centre = Value(Delay) * 0.001 * SampleRate;
        var spread = Value(Spread) * 0.01;
        var level = 1 / MathF.Sqrt(voices);
        var wanderStep = 1 - Math.Exp(-ControlInterval / (0.4 * SampleRate));
        for (var v = 0; v < MaxVoices; v++) {
            // Rates spread ±30 % around Rate, so the voices drift apart.
            var rate = Value(Rate) * (0.7 + 0.6 * v / (MaxVoices - 1.0));
            phase[v] += rate * ControlInterval / SampleRate;
            phase[v] -= Math.Floor(phase[v]);
            // A new random target every 0.2 … 0.5 s, its own interval per voice.
            if (--untilTarget[v] <= 0) {
                random[v] ^= random[v] << 13;
                random[v] ^= random[v] >> 17;
                random[v] ^= random[v] << 5;
                wanderTarget[v] = random[v] * (2.0 / uint.MaxValue) - 1;
                untilTarget[v] = (int)((0.2 + 0.3 * (random[v] & 0xFFFF) / 65_535.0) * SampleRate / ControlInterval);
            }

            wander[v] += (wanderTarget[v] - wander[v]) * wanderStep;
            var d = centre + depth * Math.Sin(2 * Math.PI * phase[v]) + drift * wander[v];
            delay[v].Set((float)Math.Max(1, d), Snapping);
            var position = voices > 1 ? v / (voices - 1.0) * 2 - 1 : 0;
            var angle = (float)((position * spread + 1) * Math.PI / 4);
            var on = v < voices ? level : 0;
            gainLeft[v].Set(on * MathF.Cos(angle), Snapping);
            gainRight[v].Set(on * MathF.Sin(angle), Snapping);
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
            line.Write(0.5f * (left[i] + right[i]));
            float l = 0, r = 0;
            for (var v = 0; v < MaxVoices; v++) {
                var d = delay[v].Next();
                var gl = gainLeft[v].Next();
                var gr = gainRight[v].Next();
                if (v >= voices) {
                    continue;
                }

                var y = line.ReadCubic(d);
                l += y * gl;
                r += y * gr;
            }

            var wet = mix.Next();
            left[i] += (l * 1.41421356f - left[i]) * wet;
            right[i] += (r * 1.41421356f - right[i]) * wet;
        }
    }
}
