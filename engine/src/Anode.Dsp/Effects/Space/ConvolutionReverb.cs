using Anode.Dsp.Delays;
using Anode.Dsp.Filters;
using Anode.Dsp.IO;
using Anode.Dsp.Instruments;
using Anode.Dsp.Spectral;

namespace Anode.Dsp.Effects.Space;

/// <summary>
///     <c>anode.space</c>, after Logic's Space Designer: a convolution reverb. Each channel is convolved with its channel
///     of an impulse response (a mono response feeds both) by a <see cref="PartitionedConvolver" />: uniformly
///     partitioned FFT convolution behind a direct-form head, so the reverb adds no latency. The partition size follows
///     the response's length (about √(4·taps), 64 … 1,024), which balances the head's cost against the FFTs'.
///     <list type="bullet">
///         <item>
///             The response is a built-in hall (2.5 s, generated: early reflections, then decorrelated noise decaying
///             at a falling rate in the highs) until <see cref="LoadFile" /> loads a WAV (any rate, resampled; at most
///             <see cref="MaxSeconds" />), whose energy is normalised to the built-in's.
///         </item>
///         <item>
///             Pre-delay delays the wet path. Length shortens the response: partitions past it are dropped and the last
///             quarter before it fades out (at 100 % the response is whole).
///         </item>
///         <item>
///             Low and high cut (12 dB/octave, off at the ends of their ranges) filter the wet signal; Mix crossfades.
///         </item>
///     </list>
///     The state carries the loaded file's path (as the Sampler's does), and loading a state loads the file.
/// </summary>
public sealed class ConvolutionReverb : AudioEffect, IFileEffect {
    public const string Id = "anode.space";
    public const double MaxSeconds = 10;
    public const double BuiltinSeconds = 2.5;

    static readonly EffectParameters P = new();
    public static readonly int Predelay = P.Add("predelay", "Pre-delay", 0, 500, 10, "ms");
    public static readonly int Length = P.Add("length", "Length", 5, 100, 100, "%");
    public static readonly int LowCut = P.Add("lowCut", "Low Cut", 20, 2_000, 20, "Hz");
    public static readonly int HighCut = P.Add("highCut", "High Cut", 1_000, 20_000, 20_000, "Hz");
    public static readonly int Mix = P.Add("mix", "Mix", 0, 100, 30, "%");

    public static EffectDescriptor Info { get; } =
        P.Describe(Id, "Space", EffectCategories.Reverb, Mix, Length, Predelay, HighCut, LowCut);

    readonly PartitionedConvolver convolverLeft = new();
    readonly PartitionedConvolver convolverRight = new();
    readonly DelayLine lineLeft = new();
    readonly DelayLine lineRight = new();
    float[][] response = [];
    float[][] builtin = [];
    int responseRate;
    bool normalised;
    SvfEqCoefficients lowCut;
    SvfEqCoefficients highCut;
    SvfEqState lowLeft;
    SvfEqState lowRight;
    SvfEqState highLeft;
    SvfEqState highRight;
    GainRamp mix;
    int predelay;
    float appliedLength = -1;
    bool lowCutOn;
    bool highCutOn;

    public ConvolutionReverb() : base(Info) { }

    public IReadOnlyList<string> FileTypes { get; } = [".wav"];

    /// <summary>The impulse response file loaded, or null for the built-in one.</summary>
    public string? File { get; private set; }

    /// <summary>The impulse response in use, at the prepared rate (its channels; read-only).</summary>
    public IReadOnlyList<float[]> Response => response;

    /// <summary>The convolvers' partition size, in samples.</summary>
    public int PartitionSize => convolverLeft.BlockSize;

    /// <summary>
    ///     Control thread: loads an impulse response from a WAV file (normalised, resampled to the prepared rate);
    ///     false when it cannot be read. The engine calls it while the effect is not processing.
    /// </summary>
    public bool LoadFile(string path) {
        WavData wav;
        try {
            wav = WavReader.Read(path);
        } catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) {
            return false;
        }

        if (wav.Frames == 0) {
            return false;
        }

        LoadResponse(wav.Channels, wav.SampleRate, true);
        File = path;
        return true;
    }

    /// <summary>
    ///     Control thread: uses <paramref name="channels" /> (one or two; more are ignored) at
    ///     <paramref name="sampleRate" /> as the impulse response, normalised to the built-in's energy when
    ///     <paramref name="normalise" />. Clears <see cref="File" />.
    /// </summary>
    public void LoadResponse(IReadOnlyList<float[]> channels, int sampleRate, bool normalise = false) {
        ArgumentOutOfRangeException.ThrowIfZero(channels.Count);
        response = [..channels.Take(2).Select(static c => (float[])c.Clone())];
        responseRate = sampleRate;
        normalised = normalise;
        if (normalise) {
            Normalise(response);
        }

        File = null;
        if (Prepared) {
            Install();
            Reset();
        }
    }

    /// <summary>Control thread: back to the built-in response.</summary>
    public void ClearFile() {
        response = [];
        File = null;
        if (Prepared) {
            Install();
            Reset();
        }
    }

    public override byte[]? SaveState() => InstrumentState.Write(Id, File, Info.Parameters, Bank);

    public override bool LoadState(ReadOnlySpan<byte> state) {
        if (!InstrumentState.TryRead(state, Id, out var file, out var values)) {
            return false;
        }

        InstrumentState.Apply(values, Bank);
        var loaded = true;
        if (file is null) {
            if (File is not null) {
                ClearFile();
            }
        } else if (file != File) {
            loaded = LoadFile(file);
        }

        Reset();
        return loaded;
    }

    protected override void OnPrepare(float sampleRate) {
        builtin = Builtin(sampleRate);
        var samples = (int)Math.Ceiling(0.5 * sampleRate) + 4;
        lineLeft.Allocate(samples);
        lineRight.Allocate(samples);
        Install();
    }

    protected override void OnReset() {
        convolverLeft.Reset();
        convolverRight.Reset();
        lineLeft.Reset();
        lineRight.Reset();
        lowLeft.Reset();
        lowRight.Reset();
        highLeft.Reset();
        highRight.Reset();
        appliedLength = -1;
    }

    protected override void Update() {
        predelay = (int)Math.Round(Value(Predelay) * 0.001 * SampleRate);
        lowCutOn = Value(LowCut) > 20;
        highCutOn = Value(HighCut) < 20_000;
        lowCut = SvfEqCoefficients.Design(SvfEqType.HighPass, SampleRate, Value(LowCut), 0.7071);
        highCut = SvfEqCoefficients.Design(SvfEqType.LowPass, SampleRate, Value(HighCut), 0.7071);
        mix.Set(Value(Mix) * 0.01f, Snapping);
        var length = Value(Length);
        if (length != appliedLength) {
            appliedLength = length;
            ApplyLength(convolverLeft, length * 0.01);
            ApplyLength(convolverRight, length * 0.01);
        }
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
            double l = convolverLeft.Tick(lineLeft.Read(predelay));
            double r = convolverRight.Tick(lineRight.Read(predelay));
            if (lowCutOn) {
                l = lowLeft.Tick(lowCut, l);
                r = lowRight.Tick(lowCut, r);
            }

            if (highCutOn) {
                l = highLeft.Tick(highCut, l);
                r = highRight.Tick(highCut, r);
            }

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

    /// <summary>Partition gains for a response cut to <paramref name="fraction" /> of its length.</summary>
    static void ApplyLength(PartitionedConvolver convolver, double fraction) {
        var total = convolver.Partitions;
        if (fraction >= 0.999) {
            for (var p = 1; p < total; p++) {
                convolver.SetPartitionGain(p, 1);
            }

            return;
        }

        var end = Math.Max(1.0, fraction * total);
        var fade = Math.Max(1.0, end / 4);
        for (var p = 1; p < total; p++) {
            var gain = p >= end ? 0 : Math.Min(1, (end - p) / fade);
            convolver.SetPartitionGain(p, (float)gain);
        }
    }

    /// <summary>Control thread: the response at the prepared rate into the convolvers.</summary>
    void Install() {
        var rate = SampleRate;
        float[][] channels;
        if (response.Length == 0) {
            channels = builtin;
        } else {
            channels = [..response.Select(c => Resample(c, responseRate, rate))];
            if (normalised) {
                // Again at the new rate, where the same response has more (or fewer) taps.
                Normalise(channels);
            }

            response = channels;
            responseRate = (int)rate;
        }

        var taps = Math.Min(channels.Max(static c => c.Length), (int)(MaxSeconds * rate));
        var block = 64;
        while (block < 1_024 && block * block < 4 * taps) {
            block <<= 1;
        }

        convolverLeft.Allocate(block, taps);
        convolverRight.Allocate(block, taps);
        convolverLeft.SetFilter(channels[0].AsSpan(0, Math.Min(taps, channels[0].Length)));
        var second = channels[^1];
        convolverRight.SetFilter(second.AsSpan(0, Math.Min(taps, second.Length)));
        appliedLength = -1;
    }

    static float[] Resample(float[] source, int from, float to) {
        if (from <= 0 || Math.Abs(from - to) < 0.5) {
            return source;
        }

        var ratio = from / (double)to;
        var length = (int)Math.Floor((source.Length - 1) / ratio) + 1;
        var result = new float[length];
        // Low-passed first when shrinking, by a windowed sinc at the new Nyquist.
        var cutoff = Math.Min(1.0, 1 / ratio);
        const int half = 16;
        for (var n = 0; n < length; n++) {
            var t = n * ratio;
            var centre = (int)Math.Floor(t);
            double sum = 0;
            for (var k = centre - (int)(half / cutoff) + 1; k <= centre + (int)(half / cutoff); k++) {
                if (k < 0 || k >= source.Length) {
                    continue;
                }

                var x = (t - k) * cutoff;
                var sinc = Math.Abs(x) < 1e-9 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
                var w = Math.Abs(x) >= half ? 0 : 0.5 + 0.5 * Math.Cos(Math.PI * x / half);
                sum += source[k] * sinc * w * cutoff;
            }

            result[n] = (float)sum;
        }

        return result;
    }

    /// <summary>Scales the response to the built-in's energy per channel (0.5² summed over its taps).</summary>
    static void Normalise(float[][] channels) {
        double energy = 0;
        foreach (var c in channels) {
            foreach (var s in c) {
                energy += (double)s * s;
            }
        }

        energy /= channels.Length;
        if (energy <= 1e-20) {
            return;
        }

        var scale = (float)(0.5 / Math.Sqrt(energy));
        foreach (var c in channels) {
            for (var i = 0; i < c.Length; i++) {
                c[i] *= scale;
            }
        }
    }

    /// <summary>
    ///     The built-in hall: a few early reflections over the first 80 ms, then noise whose highs decay faster than
    ///     its lows (RT60 2.2 s in the lows, 0.9 s in the highs), each channel its own noise; energy normalised.
    /// </summary>
    static float[][] Builtin(float sampleRate) {
        var length = (int)(BuiltinSeconds * sampleRate);
        var channels = new float[2][];
        for (var c = 0; c < 2; c++) {
            var x = new float[length];
            var random = 0x9E3779B9u ^ (uint)(c * 0x85EBCA6B + 1);
            var low = 0.0;
            var lowDecay = Math.Log(1_000) / (2.2 * sampleRate);
            var highDecay = Math.Log(1_000) / (0.9 * sampleRate);
            var smoothing = 1 - Math.Exp(-2 * Math.PI * 1_200 / sampleRate);
            var onset = (int)(0.012 * sampleRate);
            for (var i = onset; i < length; i++) {
                random ^= random << 13;
                random ^= random >> 17;
                random ^= random << 5;
                var noise = random * (2.0 / uint.MaxValue) - 1;
                low += (noise - low) * smoothing;
                var high = noise - low;
                var t = i - onset;
                var fadeIn = Math.Min(1.0, t / (0.03 * sampleRate));
                x[i] = (float)(fadeIn * (1.6 * low * Math.Exp(-lowDecay * t) + 0.5 * high * Math.Exp(-highDecay * t)));
            }

            // Early reflections.
            ReadOnlySpan<double> times = [0.011, 0.019, 0.027, 0.034, 0.047, 0.058, 0.071];
            for (var r = 0; r < times.Length; r++) {
                var at = (int)((times[r] + 0.0013 * c * (r % 3)) * sampleRate);
                x[at] += (float)((r % 2 == 0 ? 0.5 : -0.4) * Math.Exp(-r * 0.25));
            }

            channels[c] = x;
        }

        Normalise(channels);
        return channels;
    }
}
