using Anode.Dsp.Delays;
using Anode.Dsp.Filters;

namespace Anode.Dsp.Effects.Multi;

/// <summary>
///     <c>anode.remix-fx</c>, after Logic's Remix FX (docs/07-phase-6.md, "Remix FX"): two XY pads for performing on the
///     master, synced to the transport. Every control is a parameter, so a performance automates and records.
///     <para>
///         A pad works while its <c>Active</c> switch is on (a finger on it) and fades in and out over 5 ms; with both
///         off the audio passes untouched. Pad 2 (time effects) runs before pad 1 (filters). Note values below are
///         lengths at the transport's tempo (the <c>tempo</c> parameter until the engine passes one), and phases follow
///         the song position, so the effects land on the grid.
///     </para>
///     <list type="bullet">
///         <item>
///             <b>Pad 1.</b> Filter: X left of centre is a low-pass from 20 kHz down to 80 Hz, right of centre a high-pass
///             from 20 Hz up to 8 kHz; Y the resonance. Wobble: a resonant low-pass whose cutoff an LFO swings (X picks
///             1/1 … 1/32, Y the depth, up to ±4 octaves around 800 Hz). Orbit: a flanger (1–7 ms) whose sweep is synced
///             (X picks 2/1 … 1/8), Y the feedback.
///         </item>
///         <item>
///             <b>Pad 2.</b> Repeater: on touch, the grid division just begun (X picks 1/2 … 1/32) is captured and
///             repeated (each pass crossfades from the last over 2 ms); Y the repeat's level against the input. Reverse:
///             the input, played backwards a chunk at a time
///             (X picks 1/1 … 1/8); Y the level. Scratch: the read position swings back and forth on the grid (X picks
///             1/2 … 1/16), Y the depth. Gater: the level chops on the grid (X picks 1/4 … 1/32), Y the depth.
///             Downsampler: X the rate reduction (1 … 64×), Y the bit reduction (24 … 2 bits). Tape Stop: on touch the
///             tape slows to a stop over X (4 beats … 1/16), Y the level; letting go starts it again at once.
///         </item>
///     </list>
/// </summary>
public sealed class RemixFx : AudioEffect {
    public const string Id = "anode.remix-fx";
    public const double MaxSeconds = 4;

    public const int Filter = 0;
    public const int Wobble = 1;
    public const int Orbit = 2;
    public const int Repeater = 0;
    public const int Reverse = 1;
    public const int Scratch = 2;
    public const int Gater = 3;
    public const int Downsampler = 4;
    public const int TapeStop = 5;

    static readonly double[] WobbleBeats = [4, 2, 1, 0.5, 0.25, 0.125];
    static readonly double[] OrbitBeats = [8, 4, 2, 1, 0.5];
    static readonly double[] RepeaterBeats = [2, 1, 0.5, 0.25, 0.125];
    static readonly double[] ReverseBeats = [4, 2, 1, 0.5];
    static readonly double[] ScratchBeats = [2, 1, 0.5, 0.25];
    static readonly double[] GaterBeats = [1, 0.5, 0.25, 0.125];
    static readonly double[] StopBeats = [4, 2, 1, 0.5, 0.25];

    static readonly EffectParameters P = new();
    public static readonly int Pad1Active = P.Toggle("xy1Active", "Pad 1", false);
    public static readonly int Pad1Effect = P.Choice("xy1Effect", "Pad 1 Effect", ["Filter", "Wobble", "Orbit"], 0);
    public static readonly int Pad1X = P.Add("xy1X", "Pad 1 X", 0, 1, 0.5f, string.Empty);
    public static readonly int Pad1Y = P.Add("xy1Y", "Pad 1 Y", 0, 1, 0.5f, string.Empty);
    public static readonly int Pad2Active = P.Toggle("xy2Active", "Pad 2", false);

    public static readonly int Pad2Effect = P.Choice(
        "xy2Effect",
        "Pad 2 Effect",
        ["Repeater", "Reverse", "Scratch", "Gater", "Downsampler", "Tape Stop"],
        0
    );

    public static readonly int Pad2X = P.Add("xy2X", "Pad 2 X", 0, 1, 0.5f, string.Empty);
    public static readonly int Pad2Y = P.Add("xy2Y", "Pad 2 Y", 0, 1, 1, string.Empty);
    public static readonly int Tempo = P.Add("tempo", "Tempo", 20, 300, 120, "bpm");

    public static EffectDescriptor Info { get; } = P.Describe(
        Id,
        "Remix FX",
        EffectCategories.MultiEffects,
        Pad1X,
        Pad1Y,
        Pad2X,
        Pad2Y,
        Pad1Active,
        Pad2Active,
        Pad1Effect,
        Pad2Effect
    );

    readonly DelayLine orbitLeft = new();
    readonly DelayLine orbitRight = new();
    float[] historyLeft = [];
    float[] historyRight = [];
    MultimodeCoefficients filter;
    MultimodeFilter filterLeft;
    MultimodeFilter filterRight;
    GainRamp amount1;
    GainRamp amount2;
    GainRamp orbitDelay;
    double level1;
    double level2;
    double fadeStep;
    int mask;
    long written;
    int effect1;
    int effect2;
    bool active1;
    bool active2;
    double x2;
    double y2;
    double feedback;
    long anchor;
    long loop;
    int division;
    double stopSpeed;
    double stopStep;
    double stopDelay;
    double gate;
    double gateStep;
    double holdLeft;
    double holdRight;
    double holdCount;
    double holdFactor;
    double quantum;

    public RemixFx() : base(Info) { }

    protected override double ClockTempo => HostTempo(Value(Tempo));

    /// <summary>The repeater's loop at its capture, in samples (0 until one).</summary>
    public long RepeatLength => loop;

    static int Pick(double x, int count) => Math.Clamp((int)(x * count), 0, count - 1);

    protected override void OnPrepare(float sampleRate) {
        var length = 1;
        while (length < MaxSeconds * sampleRate + 4) {
            length <<= 1;
        }

        historyLeft = new float[length];
        historyRight = new float[length];
        mask = length - 1;
        orbitLeft.Allocate((int)Math.Ceiling(0.01 * sampleRate) + 8);
        orbitRight.Allocate((int)Math.Ceiling(0.01 * sampleRate) + 8);
        fadeStep = 1 - Math.Exp(-ControlInterval / (0.005 * sampleRate));
        gateStep = 1 - Math.Exp(-1 / (0.001 * sampleRate));
    }

    protected override void OnReset() {
        Array.Clear(historyLeft);
        Array.Clear(historyRight);
        orbitLeft.Reset();
        orbitRight.Reset();
        filterLeft.Reset();
        filterRight.Reset();
        written = 0;
        level1 = 0;
        level2 = 0;
        active1 = false;
        active2 = false;
        loop = 0;
        gate = 1;
        holdCount = 0;
    }

    protected override void Update() {
        var was2 = active2;
        var effectWas2 = effect2;
        active1 = Switch(Pad1Active);
        active2 = Switch(Pad2Active);
        effect1 = Choice(Pad1Effect);
        effect2 = Choice(Pad2Effect);
        double on1 = active1 ? 1 : 0;
        double on2 = active2 ? 1 : 0;
        level1 = Snapping ? on1 : level1 + (on1 - level1) * fadeStep;
        level2 = Snapping ? on2 : level2 + (on2 - level2) * fadeStep;
        if (level1 < 1e-4 && !active1) {
            level1 = 0;
        }

        if (level2 < 1e-4 && !active2) {
            level2 = 0;
        }

        amount1.Set((float)level1, Snapping);
        amount2.Set((float)level2, Snapping);
        var perSample = QuartersPerSample;
        var q = QuarterAt(0);
        x2 = Value(Pad2X);
        y2 = Value(Pad2Y);
        var touched = active2 && (!was2 || effect2 != effectWas2);
        switch (effect2) {
            case Repeater: {
                var choice = Pick(x2, RepeaterBeats.Length);
                if (touched || choice != division) {
                    // Capture the grid division just begun.
                    division = choice;
                    var beats = RepeaterBeats[choice];
                    loop = Math.Max(1, (long)Math.Round(beats / perSample));
                    var into = q - Math.Floor(q / beats) * beats;
                    anchor = written - (long)Math.Round(into / perSample);
                }

                break;
            }
            case Reverse:
                if (touched) {
                    anchor = written;
                }

                loop = Math.Max(1, (long)Math.Round(ReverseBeats[Pick(x2, ReverseBeats.Length)] / perSample));
                break;
            case Scratch:
                quantum = ScratchBeats[Pick(x2, ScratchBeats.Length)];
                break;
            case Gater:
                quantum = GaterBeats[Pick(x2, GaterBeats.Length)];
                break;
            case Downsampler:
                holdFactor = Math.Pow(64, x2);
                break;
            case TapeStop:
                if (touched) {
                    stopSpeed = 1;
                    stopDelay = 0;
                }

                stopStep = perSample / StopBeats[Pick(x2, StopBeats.Length)];
                break;
        }

        // Pad 1.
        var x1 = Value(Pad1X);
        var y1 = Value(Pad1Y);
        switch (effect1) {
            case Filter: {
                var high = x1 > 0.5;
                var hz = high ? 20 * Math.Pow(400, (x1 - 0.5) * 2) : 80 * Math.Pow(250, x1 * 2);
                var mode = high ? FilterMode.HighPass12 : FilterMode.LowPass12;
                filter = MultimodeCoefficients.Design(
                    mode,
                    SampleRate,
                    hz,
                    MultimodeCoefficients.Quality(mode, 0.9 * y1)
                );
                break;
            }
            case Wobble: {
                var beats = WobbleBeats[Pick(x1, WobbleBeats.Length)];
                var lfo = Math.Sin(2 * Math.PI * (q / beats));
                var hz = Math.Clamp(800 * Math.Pow(2, 4 * y1 * lfo), 20, 20_000);
                filter = MultimodeCoefficients.Design(
                    FilterMode.LowPass24,
                    SampleRate,
                    hz,
                    MultimodeCoefficients.Quality(FilterMode.LowPass24, 0.6)
                );
                break;
            }
            default: {
                var beats = OrbitBeats[Pick(x1, OrbitBeats.Length)];
                var sweep = 0.5 - 0.5 * Math.Cos(2 * Math.PI * (q / beats));
                orbitDelay.Set((float)((0.001 + 0.006 * sweep) * SampleRate), Snapping);
                feedback = 0.85 * y1;
                break;
            }
        }
    }

    protected override void Render(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        for (var i = 0; i < left.Length; i++) {
            var l = left[i];
            var r = right[i];
            var at = (int)(written & mask);
            historyLeft[at] = l;
            historyRight[at] = r;
            var a2 = amount2.Next();
            var a1 = amount1.Next();
            if (a2 > 0 || active2) {
                var (wl, wr) = Pad2(l, r, i);
                l += (wl - l) * a2;
                r += (wr - r) * a2;
            }

            if (a1 > 0 || active1) {
                float wl, wr;
                if (effect1 == Orbit) {
                    var d = orbitDelay.Next();
                    var ol = orbitLeft.ReadCubic(d);
                    var or = orbitRight.ReadCubic(d);
                    orbitLeft.Write(l + (float)feedback * ol);
                    orbitRight.Write(r + (float)feedback * or);
                    wl = 0.5f * (l + ol);
                    wr = 0.5f * (r + or);
                } else {
                    orbitLeft.Write(l);
                    orbitRight.Write(r);
                    wl = (float)filterLeft.Tick(filter, l);
                    wr = (float)filterRight.Tick(filter, r);
                }

                l += (wl - l) * a1;
                r += (wr - r) * a1;
            }

            left[i] = l;
            right[i] = r;
            written++;
        }
    }

    /// <summary>Pad 2's effect on the current sample (frame <paramref name="frame" /> of the render).</summary>
    (float Left, float Right) Pad2(float l, float r, int frame) {
        switch (effect2) {
            case Repeater: {
                if (loop <= 0) {
                    return (l, r);
                }

                var since = written - anchor;
                var pass = since / loop;
                if (pass <= 0) {
                    return (l, r);
                }

                // Each pass starts with a 2 ms crossfade from the previous one running on (the input, for the first).
                var into = since - pass * loop;
                var (rl, rr) = Read(pass * loop);
                var fade = 0.002 * SampleRate;
                if (into < fade) {
                    var (pl, pr) = pass == 1 ? (l, r) : Read((pass - 1) * loop);
                    var f = (float)(into / fade);
                    rl = pl + (rl - pl) * f;
                    rr = pr + (rr - pr) * f;
                }

                var y = (float)y2;
                return (l + (rl - l) * y, r + (rr - r) * y);
            }
            case Reverse: {
                var since = written - anchor;
                var into = since % loop;
                var (rl, rr) = Read(2 * into + 1);
                var fade = (float)Math.Min(1.0, Math.Min(into, loop - into) / (0.002 * SampleRate));
                var y = (float)y2;
                return (l + (rl * fade - l) * y, r + (rr * fade - r) * y);
            }
            case Scratch: {
                var q = QuarterAt(frame);
                var swing = 0.5 - 0.5 * Math.Cos(2 * Math.PI * (q / quantum));
                var delay = y2 * swing * quantum * 0.5 / QuartersPerSample;
                return ReadFractional(delay);
            }
            case Gater: {
                var q = QuarterAt(frame) + GridEpsilon;
                var open = q / quantum - Math.Floor(q / quantum) < 0.5;
                var target = open ? 1 : 1 - y2;
                gate += (target - gate) * gateStep;
                return ((float)(l * gate), (float)(r * gate));
            }
            case Downsampler: {
                holdCount -= 1;
                if (holdCount <= 0) {
                    holdCount += holdFactor;
                    var bits = 24 - 22 * y2;
                    var steps = Math.Pow(2, bits - 1);
                    holdLeft = Math.Round(l * steps) / steps;
                    holdRight = Math.Round(r * steps) / steps;
                }

                return ((float)holdLeft, (float)holdRight);
            }
            default: {
                if (!active2) {
                    return (l, r);
                }

                stopSpeed = Math.Max(0, stopSpeed - stopStep);
                stopDelay = Math.Min(stopDelay + 1 - stopSpeed, mask - 8);
                var (rl, rr) = ReadFractional(stopDelay);
                // Fades out over the last quarter of the slow-down, where the tape barely moves.
                var e = (float)Math.Min(1, 4 * stopSpeed);
                var y = (float)y2;
                return (l + (rl * e - l) * y, r + (rr * e - r) * y);
            }
        }
    }

    (float, float) Read(long delay) {
        if (delay > mask - 4 || delay > written) {
            return (0, 0);
        }

        var at = (int)((written - delay) & mask);
        return (historyLeft[at], historyRight[at]);
    }

    (float, float) ReadFractional(double delay) {
        if (delay > mask - 4 || delay > written) {
            return (0, 0);
        }

        var whole = (long)delay;
        var frac = (float)(delay - whole);
        var a = (int)((written - whole) & mask);
        var b = (int)((written - whole - 1) & mask);
        return (historyLeft[a] + (historyLeft[b] - historyLeft[a]) * frac,
            historyRight[a] + (historyRight[b] - historyRight[a]) * frac);
    }

    protected override void Finish() {
        filterLeft.FlushDenormals();
        filterRight.FlushDenormals();
    }
}
