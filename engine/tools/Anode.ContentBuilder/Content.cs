using Anode.Contracts;
using Anode.Dsp.Instruments;

namespace Anode.ContentBuilder;

/// <summary>
///     What the packs hold: drum kits, drum loops at several tempi as audio, one-shots, bass and chord MIDI loops, a synth
///     audio loop in a key and pattern loops, rendered with <paramref name="t1" /> (the T1 synth for drums) or the Drum
///     Synth.
/// </summary>
public sealed class Content(bool t1) {
    public const string DrumSynthId = "anode.drum-synth";
    const string SynthId = "anode.synth";
    const int Kick = 36;
    const int Snare = 38;
    const int Hat = 42;

    /// <summary>The e2e test pack: a 100 bpm drum loop in C (audio), a bass MIDI loop in C, a pattern loop, a kit.</summary>
    public PackBuilder TestPack(string staging) {
        var pack = new PackBuilder(
            new("test-pack", "Test Pack", "1.0.0", "Anode", "A small pack for Anode's end-to-end tests."),
            staging
        );
        DrumLoop(pack, "Drums", "Test Drums 100", 100, Beat.Basic, 0, ["Drums"]);
        pack.MidiLoop("Bass", "Test Bass C", BassLine(0, [0, 0, 7, 5]), 100, 8, 0, LoopScale.Minor, ["Bass"]);
        pack.PatternLoop("Patterns", "Test Beat", DrumPattern(Beat.Basic), 100, 4, ["Drums"]);
        pack.Kit("kit_test", "Test Kit", KitPads());
        return pack;
    }

    /// <summary>Anode Essentials: Anode's own content pack.</summary>
    public PackBuilder Essentials(string staging) {
        var pack = new PackBuilder(
            new(
                "anode-essentials",
                "Anode Essentials",
                "1.0.0",
                "Anode",
                "Drum kits, drum loops, bass and chord loops and patterns made with Anode's own instruments."
            ),
            staging
        );
        foreach (var tempo in (int[])[90, 100, 120]) {
            DrumLoop(pack, "Drums", $"Basic Beat {tempo}", tempo, Beat.Basic, null, ["Drums", "Acoustic"]);
            DrumLoop(
                pack,
                "Drums",
                $"Four on the Floor {tempo}",
                tempo,
                Beat.FourOnTheFloor,
                null,
                ["Drums", "Electronic"]
            );
            DrumLoop(pack, "Drums", $"Broken Beat {tempo}", tempo, Beat.Broken, null, ["Drums", "Electronic"]);
        }

        foreach (var (name, note) in (ReadOnlySpan<(string, int)>)[("Kick", Kick), ("Snare", Snare), ("Hat", Hat)]) {
            var (l, r) = Renderer.Render(DrumSound(note), [new(0, 2_400, note, 110)], PackBuilder.SampleRate / 2);
            pack.AudioLoop(
                "One-shots",
                $"{name} One-shot",
                l,
                r,
                null,
                null,
                null,
                LoopScale.Neither,
                ["Drums", "One-shot"]
            );
        }

        pack.MidiLoop("Bass", "Root Bass C", BassLine(0, [0, 0, 7, 5]), 110, 8, 0, LoopScale.Minor, ["Bass"]);
        pack.MidiLoop("Bass", "Walking Bass A", BassLine(9, [0, 3, 5, 7]), 110, 8, 9, LoopScale.Minor, ["Bass"]);
        pack.MidiLoop("Bass", "Octave Bass F", BassLine(5, [0, 12, 0, 12]), 120, 8, 5, LoopScale.Major, ["Bass"]);
        pack.MidiLoop(
            "Keys",
            "Pop Chords C",
            Chords(0, [[0, 4, 7], [7, 11, 14], [9, 12, 16], [5, 9, 12]]),
            100,
            16,
            0,
            LoopScale.Major,
            ["Keys", "Synth"]
        );
        pack.MidiLoop(
            "Keys",
            "Minor Chords A",
            Chords(9, [[0, 3, 7], [5, 8, 12], [3, 7, 10], [7, 10, 14]]),
            90,
            16,
            9,
            LoopScale.Minor,
            ["Keys", "Synth"]
        );
        SynthLoop(pack, "Synth", "Pluck Arp C 120", 120, 0);
        pack.PatternLoop(
            "Patterns",
            "Four on the Floor",
            DrumPattern(Beat.FourOnTheFloor),
            120,
            4,
            ["Drums", "Electronic"]
        );
        pack.PatternLoop("Patterns", "Basic Beat", DrumPattern(Beat.Basic), 100, 4, ["Drums", "Acoustic"]);
        pack.Kit("kit_essentials", "Essentials Kit", KitPads());
        return pack;
    }

    enum Beat {
        Basic,
        FourOnTheFloor,
        Broken
    }

    /// <summary>A beat's hits in 16ths of one bar: (step, note, velocity).</summary>
    static List<(int Step, int Note, int Velocity)> Hits(Beat beat) {
        var hits = new List<(int, int, int)>();
        for (var s = 0; s < 16; s += 2) {
            hits.Add((s, Hat, s % 4 == 0 ? 100 : 70));
        }

        switch (beat) {
            case Beat.FourOnTheFloor:
                hits.AddRange(
                    [(0, Kick, 120), (4, Kick, 120), (8, Kick, 120), (12, Kick, 120), (4, Snare, 100), (12, Snare, 100)]
                );
                break;
            case Beat.Broken:
                hits.AddRange(
                    [
                        (0, Kick, 120), (6, Kick, 100), (10, Kick, 110), (4, Snare, 105), (12, Snare, 105),
                        (15, Snare, 60)
                    ]
                );
                break;
            default:
                hits.AddRange([(0, Kick, 120), (8, Kick, 120), (10, Kick, 90), (4, Snare, 105), (12, Snare, 105)]);
                break;
        }

        return hits;
    }

    /// <summary>A two-bar drum loop rendered as audio, with its onsets as transients.</summary>
    void DrumLoop(PackBuilder pack, string folder, string name, double tempo, Beat beat, int? key, string[] tags) {
        const int bars = 2;
        var sixteenth = 60.0 / tempo / 4 * PackBuilder.SampleRate;
        var frames = (long)Math.Round(sixteenth * 16 * bars);
        var hits = Hits(beat);
        var parts = new List<(float[], float[])>();
        var onsets = new SortedSet<long>();
        foreach (var note in (int[])[Kick, Snare, Hat]) {
            var notes = new List<RenderNote>();
            for (var bar = 0; bar < bars; bar++) {
                foreach (var (step, n, velocity) in hits.Where(h => h.Note == note)) {
                    var start = (long)Math.Round((bar * 16 + step) * sixteenth);
                    notes.Add(new(start, (long)(sixteenth * 0.9), note, velocity));
                    onsets.Add(start);
                }
            }

            parts.Add(Renderer.Render(DrumSound(note), notes, frames, note == Hat ? 0.35f : 0.8f));
        }

        var (left, right) = Renderer.Mix([..parts]);
        pack.AudioLoop(folder, name, left, right, tempo, bars * 4, key, LoopScale.Neither, tags, [..onsets]);
    }

    /// <summary>A one-bar synth arpeggio rendered as audio, in <paramref name="tonic" /> major.</summary>
    static void SynthLoop(PackBuilder pack, string folder, string name, double tempo, int tonic) {
        var sixteenth = 60.0 / tempo / 4 * PackBuilder.SampleRate;
        var frames = (long)Math.Round(sixteenth * 32);
        int[] steps = [0, 4, 7, 12, 7, 4];
        var notes = Enumerable.Range(0, 32)
            .Select(i => new RenderNote(
                    (long)Math.Round(i * sixteenth),
                    (long)(sixteenth * 0.7),
                    60 + tonic + steps[i % steps.Length],
                    96
                )
            )
            .ToArray();
        var (left, right) = Renderer.Render(
            new(
                SynthId,
                new Dictionary<string, float> {
                    ["osc1Wave"] = 1,
                    ["cutoff"] = 2_500,
                    ["ampDecay"] = 220,
                    ["ampSustain"] = 20,
                    ["ampRelease"] = 120
                }
            ),
            notes,
            frames,
            0.5f
        );
        pack.AudioLoop(folder, name, left, right, tempo, 8, tonic, LoopScale.Major, ["Synth"]);
    }

    /// <summary>A two-bar bass line in 8ths on the given scale steps (one per half bar), from the octave below C2.</summary>
    static Note[] BassLine(int tonic, int[] steps) {
        var notes = new List<Note>();
        for (var i = 0; i < 16; i++) {
            var pitch = 36 + tonic + steps[i / 4 % steps.Length];
            notes.Add(new(i + 1, pitch, i % 4 == 0 ? 110 : 85, 1, i * 480L, 400));
        }

        return [..notes];
    }

    /// <summary>One chord a bar, as triads over <paramref name="tonic" /> in the octave from C3.</summary>
    static Note[] Chords(int tonic, int[][] chords) {
        var notes = new List<Note>();
        var id = 0;
        for (var bar = 0; bar < chords.Length; bar++) {
            foreach (var interval in chords[bar]) {
                notes.Add(new(++id, 60 + tonic + interval, 90, 1, bar * 3840L, 3600));
            }
        }

        return [..notes];
    }

    /// <summary>A one-bar Step Sequencer pattern of a beat: kick, snare and hat pad rows of 16 steps.</summary>
    static Pattern DrumPattern(Beat beat) {
        var hits = Hits(beat);
        return new([Row("row_kick", Kick, "Kick"), Row("row_snare", Snare, "Snare"), Row("row_hat", Hat, "Hat")], 0, 1);

        PatternRow Row(string id, int note, string name) =>
            new(
                id,
                PatternRowKind.Pad,
                note,
                name,
                16,
                null,
                PatternPlayMode.Forward,
                false,
                [
                    ..Enumerable.Range(0, 16)
                        .Select(s => hits.Find(h => h.Note == note && h.Step == s) is var (_, _, v) && v > 0
                                ? new PatternStep(true, v, 1, 1, 1, 0)
                                : new PatternStep(false, 100, 1, 1, 1, 0)
                        )
                ]
            );
    }

    /// <summary>A kit's pads: kick, snare and hat (the Drum Synth's models, or the T1 synth's drum sounds).</summary>
    IReadOnlyList<(int Note, string Name, InstrumentSlot Instrument)> KitPads() => [
        (Kick, "Kick", Slot(DrumSound(Kick))),
        (Snare, "Snare", Slot(DrumSound(Snare))),
        (Hat, "Hat", Slot(DrumSound(Hat)))
    ];

    static InstrumentSlot Slot(Sound sound) =>
        new(
            sound.PluginId,
            sound.PluginId == SynthId ? "Synth" : "Drum Synth",
            sound.Parameters.ToDictionary(static p => p.Key, static p => (double)p.Value),
            null
        );

    /// <summary>
    ///     A drum sound: the Drum Synth on its model for the drum (a choice parameter naming Kick, Snare or Hat) when it
    ///     is used, else the T1 synth shaped into one.
    /// </summary>
    Sound DrumSound(int note) {
        if (!t1 && BuiltinInstruments.Create(DrumSynthId) is { } drum) {
            drum.Dispose();
            // The Drum Synth's model: 0 Kick, 1 Snare, 3 Hat (a closed hat: a short decay).
            return note switch {
                Kick => new(DrumSynthId, new Dictionary<string, float> { ["model"] = 0 }),
                Snare => new(DrumSynthId, new Dictionary<string, float> { ["model"] = 1 }),
                _ => new(DrumSynthId, new Dictionary<string, float> { ["model"] = 3, ["decay"] = 15 })
            };
        }

        return note switch {
            Kick => new(
                SynthId,
                new Dictionary<string, float> {
                    ["osc1Wave"] = 3,
                    ["osc2Wave"] = 3,
                    ["oscMix"] = 0,
                    ["osc1Octave"] = -1,
                    ["filterMode"] = 0,
                    ["cutoff"] = 300,
                    ["resonance"] = 0,
                    ["keyTracking"] = 0,
                    ["filterEnvAmount"] = 60,
                    ["filterAttack"] = 0,
                    ["filterDecay"] = 60,
                    ["filterSustain"] = 0,
                    ["ampAttack"] = 0,
                    ["ampDecay"] = 220,
                    ["ampSustain"] = 0,
                    ["ampRelease"] = 40,
                    ["lfoPitch"] = 0
                }
            ),
            Snare => new(
                SynthId,
                new Dictionary<string, float> {
                    ["osc1Wave"] = 4,
                    ["osc2Wave"] = 2,
                    ["oscMix"] = 25,
                    ["filterMode"] = 3,
                    ["cutoff"] = 1_800,
                    ["resonance"] = 10,
                    ["keyTracking"] = 0,
                    ["filterEnvAmount"] = 0,
                    ["ampAttack"] = 0,
                    ["ampDecay"] = 160,
                    ["ampSustain"] = 0,
                    ["ampRelease"] = 60,
                    ["lfoPitch"] = 0
                }
            ),
            _ => new(
                SynthId,
                new Dictionary<string, float> {
                    ["osc1Wave"] = 4,
                    ["osc2Wave"] = 4,
                    ["oscMix"] = 0,
                    ["filterMode"] = 2,
                    ["cutoff"] = 9_000,
                    ["resonance"] = 0,
                    ["keyTracking"] = 0,
                    ["filterEnvAmount"] = 0,
                    ["ampAttack"] = 0,
                    ["ampDecay"] = 45,
                    ["ampSustain"] = 0,
                    ["ampRelease"] = 20,
                    ["lfoPitch"] = 0
                }
            )
        };
    }
}
