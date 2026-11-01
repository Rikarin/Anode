using Anode.Contracts;
using static Anode.Notation.Tests.TestProject;

namespace Anode.Notation.Tests;

/// <summary>
///     What the view carries besides notes: measures (meters, keys, tempo marks, rehearsal marks, barlines), the stored
///     symbols, lyrics and chord symbols at their events, score sets and parts.
/// </summary>
public sealed class ScoreContentTests {
    const int Q = 960;
    const int Bar = 3840;

    static IEnumerable<ScoreEvent> Events(ScoreView view, int part = 0, int staff = 0) =>
        view.Parts[part].Measures.SelectMany(m => m.Staves[staff].Voices).SelectMany(static v => v.Events);

    [Fact]
    public void Measures_carry_meters_tempo_marks_rehearsal_marks_and_barlines() {
        var project = new TestProject().Signatures(new SignatureEvent(0, 4, 4), new SignatureEvent(Bar * 2, 3, 4))
            .Tempos(
                new(0, 100, TempoCurve.Step),
                new(Bar + Q, 100, TempoCurve.Step),
                new(Bar * 2, 132.5, TempoCurve.Step)
            )
            .Marker("mrk_b", "Verse", Bar)
            .Marker("mrk_a", "Intro", 0);
        var track = project.Track("T", StaffStyleIds.Treble);
        project.Region(track, 0, Bar * 4, null, N(1, 60, Bar * 2 + 2880, 960));
        var view = NotationEngine.Render(project.State, ProjectScore.Empty, new());

        Assert.Equal([1, 2, 3, 4], view.Measures.Select(static m => m.Number));
        Assert.Equal([0L, Bar, Bar * 2, Bar * 2 + 2880], view.Measures.Select(static m => m.StartTick));
        Assert.Equal([true, false, true, false], view.Measures.Select(static m => m.ShowTime));
        Assert.Equal([true, false, false, false], view.Measures.Select(static m => m.ShowKey));
        Assert.Equal(new(3, 4), view.Measures[2].Time);
        Assert.Equal([new ScoreTempoMark(0, 100)], view.Measures[0].Tempos);
        Assert.Empty(view.Measures[1].Tempos);
        Assert.Equal([new ScoreTempoMark(Bar * 2, 132.5)], view.Measures[2].Tempos);
        Assert.Equal([new ScoreRehearsal(0, "mrk_a", "Intro")], view.Measures[0].Rehearsals);
        Assert.Equal([new ScoreRehearsal(Bar, "mrk_b", "Verse")], view.Measures[1].Rehearsals);
        Assert.Equal(
            [Barline.Double, Barline.Double, Barline.Single, Barline.Final],
            view.Measures.Select(static m => m.Barline)
        );
        Assert.All(view.Parts[0].Measures.Skip(1), static m => Assert.Null(m.Staves[0].Clef));
        Assert.Equal(Clef.Treble, view.Parts[0].Measures[0].Staves[0].Clef);
    }

    [Fact]
    public void A_project_without_notes_has_one_measure() {
        var project = new TestProject();
        var track = project.Track("T");
        project.Region(track, 0, Bar, null);
        var view = NotationEngine.Render(project.State, ProjectScore.Empty, new());
        var measure = Assert.Single(view.Measures);
        Assert.Equal(Barline.Final, measure.Barline);
        Assert.Equal(2, view.Parts[0].Measures[0].Staves.Count);
    }

    [Fact]
    public void Symbols_lyrics_and_chord_symbols_are_drawn_at_their_events() {
        var project = new TestProject();
        var track = project.Track("Piano");
        var other = project.Track("Other");
        project.Region(
            track,
            0,
            Bar * 2,
            null,
            N(1, 72, 0, Q),
            N(2, 74, Q, Q),
            N(3, 76, 2 * Q, 3 * Q),
            N(4, 48, 0, Bar * 2)
        );
        project.Region(other, 0, Bar, null, N(9, 60, 0, Q));
        var score = ProjectScore.Empty with {
            Symbols = [
                new("sym_stacc", ScoreSymbolKind.Staccato, track, 1),
                new("sym_acc", ScoreSymbolKind.Accent, track, 3),
                new("sym_mf", ScoreSymbolKind.Dynamic, track, Tick: Q + 100, Text: "mf"),
                new("sym_slur", ScoreSymbolKind.Slur, track, 1, EndNoteId: 3),
                new("sym_cresc", ScoreSymbolKind.Crescendo, track, Tick: 0, EndTick: Bar),
                new("sym_ped", ScoreSymbolKind.PedalDown, track, Tick: 0),
                new("sym_gone", ScoreSymbolKind.Staccato, track, 99),
                new("sym_other", ScoreSymbolKind.Accent, other, 9)
            ],
            Lyrics = [
                new("lyr_1", 1, 1, "Hel", Syllabic.Begin, false),
                new("lyr_2", 2, 1, "lo", Syllabic.End, false),
                new("lyr_3", 3, 1, "world", Syllabic.Single, true),
                new("lyr_9", 9, 1, "other", Syllabic.Single, false)
            ],
            Chords = [
                new("chd_2", track, Bar, new(PitchStep.F, 0), "maj7", null, false),
                new("chd_1", track, 0, new(PitchStep.C, 0), string.Empty, null, false)
            ]
        };

        var view = NotationEngine.Render(project.State, score, new(PartTrackId: track));
        Assert.Equal(
            ["sym_acc", "sym_cresc", "sym_mf", "sym_ped", "sym_slur", "sym_stacc"],
            view.Symbols.Select(static s => s.Id)
        );
        Assert.Equal(["lyr_1", "lyr_2", "lyr_3"], view.Lyrics.Select(static l => l.Id));
        Assert.Equal(["chd_1", "chd_2"], view.Chords.Select(static c => c.Id));

        var upper = Events(view).ToList();
        Assert.Equal(["sym_cresc", "sym_slur", "sym_stacc"], upper.Single(static e => e.Id == "n1").SymbolIds);
        Assert.Equal(["sym_mf"], upper.Single(static e => e.Id == "n2").SymbolIds);
        Assert.Equal(["sym_acc"], upper.Single(static e => e.Id == "n3").SymbolIds);
        Assert.Empty(upper.Single(static e => e.Id == "n3-2").SymbolIds);
        Assert.Equal(["lyr_1"], upper.Single(static e => e.Id == "n1").LyricIds);
        Assert.Equal(["lyr_3"], upper.Single(static e => e.Id == "n3").LyricIds);
        Assert.Equal(["chd_1"], upper.Single(static e => e.Id == "n1").ChordIds);
        Assert.Equal(["chd_2"], upper.Single(static e => e.Tick == Bar).ChordIds);

        var lower = Events(view, 0, 1).ToList();
        Assert.Equal(["sym_ped"], lower.Single(static e => e.Id == "n4").SymbolIds);
        Assert.Empty(lower.SelectMany(static e => e.ChordIds));
    }

    [Fact]
    public void A_positioned_symbol_can_name_its_staff() {
        var project = new TestProject();
        var track = project.Track("Piano");
        project.Region(track, 0, Bar, null, N(1, 72, 0, Bar), N(2, 48, 0, Bar));
        var score = ProjectScore.Empty with {
            Symbols = [new("sym_p", ScoreSymbolKind.Dynamic, track, Tick: 0, Text: "p", Staff: 1)]
        };
        var view = NotationEngine.Render(project.State, score, new());
        Assert.Empty(Events(view).Single().SymbolIds);
        Assert.Equal(["sym_p"], Events(view, 0, 1).Single().SymbolIds);
    }

    [Fact]
    public void Score_sets_order_the_parts_and_parts_are_extracted_from_them() {
        var project = new TestProject();
        var a = project.Track("A", StaffStyleIds.Treble);
        var b = project.Track("B", StaffStyleIds.Bass);
        var empty = project.Track("Empty", StaffStyleIds.Treble);
        project.Region(a, 0, Bar, null, N(1, 72, 0, Q));
        project.Region(b, 0, Bar, null, N(2, 48, 0, Q));
        var score = ProjectScore.Empty with {
            Sets = [new("scs_1", "Set", [b, a])],
            Layout = ScoreLayout.Default with { InstrumentNames = [new(b, "Basso", "Bs.")] }
        };

        var all = NotationEngine.Render(project.State, score, new());
        Assert.Equal([a, b], all.Parts.Select(static p => p.TrackId));
        Assert.DoesNotContain(all.Parts, p => p.TrackId == empty);

        var set = NotationEngine.Render(project.State, score, new("scs_1", Mode: ScoreViewMode.Page));
        Assert.Equal("scs_1", set.SetId);
        Assert.Equal(ScoreViewMode.Page, set.Mode);
        Assert.Equal([b, a], set.Parts.Select(static p => p.TrackId));
        Assert.Equal(("Basso", "Bs."), (set.Parts[0].Name, set.Parts[0].ShortName));
        Assert.Equal(("A", "A"), (set.Parts[1].Name, set.Parts[1].ShortName));

        var part = NotationEngine.Render(project.State, score, new("scs_1", a));
        Assert.Equal(a, Assert.Single(part.Parts).TrackId);
        Assert.Equal(a, part.PartTrackId);

        Assert.Throws<ArgumentException>(() => NotationEngine.Render(project.State, score, new("scs_none")));
        Assert.Throws<ArgumentException>(() => NotationEngine.Render(project.State, score, new("scs_1", empty)));
        Assert.Throws<ArgumentException>(() => NotationEngine.Render(project.State, score, new(PartTrackId: "trk_none"))
        );
    }

    [Fact]
    public void A_track_uses_its_projects_own_staff_style() {
        var project = new TestProject();
        var track = project.Track("T", "sts_mine");
        project.Region(track, 0, Bar, null, N(1, 50, 0, Q));
        var mine = new StaffStyle("sts_mine", "Mine", [new(Clef.Tenor, 0, null, 1)], null, 480, 0, false);
        var view = NotationEngine.Render(project.State, ProjectScore.Empty with { StaffStyles = [mine] }, new());
        Assert.Equal("sts_mine", view.Parts[0].StaffStyleId);
        Assert.Equal(Clef.Tenor, view.Parts[0].Measures[0].Staves[0].Clef);
        Assert.Equal(0, Events(view).First().Notes[0].StaffPosition);

        var unknown = NotationEngine.Render(project.State, ProjectScore.Empty, new());
        Assert.Equal(StaffStyleIds.Piano, unknown.Parts[0].StaffStyleId);
    }

    [Fact]
    public void Display_quantise_moves_notes_and_never_changes_the_MIDI() {
        var project = new TestProject();
        var track = project.Track("T", StaffStyleIds.Treble);
        var region = project.Region(
            track,
            0,
            Bar,
            Grid(240),
            N(1, 60, 0, 240),
            N(2, 62, 250, 230),
            N(3, 64, 480, 960)
        );
        var before = project.State;
        var sixteenth = NotationEngine.Render(project.State, ProjectScore.Empty, new());
        Assert.Equal("m1 s0 v1: C4/16b1 D4/16b1 E4/8~b1 E4/8 r/8 r/h", Describe.View(sixteenth).Trim());
        var eighth = NotationEngine.Render(
            project.State with {
                MidiRegions = project.State.MidiRegions.SetItem(
                    region,
                    project.State.MidiRegions[region] with { Notation = Grid(480) }
                )
            },
            ProjectScore.Empty,
            new()
        );
        Assert.NotEqual(Describe.View(sixteenth), Describe.View(eighth));
        Assert.Same(before, project.State);
    }
}
