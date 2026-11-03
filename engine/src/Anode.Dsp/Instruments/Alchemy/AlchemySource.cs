using Anode.Dsp.Analysis;
using Anode.Dsp.Spectral;

namespace Anode.Dsp.Instruments.Alchemy;

/// <summary>
///     A sample as <see cref="SampleAlchemy" /> resynthesises it, analysed once off the audio thread and immutable after:
///     the mono source (granular), its pitch, its partials frame by frame (additive) and its smoothed spectral envelope
///     frame by frame (spectral).
/// </summary>
/// <remarks>
///     <para>
///         <b>Pitch.</b> The median of the voiced frames of Phase 5's pitch tracker (pYIN), moved by octaves toward the
///         file's root note when it has one (<c>smpl</c>, <c>INST</c>): the frequency every key's pitch is measured
///         against. A sample with no voiced frame takes its root note's (or C4's) frequency.
///     </para>
///     <para>
///         <b>Partials.</b> Every <see cref="HopSeconds" /> a Hann-windowed FFT (about 11.7 Hz a bin) is searched near
///         each
///         harmonic of the frame's pitch (±0.45 of it) for its peak, refined by parabolic interpolation of the log
///         magnitude: up to <see cref="Partials" /> partials a frame, each an amplitude and a frequency over the
///         first partial's amplitude-weighted mean frequency (so the additive resynthesis lands on the key exactly).
///         Guided by the pitch so a partial stays the same partial from frame to frame (harmonic partial
///         tracking); an inharmonic sample still gets its peaks near each harmonic.
///     </para>
///     <para>
///         <b>Envelope.</b> Per frame the strongest component's amplitude in each of <see cref="Bands" /> bands
///         (log-spaced centres from 60 Hz to 15 kHz), smoothed 1-2-1 across the bands.
///     </para>
/// </remarks>
internal sealed class AlchemySource {
    public const int Partials = 64;
    public const int Bands = 24;
    public const double HopSeconds = 256 / 48_000.0;

    /// <summary>The longest stretch of a sample that is analysed.</summary>
    public const double MaxSeconds = 60;

    const double LowBandHz = 60;
    const double HighBandHz = 15_000;

    /// <summary>The default sample's pitch: C4.</summary>
    const double DefaultHz = 261.6255653005986;

    static readonly Lazy<AlchemySource> Builtin =
        new(static () => Analyse([DefaultSample(48_000)], 48_000, 60, DefaultHz));

    AlchemySource(int rate, float[] mono, double rootHz, int frameCount) {
        Rate = rate;
        Mono = mono;
        RootHz = rootHz;
        FrameCount = frameCount;
        Amplitudes = new float[frameCount * Partials];
        Ratios = new float[frameCount * Partials];
        Envelope = new float[frameCount * Bands];
    }

    /// <summary>The source's sample rate.</summary>
    public int Rate { get; }

    /// <summary>The source mixed to mono.</summary>
    public float[] Mono { get; }

    /// <summary>The pitch every key's is measured against (Hz).</summary>
    public double RootHz { get; }

    public int FrameCount { get; }

    /// <summary>Partial k's amplitude in frame f at <c>f × Partials + k</c>.</summary>
    public float[] Amplitudes { get; }

    /// <summary>Partial k's frequency over the first partial's mean in frame f, at <c>f × Partials + k</c>.</summary>
    public float[] Ratios { get; }

    /// <summary>Band b's envelope in frame f, at <c>f × Bands + b</c>.</summary>
    public float[] Envelope { get; }

    /// <summary>The analysis' FFT bin width (Hz).</summary>
    public double BinHz { get; private set; }

    /// <summary>The length in seconds.</summary>
    public double Seconds => Mono.Length / (double)Rate;

    /// <summary>The built-in sample, analysed (<see cref="DefaultSample" />).</summary>
    public static AlchemySource Default => Builtin.Value;

    /// <summary>The centre of band <paramref name="band" /> (Hz).</summary>
    public static double BandHz(int band) => LowBandHz * Math.Pow(HighBandHz / LowBandHz, band / (Bands - 1.0));

    /// <summary>
    ///     The generated default sample: 2.5 s of a sung "ah" on C4 (261.63 Hz), its harmonics shaped by three vowel
    ///     formants (800, 1,150 and 2,900 Hz), with a little breath and shimmer, swelling in and fading out.
    /// </summary>
    public static float[] DefaultSample(int rate) {
        var frames = (int)(2.5 * rate);
        var sample = new float[frames];
        const double hz = DefaultHz;
        (double Hz, double Width, double Gain)[] formants = [(800, 90, 1), (1_150, 110, 0.6), (2_900, 160, 0.25)];
        var harmonics = (int)(0.45 * rate / hz);
        var gains = new double[harmonics];
        for (var k = 1; k <= harmonics; k++) {
            var f = k * hz;
            var g = 0.08;
            foreach (var (centre, width, gain) in formants) {
                var x = (f - centre) / width;
                g += gain / (1 + x * x);
            }

            gains[k - 1] = g / Math.Sqrt(k);
        }

        uint noise = 0x1234567;
        double breath = 0;
        for (var i = 0; i < frames; i++) {
            var t = i / (double)rate;
            var x = 0.0;
            for (var k = 1; k <= harmonics; k++) {
                x += gains[k - 1] * Math.Sin(2 * Math.PI * k * hz * t + k * 0.7);
            }

            breath += (Oscillators.PolyBlep.White(ref noise) - breath) * 0.3;
            var shimmer = 1 + 0.06 * Math.Sin(2 * Math.PI * 3.1 * t);
            var envelope = Math.Min(1, t / 0.06) * Math.Min(1, (2.5 - t) / 0.5);
            sample[i] = (float)(envelope * (0.12 * x * shimmer + 0.02 * breath));
        }

        return sample;
    }

    /// <summary>
    ///     Analyses <paramref name="channels" /> at <paramref name="rate" /> (the root note −1 for none); with
    ///     <paramref name="knownHz" />, the sample's pitch is that (no pitch tracking: the default sample).
    /// </summary>
    public static AlchemySource Analyse(float[][] channels, int rate, int rootNote, double knownHz = 0) {
        var length = (int)Math.Min(channels[0].Length, MaxSeconds * rate);
        var mono = new float[length];
        foreach (var channel in channels) {
            for (var i = 0; i < length; i++) {
                mono[i] += channel[i] / channels.Length;
            }
        }

        var hop = Math.Max(16, (int)Math.Round(HopSeconds * rate));
        var size = 1;
        while (size < rate / 12) {
            size <<= 1;
        }

        var frameCount = Math.Max(1, (length + hop - 1) / hop);
        var track = knownHz > 0 ? new PitchTrack(hop, [], []) : PitchTracker.Track(mono, rate, hop);
        var rootHz = knownHz > 0 ? knownHz : DetectedPitch(track, rootNote);
        var source = new AlchemySource(rate, mono, rootHz, frameCount);
        source.Track(track, hop, size);
        return source;
    }

    static double DetectedPitch(PitchTrack track, int rootNote) {
        var voiced = new List<float>();
        for (var f = 0; f < track.Cents.Length; f++) {
            if (track.Voicing[f] >= 0.5f && track.Cents[f] > 0) {
                voiced.Add(track.Cents[f]);
            }
        }

        if (voiced.Count == 0) {
            return Pitch.Frequency(rootNote >= 0 ? rootNote : 60);
        }

        voiced.Sort();
        double cents = voiced[voiced.Count / 2];
        if (rootNote >= 0) {
            cents += 1_200 * Math.Round((rootNote * 100 - cents) / 1_200);
        }

        return Pitch.Frequency(cents / 100);
    }

    void Track(PitchTrack track, int hop, int size) {
        var fft = new RealFft(size);
        var frame = new float[size];
        var real = new float[fft.Bins];
        var imaginary = new float[fft.Bins];
        var magnitude = new double[fft.Bins];
        var window = new float[size];
        for (var i = 0; i < size; i++) {
            window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / size));
        }

        // A sinusoid of amplitude A peaks at A × size / 4 in a Hann-windowed spectrum.
        var scale = 4.0 / size;
        var binHz = Rate / (double)size;
        BinHz = binHz;
        var frequencies = new float[FrameCount * Partials];
        for (var f = 0; f < FrameCount; f++) {
            var centre = f * hop;
            for (var i = 0; i < size; i++) {
                var at = centre - size / 2 + i;
                frame[i] = (uint)at < (uint)Mono.Length ? Mono[at] * window[i] : 0;
            }

            fft.Forward(frame, real, imaginary);
            for (var k = 0; k < fft.Bins; k++) {
                magnitude[k] = Math.Sqrt(real[k] * (double)real[k] + imaginary[k] * (double)imaginary[k]) * scale;
            }

            var pitch = f < track.Cents.Length && track.Voicing[f] >= 0.5f && track.Cents[f] > 0
                ? Pitch.Frequency(track.Cents[f] / 100.0)
                : RootHz;
            for (var k = 1; k <= Partials; k++) {
                var at = f * Partials + k - 1;
                var expected = k * pitch;
                if (expected >= 0.45 * Rate) {
                    frequencies[at] = (float)expected;
                    continue;
                }

                var low = Math.Max(1, (int)Math.Floor((expected - 0.45 * pitch) / binHz));
                var high = Math.Min(fft.Bins - 2, (int)Math.Ceiling((expected + 0.45 * pitch) / binHz));
                var peak = low;
                for (var b = low; b <= high; b++) {
                    if (magnitude[b] > magnitude[peak]) {
                        peak = b;
                    }
                }

                var (offset, height) = Parabola(magnitude, peak);
                frequencies[at] = (float)((peak + offset) * binHz);
                Amplitudes[at] = (float)height;
            }

            Shape(magnitude, binHz, f);
        }

        // The partials' frequencies over the first partial's amplitude-weighted mean frequency (where it sounds), so
        // the resynthesis is on the pitch it tracked; the pitch itself (granular) stays the detected one.
        var loudest = 0f;
        for (var f = 0; f < FrameCount; f++) {
            loudest = Math.Max(loudest, Amplitudes[f * Partials]);
        }

        double weighted = 0, weights = 0;
        for (var f = 0; f < FrameCount; f++) {
            var a = Amplitudes[f * Partials];
            if (a > 0.1f * loudest) {
                weighted += a * frequencies[f * Partials];
                weights += a;
            }
        }

        var reference = weights > 0 && Math.Abs(1_200 * Math.Log2(weighted / weights / RootHz)) < 100
            ? weighted / weights
            : RootHz;
        for (var i = 0; i < frequencies.Length; i++) {
            Ratios[i] = (float)(frequencies[i] / reference);
        }
    }

    /// <summary>The peak's offset from bin <paramref name="peak" /> and its height, by a parabola through the log magnitudes.</summary>
    static (double Offset, double Height) Parabola(double[] magnitude, int peak) {
        var a = Math.Log(Math.Max(magnitude[peak - 1], 1e-12));
        var b = Math.Log(Math.Max(magnitude[peak], 1e-12));
        var c = Math.Log(Math.Max(magnitude[peak + 1], 1e-12));
        var denominator = a - 2 * b + c;
        if (denominator >= 0) {
            return (0, magnitude[peak]);
        }

        var offset = Math.Clamp(0.5 * (a - c) / denominator, -0.5, 0.5);
        return (offset, Math.Exp(b - 0.25 * (a - c) * offset));
    }

    void Shape(double[] magnitude, double binHz, int frame) {
        Span<double> bands = stackalloc double[Bands];
        for (var b = 0; b < Bands; b++) {
            var low = (int)Math.Floor(BandHz(b - 0.5) / binHz);
            var high = (int)Math.Ceiling(BandHz(b + 0.5) / binHz);
            var most = 0.0;
            for (var k = Math.Max(1, low); k <= Math.Min(magnitude.Length - 1, high); k++) {
                most = Math.Max(most, magnitude[k]);
            }

            bands[b] = most;
        }

        for (var b = 0; b < Bands; b++) {
            var before = bands[Math.Max(0, b - 1)];
            var after = bands[Math.Min(Bands - 1, b + 1)];
            Envelope[frame * Bands + b] = (float)(0.25 * before + 0.5 * bands[b] + 0.25 * after);
        }
    }

    static double BandHz(double band) => LowBandHz * Math.Pow(HighBandHz / LowBandHz, band / (Bands - 1.0));
}
