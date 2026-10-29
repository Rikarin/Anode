namespace Anode.Dsp.Effects.Multi;

/// <summary>
///     <c>anode.beat-breaker</c>, after Logic's Beat Breaker: cuts the incoming audio into 16 slices of a pattern synced
///     to the transport and plays them back rearranged.
///     <list type="bullet">
///         <item>
///             The pattern is Length beats long and starts on song-position multiples of its length (the transport's
///             position while it plays, a free-running clock at the tempo while stopped).
///         </item>
///         <item>
///             While slice k plays, it plays the audio of slice Source(k): from this pass of the pattern when that
///             slice has already been heard, else from the previous pass; Reverse plays it backwards; Mute silences
///             it. A slice that plays itself forwards passes the input untouched (the default pattern is the input).
///         </item>
///         <item>
///             A rearranged slice fades in and out over Fade, and so does an untouched one beside a rearranged one, so
///             the cuts do not click. The audio is read from a history of up to <see cref="MaxSeconds" />.
///         </item>
///         <item>Mix crossfades dry and wet.</item>
///     </list>
///     <para>
///         Parameter ids: <c>length</c>, <c>tempo</c>, <c>fade</c>, <c>mix</c>; then per slice n = 1 … 16:
///         <c>slice{n}Source</c> (1 … 16), <c>slice{n}Reverse</c>, <c>slice{n}Mute</c>.
///     </para>
/// </summary>
public sealed class BeatBreaker : AudioEffect {
    public const string Id = "anode.beat-breaker";
    public const int Slices = 16;
    public const double MaxSeconds = 12;

    static readonly double[] LengthBeats = [1, 2, 4, 8];
    static readonly EffectParameters P = new();
    public static readonly int Length = P.Choice("length", "Length", ["1 Beat", "2 Beats", "4 Beats", "8 Beats"], 2);
    public static readonly int Tempo = P.Add("tempo", "Tempo", 20, 300, 120, "bpm");
    public static readonly int Fade = P.Add("fade", "Fade", 0.5f, 20, 3, "ms");
    public static readonly int Mix = P.Add("mix", "Mix", 0, 100, 100, "%");

    /// <summary>Each slice's parameter indices, slice 1 first.</summary>
    public static readonly IReadOnlyList<SliceParameters> SliceParameterList =
        [..Enumerable.Range(1, Slices).Select(AddSlice)];

    public static EffectDescriptor Info { get; } =
        P.Describe(Id, "Beat Breaker", EffectCategories.MultiEffects, Length, Mix, Fade);

    readonly int[] source = new int[Slices];
    readonly bool[] reverse = new bool[Slices];
    readonly bool[] mute = new bool[Slices];
    readonly bool[] identity = new bool[Slices];
    float[] historyLeft = [];
    float[] historyRight = [];
    GainRamp mix;
    int mask;
    long written;
    double pattern;
    double slice;
    double fade;

    public BeatBreaker() : base(Info) { }

    protected override double ClockTempo => HostTempo(Value(Tempo));

    static SliceParameters AddSlice(int n) =>
        new(
            P.Stepped($"slice{n}Source", $"Slice {n} Source", 1, Slices, n, string.Empty, Slices - 1),
            P.Toggle($"slice{n}Reverse", $"Slice {n} Reverse", false),
            P.Toggle($"slice{n}Mute", $"Slice {n} Mute", false)
        );

    protected override void OnPrepare(float sampleRate) {
        var length = 1;
        while (length < MaxSeconds * sampleRate + 4) {
            length <<= 1;
        }

        historyLeft = new float[length];
        historyRight = new float[length];
        mask = length - 1;
    }

    protected override void OnReset() {
        Array.Clear(historyLeft);
        Array.Clear(historyRight);
        written = 0;
    }

    protected override void Update() {
        pattern = LengthBeats[Choice(Length)];
        slice = pattern / Slices;
        for (var k = 0; k < Slices; k++) {
            var p = SliceParameterList[k];
            source[k] = (int)Math.Round(Value(p.Source)) - 1;
            reverse[k] = Switch(p.Reverse);
            mute[k] = Switch(p.Mute);
            identity[k] = source[k] == k && !reverse[k] && !mute[k];
        }

        fade = Value(Fade) * 0.001 * SampleRate;
        mix.Set(Value(Mix) * 0.01f, Snapping);
    }

    protected override void Render(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        var perSample = QuartersPerSample;
        for (var i = 0; i < left.Length; i++) {
            var dryLeft = left[i];
            var dryRight = right[i];
            var at = (int)(written & mask);
            historyLeft[at] = dryLeft;
            historyRight[at] = dryRight;
            var q = QuarterAt(i) + GridEpsilon;
            var within = q - Math.Floor(q / pattern) * pattern;
            var k = Math.Clamp((int)(within / slice), 0, Slices - 1);
            var local = within - k * slice;
            // The fades, in samples from the slice's ends; an untouched slice only fades next to a rearranged one.
            var fromStart = local / perSample;
            var toEnd = (slice - local) / perSample;
            float wetLeft, wetRight;
            if (identity[k]) {
                var envelope = 1.0;
                if (!identity[(k + Slices - 1) % Slices]) {
                    envelope = Math.Min(envelope, fromStart / fade);
                }

                if (!identity[(k + 1) % Slices]) {
                    envelope = Math.Min(envelope, toEnd / fade);
                }

                wetLeft = envelope >= 1 ? dryLeft : (float)(dryLeft * envelope);
                wetRight = envelope >= 1 ? dryRight : (float)(dryRight * envelope);
            } else if (mute[k]) {
                wetLeft = 0;
                wetRight = 0;
            } else {
                var envelope = Math.Min(1, Math.Min(fromStart, toEnd) / fade);
                var start = q - within + source[k] * slice;
                var from = start + (reverse[k] ? slice - local : local);
                if (from > q - perSample * 0.5) {
                    from -= pattern;
                }

                var delay = (q - from) / perSample;
                if (delay >= mask - 4 || delay > written) {
                    wetLeft = 0;
                    wetRight = 0;
                } else {
                    var whole = (long)delay;
                    var frac = (float)(delay - whole);
                    var a = (int)((written - whole) & mask);
                    var b = (int)((written - whole - 1) & mask);
                    var e = (float)envelope;
                    wetLeft = e * (historyLeft[a] + (historyLeft[b] - historyLeft[a]) * frac);
                    wetRight = e * (historyRight[a] + (historyRight[b] - historyRight[a]) * frac);
                }
            }

            written++;
            var m = mix.Next();
            left[i] = dryLeft + (wetLeft - dryLeft) * m;
            right[i] = dryRight + (wetRight - dryRight) * m;
        }
    }

    /// <summary>The parameter indices of one slice.</summary>
    public sealed record SliceParameters(int Source, int Reverse, int Mute);
}
