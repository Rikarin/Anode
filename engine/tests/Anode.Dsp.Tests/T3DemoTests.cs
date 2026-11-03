using Anode.Dsp.Effects;
using Anode.Dsp.Effects.Drive;
using Anode.Dsp.Effects.Surround;
using Anode.Dsp.Instruments;
using Anode.Dsp.Instruments.Alchemy;
using Anode.Dsp.Instruments.Physical;
using Anode.Dsp.Instruments.Tape;
using Anode.Dsp.Mastering;
using Anode.Dsp.Spatial;
using Anode.Dsp.Tests.Spatial;
using Anode.Dsp.Tests.Support;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using A = Anode.Dsp.Instruments.Alchemy.SampleAlchemyParameters;
using P = Anode.Dsp.Instruments.Physical.PhysicalModelParameters;
using T = Anode.Dsp.Instruments.Tape.TapeReplayParameters;
using static Anode.Dsp.Tests.Support.Analysis;

namespace Anode.Dsp.Tests;

/// <summary>
///     The T3 instruments' and effects' listening material, measurements and CPU table, written to
///     <c>engine/artifacts/dsp-demo</c> (git-ignored) as <see cref="T2DemoTests" /> does: they run with
///     <c>ANODE_DSP_DEMO=1</c> and are skipped otherwise. docs/instruments/T3.md and docs/effects/T3.md quote them.
/// </summary>
public sealed class T3DemoTests {
    const int Beat = 24_000;
    const float Rate = Play.Rate;

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
        var peak = Math.Max(Peak(audio.Left), Peak(audio.Right));
        var scale = peak > 0.98 ? (float)(0.98 / peak) : 1f;
        TestWav.Write(
            Path.Combine(folder, name + ".wav"),
            (int)Rate,
            [audio.Left.Select(s => s * scale).ToArray(), audio.Right.Select(s => s * scale).ToArray()]
        );
    }

    static List<TimedEvent> Chords(int[][] chords, int beats) {
        var events = new List<TimedEvent>();
        for (var c = 0; c < chords.Length; c++) {
            foreach (var note in chords[c]) {
                events.Add(TimedEvent.On(c * beats * Beat + note % 3 * 300, note, 90));
                events.Add(TimedEvent.Off((c + 1) * beats * Beat - 400, note));
            }
        }

        return events;
    }

    static List<TimedEvent> Melody(int[] notes, int beats = 1) {
        var events = new List<TimedEvent>();
        for (var n = 0; n < notes.Length; n++) {
            events.Add(TimedEvent.On(n * beats * Beat, notes[n], 70 + 9 * (n % 5)));
            events.Add(TimedEvent.Off((n + 1) * beats * Beat - 800, notes[n]));
        }

        return events;
    }

    [Fact]
    public void Render_t3_demos() {
        var folder = Output();
        int[][] progression = [[48, 55, 64, 67], [45, 52, 60, 64], [41, 48, 57, 60], [43, 50, 59, 62]];
        int[] tune = [60, 62, 64, 67, 69, 67, 64, 62, 60, 55, 57, 60];
        (string Name, Func<IInstrument> Create, List<TimedEvent> Events, int Frames)[] demos = [
            ("t3-physical-guitar", static () => T3Kit.Instrument(PhysicalModel.Id), Melody(tune), 13 * Beat),
            ("t3-physical-bowed",
                static () => T3Kit.Instrument(
                    PhysicalModel.Id,
                    (P.ExciterType(1), (int)PhysicalExciter.Bow),
                    (P.Body, (int)PhysicalBody.Violin),
                    (P.VibratoDepth, 12)
                ), Melody(tune, 2), 25 * Beat),
            ("t3-physical-flute",
                static () => T3Kit.Instrument(
                    PhysicalModel.Id,
                    (P.Object, (int)PhysicalObject.Tube),
                    (P.ExciterType(1), (int)PhysicalExciter.Blow),
                    (P.ExciterType(1) + 1, 25),
                    (P.Body, 0),
                    (P.VibratoDepth, 8)
                ), Melody([..tune.Select(static n => n + 12)]), 13 * Beat),
            ("t3-physical-marimba",
                static () => T3Kit.Instrument(
                    PhysicalModel.Id,
                    (P.Object, (int)PhysicalObject.Bar),
                    (P.ExciterType(1), (int)PhysicalExciter.Strike),
                    (P.Damping, 45),
                    (P.Body, (int)PhysicalBody.Box)
                ), Melody(tune), 13 * Beat),
            ("t3-physical-drum",
                static () => T3Kit.Instrument(
                    PhysicalModel.Id,
                    (P.Object, (int)PhysicalObject.Membrane),
                    (P.ExciterType(1), (int)PhysicalExciter.Strike),
                    (P.Damping, 55),
                    (P.Body, 0)
                ), Melody([36, 36, 43, 36, 40, 36, 43, 48]), 9 * Beat),
            ("t3-tape-default", static () => T3Kit.Instrument(TapeReplay.Id), Chords(progression, 4), 17 * Beat),
            ("t3-alchemy-additive", static () => T3Kit.Instrument(SampleAlchemy.Id), Chords(progression, 4), 17 * Beat),
            ("t3-alchemy-granular",
                static () => T3Kit.Instrument(
                    SampleAlchemy.Id,
                    (A.Mode, (int)AlchemyMode.Granular),
                    (A.GrainPitch, 20),
                    (A.GrainSpray, 80),
                    (A.Speed, 30)
                ), Chords(progression, 4), 17 * Beat),
            ("t3-alchemy-spectral",
                static () => T3Kit.Instrument(SampleAlchemy.Id, (A.Mode, (int)AlchemyMode.Spectral), (A.Noise, 35)),
                Chords(progression, 4), 17 * Beat)
        ];
        foreach (var (name, create, events, frames) in demos) {
            using var instrument = create();
            Save(folder, name, Play.Render(instrument, frames, events));
        }

        var (left, right) = Fx.Music(4 * 48_000);
        foreach (var model in new[] { "tube", "tape", "transformer", "modern" }) {
            using var effect = T3Kit.Effect(
                Saturation.Id,
                (Saturation.Model, Array.IndexOf(["tube", "tape", "transformer", "modern"], model)),
                (Saturation.Drive, 70)
            );
            Save(folder, "t3-saturation-" + model, Fx.Run(effect, left, right));
        }

        foreach (var kind in Enum.GetValues<Masters.Kind>()) {
            var (l, r) = Masters.Make(kind);
            Save(folder, $"t3-master-{kind}-input", (l, r));
            using var assistant =
                (MasteringAssistant)T3Kit.Effect(MasteringAssistant.Id, (MasteringAssistant.Character, 1));
            assistant.SetChain(
                MasteringChainDesigner.Design(MasteringAnalyzer.Analyse(l, r, Rate), MasteringCharacterKind.Valve)
            );
            Save(folder, $"t3-master-{kind}-valve", Fx.Run(assistant, l, r));
        }
    }

    [Fact]
    public void Measure_t3() {
        var folder = Output();
        var report = new StringBuilder();
        var invariant = CultureInfo.InvariantCulture;
        int[] notes = [36, 48, 60, 72, 84];
        report.AppendLine("Pitch error (cents) by note:");
        report.AppendLine();
        report.AppendLine(invariant, $"| Instrument | {string.Join(" | ", notes)} |");
        report.AppendLine(invariant, $"|---|{string.Concat(notes.Select(static _ => "---:|"))}");
        (string Name, Func<IInstrument> Create, bool Averaged)[] pitched = [
            ("Physical model: string, pick", static () => T3Kit.Instrument(PhysicalModel.Id), false),
            ("Physical model: string, bow",
                static () => T3Kit.Instrument(PhysicalModel.Id, (P.ExciterType(1), (int)PhysicalExciter.Bow)), false),
            ("Physical model: bar, strike",
                static () => T3Kit.Instrument(
                    PhysicalModel.Id,
                    (P.Object, 1),
                    (P.ExciterType(1), (int)PhysicalExciter.Strike)
                ),
                false),
            ("Physical model: membrane, strike",
                static () => T3Kit.Instrument(
                    PhysicalModel.Id,
                    (P.Object, 2),
                    (P.ExciterType(1), (int)PhysicalExciter.Strike)
                ),
                false),
            ("Physical model: tube, blow",
                static () => T3Kit.Instrument(
                    PhysicalModel.Id,
                    (P.Object, 3),
                    (P.ExciterType(1), (int)PhysicalExciter.Blow)
                ),
                false),
            ("Tape replay (default set, no wow or flutter)",
                static () => T3Kit.Instrument(TapeReplay.Id, (T.Wow, 0), (T.Flutter, 0)), false),
            ("Sample Alchemy: additive", static () => T3Kit.Instrument(SampleAlchemy.Id), true),
            ("Sample Alchemy: granular", static () => T3Kit.Instrument(SampleAlchemy.Id, (A.Mode, 1)), true),
            ("Sample Alchemy: spectral", static () => T3Kit.Instrument(SampleAlchemy.Id, (A.Mode, 2)), true)
        ];
        foreach (var (name, create, averaged) in pitched) {
            var cells = notes.Select(note => {
                    using var instrument = create();
                    var (l, _) = Play.Render(instrument, 80_000, [TimedEvent.On(0, note)]);
                    var hz = averaged
                        ? T3Kit.AveragedPeakFrequency(l, Rate, NoteFrequency(note), 12_000, 64_000)
                        : T3Kit.PartialFrequency(l, Rate, NoteFrequency(note), 12_000);
                    return Cents(hz, NoteFrequency(note)).ToString("0.000", invariant);
                }
            );
            report.AppendLine(invariant, $"| {name} | {string.Join(" | ", cells)} |");
        }

        report.AppendLine();
        report.AppendLine("Physical model −60 dB decay (s), struck string:");
        report.AppendLine();
        report.AppendLine("| Note | Damping 60 % | 40 % | 20 % | Ratio 40/60 |");
        report.AppendLine("|---:|---:|---:|---:|---:|");
        foreach (var note in new[] { 36, 60, 84 }) {
            var decays = new[] { 60f, 40f, 20f }.Select(damping => {
                        using var instrument = T3Kit.Instrument(
                            PhysicalModel.Id,
                            (P.Damping, damping),
                            (P.ExciterType(1), (int)PhysicalExciter.Strike)
                        );
                        return T3Kit.DecaySeconds(
                            Play.Render(instrument, 20 * 48_000, [TimedEvent.On(0, note)]).Left,
                            Rate
                        );
                    }
                )
                .ToArray();
            report.AppendLine(
                invariant,
                $"| {note} | {decays[0]:0.00} | {decays[1]:0.00} | {decays[2]:0.00} | {decays[1] / decays[0]:0.00} |"
            );
        }

        report.AppendLine();
        report.AppendLine("Saturation THD (harmonics 2 … 10, dB) of a 1 kHz sine at −12 dBFS:");
        report.AppendLine();
        report.AppendLine("| Model | Drive 10 % | 40 % | 80 % | 100 % |");
        report.AppendLine("|---|---:|---:|---:|---:|");
        var tone = Enumerable.Range(0, 48_000)
            .Select(static i => (float)(0.25 * Math.Sin(2 * Math.PI * 1_000 * i / 48_000)))
            .ToArray();
        foreach (var (name, model) in new[] { ("Tube", 0), ("Tape", 1), ("Transformer", 2), ("Modern", 3) }) {
            var cells = new[] { 10f, 40f, 80f, 100f }.Select(drive => {
                    using var effect =
                        T3Kit.Effect(Saturation.Id, (Saturation.Model, model), (Saturation.Drive, drive));
                    return Thd(Fx.Run(effect, tone).Left).ToString("0.0", invariant);
                }
            );
            report.AppendLine(invariant, $"| {name} | {string.Join(" | ", cells)} |");
        }

        report.AppendLine();
        report.AppendLine("Mastering Assistant (12 s of material; output loudness LUFS / true peak dBTP, 4× sinc):");
        report.AppendLine();
        report.AppendLine("| Material | Input | Clean | Valve | Punch | Transparent |");
        report.AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (var kind in Enum.GetValues<Masters.Kind>()) {
            var (l, r) = Masters.Make(kind);
            var measured = MasteringAnalyzer.Analyse(l, r, Rate);
            var cells = Enum.GetValues<MasteringCharacterKind>()
                .Select(character => {
                        using var assistant = (MasteringAssistant)T3Kit.Effect(
                            MasteringAssistant.Id,
                            (MasteringAssistant.Character, (int)character)
                        );
                        assistant.SetChain(MasteringChainDesigner.Design(measured, character));
                        var (ol, or) = Fx.Run(assistant, l, r);
                        return string.Create(
                            invariant,
                            $"{Masters.Integrated(ol, or):0.00} / {ToDb(Math.Max(Fx.TruePeak(ol), Fx.TruePeak(or))):0.00}"
                        );
                    }
                );
            report.AppendLine(
                invariant,
                $"| {kind} | {measured.IntegratedLufs:0.0} LUFS, {measured.TruePeakDbtp:0.0} dBTP | {string.Join(" | ", cells)} |"
            );
        }

        File.WriteAllText(Path.Combine(folder, "t3-measured.md"), report.ToString());
        TestContext.Current.TestOutputHelper?.WriteLine(report.ToString());
    }

    static double Thd(float[] signal) {
        const int size = 16_384;
        var spectrum = Spectrum(signal, signal.Length - size, size);
        return 10 * Math.Log10(Enumerable.Range(2, 9).Sum(Band) / Band(1));

        double Band(int k) {
            var centre = (int)Math.Round(1_000.0 * k * size / 48_000);
            return Enumerable.Range(centre - 4, 9).Sum(b => spectrum[b].Magnitude * spectrum[b].Magnitude);
        }
    }

    [Fact]
    public void Measure_t3_cpu() {
        var folder = Output();
        var period = 64 / 48_000.0 * 1e6;
        var invariant = CultureInfo.InvariantCulture;
        var report = new StringBuilder();
        report.AppendLine(invariant, $"Anode.Dsp T3, 48 kHz, 64-frame blocks (period {period:0.0} µs), one core");
        report.AppendLine(
            invariant,
            $"{Environment.OSVersion}, {Environment.ProcessorCount} cores, .NET {Environment.Version}"
        );
        report.AppendLine();
        report.AppendLine("| Instrument | Voices | Median µs | Median % | p99 µs | p99 % | Per voice µs |");
        report.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        (string Name, Func<IInstrument> Create, int Voices)[] instruments = [
            ("Physical model (string, pick)", static () => T3Kit.Instrument(PhysicalModel.Id, (P.Damping, 0)), 16),
            ("Physical model (string, pick)", static () => T3Kit.Instrument(PhysicalModel.Id, (P.Damping, 0)), 1),
            ("Physical model (string, bow)",
                static () => T3Kit.Instrument(PhysicalModel.Id, (P.ExciterType(1), (int)PhysicalExciter.Bow)), 16),
            ("Physical model (string, bow)",
                static () => T3Kit.Instrument(PhysicalModel.Id, (P.ExciterType(1), (int)PhysicalExciter.Bow)), 1),
            ("Tape replay", static () => T3Kit.Instrument(TapeReplay.Id), 35),
            ("Tape replay", static () => T3Kit.Instrument(TapeReplay.Id), 1),
            ("Sample Alchemy (additive, 32 partials)", static () => T3Kit.Instrument(SampleAlchemy.Id), 16),
            ("Sample Alchemy (additive, 32 partials)", static () => T3Kit.Instrument(SampleAlchemy.Id), 1),
            ("Sample Alchemy (granular)", static () => T3Kit.Instrument(SampleAlchemy.Id, (A.Mode, 1)), 16),
            ("Sample Alchemy (spectral)", static () => T3Kit.Instrument(SampleAlchemy.Id, (A.Mode, 2)), 16)
        ];
        foreach (var (name, create, voices) in instruments) {
            using var instrument = create();
            var (median, p99) = Time(instrument, voices);
            report.AppendLine(
                invariant,
                $"| {name} | {voices} | {median:0.0} | {100 * median / period:0.00} | {p99:0.0} | {100 * p99 / period:0.00} | {median / voices:0.00} |"
            );
        }

        report.AppendLine();
        report.AppendLine("| Effect | Median µs | Median % | p99 µs | p99 % |");
        report.AppendLine("|---|---:|---:|---:|---:|");
        var (left, right) = Fx.Music();
        (string Name, Func<IAudioEffect> Create)[] effects = [
            ("Saturation (tube)", static () => T3Kit.Effect(Saturation.Id)),
            ("Saturation (tape)", static () => T3Kit.Effect(Saturation.Id, (Saturation.Model, 1))),
            ("Saturation (transformer)", static () => T3Kit.Effect(Saturation.Id, (Saturation.Model, 2))),
            ("Saturation (modern)", static () => T3Kit.Effect(Saturation.Id, (Saturation.Model, 3))),
            ("Mastering Assistant (limiter only)", static () => T3Kit.Effect(MasteringAssistant.Id)),
            ("Mastering Assistant (Valve chain)", () => {
                var assistant = (MasteringAssistant)T3Kit.Effect(
                    MasteringAssistant.Id,
                    (MasteringAssistant.Character, 1)
                );
                assistant.SetChain(
                    MasteringChainDesigner.Design(
                        MasteringAnalyzer.Analyse(left, right, Rate),
                        MasteringCharacterKind.Valve
                    )
                );
                return assistant;
            })
        ];
        foreach (var (name, create) in effects) {
            using var effect = create();
            var (median, p99) = Time(effect, left, right);
            report.AppendLine(
                invariant,
                $"| {name} | {median:0.0} | {100 * median / period:0.00} | {p99:0.0} | {100 * p99 / period:0.00} |"
            );
        }

        var hrtfs = HrtfSet.Load(HrtfSet.Find(Ear.RepositoryRoot())!);
        foreach (var (name, create, from) in new (string, Func<IFormatEffect>, SpeakerFormat)[] {
                     ("Down Mixer (5.1 → 2.0)", static () => new DownMixerInsert(), SpeakerFormat.Surround51),
                     ("Down Mixer (7.1.4 → 2.0)", static () => new DownMixerInsert(), SpeakerFormat.Surround714),
                     ("Binaural (7.1.4 → binaural)", () => {
                         var b = new BinauralInsert();
                         b.UseHrtfs(hrtfs);
                         return b;
                     }, SpeakerFormat.Surround714)
                 }) {
            using var effect = create();
            var (median, p99) = Time(effect, from);
            report.AppendLine(
                invariant,
                $"| {name} | {median:0.0} | {100 * median / period:0.00} | {p99:0.0} | {100 * p99 / period:0.00} |"
            );
        }

        File.WriteAllText(Path.Combine(folder, "t3-cpu.md"), report.ToString());
        TestContext.Current.TestOutputHelper?.WriteLine(report.ToString());
    }

    /// <summary>Holds <paramref name="voices" /> notes (struck again every 500 blocks) and times 64-frame blocks.</summary>
    static (double Median, double P99) Time(IInstrument instrument, int voices) {
        var left = new float[64];
        var right = new float[64];
        var notes = Enumerable.Range(0, voices)
            .Select(n => new MidiEvent(0, 0x90, (byte)(43 + n * 34 / Math.Max(voices, 1)), 100))
            .ToArray();
        instrument.Process(notes, left, right);
        for (var i = 0; i < 500; i++) {
            instrument.Process([], left, right);
        }

        var times = new double[20_000];
        for (var i = 0; i < times.Length; i++) {
            if (i % 500 == 0) {
                instrument.Process(notes, left, right);
                continue;
            }

            var start = Stopwatch.GetTimestamp();
            instrument.Process([], left, right);
            times[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
        }

        var measured = times.Where(static (_, i) => i % 500 != 0).Order().ToArray();
        return (measured[measured.Length / 2], measured[(int)(measured.Length * 0.99)]);
    }

    static (double Median, double P99) Time(IAudioEffect effect, float[] music, float[] musicRight) {
        var left = new float[64];
        var right = new float[64];
        var times = new double[20_000];
        for (var i = -500; i < times.Length; i++) {
            var at = Math.Max(0, i) * 64 % (music.Length - 64);
            music.AsSpan(at, 64).CopyTo(left);
            musicRight.AsSpan(at, 64).CopyTo(right);
            var start = Stopwatch.GetTimestamp();
            effect.Process(left, right, default, default);
            if (i >= 0) {
                times[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
            }
        }

        Array.Sort(times);
        return (times[times.Length / 2], times[(int)(times.Length * 0.99)]);
    }

    static (double Median, double P99) Time(IFormatEffect effect, SpeakerFormat from) {
        var layout = SpeakerLayout.Of(from);
        effect.Prepare(layout, SpeakerLayout.Of(effect.OutputFormat(from)), 48_000, 64);
        var input = new float[layout.ChannelCount * 64];
        var random = 1u;
        for (var i = 0; i < input.Length; i++) {
            random = random * 1_664_525 + 1_013_904_223;
            input[i] = (random >> 8) / 16_777_216f - 0.5f;
        }

        var output = new float[2 * 64];
        var times = new double[20_000];
        for (var i = -500; i < times.Length; i++) {
            var start = Stopwatch.GetTimestamp();
            effect.Process(new(input, layout.ChannelCount, 64), new(output, 2, 64));
            if (i >= 0) {
                times[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
            }
        }

        Array.Sort(times);
        return (times[times.Length / 2], times[(int)(times.Length * 0.99)]);
    }
}
