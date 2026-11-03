using Anode.Dsp.Analysis.Chords;
using Anode.Dsp.Tests.Support;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using static Anode.Dsp.Tests.Analysis.Chords.ChordMaterial;

namespace Anode.Dsp.Tests.Analysis.Chords;

/// <summary>
///     The Chord ID accuracies in docs/generative/README.md: written to <c>engine/artifacts/generative/chord-report.md</c>
///     with <c>ANODE_CHORD_REPORT=1</c>.
/// </summary>
public sealed class ChordReportTests {
    const double Bpm = 120;

    [Fact]
    public void Report() {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("ANODE_CHORD_REPORT") == "1",
            "Set ANODE_CHORD_REPORT=1 to measure Chord ID."
        );
        var c = CultureInfo.InvariantCulture;
        var report = new StringBuilder();
        report.AppendLine(
            c,
            $"Measured {DateTime.UtcNow:yyyy-MM-dd} on {RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}, .NET {Environment.Version}."
        );
        report.AppendLine();
        report.AppendLine("| Material | Chords | Root and quality | With the bass | Same notes and bass |");
        report.AppendLine("|---|---|---|---|---|");

        void Row(string name, IReadOnlyList<Truth> truth, IReadOnlyList<RecognisedChord> found, long unit) =>
            report.AppendLine(
                c,
                $"| {name} | {truth.Count} | {Correct(truth, found, unit, false)} | {Correct(truth, found, unit, true)} | {Correct(truth, found, unit, true, true)} |"
            );

        // MIDI: the golden path's progression in each voicing, untidy, and random progressions.
        foreach (var voicing in Enum.GetValues<Voicing>()) {
            Row(
                $"MIDI, progression, {voicing}",
                Heard(Progression, voicing),
                Midi(Notes(Progression, voicing), 8),
                Bar
            );
        }

        for (var seed = 1; seed <= 3; seed++) {
            Row(
                $"MIDI, progression, humanised with passing notes ({seed})",
                Progression,
                Midi(Humanised(Notes(Progression, Voicing.Piano), seed), 8),
                Bar
            );
        }

        var (truthMidi, foundMidi) = Random(40, 8, false, null);
        Row("MIDI, 40 random progressions × 8", truthMidi, foundMidi, Bar);

        // Audio: the Electric Piano.
        const long barFrames = 2L * Rate;
        foreach (var voicing in (Voicing[])
                 [Voicing.Piano, Voicing.Close, Voicing.UpperInversions, Voicing.FirstInversion]) {
            var audio = Render(Notes(Progression, voicing), Bpm);
            Row($"Electric Piano, progression, {voicing}", Heard(Progression, voicing), Audio(audio, 8), barFrames);
        }

        foreach (var snr in (double[])[30, 20, 15, 10]) {
            var audio = WithNoise(Render(Notes(Progression, Voicing.Piano), Bpm), snr, 5);
            Row($"Electric Piano, progression, white noise {snr} dB below", Progression, Audio(audio, 8), barFrames);
        }

        var drums = WithDrums(Render(Notes(Progression, Voicing.Piano), Bpm), Bpm, 8, 0.5f);
        Row(
            "Electric Piano over drums, the beat tracker's beats",
            Progression,
            AudioChordRecognizer.Recognise(drums, Rate, []),
            barFrames
        );
        var (truthAudio, foundAudio) = Random(10, 8, true, null);
        Row("Electric Piano, 10 random progressions × 8", truthAudio, foundAudio, barFrames);

        // Speed.
        var minute = Render(Notes([..Enumerable.Repeat(Progression, 4).SelectMany(static p => p)], Voicing.Piano), Bpm);
        var watch = Stopwatch.StartNew();
        AudioChordRecognizer.Recognise(minute, Rate, BeatFrames(32, Bpm));
        var seconds = minute.Length / (double)Rate;
        report.AppendLine();
        report.AppendLine(
            c,
            $"Audio recognition: {seconds / watch.Elapsed.TotalSeconds:0} × real time ({seconds:0} s of 48 kHz mono, one thread)."
        );

        var folder = Path.Combine(Play.EngineRoot(), "artifacts", "generative");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "chord-report.md"), report.ToString());
    }

    static IReadOnlyList<RecognisedChord> Midi(ChordNote[] notes, int bars) =>
        MidiChordRecognizer.Recognise(notes, BeatTicks(bars), Beat);

    static IReadOnlyList<RecognisedChord> Audio(float[] audio, int bars) =>
        AudioChordRecognizer.Recognise(audio, Rate, BeatFrames(bars, Bpm));

    /// <summary>
    ///     Random progressions, each played in a voicing by turn, as one truth and one finding (each run shifted after
    ///     the last).
    /// </summary>
    static (List<Truth> Truth, List<RecognisedChord> Found) Random(
        int runs,
        int bars,
        bool audio,
        DetectedMusicalKey? key
    ) {
        var truth = new List<Truth>();
        var found = new List<RecognisedChord>();
        var unit = audio ? 2L * Rate : Bar;
        for (var seed = 0; seed < runs; seed++) {
            var progression = RandomProgression(new(seed), bars);
            var voicing = (Voicing)(seed % 3);
            var notes = Notes(progression, voicing);
            var chords = audio
                ? AudioChordRecognizer.Recognise(Render(notes, Bpm), Rate, BeatFrames(bars, Bpm), key)
                : MidiChordRecognizer.Recognise(notes, BeatTicks(bars), Beat, key);
            var offset = truth.Count * unit;
            truth.AddRange(progression);
            found.AddRange(chords.Select(ch => ch with { Start = ch.Start + offset, End = ch.End + offset }));
        }

        return (truth, found);
    }
}
