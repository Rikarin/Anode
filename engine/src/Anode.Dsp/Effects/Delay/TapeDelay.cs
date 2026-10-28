using Anode.Dsp.Delays;
using Anode.Dsp.Filters;
using Anode.Dsp.Oscillators;

namespace Anode.Dsp.Effects.Delay;

/// <summary>
///     <c>anode.tape-delay</c>, after Logic's Tape Delay: a tape echo per channel. What goes onto the tape (the input plus
///     the feedback) is saturated by the record head (a tanh whose drive the Saturation sets, level-compensated for
///     small signals), and the repeats come back through the tape's band: a low cut and a high cut in the loop. The
///     playback head wanders: wow (a slow 0.5 Hz drift plus a smoothed random one, up to ±2 ms) and flutter (about 7 Hz,
///     up to ±0.2 ms), with the right channel's flutter a quarter cycle on. Delay in milliseconds or, with Sync, a note
///     value at the host's tempo; changing it glides like a varispeed tape.
/// </summary>
public sealed class TapeDelay : AudioEffect {
    public const string Id = "anode.tape-delay";

    const double WowHz = 0.5;
    const double FlutterHz = 7.1;
    const double WowSeconds = 0.002;
    const double FlutterSeconds = 0.0002;

    static readonly EffectParameters P = new();
    public static readonly int Tempo = P.Add("tempo", "Tempo", 20, 300, 120, "bpm");
    public static readonly int Sync = P.Toggle("sync", "Sync", false);
    public static readonly int Note = P.Choice("note", "Note", NoteValues.Names, NoteValues.DottedEighth);
    public static readonly int Time = P.Add("time", "Delay", 20, 2_000, 350, "ms");
    public static readonly int Feedback = P.Add("feedback", "Feedback", 0, 110, 40, "%");
    public static readonly int Wow = P.Add("wow", "Wow", 0, 100, 20, "%");
    public static readonly int Flutter = P.Add("flutter", "Flutter", 0, 100, 20, "%");
    public static readonly int Saturation = P.Add("saturation", "Saturation", 0, 100, 30, "%");
    public static readonly int LowCut = P.Add("lowCut", "Low Cut", 20, 2_000, 100, "Hz");
    public static readonly int HighCut = P.Add("highCut", "High Cut", 1_000, 20_000, 5_000, "Hz");
    public static readonly int Mix = P.Add("mix", "Mix", 0, 100, 30, "%");

    public static EffectDescriptor Info { get; } = P.Describe(Id, "Tape Delay", EffectCategories.Delay);

    readonly DelayLine lineLeft = new();
    readonly DelayLine lineRight = new();
    DelayGlide time;
    SvfEqCoefficients lowCut;
    SvfEqCoefficients highCut;
    SvfEqState lowLeft;
    SvfEqState lowRight;
    SvfEqState highLeft;
    SvfEqState highRight;
    GainRamp mix;
    double glide;
    double wowPhase;
    double flutterPhase;
    double wowStep;
    double flutterStep;
    double drift;
    double driftTarget;
    double driftCoefficient;
    float wowDepth;
    float flutterDepth;
    float feedback;
    float drive;
    uint random;

    public TapeDelay() : base(Info) { }

    protected override void OnPrepare(float sampleRate) {
        var samples = (int)Math.Ceiling((StereoDelay.MaxSeconds + 2 * WowSeconds) * sampleRate) + 8;
        lineLeft.Allocate(samples);
        lineRight.Allocate(samples);
        glide = 1 - Math.Exp(-ControlInterval / (0.15 * sampleRate));
        wowStep = WowHz / sampleRate;
        flutterStep = FlutterHz / sampleRate;
        driftCoefficient = 1 - Math.Exp(-1 / (0.3 * sampleRate));
    }

    protected override void OnReset() {
        lineLeft.Reset();
        lineRight.Reset();
        lowLeft.Reset();
        lowRight.Reset();
        highLeft.Reset();
        highRight.Reset();
        wowPhase = 0;
        flutterPhase = 0;
        drift = 0;
        driftTarget = 0;
        random = 0x9E3779B9;
    }

    protected override void Update() {
        var seconds = Switch(Sync) ? NoteValues.Seconds(Choice(Note), Value(Tempo)) : Value(Time) * 0.001;
        time.Update(Math.Clamp(seconds, 0.001, StereoDelay.MaxSeconds) * SampleRate - 1, glide, Snapping);
        feedback = Value(Feedback) * 0.01f;
        wowDepth = (float)(Value(Wow) * 0.01 * WowSeconds * SampleRate);
        flutterDepth = (float)(Value(Flutter) * 0.01 * FlutterSeconds * SampleRate);
        drive = 0.1f + 3.9f * Value(Saturation) * 0.01f;
        lowCut = SvfEqCoefficients.Design(SvfEqType.HighPass, SampleRate, Value(LowCut), 0.7071);
        highCut = SvfEqCoefficients.Design(SvfEqType.LowPass, SampleRate, Value(HighCut), 0.7071);
        mix.Set(Value(Mix) * 0.01f, Snapping);
        // A new random drift target every control interval, smoothed into a slow wander.
        driftTarget = PolyBlep.White(ref random);
    }

    protected override void Render(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        for (var i = 0; i < left.Length; i++) {
            drift += (driftTarget - drift) * driftCoefficient;
            var wow = wowDepth * (float)(0.7 * Math.Sin(2 * Math.PI * wowPhase) + 0.3 * drift);
            var flutterLeft = flutterDepth * (float)Math.Sin(2 * Math.PI * flutterPhase);
            var flutterRight = flutterDepth * (float)Math.Cos(2 * Math.PI * flutterPhase);
            wowPhase += wowStep;
            flutterPhase += flutterStep;
            if (wowPhase >= 1) {
                wowPhase -= 1;
            }

            if (flutterPhase >= 1) {
                flutterPhase -= 1;
            }

            var delay = time.Next();
            var l = highLeft.Tick(highCut, lowLeft.Tick(lowCut, lineLeft.ReadCubic(delay + wow + flutterLeft)));
            var r = highRight.Tick(highCut, lowRight.Tick(lowCut, lineRight.ReadCubic(delay + wow + flutterRight)));
            lineLeft.Write(MathF.Tanh(drive * (left[i] + feedback * (float)l)) / drive);
            lineRight.Write(MathF.Tanh(drive * (right[i] + feedback * (float)r)) / drive);
            var wet = mix.Next();
            left[i] += ((float)l - left[i]) * wet;
            right[i] += ((float)r - right[i]) * wet;
        }
    }

    protected override void Finish() {
        lowLeft.FlushDenormals();
        lowRight.FlushDenormals();
        highLeft.FlushDenormals();
        highRight.FlushDenormals();
    }
}
