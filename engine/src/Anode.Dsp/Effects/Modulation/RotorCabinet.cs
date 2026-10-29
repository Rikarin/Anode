using Anode.Dsp.Delays;
using Anode.Dsp.Filters;

namespace Anode.Dsp.Effects.Modulation;

/// <summary>
///     <c>anode.rotor-cabinet</c>, after Logic's Rotor Cabinet: a rotating-speaker cabinet (a Leslie) heard by two
///     microphones.
///     <list type="bullet">
///         <item>
///             A Linkwitz–Riley crossover at 800 Hz splits the mono sum between the treble horn and the bass drum.
///         </item>
///         <item>
///             Each rotor turns at its slow or fast rate (Speed: Brake stops them) and reaches a new rate exponentially:
///             the horn in Acceleration, the heavier drum in three times that.
///         </item>
///         <item>
///             Each microphone, Mic Angle apart around the cabinet, hears the horn through a delay that swings with the
///             horn's angle to it (the Doppler shift, up to ±0.6 ms, deeper the nearer the microphones: Mic Distance)
///             and an amplitude that rises as the horn faces it; the drum only through amplitude, and less of it.
///         </item>
///         <item>Balance sets the horn against the drum; Mix crossfades dry and wet.</item>
///     </list>
/// </summary>
public sealed class RotorCabinet : AudioEffect {
    public const string Id = "anode.rotor-cabinet";
    public const double Crossover = 800;

    static readonly EffectParameters P = new();
    public static readonly int Speed = P.Choice("speed", "Speed", ["Brake", "Slow", "Fast"], 1);
    public static readonly int HornSlow = P.Add("hornSlow", "Horn Slow", 0.2f, 2, 0.8f, "Hz");
    public static readonly int HornFast = P.Add("hornFast", "Horn Fast", 4, 9, 6.8f, "Hz");
    public static readonly int DrumSlow = P.Add("drumSlow", "Drum Slow", 0.2f, 2, 0.7f, "Hz");
    public static readonly int DrumFast = P.Add("drumFast", "Drum Fast", 3, 8, 5.9f, "Hz");
    public static readonly int Acceleration = P.Add("acceleration", "Acceleration", 0.1f, 5, 0.8f, "s");
    public static readonly int Distance = P.Add("distance", "Mic Distance", 0, 100, 40, "%");
    public static readonly int Angle = P.Add("angle", "Mic Angle", 0, 180, 120, "°");
    public static readonly int Balance = P.Add("balance", "Horn/Drum", -100, 100, 0, "%");
    public static readonly int Mix = P.Add("mix", "Mix", 0, 100, 100, "%");

    public static EffectDescriptor Info { get; } = P.Describe(
        Id,
        "Rotor Cabinet",
        EffectCategories.Modulation,
        Speed,
        Mix,
        Balance,
        Acceleration,
        Distance,
        Angle,
        HornFast,
        DrumFast
    );

    readonly DelayLine hornLine = new();
    readonly SvfState[] split = new SvfState[4];
    SvfCoefficients crossover;
    GainRamp hornDelayLeft;
    GainRamp hornDelayRight;
    GainRamp hornGainLeft;
    GainRamp hornGainRight;
    GainRamp drumGainLeft;
    GainRamp drumGainRight;
    GainRamp mix;
    double hornAngle;
    double drumAngle;
    double hornRate;
    double drumRate;

    public RotorCabinet() : base(Info) { }

    /// <summary>The rotors' current rates, in Hz.</summary>
    public (double Horn, double Drum) Rates => (hornRate, drumRate);

    protected override void OnPrepare(float sampleRate) =>
        hornLine.Allocate((int)Math.Ceiling(0.004 * sampleRate) + 8);

    protected override void OnReset() {
        hornLine.Reset();
        Array.Clear(split);
        hornAngle = 0;
        drumAngle = 0.3;
        hornRate = -1;
    }

    protected override void Update() {
        crossover = SvfCoefficients.Design(SampleRate, Crossover, 0.70710678118654752);
        var speed = Choice(Speed);
        var hornTarget = speed switch { 0 => 0, 1 => Value(HornSlow), _ => Value(HornFast) };
        var drumTarget = speed switch { 0 => 0, 1 => Value(DrumSlow), _ => Value(DrumFast) };
        var seconds = Value(Acceleration);
        if (hornRate < 0) {
            hornRate = hornTarget;
            drumRate = drumTarget;
        } else {
            hornRate += (hornTarget - hornRate) * (1 - Math.Exp(-ControlInterval / (seconds * SampleRate)));
            drumRate += (drumTarget - drumRate) * (1 - Math.Exp(-ControlInterval / (3 * seconds * SampleRate)));
        }

        hornAngle += hornRate * ControlInterval / SampleRate;
        hornAngle -= Math.Floor(hornAngle);
        drumAngle += drumRate * ControlInterval / SampleRate;
        drumAngle -= Math.Floor(drumAngle);

        var mic = Value(Angle) / 360.0;
        var nearness = 0.3 + 0.7 * (1 - Value(Distance) * 0.01);
        var swing = 0.0006 * SampleRate * nearness;
        var centre = 0.001 * SampleRate + swing;
        var balance = Value(Balance) * 0.01f;
        var horn = Math.Min(1, 1 - balance);
        var drum = Math.Min(1, 1 + balance);
        SetMic(hornAngle + mic / 2, drumAngle + mic / 2, ref hornDelayLeft, ref hornGainLeft, ref drumGainLeft);
        SetMic(hornAngle - mic / 2, drumAngle - mic / 2, ref hornDelayRight, ref hornGainRight, ref drumGainRight);
        mix.Set(Value(Mix) * 0.01f, Snapping);
        return;

        void SetMic(double h, double d, ref GainRamp delay, ref GainRamp hornGain, ref GainRamp drumGain) {
            var facing = Math.Cos(2 * Math.PI * h);
            delay.Set((float)(centre - swing * Math.Sin(2 * Math.PI * h)), Snapping);
            hornGain.Set((float)(horn * (0.65 + 0.35 * facing * nearness)), Snapping);
            drumGain.Set((float)(drum * (0.8 + 0.2 * Math.Cos(2 * Math.PI * d) * nearness)), Snapping);
        }
    }

    protected override void Render(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        for (var i = 0; i < left.Length; i++) {
            var x = 0.5 * (left[i] + right[i]);
            var low = split[0].Tick(crossover, x, SvfOutput.LowPass);
            low = split[1].Tick(crossover, low, SvfOutput.LowPass);
            var high = split[2].Tick(crossover, x, SvfOutput.HighPass);
            high = split[3].Tick(crossover, high, SvfOutput.HighPass);
            hornLine.Write((float)high);
            var hl = hornLine.ReadCubic(hornDelayLeft.Next()) * hornGainLeft.Next();
            var hr = hornLine.ReadCubic(hornDelayRight.Next()) * hornGainRight.Next();
            var dl = (float)low * drumGainLeft.Next();
            var dr = (float)low * drumGainRight.Next();
            var wet = mix.Next();
            left[i] += (hl + dl - left[i]) * wet;
            right[i] += (hr + dr - right[i]) * wet;
        }
    }

    protected override void Finish() {
        for (var i = 0; i < split.Length; i++) {
            split[i].FlushDenormals();
        }
    }
}
