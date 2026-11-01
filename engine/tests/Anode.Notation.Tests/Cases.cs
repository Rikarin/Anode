using Anode.Contracts;
using Anode.Model;
using static Anode.Notation.Tests.TestProject;

namespace Anode.Notation.Tests;

/// <summary>Named projects shared by the golden, MEI and determinism tests.</summary>
static class Cases {
    const int Q = 960;
    const int Bar = 3840;

    /// <summary>
    ///     The exit criterion's step 1 region (docs/10-phase-9.md): a piano region with chords, a tie across a bar line,
    ///     a triplet, a two-voice passage and notes slightly off the grid, under the piano style (split at C3 = 60) with
    ///     display quantise 1/16 and the 1/16-triplet grid (Logic's "16,24"; the step's "1/8" is 480 and 320, "8,12").
    /// </summary>
    public static (ProjectState State, string Track, string Region) ExitCriterion(
        int straight = 240,
        int triplet = 160
    ) {
        var project = new TestProject();
        var track = project.Track("Piano", StaffStyleIds.Piano);
        var region = project.Region(
            track,
            0,
            Bar * 3,
            Grid(straight, triplet),
            // Bar 1: a C major chord slightly late, an eighth triplet slightly off, G4 tied over the bar line; C3 held.
            N(1, 60, 12, 940),
            N(2, 64, 12, 940),
            N(3, 67, 12, 940),
            N(4, 62, 965, 300),
            N(5, 64, 1290, 300),
            N(6, 65, 1595, 300),
            N(7, 67, 1920, 2880),
            N(8, 48, 0, Bar),
            // Bar 2: E5 held over moving quarters (two voices); F2 and G2 slightly late.
            N(9, 76, Bar + Q, 3 * Q),
            N(10, 72, Bar + Q, Q),
            N(11, 74, Bar + 2 * Q, Q),
            N(12, 71, Bar + 3 * Q, Q),
            N(13, 41, Bar + 10, 1900),
            N(14, 43, Bar + 2 * Q + 10, 1900),
            // Bar 3: two sixteenths and an eighth, then a C major chord; C3 held.
            N(15, 64, 2 * Bar, 240),
            N(16, 65, 2 * Bar + 240, 240),
            N(17, 67, 2 * Bar + 480, 480),
            N(18, 60, 2 * Bar + Q, 3 * Q),
            N(19, 64, 2 * Bar + Q, 3 * Q),
            N(20, 67, 2 * Bar + Q, 3 * Q),
            N(21, 48, 2 * Bar, Bar)
        );
        return (project.State, track, region);
    }

    /// <summary>
    ///     A piano and a B♭ clarinet with every kind of stored item: articulations, dynamics, a slur, hairpins, text,
    ///     tempo text, a rehearsal mark, pedals, a trill, a fermata, an octave line, a hidden tie, lyrics with a hyphenated
    ///     word and an extender, chord symbols, markers, a key change and a tempo change, a title block.
    /// </summary>
    public static (ProjectState State, ProjectScore Score, string Piano, string Clarinet) Content() {
        var project = new TestProject()
            .Keys(new KeyEvent(0, 0, KeyMode.Major), new KeyEvent(Bar * 2, 7, KeyMode.Major))
            .Tempos(new(0, 96, TempoCurve.Step), new(Bar * 2, 108, TempoCurve.Step))
            .Marker("mrk_a", "A", Bar * 2);
        var piano = project.Track("Piano");
        var clarinet = project.Track("Clarinet", StaffStyleIds.ClarinetBb);
        project.Region(
            piano,
            0,
            Bar * 3,
            null,
            N(1, 72, 0, Q),
            N(2, 74, Q, Q),
            N(3, 76, 2 * Q, Q),
            N(4, 77, 3 * Q, Q + Q),
            N(5, 79, Bar + Q, Q),
            N(6, 78, Bar + 2 * Q, 2 * Q),
            N(7, 79, 2 * Bar, Bar),
            N(8, 48, 0, Bar),
            N(9, 43, Bar, Bar),
            N(10, 55, 2 * Bar, Bar),
            N(11, 59, 2 * Bar, Bar)
        );
        project.Region(
            clarinet,
            0,
            Bar * 3,
            null,
            N(21, 60, 0, 2 * Q),
            N(22, 62, 2 * Q, 2 * Q),
            N(23, 64, Bar, Bar),
            N(24, 66, 2 * Bar, Bar)
        );
        var score = ProjectScore.Empty with {
            Symbols = [
                new("sym_01", ScoreSymbolKind.Staccato, piano, 1),
                new("sym_02", ScoreSymbolKind.Accent, piano, 2),
                new("sym_03", ScoreSymbolKind.Tenuto, piano, 3, Placement: ScorePlacement.Below),
                new("sym_04", ScoreSymbolKind.Dynamic, piano, Tick: 0, Text: "mf"),
                new("sym_05", ScoreSymbolKind.Slur, piano, 1, EndNoteId: 3),
                new("sym_06", ScoreSymbolKind.Crescendo, piano, Tick: 0, EndTick: Bar),
                new("sym_07", ScoreSymbolKind.Diminuendo, piano, 5, EndNoteId: 6),
                new("sym_08", ScoreSymbolKind.Text, piano, Tick: Bar, Text: "dolce"),
                new("sym_09", ScoreSymbolKind.TempoText, piano, Tick: 0, Text: "Andante"),
                new("sym_10", ScoreSymbolKind.Rehearsal, piano, Tick: Bar, Text: "B"),
                new("sym_11", ScoreSymbolKind.PedalDown, piano, Tick: 0),
                new("sym_12", ScoreSymbolKind.PedalUp, piano, Tick: Bar - 240),
                new("sym_13", ScoreSymbolKind.Trill, piano, 7),
                new("sym_14", ScoreSymbolKind.Fermata, piano, 7, Placement: ScorePlacement.Above),
                new("sym_15", ScoreSymbolKind.OctaveUp, piano, Tick: Bar, EndTick: Bar * 2),
                new("sym_16", ScoreSymbolKind.TieHidden, piano, 4),
                new("sym_17", ScoreSymbolKind.Marcato, clarinet, 21),
                new("sym_18", ScoreSymbolKind.Staccatissimo, clarinet, 22),
                new("sym_19", ScoreSymbolKind.Dynamic, clarinet, 23, Text: "p")
            ],
            Lyrics = [
                new("lyr_1", 21, 1, "Hal", Syllabic.Begin, false),
                new("lyr_2", 22, 1, "le", Syllabic.Middle, false),
                new("lyr_3", 23, 1, "lu", Syllabic.End, false),
                new("lyr_4", 24, 1, "jah", Syllabic.Single, true),
                new("lyr_5", 21, 2, "Sing", Syllabic.Single, false)
            ],
            Chords = [
                new("chd_1", piano, 0, new(PitchStep.C, 0), string.Empty, null, false),
                new("chd_2", piano, 2 * Q, new(PitchStep.A, 0), "m7", null, false),
                new("chd_3", piano, Bar, new(PitchStep.F, 0), "maj7", new(PitchStep.A, 0), false),
                new("chd_4", piano, Bar * 2, new(PitchStep.G, 0), "7", null, true),
                new("chd_5", piano, Bar * 2 + 2 * Q, new(PitchStep.F, 1), "m7b5", null, false)
            ],
            Sets = [new("scs_both", "Both", [piano, clarinet])],
            Layout = ScoreLayout.Default with {
                Title = "Test & Score",
                Subtitle = "For <MEI>",
                Composer = "A. Composer",
                Header = "Header",
                Footer = "Footer",
                InstrumentNames = [new(clarinet, "Clarinet in B♭", "Cl.")]
            }
        };
        return (project.State, score, piano, clarinet);
    }

    /// <summary>A drum part: kick, snare, hi-hats and a crash, two bars.</summary>
    public static ProjectState Drums() {
        var project = new TestProject();
        var drums = project.Track("Drums", StaffStyleIds.Drums);
        var notes = new List<Note>();
        var id = 0L;
        for (var bar = 0; bar < 2; bar++) {
            for (var eighth = 0; eighth < 8; eighth++) {
                var tick = bar * Bar + eighth * 480;
                notes.Add(N(++id, eighth == 0 && bar == 1 ? 49 : 42, tick, 120));
                if (eighth is 0 or 4) {
                    notes.Add(N(++id, 36, tick, 120));
                }

                if (eighth is 2 or 6) {
                    notes.Add(N(++id, 38, tick, 120));
                }
            }
        }

        project.Region(drums, 0, Bar * 2, null, [..notes]);
        return project.State;
    }

    /// <summary>Meter changes: 4/4, 6/8, 3/4, 5/8 and 2/2, with ties across their bar lines.</summary>
    public static ProjectState Meters() {
        var project = new TestProject().Signatures(
            new SignatureEvent(0, 4, 4),
            new SignatureEvent(Bar, 6, 8),
            new SignatureEvent(Bar + 2880, 3, 4),
            new SignatureEvent(Bar + 2 * 2880, 5, 8),
            new SignatureEvent(Bar + 2 * 2880 + 2400, 2, 2)
        );
        var track = project.Track("T", StaffStyleIds.Treble);
        project.Region(
            track,
            0,
            Bar * 6,
            null,
            N(1, 60, 2 * Q, 3 * Q),
            N(2, 62, Bar + 1440, 480),
            N(3, 64, Bar + 1920, 1920),
            N(4, 65, Bar + 2880 + Q, 2880),
            N(5, 67, Bar + 2 * 2880 + 2400, Bar)
        );
        return project.State;
    }
}
