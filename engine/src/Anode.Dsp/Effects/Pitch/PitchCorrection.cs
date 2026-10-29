using Anode.Dsp.Spectral;

namespace Anode.Dsp.Effects.Pitch;

/// <summary>
///     <c>anode.pitch-correction</c>, after Logic's Pitch Correction: pulls a monophonic voice to the nearest note of a
///     scale, in real time.
///     <list type="bullet">
///         <item>
///             <b>Detection.</b> Every <see cref="Hop" /> samples, YIN (de Cheveigné and Kawahara, 2002) on the mono sum's
///             last window: the difference function by FFT, its cumulative-mean normalisation, the first trough under
///             0.12 (refined by a parabola), periods of <see cref="MinHz" /> … <see cref="MaxHz" />. No trough under 0.25,
///             or a quiet window (below −70 dBFS), is unvoiced.
///         </item>
///         <item>
///             <b>Target.</b> The nearest note of Scale in Key to the detected pitch, plus Detune; the correction (in
///             cents) moves to it with the Response time (0: at once), and back to none while unvoiced. The
///             <see cref="IAudioEffect.Reading" /> is the correction, in cents.
///         </item>
///         <item>
///             <b>Shift.</b> TD-PSOLA: analysis marks a period apart through the input, synthesis marks a period ÷ the
///             ratio apart through the output; at each synthesis mark, the Hann-windowed two periods around the latest
///             analysis mark are overlap-added, and the sum is divided by the windows' sum. Unshifted, this is the
///             input exactly. A grain needs one period of input after its mark and lands up to a period before it, so
///             the output runs two of the longest periods behind: <see cref="LatencySamples" /> (about 25 ms).
///         </item>
///     </list>
///     Mix crossfades the equally delayed dry signal and the corrected one.
/// </summary>
public sealed class PitchCorrection : AudioEffect {
    public const string Id = "anode.pitch-correction";
    public const double MinHz = 80;
    public const double MaxHz = 1_000;
    public const int Hop = 256;
    const double Trough = 0.12;
    const double Voicing = 0.25;
    const double QuietPower = 1e-7;

    /// <summary>The scales' degrees, in semitones above the key.</summary>
    static readonly int[][] ScaleDegrees = [
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11],
        [0, 2, 4, 5, 7, 9, 11],
        [0, 2, 3, 5, 7, 8, 10],
        [0, 2, 3, 5, 7, 8, 11],
        [0, 2, 3, 5, 7, 9, 11],
        [0, 2, 3, 5, 7, 9, 10],
        [0, 2, 4, 5, 7, 9, 10],
        [0, 2, 4, 7, 9],
        [0, 3, 5, 7, 10],
        [0, 3, 5, 6, 7, 10]
    ];

    public static readonly IReadOnlyList<string> Keys =
        ["C", "C♯", "D", "D♯", "E", "F", "F♯", "G", "G♯", "A", "A♯", "B"];

    public static readonly IReadOnlyList<string> Scales = [
        "Chromatic", "Major", "Minor", "Harmonic Minor", "Melodic Minor", "Dorian", "Mixolydian", "Major Pentatonic",
        "Minor Pentatonic", "Blues"
    ];

    static readonly EffectParameters P = new();
    public static readonly int Key = P.Choice("key", "Key", Keys, 0);
    public static readonly int Scale = P.Choice("scale", "Scale", Scales, 0);
    public static readonly int Response = P.Add("response", "Response", 0, 200, 10, "ms");
    public static readonly int Detune = P.Add("detune", "Detune", -100, 100, 0, "ct");
    public static readonly int Mix = P.Add("mix", "Mix", 0, 100, 100, "%");

    public static EffectDescriptor Info { get; } =
        P.Describe(Id, "Pitch Correction", EffectCategories.Pitch, Response, Key, Scale, Detune, Mix);

    readonly bool[] inScale = new bool[12];
    RealFft fft = new(4);
    float[] inLeft = [];
    float[] inRight = [];
    float[] mono = [];
    float[] outLeft = [];
    float[] outRight = [];
    float[] weight = [];
    float[] segment = [];
    float[] head = [];
    float[] segmentRe = [];
    float[] segmentIm = [];
    float[] headRe = [];
    float[] headIm = [];
    float[] correlation = [];
    double[] energy = [];
    double[] normalised = [];
    GainRamp mix;
    int mask;
    int window;
    int minPeriod;
    int maxPeriod;
    int unvoicedPeriod;
    int latency;
    long now;
    double synth;
    double mark;
    double period;
    bool voiced;
    double cents;
    double ratio = 1;
    double target;
    double smoothing;
    double detune;

    public PitchCorrection() : base(Info) { }

    public override int LatencySamples => Prepared ? latency : 0;

    /// <summary>The pitch detected at the last analysis, in Hz (0 while unvoiced).</summary>
    public double DetectedHz => voiced ? SampleRate / period : 0;

    protected override void OnPrepare(float sampleRate) {
        maxPeriod = (int)Math.Ceiling(sampleRate / MinHz);
        minPeriod = (int)Math.Floor(sampleRate / MaxHz);
        unvoicedPeriod = (int)Math.Round(0.005 * sampleRate);
        latency = 2 * maxPeriod + 2;
        window = 1;
        while (window < maxPeriod) {
            window <<= 1;
        }

        var size = 1;
        while (size < window + maxPeriod + 2) {
            size <<= 1;
        }

        fft = new(size);
        var ring = 1;
        while (ring < Math.Max(size, latency + 2 * maxPeriod) * 2) {
            ring <<= 1;
        }

        mask = ring - 1;
        inLeft = new float[ring];
        inRight = new float[ring];
        mono = new float[ring];
        outLeft = new float[ring];
        outRight = new float[ring];
        weight = new float[ring];
        segment = new float[size];
        head = new float[size];
        segmentRe = new float[fft.Bins];
        segmentIm = new float[fft.Bins];
        headRe = new float[fft.Bins];
        headIm = new float[fft.Bins];
        correlation = new float[size];
        energy = new double[size + 1];
        normalised = new double[maxPeriod + 2];
    }

    protected override void OnReset() {
        Array.Clear(inLeft);
        Array.Clear(inRight);
        Array.Clear(mono);
        Array.Clear(outLeft);
        Array.Clear(outRight);
        Array.Clear(weight);
        now = 0;
        synth = 0;
        mark = 0;
        period = unvoicedPeriod;
        voiced = false;
        cents = 0;
        ratio = 1;
        target = 0;
        Publish(0);
    }

    protected override void Update() {
        var key = Choice(Key);
        Array.Clear(inScale);
        foreach (var degree in ScaleDegrees[Choice(Scale)]) {
            inScale[(key + degree) % 12] = true;
        }

        var response = Value(Response) * 0.001;
        smoothing = response <= 0 ? 1 : 1 - Math.Exp(-Hop / (response * SampleRate));
        detune = Value(Detune);
        mix.Set(Value(Mix) * 0.01f, Snapping);
    }

    protected override void Render(
        Span<float> left,
        Span<float> right,
        ReadOnlySpan<float> sidechainLeft,
        ReadOnlySpan<float> sidechainRight
    ) {
        for (var i = 0; i < left.Length; i++) {
            var at = (int)(now & mask);
            inLeft[at] = left[i];
            inRight[at] = right[i];
            mono[at] = 0.5f * (left[i] + right[i]);
            if ((now + 1) % Hop == 0) {
                Analyse();
            }

            Synthesise();
            var t = (int)((now - latency) & mask);
            var dryLeft = inLeft[t];
            var dryRight = inRight[t];
            var w = Math.Max(weight[t], 0.25f);
            var wetLeft = outLeft[t] / w;
            var wetRight = outRight[t] / w;
            outLeft[t] = 0;
            outRight[t] = 0;
            weight[t] = 0;
            var m = mix.Next();
            left[i] = dryLeft + (wetLeft - dryLeft) * m;
            right[i] = dryRight + (wetRight - dryRight) * m;
            now++;
        }
    }

    protected override void Finish() => Publish((float)cents);

    /// <summary>Overlap-adds the grains whose input is complete.</summary>
    void Synthesise() {
        // Both mark sequences step by the detected period itself (fractional), so the output period is exactly the
        // period ÷ the ratio; a grain covers the output samples within a period of its mark and reads the input at the
        // matching (interpolated) place around the analysis mark. With a ratio of 1 the marks coincide: the input.
        var p = voiced ? period : unvoicedPeriod;
        var read = now - latency;
        while (synth + p + 1 <= now) {
            while (mark + p <= synth) {
                mark += p;
            }

            var shift = mark - synth;
            var first = (long)Math.Floor(synth - p) + 1;
            var last = (long)Math.Ceiling(synth + p) - 1;
            for (var to = Math.Max(first, read + 1); to <= last; to++) {
                var j = to - synth;
                var w = (float)(0.5 + 0.5 * Math.Cos(Math.PI * j / p));
                var from = to + shift;
                var whole = (long)Math.Floor(from);
                var frac = (float)(from - whole);
                var a = (int)(whole & mask);
                var b = (int)((whole + 1) & mask);
                var o = (int)(to & mask);
                outLeft[o] += w * (inLeft[a] + (inLeft[b] - inLeft[a]) * frac);
                outRight[o] += w * (inRight[a] + (inRight[b] - inRight[a]) * frac);
                weight[o] += w;
            }

            synth += p / ratio;
        }
    }

    /// <summary>YIN on the window ending at the current sample; then the correction's next step.</summary>
    void Analyse() {
        var size = segment.Length;
        var span = window + maxPeriod;
        var start = now - span + 1;
        double power = 0;
        energy[0] = 0;
        for (var k = 0; k < size; k++) {
            var x = k < span ? mono[(int)((start + k) & mask)] : 0;
            segment[k] = x;
            head[k] = k < window ? x : 0;
            energy[k + 1] = energy[k] + (double)x * x;
            if (k < window) {
                power += (double)x * x;
            }
        }

        if (now + 1 < span || power / window < QuietPower) {
            Unvoiced();
            return;
        }

        // r(τ) = Σ head[j]·segment[j + τ], from conj(HEAD)·SEGMENT.
        fft.Forward(segment, segmentRe, segmentIm);
        fft.Forward(head, headRe, headIm);
        for (var k = 0; k < segmentRe.Length; k++) {
            var ar = headRe[k];
            var ai = -headIm[k];
            var br = segmentRe[k];
            var bi = segmentIm[k];
            segmentRe[k] = ar * br - ai * bi;
            segmentIm[k] = ar * bi + ai * br;
        }

        fft.Inverse(segmentRe, segmentIm, correlation);
        var e0 = energy[window];
        double running = 0;
        normalised[0] = 1;
        var found = -1;
        for (var tau = 1; tau <= maxPeriod + 1 && tau < normalised.Length; tau++) {
            var et = energy[tau + window] - energy[tau];
            var d = Math.Max(0, e0 + et - 2 * correlation[tau]);
            running += d;
            normalised[tau] = running > 0 ? d * tau / running : 1;
        }

        for (var tau = minPeriod; tau <= maxPeriod; tau++) {
            if (normalised[tau] < Trough) {
                while (tau + 1 <= maxPeriod && normalised[tau + 1] < normalised[tau]) {
                    tau++;
                }

                found = tau;
                break;
            }
        }

        if (found < 0) {
            var best = minPeriod;
            for (var tau = minPeriod + 1; tau <= maxPeriod; tau++) {
                if (normalised[tau] < normalised[best]) {
                    best = tau;
                }
            }

            if (normalised[best] >= Voicing) {
                Unvoiced();
                return;
            }

            found = best;
        }

        double refined = found;
        if (found > 1 && found < maxPeriod + 1) {
            var a = normalised[found - 1];
            var b = normalised[found];
            var c = normalised[found + 1];
            var curvature = a - 2 * b + c;
            if (curvature > 1e-12) {
                refined += Math.Clamp((a - c) / (2 * curvature), -0.5, 0.5);
            }
        }

        voiced = true;
        period = refined;
        var note = 69 + 12 * Math.Log2(SampleRate / refined / 440);
        var rounded = (int)Math.Round(note);
        double nearest = rounded;
        var bestDistance = double.MaxValue;
        for (var m = rounded - 6; m <= rounded + 6; m++) {
            if (inScale[(m % 12 + 12) % 12] && Math.Abs(m - note) < bestDistance) {
                bestDistance = Math.Abs(m - note);
                nearest = m;
            }
        }

        target = (nearest - note) * 100 + detune;
        cents += (target - cents) * smoothing;
        ratio = Math.Pow(2, cents / 1_200);
    }

    void Unvoiced() {
        voiced = false;
        target = 0;
        cents += (target - cents) * smoothing;
        ratio = Math.Pow(2, cents / 1_200);
    }
}
