using System.Globalization;

namespace Anode.Spikes.Stretch;

/// <summary>Collects Markdown and echoes it to stdout as it goes.</summary>
sealed class Report {
    readonly System.Text.StringBuilder text = new();
    public string Text => text.ToString();

    public void Line(string line = "") {
        text.AppendLine(line);
        Console.WriteLine(line);
    }

    public void Table(string[] header, IEnumerable<string[]> rows) {
        Line("| " + string.Join(" | ", header) + " |");
        Line("|" + string.Concat(header.Select(static _ => "---|")));
        foreach (var row in rows) {
            Line("| " + string.Join(" | ", row) + " |");
        }

        Line();
    }
}

sealed class Suites(Report report, string output, bool writeWav) {
    const int QualityBlock = 128;
    static readonly double[] Ratios = [0.5, 0.75, 1.0, 1.25, 1.5, 2.0];
    static readonly double[] Shifts = [-12, -5, 3, 7, 12];

    static readonly StretchConfig[] Configs = [
        new(Preset.Default, false), new(Preset.Default, true), new(Preset.Cheaper, false), new(Preset.Cheaper, true)
    ];

    static readonly StretchConfig[] QualityConfigs = [new(Preset.Default, false), new(Preset.Cheaper, false)];

    readonly Dictionary<string, TestSignal> signals = Signals.All().ToDictionary(static s => s.Name);

    static string F(double v, int digits = 1) =>
        double.IsNaN(v) ? "–" : v.ToString("F" + digits, CultureInfo.InvariantCulture);

    static string Label(double ratio, double shift) =>
        shift == 0 ? $"ratio-{ratio:F2}" : $"pitch{(shift > 0 ? "+" : "-")}{Math.Abs(shift):00}";

    public void Latency() {
        report.Line("## Latency and configuration");
        report.Line();
        var rows = new List<string[]>();
        foreach (var config in Configs) {
            using var s = new Stretcher(1, Signals.Rate, config);
            static double Ms(int frames) => frames * 1000.0 / Signals.Rate;
            rows.Add(
                [
                    config.Name, $"{s.BlockFrames} ({F(Ms(s.BlockFrames))} ms)",
                    $"{s.IntervalFrames} ({F(Ms(s.IntervalFrames))} ms)",
                    $"{s.InputLatency} ({F(Ms(s.InputLatency))} ms)",
                    $"{s.OutputLatency} ({F(Ms(s.OutputLatency))} ms)",
                    F(Ms(s.InputLatency + s.OutputLatency)), $"{s.SeekLength}"
                ]
            );
        }

        report.Table(
            ["config", "block", "interval", "input latency", "output latency", "live in→out ms", "seek length"],
            rows
        );

        // Does split computation or the callback size change the audio? (It should only move the work around.)
        var vocal = signals["vocal"].Audio;

        float[] RenderWith(StretchConfig config, int block) {
            using var s = new Stretcher(1, Signals.Rate, config);
            s.SetTranspose(3);
            return Renderer.Render(s, vocal, 1.25, block).Audio[0];
        }

        var reference = RenderWith(Configs[0], 128);
        var split = RenderWith(Configs[1], 128);
        var small = RenderWith(Configs[0], 32);
        var large = RenderWith(Configs[0], 1024);
        report.Line(
            "Determinism (vocal, 1.25×, +3 st; max |difference| against default at 128-frame blocks): "
            + $"default+split {MaxDiff(reference, split):E1}, 32-frame blocks {MaxDiff(reference, small):E1}, "
            + $"1024-frame blocks {MaxDiff(reference, large):E1}."
        );
        report.Line();
    }

    static double MaxDiff(float[] a, float[] b) {
        var max = 0.0;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++) {
            max = Math.Max(max, Math.Abs(a[i] - b[i]));
        }

        return max;
    }

    Rendered Run(
        TestSignal signal,
        StretchConfig config,
        double ratio,
        double shift,
        bool formant = false,
        Func<long, double>? rateAt = null,
        string? label = null
    ) {
        using var s = new Stretcher(signal.Audio.Length, signal.SampleRate, config);
        s.SetTranspose(shift);
        if (formant) {
            s.SetFormant(1, true, 0);
        }

        var r = rateAt is null
            ? Renderer.Render(s, signal.Audio, ratio, QualityBlock)
            : Renderer.Render(s, signal.Audio, rateAt, QualityBlock);
        if (writeWav) {
            var name = label ?? Label(ratio, shift) + (formant ? "-formant" : string.Empty);
            Wav.Write(Path.Combine(output, signal.Name, config.Name, name + ".wav"), r.Audio, signal.SampleRate);
        }

        return r;
    }

    public void Quality() {
        if (writeWav) {
            foreach (var signal in signals.Values) {
                Wav.Write(Path.Combine(output, signal.Name, "input.wav"), signal.Audio, signal.SampleRate);
            }
        }

        foreach (var config in QualityConfigs) {
            Console.Error.WriteLine($"quality: {config.Name}");
            TimeStretch(config);
            PitchShift(config);
            TempoFollowing(config);
        }

        LowTones();
    }

    void TimeStretch(StretchConfig config) {
        var pitch = new List<string[]>();
        var transients = new List<string[]>();
        var balance = new List<string[]>();
        foreach (var ratio in Ratios) {
            var row = Measure(config, ratio, 0);
            pitch.Add([F(ratio, 2) + "×", ..row.Pitch]);
            transients.Add([F(ratio, 2) + "×", ..row.Transients]);
            balance.Add([F(ratio, 2) + "×", ..row.Balance]);
        }

        report.Line($"## Time stretch, `{config.Name}` (ratio = output length / input length)");
        report.Line();
        report.Table(PitchHeader("ratio"), pitch);
        report.Table(TransientHeader("ratio"), transients);
        report.Table(
            [
                "ratio", "RMS Δ dB chord", "RMS Δ drums", "RMS Δ vocal", "balance dev dB chord", "drums", "vocal",
                "sweep"
            ],
            balance
        );
    }

    static string[] PitchHeader(string first) => [
        first, "110 Hz err ¢", "440 Hz err ¢", """1 k/5 k max \|err\| ¢""", "tones non-partial dB",
        """chord max \|err\| ¢""", "chord non-partial dB", "sweep median ¢",
        "sweep p95 ¢", "vocal f0 median ¢", "vocal f0 p95 ¢", "vocal f0 gross %"
    ];

    static string[] TransientHeader(string first) => [
        first, "click 90 % width ms", "click pre-echo dB", "click onset err ms", "click peak Δ dB",
        "drum onset err median ms",
        "drum onset err max ms", "drum 90 % width Δ ms", "drum pre-onset energy Δ dB"
    ];

    (string[] Pitch, string[] Transients, string[] Balance) Measure(StretchConfig config, double ratio, double shift) {
        var transpose = Math.Pow(2, shift / 12);
        var tones = signals["tones"];
        var chord = signals["chord"];
        var sweep = signals["sweep"];
        var vocal = signals["vocal"];
        var clicks = signals["clicks"];
        var drums = signals["drums"];

        var rt = Run(tones, config, ratio, shift);
        var rc = Run(chord, config, ratio, shift);
        var rs = Run(sweep, config, ratio, shift);
        var rv = Run(vocal, config, ratio, shift);
        var rk = Run(clicks, config, ratio, shift);
        var rd = Run(drums, config, ratio, shift);

        var t = Analysis.Tones(rt.Audio[0], Signals.Rate, tones.Tones, tones.Partials, transpose);
        var c = Analysis.Tones(rc.Audio[0], Signals.Rate, chord.Tones, chord.Partials, transpose);
        var s = Analysis.SweepErrors(rs, Signals.Rate, sweep.Sweep!.Value, transpose);
        var v = Analysis.F0Errors(rv, Signals.Rate, vocal.F0!, transpose);
        var k = Analysis.Transients(clicks.Audio, rk, clicks.Onsets, Signals.Rate, 100);
        var d = Analysis.Transients(drums.Audio, rd, drums.Onsets, Signals.Rate, 40);

        string[] pitch = [
            F(t.ErrorCents[0]), F(t.ErrorCents[1]), F(Math.Max(Math.Abs(t.ErrorCents[2]), Math.Abs(t.ErrorCents[3]))),
            F(t.NonPartialDb), F(c.ErrorCents.Max(Math.Abs), 2), F(c.NonPartialDb),
            F(Analysis.MedianAbs(s)), F(Analysis.Percentile(s.Select(Math.Abs), 95)), F(Analysis.MedianAbs(v)),
            F(Analysis.Percentile(v.Select(Math.Abs), 95)),
            F(100.0 * v.Count(static e => Math.Abs(e) > 50) / Math.Max(v.Length, 1))
        ];
        string[] transients = [
            F(Analysis.Percentile(k.Select(static x => x.Width90Ms), 50)),
            F(Analysis.Percentile(k.Select(static x => x.PreEchoDb), 50)),
            F(Analysis.Percentile(k.Select(static x => x.OnsetErrorMs), 50)),
            F(Analysis.Percentile(k.Select(static x => x.PeakDropDb), 50)),
            F(Analysis.MedianAbs(d.Select(static x => x.OnsetErrorMs))), F(d.Max(static x => Math.Abs(x.OnsetErrorMs))),
            F(Analysis.Percentile(d.Select(static x => x.WidthChangeMs), 50)),
            F(Analysis.Percentile(d.Select(static x => x.PreEchoChangeDb), 50))
        ];
        string[] balance = [
            F(Analysis.RmsDb(rc.Audio) - Analysis.RmsDb(chord.Audio), 2),
            F(Analysis.RmsDb(rd.Audio) - Analysis.RmsDb(drums.Audio), 2),
            F(Analysis.RmsDb(rv.Audio) - Analysis.RmsDb(vocal.Audio), 2),
            F(Analysis.SpectralDeviationDb(chord.Audio[0], rc.Audio[0], Signals.Rate), 2),
            F(Analysis.SpectralDeviationDb(drums.Audio[0], rd.Audio[0], Signals.Rate), 2),
            F(Analysis.SpectralDeviationDb(vocal.Audio[0], rv.Audio[0], Signals.Rate), 2),
            F(Analysis.SpectralDeviationDb(sweep.Audio[0], rs.Audio[0], Signals.Rate), 2)
        ];
        return (pitch, transients, balance);
    }

    void PitchShift(StretchConfig config) {
        var pitch = new List<string[]>();
        var transients = new List<string[]>();
        var formants = new List<string[]>();
        var vocal = signals["vocal"];
        foreach (var shift in Shifts) {
            var label = (shift > 0 ? "+" : string.Empty) + F(shift, 0) + " st";
            var row = Measure(config, 1, shift);
            pitch.Add([label, ..row.Pitch]);
            transients.Add([label, ..row.Transients]);

            var plain = Run(vocal, config, 1, shift);
            var kept = Run(vocal, config, 1, shift, true);
            var a = Analysis.FormantShift(vocal.Audio[0], plain.Audio[0], Signals.Rate);
            var b = Analysis.FormantShift(vocal.Audio[0], kept.Audio[0], Signals.Rate);
            var f0 = Analysis.F0Errors(kept, Signals.Rate, vocal.F0!, Math.Pow(2, shift / 12));
            formants.Add(
                [
                    label, F(a.ShiftSemitones), F(a.DeviationDb),
                    F(Analysis.RmsDb(plain.Audio) - Analysis.RmsDb(vocal.Audio), 2),
                    F(b.ShiftSemitones), F(b.DeviationDb),
                    F(Analysis.RmsDb(kept.Audio) - Analysis.RmsDb(vocal.Audio), 2),
                    F(Analysis.MedianAbs(f0)),
                    F(100.0 * f0.Count(static e => Math.Abs(e) > 50) / Math.Max(f0.Length, 1))
                ]
            );
        }

        report.Line($"## Pitch shift at 1×, `{config.Name}`");
        report.Line();
        report.Table(PitchHeader("shift"), pitch);
        report.Table(TransientHeader("shift"), transients);
        report.Line(
            "Formants on the vocal: the shift (semitones) that best re-aligns the output's spectral envelope with the "
            + "input's (0 = preserved; = the pitch shift when the formants move with it), and the mean envelope "
            + "difference."
        );
        report.Line();
        report.Table(
            [
                "shift", "plain: formant shift st", "envelope dev dB", "RMS Δ dB", "compensated: formant shift st",
                "envelope dev dB",
                "RMS Δ dB", "f0 median ¢", "f0 gross %"
            ],
            formants
        );
    }

    /// <summary>Transposed low tones against the analysis block size: does a longer block fix the bass tuning?</summary>
    public void LowTones() {
        StretchConfig[] configs = [
            new(Preset.Cheaper, false), new(Preset.Default, false), new(Preset.Default, false, 9600, 2400),
            new(Preset.Default, false, 14400, 3600)
        ];
        double[] tones = [55, 82.41, 110, 220, 440];
        var signal = new TestSignal(
            "low-tones",
            [Signals.Additive(6, tones, [0.2, 0.2, 0.2, 0.2, 0.2])],
            Signals.Rate
        ) { Tones = tones, Partials = tones };
        var rows = new List<string[]>();
        foreach (var config in configs) {
            foreach (var shift in new double[] { -12, -5, 3, 7, 12 }) {
                var r = Run(signal, config, 1, shift);
                var t = Analysis.Tones(r.Audio[0], Signals.Rate, tones, tones, Math.Pow(2, shift / 12));
                rows.Add([config.Name, F(shift, 0), ..t.ErrorCents.Select(static e => F(e)), F(t.NonPartialDb)]);
            }
        }

        report.Line("## Transposed low tones against block size (pitch error in cents per input tone)");
        report.Line();
        report.Table(["config", "shift st", ..tones.Select(static f => $"{f:0.#} Hz"), "non-partial dB"], rows);
    }

    void TempoFollowing(StretchConfig config) {
        var rows = new List<string[]>();
        var drums = signals["drums"];
        var clicks = signals["clicks"];
        double length = drums.Audio[0].Length;
        (string Name, Func<long, double> RateAt)[] cases = [
            ("ramp 0.8→1.25×", k => 1 / (0.8 + 0.45 * Math.Min(1, k / length))),
            ("wobble 1±0.15× at 0.5 Hz", static k => 1 / (1 + 0.15 * Math.Sin(2 * Math.PI * 0.5 * k / Signals.Rate))),
            ("steps 1.0/1.33/0.8× every 1 s", static k => (k / Signals.Rate % 3) switch {
                0 => 1.0,
                1 => 0.75,
                _ => 1.25
            })
        ];
        foreach (var (name, rateAt) in cases) {
            var label = new string(name.Where(char.IsAsciiLetterOrDigit).ToArray());
            var d = Analysis.Transients(
                drums.Audio,
                Run(drums, config, 1, 0, rateAt: rateAt, label: label),
                drums.Onsets,
                Signals.Rate,
                40
            );
            var c = Analysis.Transients(
                clicks.Audio,
                Run(clicks, config, 1, 0, rateAt: rateAt, label: label),
                clicks.Onsets,
                Signals.Rate,
                100
            );
            rows.Add(
                [
                    name, F(Analysis.MedianAbs(d.Select(static x => x.OnsetErrorMs))),
                    F(d.Max(static x => Math.Abs(x.OnsetErrorMs))),
                    F(Analysis.MedianAbs(c.Select(static x => x.OnsetErrorMs))),
                    F(c.Max(static x => Math.Abs(x.OnsetErrorMs))),
                    F(Analysis.Percentile(c.Select(static x => x.Width90Ms), 50))
                ]
            );
        }

        report.Line($"## Tempo following (rate changes every 128-frame block), `{config.Name}`");
        report.Line();
        report.Table(
            [
                "rate curve", "drum onset err median ms", "drum max ms", "click onset err median ms", "click max ms",
                "click 90 % width ms"
            ],
            rows
        );
    }

    public void Cpu() {
        Console.Error.WriteLine("cpu");
        var mix = Signals.Mix(20);
        (string Name, double Ratio, double Shift, bool Formant)[] modes = [
            ("1×", 1, 0, false), ("0.5×", 0.5, 0, false), ("1.25×", 1.25, 0, false), ("2×", 2, 0, false),
            ("+7 st", 1, 7, false),
            ("+7 st formant", 1, 7, true), ("1.25× −5 st formant", 1.25, -5, true)
        ];
        var rows = new List<string[]>();
        foreach (var config in Configs) {
            foreach (var channels in new[] { 1, 2 }) {
                var audio = mix.Take(channels).ToArray();
                var row = new List<string> { config.Name, channels.ToString(CultureInfo.InvariantCulture) };
                foreach (var mode in modes) {
                    var samples = new List<double>();
                    for (var rep = 0; rep < 4; rep++) {
                        using var s = new Stretcher(channels, Signals.Rate, config);
                        s.SetTranspose(mode.Shift);
                        if (mode.Formant) {
                            s.SetFormant(1, true, 0);
                        }

                        var r = Renderer.Render(s, audio, mode.Ratio, 128);
                        var seconds = r.TotalTicks / (double)System.Diagnostics.Stopwatch.Frequency;
                        var audioSeconds = r.Audio[0].Length / (double)Signals.Rate;
                        if (rep > 0) { // the first is warm-up
                            samples.Add(100 * seconds / audioSeconds / channels);
                        }
                    }

                    row.Add(F(Analysis.Percentile(samples, 50), 2));
                }

                rows.Add([..row]);
            }
        }

        report.Line("## CPU: % of one core per channel (output-time real-time factor × 100 / channels)");
        report.Line();
        report.Line("20 s of a chord + drums + vocal mix, 128-frame output blocks, median of 3 runs after a warm-up.");
        report.Line();
        report.Table(["config", "ch", ..modes.Select(static m => m.Name)], rows);
    }

    public void Blocks() {
        Console.Error.WriteLine("blocks");
        var mix = Signals.Mix(20);
        var rows = new List<string[]>();
        foreach (var config in Configs) {
            foreach (var block in new[] { 32, 64, 128, 256, 512, 1024 }) {
                Rendered? r = null;
                for (var rep = 0; rep < 2; rep++) {
                    using var s = new Stretcher(2, Signals.Rate, config);
                    r = Renderer.Render(s, mix, 1.25, block);
                }

                var us = r!.CallTicks.Take(r.Calls)
                    .Select(static t => t * 1e6 / System.Diagnostics.Stopwatch.Frequency)
                    .ToArray();
                var budget = block * 1e6 / Signals.Rate;
                rows.Add(
                    [
                        config.Name, block.ToString(CultureInfo.InvariantCulture), F(budget, 0), F(us.Average()),
                        F(Analysis.Percentile(us, 50)), F(Analysis.Percentile(us, 99)),
                        F(Analysis.Percentile(us, 99.9)), F(us.Max()),
                        F(100 * Analysis.Percentile(us, 99.9) / budget, 0)
                    ]
                );
            }
        }

        report.Line("## Callback size: time per process call, stereo, 1.25×");
        report.Line();
        report.Table(
            [
                "config", "frames", "budget µs", "mean µs", "median µs", "p99 µs", "p99.9 µs", "max µs",
                "p99.9 % of budget"
            ],
            rows
        );
        report.Line(
            "One run of 20 s per row (after a warm-up run); max includes OS preemption, p99.9 is the spectral-block cost."
        );
        report.Line();
    }

    public void Files(IEnumerable<string> paths) {
        report.Line("## Supplied files (`default`)");
        report.Line();
        var rows = new List<string[]>();
        var config = QualityConfigs[0];
        foreach (var path in paths) {
            var (audio, rate) = Wav.Read(path);
            var signal = new TestSignal(Path.GetFileNameWithoutExtension(path), audio, rate);
            Console.Error.WriteLine($"file: {signal.Name}");
            if (writeWav) {
                Wav.Write(Path.Combine(output, signal.Name, "input.wav"), audio, rate);
            }

            foreach (var ratio in Ratios) {
                var r = Run(signal, config, ratio, 0);
                var seconds = r.TotalTicks / (double)System.Diagnostics.Stopwatch.Frequency;
                rows.Add(
                    [
                        signal.Name, F(ratio, 2) + "×", F(Analysis.RmsDb(r.Audio) - Analysis.RmsDb(audio), 2),
                        F(Analysis.SpectralDeviationDb(audio[0], r.Audio[0], rate), 2),
                        F(100 * seconds / (r.Audio[0].Length / (double)rate) / audio.Length, 2)
                    ]
                );
            }

            foreach (var shift in Shifts) {
                Run(signal, config, 1, shift);
                Run(signal, config, 1, shift, true);
            }
        }

        report.Table(["file", "ratio", "RMS Δ dB", "balance dev dB", "% core per channel"], rows);
    }
}
