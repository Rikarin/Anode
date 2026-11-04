using Anode.Dsp.Filters;
using Anode.Dsp.Oscillators;

namespace Anode.Dsp.Effects.Utility;

/// <summary>
///     <c>anode.test-oscillator</c>: a signal to measure a chain with, in place of the input. A sine, or a triangle,
///     square
///     or sawtooth band-limited by polyBLEP, at Frequency; white noise; or pink noise, white noise through low-passes two
///     octaves apart, each weighted by the root of its period, which falls 3 dB an octave. Sweep replaces Frequency with a
///     logarithmic sweep from <see cref="SweepLow" /> to <see cref="SweepHigh" /> Hz over Sweep Time, repeating. Level is
///     the waveform's peak in dBFS. Channel picks where it goes; the other channel is silent. The
///     <see cref="IAudioEffect.Reading" /> is its peak (dBFS) over the last buffer.
/// </summary>
public sealed class TestOscillator : AudioEffect {
    public const string Id = "anode.test-oscillator";
    public const double SweepLow = 20;
    public const double SweepHigh = 20_000;
    public const int Sine = 0;
    public const int Triangle = 1;
    public const int Square = 2;
    public const int Sawtooth = 3;
    public const int WhiteNoise = 4;
    public const int PinkNoise = 5;

    /// <summary>The pink filter's lowest corner, Hz; each next one is two octaves up.</summary>
    const double PinkLow = 20;

    const int PinkPoles = 6;

    /// <summary>A pink sample's level is scaled to about the white noise's.</summary>
    const float PinkScale = 0.45f;

    static readonly EffectParameters P = new();

    public static readonly int Wave = P.Choice(
        "waveform",
        "Waveform",
        ["Sine", "Triangle", "Square", "Sawtooth", "White Noise", "Pink Noise"],
        Sine
    );

    public static readonly int Frequency = P.Add("frequency", "Frequency", 20, 20_000, 1_000, "Hz");
    public static readonly int Level = P.Add("level", "Level", -96, 0, -18, "dB");
    public static readonly int Output = P.Choice("channel", "Channel", ["Both", "Left", "Right"], 0);
    public static readonly int Sweep = P.Toggle("sweep", "Sweep", false);
    public static readonly int SweepTime = P.Add("sweepTime", "Sweep Time", 1, 60, 10, "s");

    public static EffectDescriptor Info { get; } =
        P.Describe(Id, "Test Oscillator", EffectCategories.Utility, Frequency, Level, Wave);

    readonly double[] pinkCoefficients = new double[PinkPoles];
    readonly double[] pinkWeights = new double[PinkPoles];
    readonly OnePole[] pink = new OnePole[PinkPoles];
    GainRamp level;
    double phase;
    double increment;
    double sweepRatio = 1;
    double sweepIncrement;
    double sweepStart;
    long sweepLeft;
    long sweepLength;
    int wave;
    bool sweeping;
    float toLeft;
    float toRight;
    float peak;
    uint noise = 1;

    public TestOscillator() : base(Info) { }

    protected override void OnPrepare(float sampleRate) {
        for (var i = 0; i < PinkPoles; i++) {
            var corner = PinkLow * Math.Pow(4, i);
            pinkCoefficients[i] = OnePole.Coefficient(sampleRate, corner);
            pinkWeights[i] = Math.Sqrt(PinkLow / corner);
        }
    }

    protected override void OnReset() {
        foreach (ref var pole in pink.AsSpan()) {
            pole.Reset();
        }

        phase = 0;
        noise = 0x2545F491;
        sweepLeft = 0;
        peak = 0;
        Publish(FloorDb);
    }

    protected override void Update() {
        wave = Choice(Wave);
        increment = Value(Frequency) / SampleRate;
        level.Set(Decibels.ToGain(Value(Level)), Snapping);
        var channel = Choice(Output);
        toLeft = channel == 2 ? 0 : 1;
        toRight = channel == 1 ? 0 : 1;
        var sweep = Switch(Sweep);
        if (sweep && !sweeping) {
            sweepLeft = 0;
        }

        sweeping = sweep;
        sweepLength = (long)(Value(SweepTime) * SampleRate);
        sweepRatio = Math.Pow(SweepHigh / SweepLow, 1.0 / sweepLength);
        sweepStart = SweepLow / SampleRate;
    }

    protected override void Render(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        var max = peak;
        for (var i = 0; i < left.Length; i++) {
            var step = increment;
            if (sweeping) {
                if (sweepLeft <= 0) {
                    sweepLeft = sweepLength;
                    sweepIncrement = sweepStart;
                }

                sweepLeft--;
                step = sweepIncrement;
                sweepIncrement *= sweepRatio;
            }

            var sample = Next(step) * level.Next();
            left[i] = sample * toLeft;
            right[i] = sample * toRight;
            max = MathF.Max(max, MathF.Abs(sample));
        }

        peak = max;
    }

    protected override void Finish() {
        Publish(Decibels.FromGain(peak));
        peak = 0;
    }

    float Next(double step) {
        if (wave == WhiteNoise) {
            return PolyBlep.White(ref noise);
        }

        if (wave == PinkNoise) {
            double white = PolyBlep.White(ref noise);
            double sum = 0;
            for (var p = 0; p < PinkPoles; p++) {
                sum += pinkWeights[p] * pink[p].LowPass(pinkCoefficients[p], white);
            }

            return Math.Clamp((float)sum * PinkScale, -1, 1);
        }

        var sample = wave switch {
            Sine => PolyBlep.Sine(phase),
            Triangle => PolyBlep.Triangle(phase, step),
            Square => PolyBlep.Pulse(phase, step, 0.5),
            _ => PolyBlep.Saw(phase, step)
        };
        phase += step;
        if (phase >= 1) {
            phase -= 1;
        }

        return sample;
    }
}
