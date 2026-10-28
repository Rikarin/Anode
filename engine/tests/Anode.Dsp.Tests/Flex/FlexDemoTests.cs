using Anode.Dsp.Flex;
using Anode.Dsp.Tests.Support;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using static Anode.Dsp.Tests.Flex.FlexKit;

namespace Anode.Dsp.Tests.Flex;

/// <summary>
///     Listening material and the measurements in docs/flex/README.md, written to <c>engine/artifacts/flex-demo</c>
///     (git-ignored): golden renders of every algorithm on a drum loop and a melody, and <c>results.md</c>. They run with
///     <c>ANODE_DSP_DEMO=1</c>, as Phase 4's effect demos do, and are skipped otherwise: timings need a quiet machine.
/// </summary>
public sealed class FlexDemoTests {
    static string Output() {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("ANODE_DSP_DEMO") == "1",
            "Set ANODE_DSP_DEMO=1 to render the demos."
        );
        Assert.SkipUnless(FlexRenderers.StretchAvailable, "an_stretch is not built (engine/native/build.sh).");
        var path = Path.Combine(Play.EngineRoot(), "artifacts", "flex-demo");
        Directory.CreateDirectory(path);
        return path;
    }

    static void Save(string path, float[] interleaved) {
        var peak = interleaved.Max(Math.Abs);
        var scale = peak > 0.98 ? 0.98f / peak : 1f;
        TestWav.Write(
            path,
            Rate,
            [
                Channel(interleaved, 2).Select(s => s * scale).ToArray(),
                Channel(interleaved, 2, 1).Select(s => s * scale).ToArray()
            ]
        );
    }

    static string F(double value, string format = "F2") => value.ToString(format, CultureInfo.InvariantCulture);

    /// <summary>
    ///     A warp point on every beat of a <paramref name="beats" />-beat 100 bpm file, played at <paramref name="bpm" />.
    /// </summary>
    static FlexWarpPoint[] Follow(int beats, double bpm) {
        var file = Rate * 60 / 100.0;
        var played = Rate * 60 / bpm;
        return Warp([..Enumerable.Range(0, beats + 1).Select(b => ((long)Math.Round(b * played), b * file))]);
    }

    [Fact]
    public void Render_golden_files() {
        var folder = Output();
        var drums = ArraySource.Stereo(DrumLoop(16));
        var melody = ArraySource.Stereo(Melody());
        Save(Path.Combine(folder, "drums-original.wav"), drums.Data);
        Save(Path.Combine(folder, "melody-original.wav"), melody.Data);

        // Drums from 100 to 120 bpm, then to 80 bpm; one beat's hit dragged 60 ms late by a marker.
        var faster = Follow(16, 120);
        var slower = Follow(16, 80);
        var dragged = Follow(16, 100);
        dragged[5].Offset += Rate * 6 / 100;

        // The melody at 80 % speed, transposed up 3 semitones.
        var melodyMap = Constant((long)(melody.Frames / 0.8), 0.8);
        foreach (var kind in Algorithms) {
            var name = kind.ToString().ToLowerInvariant();
            Save(Path.Combine(folder, $"drums-120bpm-{name}.wav"), RenderAll(kind, drums, faster));
            Save(Path.Combine(folder, $"drums-80bpm-{name}.wav"), RenderAll(kind, drums, slower));
            Save(Path.Combine(folder, $"drums-marker-{name}.wav"), RenderAll(kind, drums, dragged));
            Save(Path.Combine(folder, $"melody-slower-up3-{name}.wav"), RenderAll(kind, melody, melodyMap, Shift(300)));
        }
    }

    [Fact]
    public void Measure() {
        var folder = Output();
        var report = new StringBuilder();
        report.AppendLine(
            CultureInfo.InvariantCulture,
            $"Machine: {Environment.ProcessorCount} logical cores, {Environment.OSVersion}, .NET {Environment.Version}, an_stretch release build."
        );
        report.AppendLine();

        report.AppendLine("## Onsets against the warp map (ms)");
        report.AppendLine();
        report.AppendLine("| algorithm | onsets | median | worst |");
        report.AppendLine("|---|---|---|---|");
        foreach (var kind in Algorithms.Where(static k => k != FlexKind.Tempophone)) {
            var (median, worst, count) = FlexAlgorithmTests.OnsetErrors(kind);
            report.AppendLine(
                CultureInfo.InvariantCulture,
                $"| {kind} | {count} | {F(median, "F3")} | {F(worst, "F3")} |"
            );
        }

        report.AppendLine();
        report.AppendLine("## Pitch of transposed tones (cents off the target)");
        report.AppendLine();
        report.AppendLine("| algorithm | rate, shift | 220 Hz | 440 Hz | 1000 Hz |");
        report.AppendLine("|---|---|---|---|---|");
        foreach (var kind in Stretch) {
            foreach (var (rate, cents) in new[] { (1.0, 700.0), (1.0, -500.0), (0.8, 300.0), (1.25, -1_200.0) }) {
                var row = new[] { 220.0, 440, 1_000 }.Select(f => F(FlexAlgorithmTests.PitchError(kind, f, rate, cents))
                );
                report.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"| {kind} | {rate}×, {cents:+0;-0} ¢ | {string.Join(" | ", row)} |"
                );
            }
        }

        foreach (var rate in new[] { 0.8, 1.25, 1.5 }) {
            var row = new[] { 220.0, 440, 1_000 }.Select(f => F(
                    FlexAlgorithmTests.PitchError(FlexKind.Speed, f, rate, 0),
                    "F3"
                )
            );
            report.AppendLine(
                CultureInfo.InvariantCulture,
                $"| Speed | {rate}× (target f·rate) | {string.Join(" | ", row)} |"
            );
        }

        report.AppendLine();
        report.AppendLine("## Formants of a vowel at 220 Hz (shift of the spectral envelope, semitones)");
        report.AppendLine();
        report.AppendLine("| shift | Pitch (compensated) | Polyphonic |");
        report.AppendLine("|---|---|---|");
        foreach (var cents in new[] { -700.0, -500, 300, 500, 700 }) {
            report.AppendLine(
                CultureInfo.InvariantCulture,
                $"| {cents:+0;-0} ¢ | {F(FlexAlgorithmTests.FormantShift(FlexKind.Pitch, cents), "F1")} | {F(FlexAlgorithmTests.FormantShift(FlexKind.Polyphonic, cents), "F1")} |"
            );
        }

        report.AppendLine();
        report.AppendLine("## Crossfades between anchored streams (Polyphonic, a 440 + 660 Hz tone)");
        report.AppendLine();
        report.AppendLine("Lowest 5 ms RMS inside the crossfades against the median 5 ms RMS outside them (dB).");
        report.AppendLine();
        report.AppendLine("| rate, shift | in-phase lead-in, raised cosine | lead-in on the warp map, equal power |");
        report.AppendLine("|---|---|---|");
        foreach (var (rate, cents) in new[] { (1.25, 0.0), (0.8, 0.0), (1.25, 300.0), (1.0, -500.0) }) {
            var shipped = cents == 0 ? F(CrossfadeDip(rate, cents, true)) + " (shipped)" : "—";
            var other = F(CrossfadeDip(rate, cents, false)) + (cents == 0 ? string.Empty : " (shipped)");
            report.AppendLine(CultureInfo.InvariantCulture, $"| {rate}×, {cents:+0;-0} ¢ | {shipped} | {other} |");
        }

        report.AppendLine();
        report.AppendLine("## Monophonic block (stereo clicks, and tones through the pitch cases)");
        report.AppendLine();
        report.AppendLine(
            "| block / interval | click onset median / worst ms | pre-echo dB | worst ¢ at 220 Hz | 330 Hz | 110 Hz | priming frames |"
        );
        report.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var (block, interval) in new[] { (0.12, 0.03), (0.1, 0.025), (0.08, 0.02), (0.06, 0.015) }) {
            report.AppendLine(MonophonicRow(block, interval));
        }

        report.AppendLine();
        report.AppendLine(
            "## CPU (stereo, 20 s of drums and melody, 1.2× with +300 ¢ where it applies; process CPU time, best of three)"
        );
        report.AppendLine();
        report.AppendLine("| algorithm | call frames | frames/s | × real time | 64 stereo tracks, % of a core |");
        report.AppendLine("|---|---|---|---|---|");
        foreach (var kind in Algorithms) {
            foreach (var block in new[] { 512, 16_384 }) {
                var framesPerSecond = Cpu(kind, block);
                var realTime = framesPerSecond / Rate;
                report.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"| {kind} | {block} | {F(framesPerSecond, "N0")} | {F(realTime, "F0")} | {F(6_400 / realTime, "F1")} |"
                );
            }
        }

        File.WriteAllText(Path.Combine(folder, "results.md"), report.ToString());
    }

    static double CrossfadeDip(double rate, double cents, bool aligned) {
        var source = ArraySource.Stereo(Tones(14 * Rate, 0.3, 440, 660));
        var warp = Constant(9 * Rate, rate);
        using var renderer = new StretchRenderer(FlexKind.Polyphonic, 2, Rate, 4_096);
        renderer.PhaseAlignedLeadIn = aligned;
        var pitch = cents == 0 ? [] : Shift(cents);
        var audio = Channel(Render(renderer, source, warp, pitch, warp[^1].Offset, 0, 9 * Rate, 512), 2);
        // Priming is a segment, a lead-in and a crossfade: 64, 8 and 2 stretcher intervals.
        var interval = renderer.PrimingFrames / 74;
        var segment = 64 * interval;
        var fade = 2 * interval;
        var inside = new List<double>();
        var outside = new List<double>();
        for (var start = Rate / 2; start + 240 < audio.Length - Rate / 2; start += 48) {
            var rms = Rms(audio.AsSpan(start, 240));
            var position = (start + 120) % segment;
            (position >= segment - fade ? inside : outside).Add(rms);
        }

        return 20 * Math.Log10(inside.Min() / outside.Order().ElementAt(outside.Count / 2));
    }

    static string MonophonicRow(double block, double interval) {
        var clicks = Enumerable.Range(0, 20).Select(static i => 6_000L + i * 12_000).ToArray();
        var mono = Clicks(5 * Rate, clicks);
        var warp = Warp((0, 0), (60_000, 48_000), (150_000, 160_000), (230_000, 230_000));
        using var renderer = new StretchRenderer(FlexKind.Monophonic, 2, Rate, 4_096, block, interval);
        var audio = Channel(Render(renderer, ArraySource.Stereo(mono), warp, [], 230_000, 0, 230_000, 512), 2);
        var errors = new List<double>();
        double preEcho = 0, energy = 0;
        foreach (var click in clicks) {
            var expected = OffsetOf(warp, Onset(mono, click));
            if (double.IsNaN(expected) || expected > 225_000) {
                continue;
            }

            var onset = Onset(audio, expected);
            errors.Add((onset - expected) * 1_000.0 / Rate);
            for (var i = (int)onset - Rate / 20; i < (int)onset - Rate / 1_000; i++) {
                preEcho += audio[i] * audio[i];
            }

            for (var i = (int)onset; i < (int)onset + Rate / 50; i++) {
                energy += audio[i] * audio[i];
            }
        }

        var (median, worst) = Stats(errors);
        // The worst pitch error over the four rate and shift cases of the pitch test, per tone.
        var pitch = new[] { 220.0, 330, 110 }.Select(f => {
                    var cases = new[] { (1.0, 700.0), (1.0, -500.0), (0.8, 300.0), (1.25, -1_200.0) };
                    return F(
                        cases.Max(c => {
                                var source = ArraySource.Stereo(Tones(5 * Rate, 0.5, f));
                                var length = (long)(4 * Rate / c.Item1);
                                using var tone = new StretchRenderer(
                                    FlexKind.Monophonic,
                                    2,
                                    Rate,
                                    4_096,
                                    block,
                                    interval
                                );
                                var warp = Constant(length, c.Item1);
                                var output = Channel(
                                    Render(tone, source, warp, Shift(c.Item2), length, 0, (int)length, 512),
                                    2
                                );
                                var measured = Support.Analysis.PeakFrequency(output, Rate, (int)(length / 2) - 20_000);
                                return Math.Abs(Support.Analysis.Cents(measured, f * Math.Pow(2, c.Item2 / 1_200)));
                            }
                        )
                    );
                }
            )
            .ToArray();
        return $"| {block * 1_000:F0} / {interval * 1_000:F0} ms | {F(median)} / {F(worst)} | {F(10 * Math.Log10(preEcho / energy), "F1")} | {pitch[0]} | {pitch[1]} | {pitch[2]} | {renderer.PrimingFrames} |";
    }

    static double Cpu(FlexKind kind, int block) {
        var drums = DrumLoop(32);
        var melody = Melody();
        var mix = new float[20 * Rate];
        for (var i = 0; i < mix.Length; i++) {
            mix[i] = 0.5f * (drums[i % drums.Length] + melody[i % melody.Length]);
        }

        var source = ArraySource.Stereo(mix);
        var length = (long)(mix.Length / 1.2) - Rate;
        var warp = Constant(length, 1.2);
        var pitch = Stretch.Contains(kind) ? Shift(300) : [];
        using var renderer = FlexRenderers.Create(kind, 2, Rate, 16_384);
        var buffer = new float[block * 2];
        for (long offset = 0; offset + block <= length; offset += block) {
            renderer.Render(new(kind, warp, pitch, length), offset, buffer, 2, source); // warm up, past tiering
        }

        var best = double.MaxValue;
        for (var run = 0; run < 3; run++) {
            renderer.Reset();
            var start = Process.GetCurrentProcess().TotalProcessorTime;
            for (long offset = 0; offset + block <= length; offset += block) {
                renderer.Render(new(kind, warp, pitch, length), offset, buffer, 2, source);
            }

            best = Math.Min(best, (Process.GetCurrentProcess().TotalProcessorTime - start).TotalSeconds);
        }

        return length / block * block / best;
    }
}
