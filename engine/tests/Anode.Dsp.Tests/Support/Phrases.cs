using Anode.Dsp.Instruments;
using Anode.Dsp.Instruments.Sampler;
using Anode.Dsp.Instruments.Synth;
using P = Anode.Dsp.Instruments.Synth.SynthParameters;

namespace Anode.Dsp.Tests.Support;

/// <summary>Short musical phrases and the patches and sample sets that play them, for golden renders and demos.</summary>
public static class Phrases {
    const int Beat = 12_000; // 120 bpm at 48 kHz

    /// <summary>A chord, a melody with the pedal and the mod wheel, a bend.</summary>
    public static List<TimedEvent> Golden() => [
        TimedEvent.On(0, 48), TimedEvent.On(0, 55, 80), TimedEvent.On(0, 64, 90),
        TimedEvent.Off(Beat, 48), TimedEvent.Off(Beat, 55), TimedEvent.Off(Beat, 64),
        TimedEvent.On(Beat, 72, 110), TimedEvent.Cc(Beat + 2_000, 1, 100), TimedEvent.Off(Beat + 6_000, 72),
        TimedEvent.Cc(Beat + 6_000, 64, 127), TimedEvent.On(Beat + 6_000, 74, 60), TimedEvent.Off(Beat + 9_000, 74),
        TimedEvent.On(Beat + 9_000, 76, 127), TimedEvent.Bend(Beat + 12_000, 4_000), TimedEvent.Off(Beat + 14_000, 76),
        TimedEvent.Cc(Beat + 18_000, 64, 0), TimedEvent.Cc(Beat + 18_000, 1, 0)
    ];

    public const int GoldenFrames = 2 * Beat + 24_000;

    /// <summary>The golden synth patch: two layers, a resonant swept filter, glide, vibrato on the wheel.</summary>
    public static IInstrument Synth() =>
        Play.Create(
            AnodeSynth.Id,
            (P.Osc2Wave, 1),
            (P.Osc2Octave, -1),
            (P.Osc2Width, 30),
            (P.Unison, 2),
            (P.Resonance, 40),
            (P.FilterEnvAmount, 50),
            (P.Glide, 30),
            (P.LfoPitch, 0.4f),
            (P.AmpRelease, 200)
        );

    /// <summary>
    ///     Writes a plucked-string SFZ into <paramref name="folder" />: two velocity layers per octave zone, the soft
    ///     layer as two round-robin samples, the loud one looped; returns its path.
    /// </summary>
    public static string PluckSfz(string folder) {
        var lines = new List<string> {
            "<control> default_path=samples/", "<global> ampeg_release=0.25 amp_veltrack=80"
        };
        Directory.CreateDirectory(Path.Combine(folder, "samples"));
        var seed = 1;
        foreach (var root in new[] { 36, 48, 60, 72, 84 }) {
            var frequency = 440 * Math.Pow(2, (root - 69) / 12.0);
            for (var rr = 1; rr <= 2; rr++) {
                var soft = $"soft {root}-{rr}.wav";
                TestWav.Write(
                    Path.Combine(folder, "samples", soft),
                    44_100,
                    [TestWav.Pluck(frequency, 44_100, 1.2, seed++)],
                    rootNote: root
                );
                lines.Add($"<group> lokey={root - 6} hikey={root + 5} hivel=90 seq_length=2 seq_position={rr}");
                lines.Add($"<region> sample={soft}");
            }

            var loud = $"loud {root}.wav";
            var samples = TestWav.Pluck(frequency, 44_100, 1.5, seed++);
            TestWav.Write(
                Path.Combine(folder, "samples", loud),
                44_100,
                [samples, TestWav.Pluck(frequency, 44_100, 1.5, seed++)],
                rootNote: root
            );
            var period = (int)Math.Round(44_100 / frequency);
            lines.Add($"<group> lokey={root - 6} hikey={root + 5} lovel=91");
            lines.Add(
                $"<region> sample={loud} loop_mode=loop_sustain loop_start={20_000} loop_end={20_000 + 40 * period - 1} pan=-20"
            );
        }

        var path = Path.Combine(folder, "plucks.sfz");
        File.WriteAllLines(path, lines);
        return path;
    }

    /// <summary>The golden sampler: <see cref="PluckSfz" /> loaded.</summary>
    public static IInstrument Sampler(string folder) {
        var sampler = Play.Create(AnodeSampler.Id, (AnodeSampler.Release, 50));
        if (!sampler.LoadFile(PluckSfz(folder))) {
            throw new InvalidOperationException("The pluck SFZ did not load.");
        }

        return sampler;
    }
}
