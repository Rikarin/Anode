using Anode.Dsp.Filters;

namespace Anode.Dsp.Effects.Multi;

/// <summary>
///     <c>anode.step-fx</c>, after Logic's Step FX: three 16-step sequencers, synced to the transport, that move a
///     filter's cutoff, the volume and the pan.
///     <list type="bullet">
///         <item>
///             The step is the song position (quarter notes, from the transport while it plays; a free-running clock at
///             the tempo while stopped) divided by the Rate note value, modulo Steps: so step 1 starts on the bar line
///             of a bar that starts on a multiple of the pattern, and a tempo change or ramp moves the steps with it.
///         </item>
///         <item>
///             Smoothing glides from the previous step's value over that share of the step (0 %: a hard step). Volume
///             and pan are per sample, so a step lands on its sample; the cutoff is set every control interval.
///         </item>
///         <item>
///             Filter: the resonant multimode filter (low-, band- or high-pass, 12 dB/octave) between Cutoff Min and
///             Cutoff Max (exponentially, by the step's 0 … 100 %). Each sequencer has an on switch.
///         </item>
///         <item>Mix crossfades dry and wet.</item>
///     </list>
///     <para>
///         Parameter ids: <c>rate</c>, <c>steps</c>, <c>tempo</c>, <c>smoothing</c>, <c>filterOn</c>, <c>filterType</c>,
///         <c>cutoffMin</c>, <c>cutoffMax</c>, <c>resonance</c>, <c>volumeOn</c>, <c>panOn</c>, <c>mix</c>; then per
///         step n = 1 … 16: <c>cutoff{n}</c>, <c>volume{n}</c> (0 … 100 %), <c>pan{n}</c> (−100 … 100).
///     </para>
/// </summary>
public sealed class StepFx : AudioEffect {
    public const string Id = "anode.step-fx";
    public const int MaxSteps = 16;

    static readonly EffectParameters P = new();
    public static readonly int Rate = P.Choice("rate", "Rate", NoteValues.Names, NoteValues.Sixteenth);
    public static readonly int Steps = P.Stepped("steps", "Steps", 1, MaxSteps, MaxSteps, string.Empty, MaxSteps - 1);
    public static readonly int Tempo = P.Add("tempo", "Tempo", 20, 300, 120, "bpm");
    public static readonly int Smoothing = P.Add("smoothing", "Smoothing", 0, 100, 10, "%");
    public static readonly int FilterOn = P.Toggle("filterOn", "Filter", true);

    public static readonly int FilterType =
        P.Choice("filterType", "Filter Type", ["Low-pass", "Band-pass", "High-pass"], 0);

    public static readonly int CutoffMin = P.Add("cutoffMin", "Cutoff Min", 20, 20_000, 300, "Hz");
    public static readonly int CutoffMax = P.Add("cutoffMax", "Cutoff Max", 20, 20_000, 8_000, "Hz");
    public static readonly int Resonance = P.Add("resonance", "Resonance", 0, 100, 30, "%");
    public static readonly int VolumeOn = P.Toggle("volumeOn", "Volume", true);
    public static readonly int PanOn = P.Toggle("panOn", "Pan", false);
    public static readonly int Mix = P.Add("mix", "Mix", 0, 100, 100, "%");

    /// <summary>Step n's cutoff (0 … 100 %), n = 0 … 15.</summary>
    public static readonly IReadOnlyList<int> CutoffSteps = [
        ..Enumerable.Range(0, MaxSteps)
            .Select(static i => P.Add($"cutoff{i + 1}", $"Cutoff {i + 1}", 0, 100, (i * 37 + 20) % 100, "%"))
    ];

    /// <summary>Step n's volume (0 … 100 %).</summary>
    public static readonly IReadOnlyList<int> VolumeSteps = [
        ..Enumerable.Range(0, MaxSteps)
            .Select(static i => P.Add(
                    $"volume{i + 1}",
                    $"Volume {i + 1}",
                    0,
                    100,
                    i % 4 == 0 ? 100 : i % 2 == 0 ? 70 : 40,
                    "%"
                )
            )
    ];

    /// <summary>Step n's pan (−100 … 100).</summary>
    public static readonly IReadOnlyList<int> PanSteps = [
        ..Enumerable.Range(0, MaxSteps)
            .Select(static i => P.Add($"pan{i + 1}", $"Pan {i + 1}", -100, 100, i % 2 == 0 ? -40 : 40, string.Empty))
    ];

    public static EffectDescriptor Info { get; } = P.Describe(
        Id,
        "Step FX",
        EffectCategories.MultiEffects,
        Rate,
        Mix,
        CutoffMin,
        CutoffMax,
        Resonance,
        Smoothing,
        Steps
    );

    readonly float[] cutoff = new float[MaxSteps];
    readonly float[] volume = new float[MaxSteps];
    readonly float[] pan = new float[MaxSteps];
    MultimodeCoefficients filter;
    MultimodeFilter filterLeft;
    MultimodeFilter filterRight;
    GainRamp mix;
    double beats;
    double smoothing;
    int steps;
    bool filterOn;
    bool volumeOn;
    bool panOn;

    public StepFx() : base(Info) { }

    protected override double ClockTempo => HostTempo(Value(Tempo));

    /// <summary>The step (0 … Steps − 1) at the last control point.</summary>
    public int CurrentStep { get; private set; }

    protected override void OnPrepare(float sampleRate) { }

    protected override void OnReset() {
        filterLeft.Reset();
        filterRight.Reset();
    }

    protected override void Update() {
        beats = NoteValues.BeatsOf(Choice(Rate));
        steps = (int)Math.Round(Value(Steps));
        smoothing = Value(Smoothing) * 0.01;
        filterOn = Switch(FilterOn);
        volumeOn = Switch(VolumeOn);
        panOn = Switch(PanOn);
        for (var s = 0; s < MaxSteps; s++) {
            cutoff[s] = Value(CutoffSteps[s]) * 0.01f;
            volume[s] = Value(VolumeSteps[s]) * 0.01f;
            pan[s] = Value(PanSteps[s]) * 0.01f;
        }

        var at = Position(QuarterAt(0));
        CurrentStep = at.Step;
        if (filterOn) {
            var amount = Step(cutoff, at);
            var low = Value(CutoffMin);
            var high = Value(CutoffMax);
            var hz = low * Math.Pow(high / low, amount);
            var mode = Choice(FilterType) switch {
                0 => FilterMode.LowPass12,
                1 => FilterMode.BandPass12,
                _ => FilterMode.HighPass12
            };
            filter = MultimodeCoefficients.Design(
                mode,
                SampleRate,
                hz,
                MultimodeCoefficients.Quality(mode, Value(Resonance) * 0.01)
            );
        }

        mix.Set(Value(Mix) * 0.01f, Snapping);
    }

    (int Step, double Fraction) Position(double quarter) {
        var position = (quarter + GridEpsilon) / beats;
        var index = Math.Floor(position);
        var step = (int)(((long)index % steps + steps) % steps);
        return (step, position - index);
    }

    double Step(float[] values, (int Step, double Fraction) at) {
        var current = values[at.Step];
        if (smoothing <= 0 || at.Fraction >= smoothing) {
            return current;
        }

        var previous = values[(at.Step + steps - 1) % steps];
        return previous + (current - previous) * (at.Fraction / smoothing);
    }

    protected override void Render(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        for (var i = 0; i < left.Length; i++) {
            var at = Position(QuarterAt(i));
            double l = left[i];
            double r = right[i];
            if (filterOn) {
                l = filterLeft.Tick(filter, l);
                r = filterRight.Tick(filter, r);
            }

            if (volumeOn) {
                var v = Step(volume, at);
                l *= v;
                r *= v;
            }

            if (panOn) {
                var angle = (Step(pan, at) + 1) * Math.PI / 4;
                l *= Math.Cos(angle) * Math.Sqrt(2);
                r *= Math.Sin(angle) * Math.Sqrt(2);
            }

            var wet = mix.Next();
            left[i] += ((float)l - left[i]) * wet;
            right[i] += ((float)r - right[i]) * wet;
        }
    }

    protected override void Finish() {
        filterLeft.FlushDenormals();
        filterRight.FlushDenormals();
    }
}
