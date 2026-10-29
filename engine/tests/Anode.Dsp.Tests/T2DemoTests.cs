using Anode.Dsp.Instruments;
using Anode.Dsp.Instruments.Drums;
using Anode.Dsp.Instruments.Fm;
using Anode.Dsp.Instruments.Organ;
using Anode.Dsp.Instruments.Piano;
using Anode.Dsp.Instruments.Quick;
using Anode.Dsp.Instruments.Synth;
using Anode.Dsp.Instruments.Wavetable;
using Anode.Dsp.MidiEffects;
using Anode.Dsp.Tests.Support;
using Anode.Dsp.Transport;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using D = Anode.Dsp.Instruments.Drums.DrumSynthParameters;
using E = Anode.Dsp.Instruments.Piano.ElectricPianoParameters;
using F = Anode.Dsp.Instruments.Fm.FmParameters;
using O = Anode.Dsp.Instruments.Organ.OrganParameters;
using Q = Anode.Dsp.Instruments.Quick.QuickSamplerParameters;
using W = Anode.Dsp.Instruments.Wavetable.WavetableParameters;

namespace Anode.Dsp.Tests;

/// <summary>
///     The T2 instruments' and MIDI effects' listening material and CPU table, written to
///     <c>engine/artifacts/dsp-demo</c> (git-ignored) as <see cref="DemoTests" /> does: they run with
///     <c>ANODE_DSP_DEMO=1</c> and are skipped otherwise.
/// </summary>
public sealed class T2DemoTests {
    /// <summary>A beat at 120 bpm, in frames.</summary>
    const int Beat = 24_000;

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
                events.Add(TimedEvent.On(c * beats * Beat + note % 3 * 300, note, velocity));
                events.Add(TimedEvent.Off((c + 1) * beats * Beat - 400, note));
            }
        }

        return events;
    }

    static readonly int[][] Progression = [[50, 57, 60, 65], [48, 55, 58, 64], [46, 53, 57, 62], [45, 52, 57, 61]];

    static void Mix((float[] Left, float[] Right) into, (float[] Left, float[] Right) from, float gain = 1) {
        for (var i = 0; i < into.Left.Length; i++) {
            into.Left[i] += from.Left[i] * gain;
            into.Right[i] += from.Right[i] * gain;
        }
    }

    [Fact]
    public void Render_t2_demos() {
        var folder = Output();

        // A Drum Synth kit: one instance a pad, as a Drum Machine Designer kit plays it.
        var bars = 4;
        var frames = bars * 4 * Beat;
        var kit = (new float[frames], new float[frames]);
        (DrumModel Model, int[] Steps, (int, float)[] Settings)[] pads = [
            (DrumModel.Kick, [0, 6, 8, 11], [(D.Decay, 45), (D.Drive, 30)]),
            (DrumModel.Snare, [4, 12], [(D.Snap, 70)]),
            (DrumModel.Clap, [12], [(D.Volume, -12)]),
            (DrumModel.Hat, [0, 2, 4, 6, 8, 10, 12, 14], [(D.Decay, 15), (D.Volume, -12)]),
            (DrumModel.Hat, [3, 15], [(D.Decay, 70), (D.Volume, -15)]),
            (DrumModel.Tom, [13, 14], [(D.Tune, 3)]),
            (DrumModel.Cymbal, [0], [(D.Volume, -14)]),
            (DrumModel.Percussion, [5, 9], [(D.Tone, 90), (D.Volume, -16)])
        ];
        foreach (var (model, steps, settings) in pads) {
            using var pad = Play.Create(DrumSynth.Id, [(D.Model, (int)model), .. settings]);
            var events = new List<TimedEvent>();
            for (var bar = 0; bar < bars; bar++) {
                foreach (var step in steps) {
                    events.Add(TimedEvent.On(bar * 4 * Beat + step * Beat / 4, 60, 90 + step % 3 * 15));
                }
            }

            Mix(kit, Play.Render(pad, frames, events));
        }

        Save(folder, "t2-drum-synth-kit", kit);

        using (var fm = Play.Create(FmSynth.Id)) {
            Save(folder, "t2-fm-electric-piano", Play.Render(fm, 17 * Beat, Chords(Progression, 4)));
        }

        using (var bell = Play.Create(
                   FmSynth.Id,
                   (F.Algorithm, 4),
                   (F.Ratio(2), 3.5f),
                   (F.Level(2), 60),
                   (F.Decay(1), 6_000),
                   (F.Sustain(1), 0),
                   (F.Decay(2), 3_000),
                   (F.Sustain(2), 0)
               )) {
            var line = new[] { 72, 79, 76, 84, 83, 79, 76, 74 };
            var events = line.SelectMany((n, i) => new[] { TimedEvent.On(i * Beat, n, 100), TimedEvent.Off(i * Beat + Beat / 2, n) })
                .ToList();
            Save(folder, "t2-fm-bells", Play.Render(bell, 12 * Beat, events));
        }

        using (var organ = Play.Create(DrawbarOrgan.Id, (O.Drawbar(4), 6), (O.Drawbar(9), 4))) {
            var events = Chords(Progression, 4, 100);
            events.Add(TimedEvent.Cc(8 * Beat, 1, 127)); // the rotary to fast
            events.Add(TimedEvent.Cc(13 * Beat, 1, 0));
            Save(folder, "t2-organ-rotary", Play.Render(organ, 18 * Beat, events));
        }

        using (var tines = Play.Create(ElectricPiano.Id, (E.TremoloDepth, 40))) {
            Save(folder, "t2-electric-piano-tines", Play.Render(tines, 17 * Beat, Chords(Progression, 4, 100)));
        }

        using (var reeds = Play.Create(ElectricPiano.Id, (E.Model, 1), (E.TremoloDepth, 30), (E.Bark, 60))) {
            Save(folder, "t2-electric-piano-reeds", Play.Render(reeds, 17 * Beat, Chords(Progression, 4, 115)));
        }

        using (var pad = Play.Create(
                   WavetableSynth.Id,
                   (W.EnvelopeAttack(1), 600),
                   (W.EnvelopeAttack(1) + 3, 2_000),
                   (W.EnvelopeAttack(3) + 1, 6_000),
                   (W.Lfo1Sync, 2) // 1/2
               )) {
            Save(folder, "t2-wavetable-pad", Play.Render(pad, 18 * Beat, Chords(Progression, 4, 80)));
        }

        // An arpeggio: Arpeggiator (1/16, up-down, 2 octaves) into the synth, at 120 bpm.
        using (var synth = Play.Create(AnodeSynth.Id, (SynthParameters.AmpRelease, 120), (SynthParameters.Cutoff, 2_500))) {
            using var arp = MidiRun.Create(BuiltinMidiEffects.ArpeggiatorId, (Arpeggiator.Mode, 2), (Arpeggiator.Octaves, 2));
            var held = Chords(Progression, 4);
            var contexts = MidiRun.Contexts(17 * Beat, static _ => 120);
            var notes = MidiRun.Run([arp], contexts, held)
                .Select(static e => new TimedEvent(e.Frame, e.Status, e.Data1, e.Data2))
                .ToList();
            Save(folder, "t2-arpeggiator", Play.Render(synth, 17 * Beat, notes));
        }

        // The Quick Sampler slicing the drum kit's first bar, played back in a new order.
        var loop = Path.Combine(folder, "t2-drum-synth-kit.wav");
        using (var slicer = Play.Create(QuickSampler.Id, (Q.Mode, (int)QuickSamplerMode.Slice), (Q.SliceMode, 3), (Q.End, 25))) {
            Assert.True(slicer.LoadFile(loop));
            int[] order = [0, 1, 2, 3, 4, 5, 6, 7, 0, 0, 2, 3, 12, 13, 14, 15];
            var events = new List<TimedEvent>();
            for (var bar = 0; bar < 2; bar++) {
                for (var s = 0; s < order.Length; s++) {
                    events.Add(TimedEvent.On(bar * 4 * Beat + s * Beat / 4, 36 + order[s], 120));
                }
            }

            Save(folder, "t2-quick-sampler-slices", Play.Render(slicer, 8 * Beat, events));
        }
    }

    [Fact]
    public void Measure_t2_cpu() {
        var folder = Output();
        var period = 64 / 48_000.0 * 1e6;
        var report = new StringBuilder();
        report.AppendLine(
            CultureInfo.InvariantCulture,
            $"Anode.Dsp T2 instruments and MIDI FX, 48 kHz, 64-frame blocks (period {period:0.0} µs), one core"
        );
        report.AppendLine(
            CultureInfo.InvariantCulture,
            $"{Environment.OSVersion}, {Environment.ProcessorCount} cores, .NET {Environment.Version}"
        );
        report.AppendLine();
        report.AppendLine("| Instrument | Voices | Median µs | Median % | p99 µs | p99 % |");
        report.AppendLine("|---|---:|---:|---:|---:|---:|");
        var sample = Path.Combine(folder, "t2-cpu-tone.wav");
        var tone = TestWav.Pluck(220, 48_000, 4, 3);
        TestWav.Write(sample, 48_000, [tone, tone]);
        (string Name, Func<IInstrument> Create, int Voices)[] cases = [
            ("Drum Synth (cymbal)", () => Play.Create(DrumSynth.Id, (D.Model, (int)DrumModel.Cymbal), (D.Decay, 100)), 8),
            ("Drum Synth (kick)", () => Play.Create(DrumSynth.Id, (D.Decay, 100)), 8),
            ("FM synth", () => Play.Create(FmSynth.Id, (F.Sustain(1), 100), (F.Sustain(3), 100)), 16),
            ("FM synth", () => Play.Create(FmSynth.Id), 1),
            ("Drawbar organ (C3, rotary)", () => Play.Create(DrawbarOrgan.Id), 32),
            ("Drawbar organ (C3, rotary)", () => Play.Create(DrawbarOrgan.Id), 1),
            ("Electric piano", () => Play.Create(ElectricPiano.Id, (E.Decay, 100), (E.TremoloDepth, 50)), 32),
            ("Wavetable synth", () => Play.Create(WavetableSynth.Id, (W.SubLevel, 50)), 16),
            ("Wavetable synth", () => Play.Create(WavetableSynth.Id), 1),
            ("Quick Sampler (classic)", () => {
                    var q = Play.Create(QuickSampler.Id);
                    Assert.True(q.LoadFile(sample));
                    return q;
                }, 16)
        ];
        foreach (var (name, create, voices) in cases) {
            using var instrument = create();
            var (median, p99) = Time(instrument, voices);
            report.AppendLine(
                CultureInfo.InvariantCulture,
                $"| {name} | {voices} | {median:0.0} | {100 * median / period:0.00} | {p99:0.0} | {100 * p99 / period:0.00} |"
            );
        }

        report.AppendLine();
        report.AppendLine("| MIDI effect | Load | Median µs | p99 µs |");
        report.AppendLine("|---|---|---:|---:|");
        foreach (var descriptor in BuiltinMidiEffects.All) {
            using var effect = MidiRun.Create(descriptor.Id);
            var (median, p99) = TimeMidi(effect);
            report.AppendLine(
                CultureInfo.InvariantCulture,
                $"| {descriptor.Name} | 8 held notes, a CC a block | {median:0.00} | {p99:0.00} |"
            );
        }

        File.WriteAllText(Path.Combine(folder, "t2-cpu.md"), report.ToString());
        TestContext.Current.TestOutputHelper?.WriteLine(report.ToString());
    }

    /// <summary>
    ///     Holds <paramref name="voices" /> notes (struck again every 500 blocks, so drums and one-shots keep sounding),
    ///     then times 64-frame blocks: the median and the 99th percentile.
    /// </summary>
    static (double Median, double P99) Time(IInstrument instrument, int voices) {
        var left = new float[64];
        var right = new float[64];
        var notes = Enumerable.Range(0, voices)
            .Select(n => new MidiEvent(0, 0x90, (byte)(36 + n * 48 / Math.Max(voices, 1) + n % 2), 100))
            .ToArray();
        instrument.Process(notes, left, right);
        for (var i = 0; i < 500; i++) {
            instrument.Process([], left, right);
        }

        var times = new double[20_000];
        for (var i = 0; i < times.Length; i++) {
            if (i % 500 == 0) {
                instrument.Process(notes, left, right); // keep the voices going (drums, one-shots)
                continue;
            }

            var start = Stopwatch.GetTimestamp();
            instrument.Process([], left, right);
            times[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
        }

        var measured = times.Where((_, i) => i % 500 != 0).Order().ToArray();
        return (measured[measured.Length / 2], measured[(int)(measured.Length * 0.99)]);
    }

    static (double Median, double P99) TimeMidi(IMidiEffect effect) {
        var output = new MidiEventBuffer();
        var chord = Enumerable.Range(0, 8).Select(static n => new MidiEvent(0, 0x90, (byte)(48 + n * 3), 100)).ToArray();
        var cc = new MidiEvent[] { new(10, 0xB0, 1, 64) };
        var times = new double[20_000];
        var quarter = 0.0;
        for (var i = -1; i < times.Length; i++) {
            var context = new TransportContext(true, 0, quarter, 120, 4, 4, 0, 48_000, 64);
            output.Clear();
            var start = Stopwatch.GetTimestamp();
            effect.Process(context, i < 0 ? chord : cc, output);
            if (i >= 0) {
                times[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
            }

            quarter += 64 * context.QuartersPerSample;
        }

        Array.Sort(times);
        return (times[times.Length / 2], times[(int)(times.Length * 0.99)]);
    }
}
