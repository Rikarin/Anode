using Anode.Dsp.Effects;
using Anode.Dsp.Instruments;
using Anode.Dsp.Instruments.Sampler;
using Anode.Dsp.Instruments.Synth;
using Anode.Dsp.Tests.Support;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using P = Anode.Dsp.Instruments.Synth.SynthParameters;

namespace Anode.Dsp.Tests;

/// <summary>
///     Listening material and CPU figures, written to <c>engine/artifacts/dsp-demo</c> (git-ignored). They run with
///     <c>ANODE_DSP_DEMO=1</c> and are skipped otherwise: the demos take a while and timings need a quiet machine.
/// </summary>
public sealed class DemoTests {
    const int Beat = 12_000;

    static string Output() {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("ANODE_DSP_DEMO") == "1",
            "Set ANODE_DSP_DEMO=1 to render the demos."
        );
        var path = Path.Combine(Play.EngineRoot(), "artifacts", "dsp-demo");
        Directory.CreateDirectory(path);
        return path;
    }

    static void Save(string folder, string name, (float[] Left, float[] Right) audio) {
        var peak = Math.Max(Support.Analysis.Peak(audio.Left), Support.Analysis.Peak(audio.Right));
        var scale = peak > 0.98 ? (float)(0.98 / peak) : 1f;
        TestWav.Write(
            Path.Combine(folder, name + ".wav"),
            (int)Play.Rate,
            [audio.Left.Select(s => s * scale).ToArray(), audio.Right.Select(s => s * scale).ToArray()]
        );
    }

    static List<TimedEvent> Chords(int[][] chords, int beats, int velocity = 90) {
        var events = new List<TimedEvent>();
        for (var c = 0; c < chords.Length; c++) {
            foreach (var note in chords[c]) {
                events.Add(TimedEvent.On(c * beats * Beat, note, velocity));
                events.Add(TimedEvent.Off((c + 1) * beats * Beat - 200, note));
            }
        }

        return events;
    }

    static List<TimedEvent> Melody((int Note, double Beats)[] notes, int velocity = 100, double gate = 0.9) {
        var events = new List<TimedEvent>();
        double at = 0;
        foreach (var (note, beats) in notes) {
            if (note >= 0) {
                events.Add(TimedEvent.On((long)(at * Beat), note, velocity));
                events.Add(TimedEvent.Off((long)((at + beats * gate) * Beat), note));
            }

            at += beats;
        }

        return events;
    }

    [Fact]
    public void Render_demos() {
        var folder = Output();
        int[][] progression = [[48, 55, 60, 64, 67], [45, 52, 57, 60, 64], [41, 48, 53, 57, 60], [43, 50, 55, 59, 62]];

        using (var pad = Play.Create(
                   AnodeSynth.Id,
                   (P.Unison, 4),
                   (P.UnisonDetune, 18),
                   (P.AmpAttack, 400),
                   (P.AmpRelease, 1_500),
                   (P.Cutoff, 900),
                   (P.FilterEnvAmount, 20),
                   (P.FilterAttack, 800),
                   (P.FilterDecay, 3_000),
                   (P.LfoFilter, 15),
                   (P.LfoRate, 0.3f),
                   (P.LfoModWheel, 0),
                   (P.LfoPitch, 0)
               )) {
            Save(folder, "synth-pad", Play.Render(pad, 18 * Beat, Chords(progression, 4)));
        }

        using (var bass = Play.Create(
                   AnodeSynth.Id,
                   (P.Osc1Wave, 0),
                   (P.Osc2Wave, 1),
                   (P.Osc2Octave, -1),
                   (P.OscMix, 40),
                   (P.Cutoff, 300),
                   (P.Resonance, 55),
                   (P.FilterEnvAmount, 55),
                   (P.FilterDecay, 250),
                   (P.FilterSustain, 10),
                   (P.AmpDecay, 400),
                   (P.AmpSustain, 60),
                   (P.AmpRelease, 80),
                   (P.Glide, 60),
                   (P.Volume, -3)
               )) {
            var line = new (int, double)[] {
                (36, 0.5), (36, 0.5), (48, 0.5), (36, 0.5), (39, 0.5), (41, 0.5), (43, 0.5), (46, 0.5), (36, 0.5),
                (36, 0.5), (48, 0.5), (36, 0.5), (34, 0.5), (31, 0.5), (34, 0.5), (35, 0.5)
            };
            Save(folder, "synth-bass", Play.Render(bass, 9 * Beat, Melody(line, 110, 0.7)));
        }

        using (var lead = Play.Create(
                   AnodeSynth.Id,
                   (P.Osc1Wave, 1),
                   (P.Osc1Width, 35),
                   (P.Osc2Wave, 0),
                   (P.Osc2Fine, 9),
                   (P.Cutoff, 2_500),
                   (P.Resonance, 25),
                   (P.Glide, 80),
                   (P.LfoPitch, 0.5f),
                   (P.LfoRate, 5.5f),
                   (P.Unison, 2),
                   (P.UnisonDetune, 8)
               )) {
            var tune = new (int, double)[] {
                (72, 1), (74, 0.5), (76, 0.5), (79, 1.5), (76, 0.5), (74, 1), (72, 1), (69, 2), (-1, 0.5), (71, 0.5),
                (72, 3)
            };
            var events = Melody(tune, 100, 0.98);
            events.Add(TimedEvent.Cc(4 * Beat, 1, 90));
            events.Add(TimedEvent.Bend((long)(10.5 * Beat), 4_000));
            events.Add(TimedEvent.Bend(11 * Beat, 0));
            Save(folder, "synth-lead", Play.Render(lead, 13 * Beat, events));
        }

        using (var sweep = Play.Create(
                   AnodeSynth.Id,
                   (P.Osc1Wave, 0),
                   (P.OscMix, 0),
                   (P.FilterMode, 0),
                   (P.Resonance, 80),
                   (P.LfoShape, 4),
                   (P.LfoRate, 4),
                   (P.LfoFilter, 60),
                   (P.LfoModWheel, 0),
                   (P.LfoPitch, 0),
                   (P.Cutoff, 800),
                   (P.FilterEnvAmount, 0)
               )) {
            Save(folder, "synth-sample-and-hold", Play.Render(sweep, 8 * Beat, Chords([[36, 48]], 8)));
        }

        var samples = Path.Combine(folder, "pluck-sfz");
        using (var sampler = Phrases.Sampler(samples)) {
            var arpeggio = new List<TimedEvent>();
            var at = 0L;
            foreach (var chord in progression) {
                for (var r = 0; r < 2; r++) {
                    foreach (var note in chord) {
                        arpeggio.Add(TimedEvent.On(at, note + 12, 60 + (int)(at / 1_000 % 60)));
                        arpeggio.Add(TimedEvent.Off(at + Beat, note + 12));
                        at += Beat / 4;
                    }
                }
            }

            Save(folder, "sampler-plucks", Play.Render(sampler, (int)at + 2 * Beat, arpeggio));
        }

        using var golden = Phrases.Synth();
        Save(folder, "synth-golden-phrase", Play.Render(golden, Phrases.GoldenFrames, Phrases.Golden()));
    }

    [Fact]
    public void Measure_cpu() {
        var folder = Output();
        var report = new StringBuilder();
        report.AppendLine(
            CultureInfo.InvariantCulture,
            $"Anode.Dsp instruments, 48 kHz, 64-frame blocks (period {64 / 48.0:0.000} ms)"
        );
        report.AppendLine(
            CultureInfo.InvariantCulture,
            $"{Environment.OSVersion}, {Environment.ProcessorCount} cores, .NET {Environment.Version}"
        );
        report.AppendLine(
            Measure("synth, 16 voices, unison 1", Play.Create(AnodeSynth.Id, (P.Unison, 1), (P.AmpRelease, 10_000)), 16)
        );
        report.AppendLine(
            Measure("synth, 16 voices, unison 4", Play.Create(AnodeSynth.Id, (P.Unison, 4), (P.AmpRelease, 10_000)), 16)
        );
        report.AppendLine(Measure("synth, 1 voice, unison 1", Play.Create(AnodeSynth.Id, (P.Unison, 1)), 1));
        var sampler = Phrases.Sampler(Path.Combine(folder, "pluck-sfz"));
        sampler.SetParameter(AnodeSampler.Release, 10_000);
        report.AppendLine(Measure("sampler, 32 voices (looped, stereo)", sampler, 32, 100));
        File.WriteAllText(Path.Combine(folder, "cpu.txt"), report.ToString());
        TestContext.Current.TestOutputHelper?.WriteLine(report.ToString());
    }

    /// <summary>
    ///     Each effect at its <see cref="EffectPresets" /> setting on four seconds of <see cref="Fx.Music" /> (looped from
    ///     its 1.5 s), next to the dry signal: <c>effect-&lt;name&gt;.wav</c>.
    /// </summary>
    [Fact]
    public void Render_effect_demos() {
        var folder = Output();
        var (left, right) = Fx.Music();
        var dryLeft = Enumerable.Range(0, 192_000).Select(i => left[i % left.Length]).ToArray();
        var dryRight = Enumerable.Range(0, 192_000).Select(i => right[i % right.Length]).ToArray();
        Save(folder, "effect-dry", (dryLeft, dryRight));
        foreach (var descriptor in BuiltinEffects.All) {
            using var effect = Fx.Create(descriptor.Id, EffectPresets.For(descriptor.Id));
            Save(
                folder,
                "effect-" + descriptor.Id["anode.".Length..],
                Fx.Run(effect, dryLeft, dryRight, sidechainLeft: dryRight)
            );
        }
    }

    /// <summary>
    ///     The CPU of one instance of each effect at its <see cref="EffectPresets" /> setting, 48 kHz, 64-frame blocks of
    ///     program material: <c>effects-cpu.txt</c>.
    /// </summary>
    [Fact]
    public void Measure_effect_cpu() {
        var folder = Output();
        var report = new StringBuilder();
        var period = 64 / 48_000.0 * 1e6;
        report.AppendLine(
            CultureInfo.InvariantCulture,
            $"Anode.Dsp effects, one instance, 48 kHz, 64-frame blocks (period {period:0.0} µs)"
        );
        report.AppendLine(
            CultureInfo.InvariantCulture,
            $"{Environment.OSVersion}, {Environment.ProcessorCount} cores, .NET {Environment.Version}"
        );
        report.AppendLine("| Effect | Id | Median µs | Median % | p99 µs | p99 % |");
        report.AppendLine("|---|---|---:|---:|---:|---:|");
        var (music, musicRight) = Fx.Music();
        foreach (var descriptor in BuiltinEffects.All) {
            using var effect = Fx.Create(descriptor.Id, EffectPresets.For(descriptor.Id));
            var left = new float[64];
            var right = new float[64];
            var times = new double[20_000];
            for (var i = -2_000; i < times.Length; i++) {
                var at = (i + 2_000) * 64 % (music.Length - 64);
                music.AsSpan(at, 64).CopyTo(left);
                musicRight.AsSpan(at, 64).CopyTo(right);
                var start = Stopwatch.GetTimestamp();
                effect.Process(left, right, right, right);
                if (i >= 0) {
                    times[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
                }
            }

            Array.Sort(times);
            var median = times[times.Length / 2];
            var p99 = times[(int)(times.Length * 0.99)];
            report.AppendLine(
                CultureInfo.InvariantCulture,
                $"| {descriptor.Name} | {descriptor.Id} | {median:0.0} | {100 * median / period:0.00} | {p99:0.0} | {100 * p99 / period:0.00} |"
            );
        }

        File.WriteAllText(Path.Combine(folder, "effects-cpu.txt"), report.ToString());
        TestContext.Current.TestOutputHelper?.WriteLine(report.ToString());
    }

    /// <summary>Holds <paramref name="voices" /> notes, then times 64-frame blocks: the median and the 99th percentile.</summary>
    static string Measure(string name, IInstrument instrument, int voices, int velocity = 80) {
        using var _ = instrument;
        var left = new float[64];
        var right = new float[64];
        var notes = Enumerable.Range(0, voices)
            .Select(n => new MidiEvent(0, 0x90, (byte)(36 + n * 48 / Math.Max(voices, 1) + n % 2), (byte)velocity))
            .ToArray();
        instrument.Process(notes, left, right);
        for (var i = 0; i < 2_000; i++) {
            instrument.Process([], left, right);
        }

        var times = new double[20_000];
        for (var i = 0; i < times.Length; i++) {
            var start = Stopwatch.GetTimestamp();
            instrument.Process([], left, right);
            times[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
        }

        Array.Sort(times);
        var median = times[times.Length / 2];
        var p99 = times[(int)(times.Length * 0.99)];
        var period = 64 / 48_000.0 * 1e6;
        var active = instrument is AnodeSynth s ? s.ActiveVoices : instrument is AnodeSampler m ? m.ActiveVoices : -1;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{name,-40} {active,2} active: median {median,7:0.0} µs ({100 * median / period,5:0.00} %), p99 {p99,7:0.0} µs ({100 * p99 / period,5:0.00} %)"
        );
    }
}
