namespace Anode.Spikes.Stretch;

/// <summary>A test signal with what is known about it, for the quality measurements.</summary>
sealed record TestSignal(string Name, float[][] Audio, int SampleRate) {
    /// <summary>Steady tones whose pitch is checked (Hz).</summary>
    public double[] Tones { get; init; } = [];

    /// <summary>Every steady partial (Hz), for the tonal-purity measure.</summary>
    public double[] Partials { get; init; } = [];

    /// <summary>A logarithmic sweep: start and end frequency and its length in seconds.</summary>
    public (double From, double To, double Seconds)? Sweep { get; init; }

    /// <summary>Onset positions (input frames) of clicks or drum hits.</summary>
    public long[] Onsets { get; init; } = [];

    /// <summary>The fundamental (Hz) at a time in seconds, or NaN where it is unvoiced or gliding.</summary>
    public Func<double, double>? F0 { get; init; }

    public double Seconds => Audio[0].Length / (double)SampleRate;
}

static class Signals {
    public const int Rate = 48_000;

    public static IReadOnlyList<TestSignal> All() => [Sweep(), Tones(), Chord(), Clicks(), Drums(), Vocal()];

    /// <summary>A logarithmic sine sweep, 40 Hz to 12 kHz over 8 s, at -6 dBFS.</summary>
    public static TestSignal Sweep() {
        const double from = 40;
        const double to = 12_000;
        const double seconds = 8;
        var n = (int)(seconds * Rate);
        var x = new float[n];
        var k = Math.Log(to / from);
        for (var i = 0; i < n; i++) {
            var t = i / (double)Rate;
            var phase = 2 * Math.PI * from * seconds / k * (Math.Exp(t / seconds * k) - 1);
            x[i] = (float)(0.5 * Math.Sin(phase) * Fade(i, n, 0.01));
        }

        return new TestSignal("sweep", [x], Rate) { Sweep = (from, to, seconds) };
    }

    /// <summary>Four steady sines across the range (110, 440, 1000, 5000 Hz), 6 s.</summary>
    public static TestSignal Tones() {
        double[] tones = [110, 440, 1000, 5000];
        var x = Additive(6, tones, [0.2, 0.2, 0.2, 0.2]);
        return new TestSignal("tones", [x], Rate) { Tones = tones, Partials = tones };
    }

    /// <summary>
    ///     C major (C4 E4 G4 C5), four harmonics per note at 1/h: closely spaced partials (C4×3 and G4×2 are 0.9 Hz
    ///     apart), 6 s.
    /// </summary>
    public static TestSignal Chord() {
        double[] notes = [261.63, 329.63, 392.00, 523.25];
        var partials = new List<double>();
        var amplitudes = new List<double>();
        foreach (var f in notes) {
            for (var h = 1; h <= 4; h++) {
                partials.Add(f * h);
                amplitudes.Add(0.12 / h);
            }
        }

        var x = Additive(6, [..partials], [..amplitudes]);
        return new TestSignal("chord", [x], Rate) { Tones = notes, Partials = [..partials] };
    }

    /// <summary>Twelve 1 ms decaying noise clicks every 0.5 s over silence.</summary>
    public static TestSignal Clicks() {
        var n = 6 * Rate;
        var x = new float[n];
        var random = new Random(1);
        var onsets = new List<long>();
        for (var c = 0; c < 12; c++) {
            var at = Rate / 4 + c * Rate / 2;
            onsets.Add(at);
            for (var i = 0; i < 48; i++) {
                x[at + i] = (float)(0.8 * (random.NextDouble() * 2 - 1) * Math.Exp(-i / 8.0));
            }
        }

        return new TestSignal("clicks", [x], Rate) { Onsets = [..onsets] };
    }

    /// <summary>A synthesized kit at 120 bpm: kick on 1 and 3, snare on 2 and 4, closed hats on eighths, 8 s.</summary>
    public static TestSignal Drums() {
        var n = 8 * Rate;
        var x = new float[n];
        var random = new Random(2);
        var eighth = Rate / 4; // 120 bpm
        var onsets = new List<long>();
        for (var step = 0u; step < 32; step++) {
            var at = (int)step * eighth + Rate / 8;
            onsets.Add(at);
            Hat(x, at, random);
            if (step % 4 == 0) {
                Kick(x, at);
            } else if (step % 4 == 2) {
                Snare(x, at, random);
            }
        }

        return new TestSignal("drums", [x], Rate) { Onsets = [..onsets] };
    }

    /// <summary>
    ///     A vowel ("ah") sung on four notes with delayed 5.5 Hz vibrato of ±40 cents: a band-limited glottal-like
    ///     source (harmonics at 1/h) through four formant resonators (700, 1220, 2600, 3500 Hz), 6 s.
    /// </summary>
    public static TestSignal Vocal() {
        (double Start, double End, double F0)[] notes =
            [(0.1, 1.4, 180), (1.5, 2.8, 202), (2.9, 4.2, 240), (4.3, 5.8, 214)];

        double F0At(double t) {
            foreach (var (start, end, f0) in notes) {
                if (t >= start && t < end) {
                    var depth = Math.Clamp((t - start - 0.3) / 0.3, 0, 1) * 40;
                    return f0 * Math.Pow(2, depth * Math.Sin(2 * Math.PI * 5.5 * (t - start)) / 1200);
                }
            }

            return double.NaN;
        }

        double Envelope(double t) {
            foreach (var (start, end, _) in notes) {
                if (t >= start && t < end) {
                    var a = Math.Clamp((t - start) / 0.04, 0, 1);
                    var r = Math.Clamp((end - t) / 0.06, 0, 1);
                    return Math.Sin(a * Math.PI / 2) * Math.Sin(r * Math.PI / 2);
                }
            }

            return 0;
        }

        var n = 6 * Rate;
        var x = new double[n];
        var phase = 0.0;
        var lastF0 = notes[0].F0;
        for (var i = 0; i < n; i++) {
            var t = i / (double)Rate;
            var f0 = F0At(t);
            if (double.IsNaN(f0)) {
                f0 = lastF0;
            }

            lastF0 = f0;
            phase += 2 * Math.PI * f0 / Rate;
            var env = Envelope(t);
            if (env == 0) {
                continue;
            }

            var sum = 0.0;
            for (var h = 1; h * f0 < 10_000; h++) {
                sum += Math.Sin(h * phase) / h;
            }

            x[i] = sum * env;
        }

        foreach (var (f, b) in new[] { (700.0, 80.0), (1220.0, 90.0), (2600.0, 120.0), (3500.0, 150.0) }) {
            Resonate(x, f, b);
        }

        var peak = 0.0;
        foreach (var v in x) {
            peak = Math.Max(peak, Math.Abs(v));
        }

        var y = new float[n];
        for (var i = 0; i < n; i++) {
            y[i] = (float)(x[i] / peak * 0.5);
        }

        return new TestSignal("vocal", [y], Rate) { F0 = Steady };

        // Steady parts only: skip the attack, the release and the note changes.
        double Steady(double t) {
            foreach (var (start, end, _) in notes) {
                if (t >= start + 0.06 && t < end - 0.08) {
                    return F0At(t);
                }
            }

            return double.NaN;
        }
    }

    /// <summary>Chord, drums and vocal together, repeated to <paramref name="seconds" />, as two decorrelated channels.</summary>
    public static float[][] Mix(double seconds) {
        var chord = Chord().Audio[0];
        var drums = Drums().Audio[0];
        var vocal = Vocal().Audio[0];
        var n = (int)(seconds * Rate);
        var left = new float[n];
        var right = new float[n];
        for (var i = 0; i < n; i++) {
            var c = chord[i % chord.Length];
            var d = drums[i % drums.Length];
            var v = vocal[i % vocal.Length];
            left[i] = 0.5f * c + 0.6f * d + 0.4f * v;
            right[i] = 0.4f * c + 0.6f * d + 0.5f * v;
            if (i >= 480) {
                right[i] += 0.2f * left[i - 480];
            }
        }

        return [left, right];
    }

    public static float[] Additive(double seconds, double[] frequencies, double[] amplitudes) {
        var n = (int)(seconds * Rate);
        var x = new float[n];
        for (var i = 0; i < n; i++) {
            var t = i / (double)Rate;
            var sum = 0.0;
            for (var p = 0; p < frequencies.Length; p++) {
                sum += amplitudes[p] * Math.Sin(2 * Math.PI * frequencies[p] * t + p);
            }

            x[i] = (float)(sum * Fade(i, n, 0.02));
        }

        return x;
    }

    static double Fade(int i, int n, double seconds) {
        var f = seconds * Rate;
        return Math.Min(1, Math.Min(i / f, (n - 1 - i) / f));
    }

    static void Kick(float[] x, int at) {
        var phase = 0.0;
        for (var i = 0; i < Rate / 2 && at + i < x.Length; i++) {
            var t = i / (double)Rate;
            phase += 2 * Math.PI * (50 + 100 * Math.Exp(-t / 0.03)) / Rate;
            x[at + i] += (float)(0.7 * Math.Sin(phase) * Math.Exp(-t / 0.15));
        }
    }

    static void Snare(float[] x, int at, Random random) {
        for (var i = 0; i < Rate / 3 && at + i < x.Length; i++) {
            var t = i / (double)Rate;
            var noise = (random.NextDouble() * 2 - 1) * 0.45 * Math.Exp(-t / 0.12);
            var tone = 0.35 * Math.Sin(2 * Math.PI * 190 * t) * Math.Exp(-t / 0.06);
            x[at + i] += (float)(noise + tone);
        }
    }

    static void Hat(float[] x, int at, Random random) {
        var previous = 0.0;
        for (var i = 0; i < Rate / 10 && at + i < x.Length; i++) {
            var t = i / (double)Rate;
            var noise = random.NextDouble() * 2 - 1;
            x[at + i] += (float)(0.2 * (noise - previous) * Math.Exp(-t / 0.03)); // first difference: bright
            previous = noise;
        }
    }

    /// <summary>A two-pole resonator, normalised to roughly unity gain at its centre.</summary>
    static void Resonate(double[] x, double frequency, double bandwidth) {
        var r = Math.Exp(-Math.PI * bandwidth / Rate);
        var theta = 2 * Math.PI * frequency / Rate;
        var a1 = 2 * r * Math.Cos(theta);
        var a2 = -r * r;
        var gain = (1 - r) * Math.Sqrt(1 - 2 * r * Math.Cos(2 * theta) + r * r);
        double y1 = 0, y2 = 0;
        for (var i = 0; i < x.Length; i++) {
            var y = gain * x[i] + a1 * y1 + a2 * y2;
            y2 = y1;
            y1 = y;
            x[i] = y;
        }
    }
}
