using Anode.Contracts;

namespace Anode.ContentBuilder;

/// <summary>
///     The Session Players' content (docs/12-phase-11.md, workstream D): the three Drummer kits (acoustic-style,
///     electronic, brushes), rendered from Anode's own instruments (the Drum Synth, and the physical model once it is
///     in) as multi-output Drum Machine Designer kits with their pads on <c>DrumKitNotes</c>. The styles themselves are
///     data in Anode.Generative, not pack content.
/// </summary>
/// <remarks>
///     A kit is a kit patch (a stack with pads, as <see cref="PackBuilder.Kit" /> writes it): a Drummer track made from
///     it has a member instrument track per pad, so each kit piece plays on its own strip. Every note the Drummer plays
///     has a pad. The drum styles name these kits by id (<c>kitPatchId</c>).
/// </remarks>
static class SessionPlayerContent {
    const string SynthId = "anode.synth";

    // The Drum Synth's models (its choice parameter).
    const int Kick = 0;
    const int Snare = 1;
    const int Clap = 2;
    const int Hat = 3;
    const int Tom = 4;
    const int Cymbal = 5;
    const int Percussion = 6;

    /// <summary>Whether this build makes the kits.</summary>
    public static bool Ready => true;

    /// <summary>
    ///     Adds the Drummer kits to <paramref name="pack" /> (the test pack: one kit), drums from the T1 synth when
    ///     <paramref name="t1" />, as <see cref="Content" /> does.
    /// </summary>
    public static void AddKits(PackBuilder pack, bool test, bool t1) {
        pack.Kit("kit_drummer_acoustic", "Session Acoustic Kit", Pads(Acoustic, t1));
        if (test) {
            return;
        }

        pack.Kit("kit_drummer_electronic", "Session Electronic Kit", Pads(Electronic, t1));
        pack.Kit("kit_drummer_brushes", "Session Brushes Kit", Pads(Brushes, t1));
    }

    /// <summary>A pad: its note, name, the Drum Synth's model and the settings that differ from the model's defaults.</summary>
    sealed record Pad(int Note, string Name, int Model, (string Id, float Value)[] Settings);

    /// <summary>An acoustic-style kit: a round kick, a crisp snare, tuned toms, washy cymbals.</summary>
    static readonly Pad[] Acoustic = [
        new(
            DrumKitNotes.Kick,
            "Kick",
            Kick,
            [("tune", -2), ("decay", 45), ("tone", 40), ("snap", 35), ("sweep", 45), ("drive", 10)]
        ),
        new(DrumKitNotes.SideStick, "Side Stick", Percussion, [("tune", 12), ("decay", 5), ("tone", 20), ("pan", 10)]),
        new(DrumKitNotes.Snare, "Snare", Snare, [("decay", 45), ("tone", 60), ("snap", 65), ("pan", 5)]),
        new(DrumKitNotes.Clap, "Clap", Clap, [("decay", 40), ("pan", 5)]),
        new(DrumKitNotes.FloorTom, "Floor Tom", Tom, [("tune", -7), ("decay", 60), ("tone", 40), ("pan", -30)]),
        new(DrumKitNotes.ClosedHiHat, "Closed Hi-Hat", Hat, [("decay", 12), ("tone", 70), ("pan", 25), ("volume", -9)]),
        new(DrumKitNotes.PedalHiHat, "Pedal Hi-Hat", Hat, [("decay", 8), ("tone", 55), ("pan", 25), ("volume", -12)]),
        new(DrumKitNotes.LowTom, "Low Tom", Tom, [("tune", -4), ("decay", 55), ("tone", 45), ("pan", -15)]),
        new(DrumKitNotes.OpenHiHat, "Open Hi-Hat", Hat, [("decay", 60), ("tone", 70), ("pan", 25), ("volume", -9)]),
        new(DrumKitNotes.MidTom, "Mid Tom", Tom, [("decay", 50), ("tone", 50), ("pan", 5)]),
        new(DrumKitNotes.Crash, "Crash", Cymbal, [("decay", 80), ("tone", 65), ("pan", -35), ("volume", -10)]),
        new(DrumKitNotes.HighTom, "High Tom", Tom, [("tune", 4), ("decay", 45), ("tone", 55), ("pan", 20)]),
        new(
            DrumKitNotes.Ride,
            "Ride",
            Cymbal,
            [("decay", 55), ("tone", 40), ("snap", 20), ("pan", 35), ("volume", -11)]
        ),
        new(
            DrumKitNotes.RideBell,
            "Ride Bell",
            Cymbal,
            [("tune", 7), ("decay", 45), ("tone", 80), ("snap", 10), ("pan", 35), ("volume", -11)]
        ),
        new(
            DrumKitNotes.Tambourine,
            "Tambourine",
            Hat,
            [("tune", 5), ("decay", 25), ("snap", 80), ("pan", -20), ("volume", -12)]
        ),
        new(DrumKitNotes.Cowbell, "Cowbell", Percussion, [("tone", 100), ("decay", 30), ("pan", -10), ("volume", -12)]),
        new(
            DrumKitNotes.Shaker,
            "Shaker",
            Hat,
            [("decay", 10), ("tone", 90), ("snap", 90), ("pan", 30), ("volume", -15)]
        )
    ];

    /// <summary>An electronic kit: a long swept kick, a snappy snare, short metallic hats, zapped toms.</summary>
    static readonly Pad[] Electronic = [
        new(DrumKitNotes.Kick, "Kick", Kick, [("decay", 70), ("tone", 30), ("snap", 20), ("sweep", 70), ("drive", 25)]),
        new(DrumKitNotes.SideStick, "Rim", Percussion, [("tune", 14), ("decay", 3), ("tone", 60)]),
        new(DrumKitNotes.Snare, "Snare", Snare, [("tune", 2), ("decay", 40), ("tone", 70), ("snap", 80)]),
        new(DrumKitNotes.Clap, "Clap", Clap, [("decay", 55), ("tone", 55)]),
        new(DrumKitNotes.FloorTom, "Floor Tom", Tom, [("tune", -9), ("decay", 55), ("tone", 20), ("pan", -30)]),
        new(DrumKitNotes.ClosedHiHat, "Closed Hi-Hat", Hat, [("decay", 6), ("tone", 80), ("pan", 15), ("volume", -10)]),
        new(DrumKitNotes.PedalHiHat, "Pedal Hi-Hat", Hat, [("decay", 4), ("tone", 70), ("pan", 15), ("volume", -13)]),
        new(DrumKitNotes.LowTom, "Low Tom", Tom, [("tune", -5), ("decay", 50), ("tone", 20), ("pan", -15)]),
        new(DrumKitNotes.OpenHiHat, "Open Hi-Hat", Hat, [("decay", 45), ("tone", 80), ("pan", 15), ("volume", -10)]),
        new(DrumKitNotes.MidTom, "Mid Tom", Tom, [("tune", -1), ("decay", 45), ("tone", 20)]),
        new(DrumKitNotes.Crash, "Crash", Cymbal, [("decay", 70), ("tone", 75), ("pan", -25), ("volume", -11)]),
        new(DrumKitNotes.HighTom, "High Tom", Tom, [("tune", 3), ("decay", 40), ("tone", 20), ("pan", 20)]),
        new(
            DrumKitNotes.Ride,
            "Ride",
            Cymbal,
            [("decay", 40), ("tone", 60), ("snap", 30), ("pan", 25), ("volume", -12)]
        ),
        new(
            DrumKitNotes.RideBell,
            "Ride Bell",
            Cymbal,
            [("tune", 9), ("decay", 35), ("tone", 90), ("pan", 25), ("volume", -12)]
        ),
        new(
            DrumKitNotes.Tambourine,
            "Tambourine",
            Hat,
            [("tune", 7), ("decay", 20), ("snap", 85), ("pan", -20), ("volume", -13)]
        ),
        new(DrumKitNotes.Cowbell, "Cowbell", Percussion, [("tone", 100), ("decay", 40), ("pan", -10), ("volume", -11)]),
        new(
            DrumKitNotes.Shaker,
            "Shaker",
            Hat,
            [("decay", 5), ("tone", 95), ("snap", 95), ("pan", 30), ("volume", -15)]
        )
    ];

    /// <summary>A brushes kit: a soft kick, a swept snare (all noise, long), dark cymbals, felted toms.</summary>
    static readonly Pad[] Brushes = [
        new(
            DrumKitNotes.Kick,
            "Kick",
            Kick,
            [("tune", -4), ("decay", 35), ("tone", 30), ("snap", 10), ("sweep", 25), ("volume", -9)]
        ),
        new(
            DrumKitNotes.SideStick,
            "Side Stick",
            Percussion,
            [("tune", 10), ("decay", 6), ("tone", 15), ("volume", -9)]
        ),
        new(
            DrumKitNotes.Snare,
            "Brush Snare",
            Snare,
            [("decay", 70), ("tone", 30), ("snap", 95), ("velocitySensitivity", 90), ("volume", -8)]
        ),
        new(DrumKitNotes.Clap, "Brush Slap", Clap, [("decay", 30), ("tone", 30), ("volume", -12)]),
        new(
            DrumKitNotes.FloorTom,
            "Floor Tom",
            Tom,
            [("tune", -7), ("decay", 45), ("tone", 25), ("pan", -30), ("volume", -9)]
        ),
        new(
            DrumKitNotes.ClosedHiHat,
            "Closed Hi-Hat",
            Hat,
            [("decay", 10), ("tone", 45), ("pan", 25), ("volume", -13)]
        ),
        new(DrumKitNotes.PedalHiHat, "Pedal Hi-Hat", Hat, [("decay", 9), ("tone", 40), ("pan", 25), ("volume", -12)]),
        new(
            DrumKitNotes.LowTom,
            "Low Tom",
            Tom,
            [("tune", -4), ("decay", 40), ("tone", 25), ("pan", -15), ("volume", -9)]
        ),
        new(DrumKitNotes.OpenHiHat, "Open Hi-Hat", Hat, [("decay", 50), ("tone", 45), ("pan", 25), ("volume", -13)]),
        new(DrumKitNotes.MidTom, "Mid Tom", Tom, [("decay", 40), ("tone", 30), ("pan", 5), ("volume", -9)]),
        new(DrumKitNotes.Crash, "Crash", Cymbal, [("decay", 75), ("tone", 35), ("pan", -35), ("volume", -13)]),
        new(
            DrumKitNotes.HighTom,
            "High Tom",
            Tom,
            [("tune", 4), ("decay", 35), ("tone", 35), ("pan", 20), ("volume", -9)]
        ),
        new(
            DrumKitNotes.Ride,
            "Ride",
            Cymbal,
            [("decay", 70), ("tone", 30), ("snap", 15), ("pan", 35), ("volume", -12)]
        ),
        new(
            DrumKitNotes.RideBell,
            "Ride Bell",
            Cymbal,
            [("tune", 5), ("decay", 50), ("tone", 60), ("pan", 35), ("volume", -13)]
        ),
        new(
            DrumKitNotes.Tambourine,
            "Tambourine",
            Hat,
            [("tune", 3), ("decay", 22), ("snap", 70), ("pan", -20), ("volume", -15)]
        ),
        new(DrumKitNotes.Cowbell, "Cowbell", Percussion, [("tone", 90), ("decay", 25), ("pan", -10), ("volume", -15)]),
        new(
            DrumKitNotes.Shaker,
            "Shaker",
            Hat,
            [("decay", 12), ("tone", 70), ("snap", 90), ("pan", 30), ("volume", -17)]
        )
    ];

    static List<(int Note, string Name, InstrumentSlot Instrument)> Pads(Pad[] kit, bool t1) =>
        [..kit.Select(p => (p.Note, p.Name, t1 ? Synth(p) : DrumSynth(p)))];

    static InstrumentSlot DrumSynth(Pad pad) {
        var parameters = new Dictionary<string, double> { ["model"] = pad.Model };
        foreach (var (id, value) in pad.Settings) {
            parameters[id] = value;
        }

        return new(Content.DrumSynthId, "Drum Synth", parameters, null);
    }

    /// <summary>Without the Drum Synth: the T1 synth shaped into a kick (kick and toms), a snare (snares and claps) or a hat.</summary>
    static InstrumentSlot Synth(Pad pad) {
        var parameters = pad.Model switch {
            Kick or Tom => new Dictionary<string, double> {
                ["osc1Wave"] = 3,
                ["osc2Wave"] = 3,
                ["oscMix"] = 0,
                ["osc1Octave"] = pad.Model == Kick ? -1 : 0,
                ["filterMode"] = 0,
                ["cutoff"] = pad.Model == Kick ? 300 : 900,
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
            },
            Snare or Clap => new Dictionary<string, double> {
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
            },
            _ => new Dictionary<string, double> {
                ["osc1Wave"] = 4,
                ["osc2Wave"] = 4,
                ["oscMix"] = 0,
                ["filterMode"] = 2,
                ["cutoff"] = 9_000,
                ["resonance"] = 0,
                ["keyTracking"] = 0,
                ["filterEnvAmount"] = 0,
                ["ampAttack"] = 0,
                ["ampDecay"] = pad.Model == Cymbal ? 400 : 45,
                ["ampSustain"] = 0,
                ["ampRelease"] = 20,
                ["lfoPitch"] = 0
            }
        };
        return new(SynthId, "Synth", parameters, null);
    }
}
