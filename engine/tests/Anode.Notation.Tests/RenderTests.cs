using Anode.Contracts;
using static Anode.Notation.Tests.TestProject;

namespace Anode.Notation.Tests;

/// <summary>
///     Hand-made cases (docs/10-phase-9.md, workstream A; the rules: docs/notation/README.md), each checked as the
///     compact text of <see cref="Describe" />: which notes, values, ties, tuplets, beams, voices and accidentals.
/// </summary>
public sealed class RenderTests {
    const int Q = 960;
    const int Bar = 3840;

    /// <summary>One track of <paramref name="style" /> with one region at the start holding the notes.</summary>
    static ScoreView Render(string style, RegionNotation? notation, params Note[] notes) {
        var project = new TestProject();
        var track = project.Track("T", style);
        project.Region(track, 0, Bar * 8, notation, notes);
        return NotationEngine.Render(project.State, ProjectScore.Empty, new());
    }

    static void Same(string expected, ScoreView view, int part = 0) =>
        Assert.Equal(
            expected.Replace("\r", string.Empty, StringComparison.Ordinal).Trim(),
            Describe.View(view, part).Trim()
        );

    [Fact]
    public void Instrument_names_default_to_the_track_name_and_its_first_five_characters() {
        var project = new TestProject();
        var clarinet = project.Track("Clarinet", StaffStyleIds.ClarinetBb);
        var oboe = project.Track("Oboe", StaffStyleIds.Treble);
        var horn = project.Track("Horn", StaffStyleIds.HornF);
        foreach (var track in new[] { clarinet, oboe, horn }) {
            project.Region(track, 0, Bar, null, N(1, 60, 0, Q));
        }

        // A layout entry names only what differs ("" is unset): the horn's short name.
        var layout = ScoreLayout.Default with { InstrumentNames = [new(horn, string.Empty, "Hn.")] };
        var view = NotationEngine.Render(project.State, ProjectScore.Empty with { Layout = layout }, new());
        Assert.Equal(
            [("Clarinet", "Clari"), ("Oboe", "Oboe"), ("Horn", "Hn.")],
            view.Parts.Select(static p => (p.Name, p.ShortName))
        );
        Assert.Equal("Clari", NotationEngine.DefaultShortName("Clarinet"));
        Assert.Equal("Abc\uD834\uDD1E", NotationEngine.DefaultShortName("Abc\uD834\uDD1Edef"));
        Assert.Equal("Abcd", NotationEngine.DefaultShortName("Abcd\uD834\uDD1E"));
    }

    [Fact]
    public void A_note_across_a_bar_line_is_tied() =>
        Same(
            """
            m1 s0 v1: r/h r/q C5/q~
            m2 s0 v1: C5/q r/q r/h
            """,
            Render(StaffStyleIds.Treble, null, N(1, 72, 3 * Q, 2 * Q))
        );

    [Fact]
    public void Dotted_values_start_on_their_beat() =>
        Same(
            "m1 s0 v1: C4/q. D4/8 E4/h",
            Render(StaffStyleIds.Treble, null, N(1, 60, 0, 1440), N(2, 62, 1440, 480), N(3, 64, 2 * Q, 2 * Q))
        );

    [Fact]
    public void Without_dots_a_dotted_quarter_is_a_tied_quarter_and_eighth() =>
        Same(
            "m1 s0 v1: C4/q~ C4/8b1 D4/8b1 E4/h",
            Render(
                StaffStyleIds.Treble,
                RegionNotation.Default with { MaxDots = 0 },
                N(1, 60, 0, 1440),
                N(2, 62, 1440, 480),
                N(3, 64, 2 * Q, 2 * Q)
            )
        );

    [Fact]
    public void Syncopation_off_ties_at_the_beat_and_beams_the_half_bar() =>
        Same(
            "m1 s0 v1: C4/8b1 D4/8~b1 D4/8b1 E4/8b1 F4/h",
            Render(
                StaffStyleIds.Treble,
                null,
                N(1, 60, 0, 480),
                N(2, 62, 480, Q),
                N(3, 64, 1440, 480),
                N(4, 65, 2 * Q, 2 * Q)
            )
        );

    [Fact]
    public void Syncopation_on_shows_the_syncopated_quarter_whole() =>
        Same(
            "m1 s0 v1: C4/8 D4/q E4/8 F4/h",
            Render(
                StaffStyleIds.Treble,
                RegionNotation.Default with { Syncopation = true },
                N(1, 60, 0, 480),
                N(2, 62, 480, Q),
                N(3, 64, 1440, 480),
                N(4, 65, 2 * Q, 2 * Q)
            )
        );

    [Fact]
    public void Syncopation_decides_a_half_note_on_beat_two() {
        Same("m1 s0 v1: r/q C4/q~ C4/q r/q", Render(StaffStyleIds.Treble, null, N(1, 60, Q, 2 * Q)));
        Same(
            "m1 s0 v1: r/q C4/h r/q",
            Render(StaffStyleIds.Treble, RegionNotation.Default with { Syncopation = true }, N(1, 60, Q, 2 * Q))
        );
    }

    [Fact]
    public void Interpretation_lengthens_short_notes_to_the_beat() {
        var notes = new[] { N(1, 60, 0, 200), N(2, 62, Q, 200), N(3, 64, 2 * Q, 200), N(4, 65, 3 * Q, 200) };
        Same("m1 s0 v1: C4/q D4/q E4/q F4/q", Render(StaffStyleIds.Treble, null, notes));
        Same(
            "m1 s0 v1: C4/16 r/16 r/8 D4/16 r/16 r/8 E4/16 r/16 r/8 F4/16 r/16 r/8",
            Render(StaffStyleIds.Treble, RegionNotation.Default with { Interpretation = false }, notes)
        );
    }

    [Fact]
    public void Interpretation_shortens_a_note_just_past_a_beat_to_tidy_rests() {
        Same("m1 s0 v1: C4/q r/q r/h", Render(StaffStyleIds.Treble, null, N(1, 60, 0, 1200)));
        Same(
            "m1 s0 v1: C4/q~ C4/16 r/16 r/8 r/h",
            Render(StaffStyleIds.Treble, RegionNotation.Default with { Interpretation = false }, N(1, 60, 0, 1200))
        );
    }

    [Fact]
    public void Notes_slightly_off_the_grid_are_drawn_on_it() =>
        Same(
            "m1 s0 v1: C4/8b1 D4/8b1 E4/8b1 F4/8b1 G4/h",
            Render(
                StaffStyleIds.Treble,
                null,
                N(1, 60, 15, 450),
                N(2, 62, 470, 500),
                N(3, 64, 975, 440),
                N(4, 65, 1430, 470),
                N(5, 67, 1910, 1900)
            )
        );

    [Fact]
    public void Eighth_triplets_are_one_group_and_sixteenth_triplets_two() {
        Same(
            "m1 s0 v1: C4/8t1b1 D4/8t1b1 E4/8t1b1 F4/16t2b2 G4/16t2b2 A4/16t2b2 B4/16t3b3 C5/16t3b3 D5/16t3b3 r/h",
            Render(
                StaffStyleIds.Treble,
                null,
                N(1, 60, 0, 320),
                N(2, 62, 320, 320),
                N(3, 64, 640, 320),
                N(4, 65, 960, 160),
                N(5, 67, 1120, 160),
                N(6, 69, 1280, 160),
                N(7, 71, 1440, 160),
                N(8, 72, 1600, 160),
                N(9, 74, 1760, 160)
            )
        );
    }

    [Fact]
    public void A_triplet_quarter_and_eighth_share_a_bracket() =>
        Same(
            "m1 s0 v1: C4/q t1 D4/8t1 r/q r/h".Replace("/q t1", "/qt1", StringComparison.Ordinal),
            Render(StaffStyleIds.Treble, null, N(1, 60, 0, 640), N(2, 62, 640, 320))
        );

    [Fact]
    public void Without_a_triplet_grid_triplets_snap_to_the_straight_grid() =>
        Same(
            "m1 s0 v1: C4/16b1 D4/8b1 E4/16b1 r/q r/h",
            Render(StaffStyleIds.Treble, Grid(240), N(1, 60, 0, 320), N(2, 62, 320, 320), N(3, 64, 640, 320))
        );

    [Fact]
    public void A_held_note_over_moving_notes_makes_two_voices() =>
        Same(
            """
            m1 s0 v1: E5/h r/h
            m1 s0 v2: C5/q D5/q r/h
            """,
            Render(StaffStyleIds.Treble, null, N(1, 76, 0, 2 * Q), N(2, 72, 0, Q), N(3, 74, Q, Q))
        );

    [Fact]
    public void A_held_bass_under_a_melody_is_voice_two() =>
        Same(
            """
            m1 s0 v1: E4/q G4/q E4/q G4/q
            m1 s0 v2: C4/w
            """,
            Render(
                StaffStyleIds.Treble,
                null,
                N(1, 60, 0, Bar),
                N(2, 64, 0, Q),
                N(3, 67, Q, Q),
                N(4, 64, 2 * Q, Q),
                N(5, 67, 3 * Q, Q)
            )
        );

    [Fact]
    public void One_voice_staves_make_chords_and_cut_overlaps() =>
        Same(
            "m1 s0 v1: C4+E4/q D4/q r/h",
            Render(StaffStyleIds.ClarinetBb, null, N(1, 76 - 12, 0, 2 * Q), N(2, 60, 0, Q), N(3, 62, Q, Q))
        );

    [Fact]
    public void Legato_overlaps_are_cut_with_no_overlap() =>
        Same(
            "m1 s0 v1: C4/q D4/q E4/h",
            Render(
                StaffStyleIds.Treble,
                Grid(240) with { Interpretation = false },
                N(1, 60, 0, Q + 240),
                N(2, 62, Q, Q + 240),
                N(3, 64, 2 * Q, 2 * Q)
            )
        );

    [Fact]
    public void Legato_overlaps_make_voices_without_no_overlap() =>
        Same(
            """
            m1 s0 v1: r/q D4/q r/h
            m1 s0 v2: C4/q~ C4/16 r/16 r/8 r/h
            """,
            Render(
                StaffStyleIds.Treble,
                Grid(240) with { Interpretation = false, NoOverlap = false },
                N(1, 60, 0, Q + 240),
                N(2, 62, Q, Q)
            )
        );

    [Fact]
    public void The_piano_splits_at_middle_C() =>
        Same(
            """
            m1 s0 v1: C4/h r/h
            m1 s1 v1: B3/h r/h
            """,
            Render(StaffStyleIds.Piano, null, N(1, 60, 0, 2 * Q), N(2, 59, 0, 2 * Q))
        );

    [Fact]
    public void An_empty_measure_is_a_whole_rest_in_any_meter() {
        var project = new TestProject().Signatures(new SignatureEvent(0, 3, 4));
        var track = project.Track("T", StaffStyleIds.Treble);
        project.Region(track, 0, 2880 * 2, null, N(1, 60, 2880, 2880));
        var view = NotationEngine.Render(project.State, ProjectScore.Empty, new());
        Same(
            """
            m1 s0 v1: r/w
            m2 s0 v1: C4/h.
            """,
            view
        );
        Assert.Equal(2880, view.Parts[0].Measures[0].Staves[0].Voices[0].Events[0].DurationTicks);
    }

    [Fact]
    public void Compound_meters_beam_and_tie_by_the_dotted_beat() {
        var project = new TestProject().Signatures(new SignatureEvent(0, 6, 8));
        var track = project.Track("T", StaffStyleIds.Treble);
        project.Region(
            track,
            0,
            Bar * 4,
            null,
            N(1, 60, 0, 1440),
            N(2, 62, 1440, 480),
            N(3, 64, 1920, 480),
            N(4, 65, 2400, 480),
            N(5, 67, 2880, 960),
            N(6, 69, 3840, 480),
            N(7, 71, 4320, 1440),
            N(8, 72, 5760, 2880)
        );
        Same(
            """
            m1 s0 v1: C4/q. D4/8b1 E4/8b1 F4/8b1
            m2 s0 v1: G4/q A4/8 B4/q.
            m3 s0 v1: C5/h.
            """,
            NotationEngine.Render(project.State, ProjectScore.Empty, new())
        );
    }

    [Fact]
    public void Compound_meters_split_a_half_note_at_the_beat() {
        var project = new TestProject().Signatures(new SignatureEvent(0, 6, 8));
        var track = project.Track("T", StaffStyleIds.Treble);
        project.Region(track, 0, Bar, null, N(1, 60, 0, 1920));
        Same("m1 s0 v1: C4/q.~ C4/8 r/8 r/8", NotationEngine.Render(project.State, ProjectScore.Empty, new()));
    }

    [Fact]
    public void Three_four_writes_a_half_note_on_beat_two() {
        var project = new TestProject().Signatures(new SignatureEvent(0, 3, 4));
        var track = project.Track("T", StaffStyleIds.Treble);
        project.Region(track, 0, Bar, null, N(1, 60, Q, 2 * Q));
        Same("m1 s0 v1: r/q C4/h", NotationEngine.Render(project.State, ProjectScore.Empty, new()));
    }

    [Fact]
    public void Five_eight_groups_three_and_two() {
        var project = new TestProject().Signatures(new SignatureEvent(0, 5, 8));
        var track = project.Track("T", StaffStyleIds.Treble);
        project.Region(
            track,
            0,
            2400,
            null,
            N(1, 60, 0, 480),
            N(2, 62, 480, 480),
            N(3, 64, 960, 480),
            N(4, 65, 1440, 480),
            N(5, 67, 1920, 480)
        );
        Same(
            "m1 s0 v1: C4/8b1 D4/8b1 E4/8b1 F4/8b2 G4/8b2",
            NotationEngine.Render(project.State, ProjectScore.Empty, new())
        );
    }

    [Fact]
    public void Accidentals_follow_the_key_and_carry_through_the_bar() {
        var project = new TestProject().Keys(new(0, 7, KeyMode.Major), new(Bar, 5, KeyMode.Major));
        var track = project.Track("T", StaffStyleIds.Treble);
        project.Region(
            track,
            0,
            Bar * 2,
            null,
            N(1, 66, 0, Q),
            N(2, 65, Q, Q),
            N(3, 65, 2 * Q, Q),
            N(4, 66, 3 * Q, Q),
            N(5, 70, Bar, Q),
            N(6, 71, Bar + Q, Q),
            N(7, 70, Bar + 2 * Q, Q),
            N(8, 61, Bar + 3 * Q, Q)
        );
        var view = NotationEngine.Render(project.State, ProjectScore.Empty, new());
        Same(
            """
            m1 s0 v1: F#4/q !F4/q F4/q !F#4/q
            m2 s0 v1: Bb4/q !B4/q !Bb4/q !Db4/q
            """,
            view
        );
        Assert.Equal(new(1, KeyMode.Major), view.Measures[0].Key);
        Assert.Equal(new(-1, KeyMode.Major), view.Measures[1].Key);
        Assert.True(view.Measures[1].ShowKey);
        Assert.Equal(Barline.Double, view.Measures[0].Barline);
        Assert.Equal(Barline.Final, view.Measures[1].Barline);
    }

    [Fact]
    public void C_major_spells_black_keys_as_C_sharp_E_flat_F_sharp_G_sharp_B_flat() =>
        Same(
            "m1 s0 v1: !C#4/8b1 !Eb4/8b1 !F#4/8b1 !G#4/8b1 !Bb4/8b2 !B4/8b2 r/q",
            Render(
                StaffStyleIds.Treble,
                null,
                N(1, 61, 0, 480),
                N(2, 63, 480, 480),
                N(3, 66, 960, 480),
                N(4, 68, 1440, 480),
                N(5, 70, 1920, 480),
                N(6, 71, 2400, 480)
            )
        );

    [Fact]
    public void Minor_keys_raise_the_leading_tone_as_a_sharp() {
        var project = new TestProject().Keys(new KeyEvent(0, 2, KeyMode.Minor));
        var track = project.Track("T", StaffStyleIds.Treble);
        project.Region(track, 0, Bar, null, N(1, 61, 0, Q), N(2, 62, Q, Q), N(3, 70, 2 * Q, Q), N(4, 71, 3 * Q, Q));
        Same("m1 s0 v1: !C#4/q D4/q Bb4/q !B4/q", NotationEngine.Render(project.State, ProjectScore.Empty, new()));
    }

    [Fact]
    public void A_transposing_part_is_written_a_step_up_and_the_full_score_at_concert_pitch() {
        var project = new TestProject();
        var piano = project.Track("Piano");
        var clarinet = project.Track("Clarinet", StaffStyleIds.ClarinetBb);
        project.Region(piano, 0, Bar, null, N(1, 60, 0, Bar));
        project.Region(clarinet, 0, Bar, null, N(2, 60, 0, Q), N(3, 66, Q, Q), N(4, 70, 2 * Q, 2 * Q));
        var score = NotationEngine.Render(project.State, ProjectScore.Empty, new());
        Assert.False(score.Transposing);
        Assert.Equal(0, score.Parts[1].Transpose);
        Same("m1 s0 v1: C4/q !F#4/q !Bb4/h", score, 1);

        var part = NotationEngine.Render(project.State, ProjectScore.Empty, new(PartTrackId: clarinet));
        Assert.True(part.Transposing);
        Assert.Equal(clarinet, Assert.Single(part.Parts).TrackId);
        Assert.Equal(2, part.Parts[0].Transpose);
        Same("m1 s0 v1: D4/q !G#4/q !C5/h", part);
        Assert.Equal(60, part.Parts[0].Measures[0].Staves[0].Voices[0].Events[0].Notes[0].Pitch);

        var transposing = NotationEngine.Render(
            project.State,
            ProjectScore.Empty with { Layout = ScoreLayout.Default with { TransposingScore = true } },
            new()
        );
        Same("m1 s0 v1: D4/q !G#4/q !C5/h", transposing, 1);
        Same("m1 s0 v1: C4/w\nm1 s1 v1: r/w", transposing);
    }

    [Fact]
    public void Octave_clefs_put_the_octave_in_the_clef() {
        var view = Render(StaffStyleIds.Guitar, null, N(1, 40, 0, Bar));
        var note = view.Parts[0].Measures[0].Staves[0].Voices[0].Events[0].Notes[0];
        Assert.Equal(new(PitchStep.E, 0, 2), note.Written);
        Assert.Equal(-7, note.StaffPosition);
        Assert.Equal(Clef.Treble8vb, view.Parts[0].Measures[0].Staves[0].Clef);
    }

    [Fact]
    public void The_tenor_sax_is_written_a_ninth_up_on_the_treble_clef() {
        var project = new TestProject();
        var sax = project.Track("Sax", StaffStyleIds.TenorSax);
        project.Region(sax, 0, Bar, null, N(1, 58, 0, Bar));
        var part = NotationEngine.Render(project.State, ProjectScore.Empty, new(PartTrackId: sax));
        Same("m1 s0 v1: !C5/w", part);
    }

    [Fact]
    public void Drums_use_the_drum_map_positions_and_noteheads() {
        var view = Render(
            StaffStyleIds.Drums,
            null,
            N(1, 36, 0, 240),
            N(2, 42, 0, 240),
            N(3, 42, 480, 240),
            N(4, 38, 960, 240),
            N(5, 42, 960, 240),
            N(6, 46, 1440, 240)
        );
        Same("m1 s0 v1: F4+G5/8b1 G5/8b1 C5+G5/8b1 G5/8b1 r/h", view);
        var events = view.Parts[0].Measures[0].Staves[0].Voices[0].Events;
        Assert.Equal([1, 9], events[0].Notes.Select(static n => n.StaffPosition));
        Assert.Equal([Notehead.Normal, Notehead.Cross], events[0].Notes.Select(static n => n.Notehead));
        Assert.Equal(Notehead.CircleCross, events[3].Notes[0].Notehead);
        Assert.All(events.SelectMany(static e => e.Notes), static n => Assert.Null(n.Accidental));
    }

    [Fact]
    public void Stems_point_away_from_the_farthest_note() {
        var view = Render(
            StaffStyleIds.Treble,
            null,
            N(1, 60, 0, Q),
            N(2, 79, Q, Q),
            N(3, 71, 2 * Q, Q),
            N(4, 60, 3 * Q, 240),
            N(5, 81, 3 * Q + 240, 720)
        );
        var events = view.Parts[0].Measures[0].Staves[0].Voices[0].Events;
        Assert.Equal(
            [StemDirection.Up, StemDirection.Down, StemDirection.Down, StemDirection.Down, StemDirection.Down],
            events.Select(static e => e.Stem)
        );
    }
}
