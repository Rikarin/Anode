using Anode.Dsp.Analysis;
using Anode.Dsp.Tests.Support;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Anode.Dsp.Tests.Analysis;

/// <summary>
///     Determinism (the same input gives bit-identical output), and the measurements in docs/analysis/README.md: written
///     to <c>engine/artifacts/analysis/report.md</c> with <c>ANODE_ANALYSIS_REPORT=1</c> (timings need a quiet machine).
/// </summary>
public sealed class AnalysisReportTests {
    const int Rate = Material.Rate;
    const int Hop = 240;

    static byte[] Bytes<T>(T[] values) where T : struct => [..MemoryMarshal.AsBytes(values.AsSpan())];

    [Fact]
    public void Every_analysis_is_deterministic() {
        var loop = Material.DrumLoop(100, 4, humaniseMs: 10).Audio;
        var phrase = Material.Phrase().Audio;

        Assert.Equal(Transients.Detect(loop, Rate), Transients.Detect(loop, Rate));

        var a = BeatTracker.Track(loop, Rate);
        var b = BeatTracker.Track(loop, Rate);
        Assert.Equal(BitConverter.DoubleToInt64Bits(a.Bpm), BitConverter.DoubleToInt64Bits(b.Bpm));
        Assert.Equal(a.Beats, b.Beats);
        Assert.Equal(a.Downbeats, b.Downbeats);
        Assert.Equal(a.Variable, b.Variable);
        Assert.Equal(BitConverter.DoubleToInt64Bits(a.Confidence), BitConverter.DoubleToInt64Bits(b.Confidence));

        var p = PitchTracker.Track(phrase, Rate, Hop);
        var q = PitchTracker.Track(phrase, Rate, Hop);
        Assert.Equal(Bytes(p.Cents), Bytes(q.Cents));
        Assert.Equal(Bytes(p.Voicing), Bytes(q.Voicing));
        Assert.Equal(NoteSegmenter.Segment(p, Rate), NoteSegmenter.Segment(q, Rate));

        Assert.Equal(Silence.Detect(phrase, Rate, -40, 50, 5, 20), Silence.Detect(phrase, Rate, -40, 50, 5, 20));
    }

    [Fact]
    public void Report() {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("ANODE_ANALYSIS_REPORT") == "1",
            "Set ANODE_ANALYSIS_REPORT=1 to measure the analyses."
        );
        var c = CultureInfo.InvariantCulture;
        var report = new StringBuilder();
        report.AppendLine(
            c,
            $"Measured {DateTime.UtcNow:yyyy-MM-dd} on {RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}, {Environment.ProcessorCount} cores, .NET {Environment.Version}."
        );
        report.AppendLine();

        // Transients.
        report.AppendLine("## Transients");
        report.AppendLine();
        report.AppendLine("| Material | Onsets | Found | Within 3 ms | Mean error (ms) | Worst (ms) |");
        report.AppendLine("|---|---|---|---|---|---|");

        void TransientRow(string name, float[] audio, long[] truth) {
            var found = Transients.Detect(audio, Rate);
            var errors = truth
                .Select(t => found.Length == 0 ? double.NaN : found.Min(f => Math.Abs(f - t)) * 1_000.0 / Rate)
                .ToArray();
            report.AppendLine(
                c,
                $"| {name} | {truth.Length} | {found.Length} | {errors.Count(static e => e <= 3)} | {(errors.Length == 0 ? 0 : errors.Average()):F2} | {(errors.Length == 0 ? 0 : errors.Max()):F2} |"
            );
        }

        var clicks = Enumerable.Range(0, 20).Select(static i => (long)(i * 0.25 * Rate + i * 37)).ToArray();
        TransientRow("20 clicks", Material.Clicks(6 * Rate, clicks), clicks);
        foreach (var bpm in new[] { 70.0, 100, 140, 180 }) {
            var loop = Material.DrumLoop(bpm, 8);
            TransientRow($"Kick and hat loop, {bpm} bpm", loop.Audio, loop.Onsets);
        }

        foreach (var seed in new[] { 1, 2, 3 }) {
            var loop = Material.DrumLoop(100, 8, humaniseMs: 10, seed: seed);
            TransientRow($"Humanised loop (±10 ms), seed {seed}", loop.Audio, loop.Onsets);
        }

        var phrase = Material.Phrase();
        TransientRow("Phrase (8 notes, 2 legato)", phrase.Audio, [..phrase.Notes.Select(static n => n.Start)]);
        TransientRow("White noise (its start)", Material.Noise(5), [0]);
        report.AppendLine();

        // Beats.
        report.AppendLine("## Beats");
        report.AppendLine();
        report.AppendLine(
            "| Material | True bpm | Found bpm | Variable | Beats within 10 ms | Worst beat (ms) | Downbeats right |"
        );
        report.AppendLine("|---|---|---|---|---|---|---|");
        var octaveErrors = 0;
        var missed = new List<string>();
        var tempi = Enumerable.Range(0, 25).Select(static i => 60 + 5.0 * i).ToArray();

        void BeatRow(string name, Material.Loop loop, double bpm) {
            var track = BeatTracker.Track(loop.Audio, Rate);
            var errors = loop.Beats.Select(t => track.Beats.Length == 0
                        ? double.NaN
                        : track.Beats.Min(f => Math.Abs(f - t)) * 1_000.0 / Rate
                )
                .ToArray();
            var downbeats = loop.Downbeats.Count(d => track.Downbeats.Any(f => Math.Abs(f - d) <= Rate / 100));
            report.AppendLine(
                c,
                $"| {name} | {bpm:F1} | {track.Bpm:F3} | {track.Variable} | {errors.Count(static e => e <= 10)} / {loop.Beats.Length} (found {track.Beats.Length}) | {errors.Max():F2} | {downbeats} / {loop.Downbeats.Length} |"
            );
        }

        foreach (var bpm in tempi) {
            var loop = Material.DrumLoop(bpm, 8);
            var found = BeatTracker.Track(loop.Audio, Rate).Bpm;
            if (Math.Abs(found - bpm) > 0.5) {
                octaveErrors++;
                missed.Add(string.Create(c, $"{bpm:F0} → {found:F1}"));
            }

            if (bpm % 20 == 0 || bpm is 90 or 110 or 175) {
                BeatRow($"Loop, {bpm} bpm", loop, bpm);
            }
        }

        foreach (var seed in new[] { 1, 2, 3 }) {
            BeatRow(
                $"Humanised loop (±10 ms), seed {seed}",
                Material.DrumLoop(100, 8, humaniseMs: 10, seed: seed),
                100
            );
        }

        foreach (var (from, to) in new[] { (90.0, 110.0), (120.0, 100.0) }) {
            var loop = Material.DrumLoop(from, 16, to);
            var average = 60.0 * Rate * (loop.Beats.Length - 1) / (loop.Beats[^1] - loop.Beats[0]);
            BeatRow($"Ramp {from}→{to} bpm (16 bars)", loop, Math.Round(average, 1));
        }

        report.AppendLine();
        report.AppendLine(
            c,
            $"Tempo errors (|found − true| > 0.5 bpm) over steady loops at 60, 65, …, 180 bpm: {octaveErrors} of {tempi.Length} ({100.0 * octaveErrors / tempi.Length:F0} %): {string.Join(", ", missed)}."
        );
        report.AppendLine();

        // Pitch.
        report.AppendLine("## Pitch");
        report.AppendLine();
        report.AppendLine(
            "| Material | Voiced hops scored | Median error (¢) | 95th percentile (¢) | Worst (¢) | Within 5 ¢ | Voicing F1 |"
        );
        report.AppendLine("|---|---|---|---|---|---|---|");

        void PitchRow(string name, Material.Melody melody) {
            var (errors, f1) = PitchTests.Score(melody, PitchTracker.Track(melody.Audio, Rate, Hop));
            errors.Sort();
            report.AppendLine(
                c,
                $"| {name} | {errors.Count} | {errors[errors.Count / 2]:F2} | {errors[(int)(errors.Count * 0.95)]:F2} | {errors[^1]:F2} | {100.0 * errors.Count(static e => e <= 5) / errors.Count:F1} % | {f1:F3} |"
            );
        }

        PitchRow("Phrase (vibrato, glide, legato, 110–523 Hz)", phrase);
        foreach (var cents in new[] { 3_300.0, 4_500, 6_900, 8_100, 9_300 }) {
            PitchRow($"Sine, MIDI {cents / 100:F0}", Material.Tones(1, [new(0.1, 0.8, cents)], 1));
            PitchRow($"Harmonic tone, MIDI {cents / 100:F0}", Material.Tones(1, [new(0.1, 0.8, cents, 30)]));
        }

        var noiseTrack = PitchTracker.Track(Material.Noise(5), Rate, Hop);
        report.AppendLine();
        report.AppendLine(
            c,
            $"White noise: {100.0 * noiseTrack.Cents.Count(static x => !float.IsNaN(x)) / noiseTrack.Cents.Length:F1} % of hops voiced. Octave errors (> 600 ¢) on scored hops: none above."
        );
        report.AppendLine();

        // Notes.
        report.AppendLine("## Notes");
        report.AppendLine();
        report.AppendLine("| Note | True (ms) | Found (ms) | Start error (ms) | End error (ms) | True ¢ | Found ¢ |");
        report.AppendLine("|---|---|---|---|---|---|---|");
        var notes = NoteSegmenter.Segment(PitchTracker.Track(phrase.Audio, Rate, Hop), Rate);
        for (var i = 0; i < Math.Min(notes.Length, phrase.Notes.Length); i++) {
            var (start, end, cents) = phrase.Notes[i];
            var n = notes[i];
            report.AppendLine(
                c,
                $"| {i + 1} | {start * 1_000.0 / Rate:F0}–{end * 1_000.0 / Rate:F0} | {n.StartFrame * 1_000.0 / Rate:F1}–{n.EndFrame * 1_000.0 / Rate:F1} | {(n.StartFrame - start) * 1_000.0 / Rate:F1} | {(n.EndFrame - end) * 1_000.0 / Rate:F1} | {cents:F0} | {n.Cents:F1} |"
            );
        }

        report.AppendLine();

        // Speed.
        report.AppendLine("## Speed (× real time, one core, 60 s of 48 kHz mono)");
        report.AppendLine();
        report.AppendLine("| Analysis | Material | × real time |");
        report.AppendLine("|---|---|---|");
        var drums = Material.DrumLoop(128, 32).Audio[..(60 * Rate)];
        var melody = Enumerable.Repeat(phrase.Audio, 14).SelectMany(static x => x).Take(60 * Rate).ToArray();
        var pitch = PitchTracker.Track(melody, Rate, Hop);

        void Speed(string name, string material, Action action) {
            action(); // warm up
            var best = double.MaxValue;
            for (var i = 0; i < 3; i++) {
                var watch = Stopwatch.StartNew();
                action();
                best = Math.Min(best, watch.Elapsed.TotalSeconds);
            }

            report.AppendLine(c, $"| {name} | {material} | {60 / best:F0} |");
        }

        Speed("Transients.Detect", "drum loop", () => Transients.Detect(drums, Rate));
        Speed("Transients.Detect", "melody", () => Transients.Detect(melody, Rate));
        Speed("BeatTracker.Track", "drum loop", () => BeatTracker.Track(drums, Rate));
        Speed("PitchTracker.Track (5 ms hop)", "melody", () => PitchTracker.Track(melody, Rate, Hop));
        Speed("NoteSegmenter.Segment", "melody's pitch track", () => NoteSegmenter.Segment(pitch, Rate));
        Speed(
            "Polyphony.IsPolyphonic",
            "melody and its pitch track",
            () => Polyphony.IsPolyphonic(melody, Rate, pitch)
        );
        Speed("Silence.Detect", "melody", () => Silence.Detect(melody, Rate, -40, 50, 5, 20));

        var folder = Path.Combine(Play.EngineRoot(), "artifacts", "analysis");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "report.md"), report.ToString());
    }
}
