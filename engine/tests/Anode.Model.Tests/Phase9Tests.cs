using Anode.Contracts;
using Anode.Model.Editing;

namespace Anode.Model.Tests;

/// <summary>
///     Phase 9's model (docs/10-phase-9.md): the stored score content and its commands, attachment (a note's symbols
///     and lyrics go with it and come back on undo), the score edits through the notation stand-in, and
///     <c>importTracks</c>. Also the score's element ids, which the editor mirrors.
/// </summary>
public sealed class Phase9Tests {
    /// <summary>A project with a piano track and a region of four quarter notes C4 D4 E4 F4 in bar 1, and a second track.</summary>
    static (ProjectState State, SequentialIds Ids, string Track, string Other, MidiRegion Region) Project() {
        var ids = new SequentialIds();
        var state = ProjectDefaults.NewProject("prj_1", "Song", 48_000);
        state = Apply(state, ids, new AddTrackCommand("Piano", Kind: TrackKind.Instrument));
        state = Apply(state, ids, new AddTrackCommand("Clarinet", Kind: TrackKind.Instrument));
        var track = state.Tracks[0].Id;
        state = Apply(state, ids, new AddMidiRegionCommand(track, 0, 3840));
        var regionId = state.MidiRegions.Keys.Single();
        state = Apply(
            state,
            ids,
            new AddNotesCommand(
                regionId,
                [..new[] { 60, 62, 64, 65 }.Select(static (p, i) => new NewNote(p, 90, 1, i * 960L, 960))]
            )
        );
        return (state, ids, track, state.Tracks[1].Id, state.MidiRegions[regionId]);
    }

    static ProjectState Apply(ProjectState state, IIdSource ids, params EditCommand[] commands) =>
        Edits.Apply(state, commands, ids, RandomProjects.Plugins, notation: ScoreNotationDouble.Instance).State;

    static EditResult Result(ProjectState state, IIdSource ids, params EditCommand[] commands) =>
        Edits.Apply(state, commands, ids, RandomProjects.Plugins, notation: ScoreNotationDouble.Instance);

    static void Same(ProjectState expected, ProjectState actual) =>
        Assert.Equal(RandomProjects.Json(expected), RandomProjects.Json(actual));

    [Fact]
    public void The_score_content_is_stored_and_part_of_the_project() {
        var (state, ids, track, other, region) = Project();
        state = Apply(
            state,
            ids,
            new SetStaffStyleCommand(new("sts_mine", " Mine ", [new(Clef.Alto, 0, null, 1)], null, 240, 0, false)),
            new AssignStaffStyleCommand([other], "sts_mine"),
            new AddScoreSymbolsCommand(
                [
                    new(ScoreSymbolKind.Staccato, track, region.Notes[0].Id),
                    new(ScoreSymbolKind.Dynamic, track, Tick: 0, Text: "mf"),
                    new(ScoreSymbolKind.Slur, track, region.Notes[0].Id, EndNoteId: region.Notes[2].Id),
                    new(ScoreSymbolKind.Crescendo, track, Tick: 960, EndTick: 2880)
                ]
            ),
            new SetLyricsCommand(
                1,
                [new(region.Notes[0].Id, "Hel", Syllabic.Begin), new(region.Notes[1].Id, "lo", Syllabic.End)]
            ),
            new AddChordSymbolsCommand(
                [new(track, 0, new(PitchStep.C, 0), string.Empty), new(track, 1920, new(PitchStep.A, 0), "m7")]
            ),
            new SetScoreSetCommand(new("scs_both", "Both", [track, other])),
            new SetScoreLayoutCommand(
                ScoreLayout.Default with { Title = "Song", InstrumentNames = [new(other, "Clarinet in B♭", "Cl.")] }
            ),
            new SetRegionNotationCommand([region.Id], 480, MaxDots: 2)
        );
        var score = state.ToProject(string.Empty, 0, false).Score;
        Assert.Equal("Mine", Assert.Single(score.StaffStyles).Name);
        Assert.Equal("sts_mine", state.Tracks[1].StaffStyleId);
        Assert.Equal(4, score.Symbols.Count);
        Assert.All(score.Symbols, static s => Assert.StartsWith("sym_", s.Id, StringComparison.Ordinal));
        Assert.Equal(["Hel", "lo"], score.Lyrics.Select(static l => l.Text).Order(StringComparer.Ordinal));
        Assert.Equal(2, score.Chords.Count);
        Assert.Equal([track, other], Assert.Single(score.Sets).TrackIds);
        Assert.Equal("Song", score.Layout.Title);
        Assert.Equal(new(480, 0, true, true, false, 2), state.MidiRegions[region.Id].Notation);

        // A note's syllable keeps its id when it changes; an empty text removes it.
        var hel = state.Lyrics.Values.Single(static l => l.Text == "Hel");
        state = Apply(
            state,
            ids,
            new SetLyricsCommand(1, [new(hel.NoteId, "Hal", Syllabic.Begin), new(region.Notes[1].Id, string.Empty)])
        );
        Assert.Equal("Hal", Assert.Single(state.Lyrics.Values).Text);
        Assert.Equal(hel.Id, state.Lyrics.Values.Single().Id);

        // Deleting the style returns its tracks to the default.
        state = Apply(state, ids, new DeleteStaffStyleCommand("sts_mine"));
        Assert.Null(state.Tracks[1].StaffStyleId);
        Assert.Empty(state.StaffStyles);
    }

    public static TheoryData<string> BadCommands() => [
        "factory id", "dynamic text", "slur at ticks", "articulation at a tick", "wedge without end",
        "note of another track",
        "two staves no split", "grid", "drum map without percussion", "verse", "chord alteration", "layout margins",
        "audio track style", "set track twice", "cell notation"
    ];

    [Theory]
    [MemberData(nameof(BadCommands))]
    public void Commands_that_break_the_rules_are_refused(string what) {
        var (state, ids, track, other, region) = Project();
        var note = region.Notes[0].Id;
        state = Apply(state, ids, new AddTrackCommand("Audio", Kind: TrackKind.Audio), new AddSceneCommand());
        var audio = state.Tracks[2].Id;
        EditCommand command = what switch {
            "factory id" => new SetStaffStyleCommand(
                new("factory.mine", "Mine", [new(Clef.Treble, 0, null, 1)], null, 0, 0, false)
            ),
            "dynamic text" => new AddScoreSymbolsCommand([new(ScoreSymbolKind.Dynamic, track, Tick: 0, Text: "loud")]),
            "slur at ticks" => new AddScoreSymbolsCommand([new(ScoreSymbolKind.Slur, track, Tick: 0, EndTick: 960)]),
            "articulation at a tick" => new AddScoreSymbolsCommand([new(ScoreSymbolKind.Staccato, track, Tick: 0)]),
            "wedge without end" => new AddScoreSymbolsCommand([new(ScoreSymbolKind.Crescendo, track, Tick: 0)]),
            "note of another track" => new AddScoreSymbolsCommand([new(ScoreSymbolKind.Accent, other, note)]),
            "two staves no split" => new SetStaffStyleCommand(
                new("sts_1", "Grand", [new(Clef.Treble, 0, null, 1), new(Clef.Bass, 0, null, 1)], null, 0, 0, true)
            ),
            "grid" => new SetRegionNotationCommand([region.Id], 100),
            "drum map without percussion" => new SetStaffStyleCommand(
                new("sts_1", "Kit", [new(Clef.Treble, 0, null, 1)], [new(36, 0, Notehead.Normal, "Kick")], 0, 0, false)
            ),
            "verse" => new SetLyricsCommand(9, [new(note, "la")]),
            "chord alteration" => new AddChordSymbolsCommand([new(track, 0, new(PitchStep.C, 3), string.Empty)]),
            "layout margins" => new SetScoreLayoutCommand(ScoreLayout.Default with { Margins = new(100, 100, 15, 15) }),
            "audio track style" => new AssignStaffStyleCommand([audio], StaffStyleIds.Treble),
            "set track twice" => new SetScoreSetCommand(new("scs_1", "Set", [track, track])),
            _ => new SetCellCommand(
                track,
                state.LiveLoops.Scenes[0].Id,
                new(Midi: region with { Notation = RegionNotation.Default with { MaxDots = 7 } })
            )
        };
        Assert.Throws<EditException>(() => Result(state, ids, command));
    }

    [Fact]
    public void A_cell_keeps_notation_other_than_the_default() {
        var (state, ids, track, _, region) = Project();
        state = Apply(state, ids, new AddSceneCommand());
        var notation = RegionNotation.Default with { MaxDots = 2, DisplayQuantizeTicks = 480 };
        state = Apply(
            state,
            ids,
            new SetCellCommand(track, state.LiveLoops.Scenes[0].Id, new(Midi: region with { Notation = notation }))
        );
        Assert.Equal(notation, state.LiveLoops.Cells[0].Content.Midi!.Notation);
    }

    /// <summary>The project with a staccato and a lyric on its first note, a slur from its first to its third.</summary>
    static (ProjectState State, SequentialIds Ids, string Track, string Other, MidiRegion Region) Attached() {
        var (state, ids, track, other, region) = Project();
        state = Apply(
            state,
            ids,
            new AddScoreSymbolsCommand(
                [
                    new(ScoreSymbolKind.Staccato, track, region.Notes[0].Id),
                    new(ScoreSymbolKind.Slur, track, region.Notes[0].Id, EndNoteId: region.Notes[2].Id),
                    new(ScoreSymbolKind.Accent, track, region.Notes[3].Id),
                    new(ScoreSymbolKind.Dynamic, track, Tick: 0, Text: "p")
                ]
            ),
            new SetLyricsCommand(1, [new(region.Notes[0].Id, "la")]),
            new AddChordSymbolsCommand([new(track, 0, new(PitchStep.C, 0), string.Empty)]),
            new SetScoreSetCommand(new("scs_1", "All", [track, other])),
            new SetScoreLayoutCommand(ScoreLayout.Default with { InstrumentNames = [new(track, "Piano", "Pno.")] })
        );
        return (state, ids, track, other, state.MidiRegions[region.Id]);
    }

    [Fact]
    public void A_notes_symbols_and_lyrics_go_with_it_and_come_back_on_undo() {
        var (state, ids, _, _, region) = Attached();
        var result = Result(state, ids, new DeleteNotesCommand(region.Id, [region.Notes[0].Id]));
        Assert.Equal(
            [ScoreSymbolKind.Accent, ScoreSymbolKind.Dynamic],
            result.State.ScoreSymbols.Values.Select(static s => s.Kind).Order()
        );
        Assert.Empty(result.State.Lyrics);
        Assert.Contains(result.Forward, static o => o is LyricRemoved);
        Same(state, result.State.Apply(result.Inverse));

        // The slur's end note alone takes the slur with it.
        result = Result(state, ids, new DeleteNotesCommand(region.Id, [region.Notes[2].Id]));
        Assert.DoesNotContain(result.State.ScoreSymbols.Values, static s => s.Kind == ScoreSymbolKind.Slur);
        Assert.Equal(3, result.State.ScoreSymbols.Count);
        Same(state, result.State.Apply(result.Inverse));
    }

    [Fact]
    public void Deleting_a_region_or_a_track_takes_its_score_content() {
        var (state, ids, track, other, region) = Attached();
        var result = Result(state, ids, new DeleteRegionsCommand([region.Id]));
        Assert.Equal(ScoreSymbolKind.Dynamic, Assert.Single(result.State.ScoreSymbols.Values).Kind);
        Assert.Empty(result.State.Lyrics);
        Same(state, result.State.Apply(result.Inverse));

        result = Result(state, ids, new RemoveTrackCommand(track));
        Assert.Empty(result.State.ScoreSymbols);
        Assert.Empty(result.State.Lyrics);
        Assert.Empty(result.State.ChordSymbols);
        Assert.Equal([other], result.State.ScoreSets["scs_1"].TrackIds);
        Assert.Empty(result.State.ScoreLayout.InstrumentNames);
        Same(state, result.State.Apply(result.Inverse));
    }

    [Fact]
    public void A_notes_symbols_follow_it_to_another_track_and_through_alternatives() {
        var (state, ids, track, other, region) = Attached();
        var moved = Apply(state, ids, new MoveRegionCommand(region.Id, 0, other));
        Assert.All(
            moved.ScoreSymbols.Values.Where(static s => s.NoteId is not null),
            s => Assert.Equal(other, s.TrackId)
        );
        Assert.Equal(track, moved.ScoreSymbols.Values.Single(static s => s.Kind == ScoreSymbolKind.Dynamic).TrackId);

        // A new alternative puts the notes away: their symbols wait with them, and come back with the alternative.
        var alternated = Apply(state, ids, new NewTrackAlternativeCommand(track));
        Assert.Empty(alternated.MidiRegionsOf(track));
        Assert.Equal(state.ScoreSymbols.Count, alternated.ScoreSymbols.Count);
        Assert.Single(alternated.Lyrics);
        var first = alternated.FindTrack(track)!.Alternatives[0].Id;
        var back = Apply(alternated, ids, new SelectTrackAlternativeCommand(track, first));
        Assert.Equal(state.ScoreSymbols.Values, back.ScoreSymbols.Values);
    }

    [Fact]
    public void Quantising_keeps_the_notes_and_their_symbols() {
        var (state, ids, _, _, region) = Attached();
        var quantised = Apply(state, ids, new QuantizeNotesCommand(region.Id, 480, 1, 0));
        Assert.Equal(state.ScoreSymbols.Values, quantised.ScoreSymbols.Values);
    }

    [Fact]
    public void Moving_score_notes_steps_through_the_key_and_undoes_transposition() {
        var (state, ids, track, other, region) = Project();
        // E4 one step up is F4 in C major; in G major F♯4.
        var e = region.Notes[2];
        var moved = Apply(state, ids, new MoveScoreNotesCommand(region.Id, [e.Id], 1));
        Assert.Equal(65, moved.MidiRegions[region.Id].Notes.Single(n => n.Id == e.Id).Pitch);
        var g = Apply(state, ids, new SetKeysCommand([new(0, 7, KeyMode.Major)]));
        moved = Apply(g, ids, new MoveScoreNotesCommand(region.Id, [e.Id], 1));
        Assert.Equal(66, moved.MidiRegions[region.Id].Notes.Single(n => n.Id == e.Id).Pitch);
        // A forced flat, two steps down: C4 → A♭3; and the start moves by the ticks given.
        var c = region.Notes[0];
        moved = Apply(state, ids, new MoveScoreNotesCommand(region.Id, [c.Id], -2, 240, Accidental.Flat));
        var note = moved.MidiRegions[region.Id].Notes.Single(n => n.Id == c.Id);
        Assert.Equal((56, 240L), (note.Pitch, note.StartTick));
        // On a B♭ clarinet staff, written D4 (concert C4) one step up is written E4: concert D4.
        state = Apply(state, ids, new AssignStaffStyleCommand([track], StaffStyleIds.ClarinetBb));
        moved = Apply(state, ids, new MoveScoreNotesCommand(region.Id, [c.Id], 1));
        Assert.Equal(62, moved.MidiRegions[region.Id].Notes.Single(n => n.Id == c.Id).Pitch);
        // One undo step.
        var result = Result(state, ids, new MoveScoreNotesCommand(region.Id, [c.Id, e.Id], 3, -0));
        Same(state, result.State.Apply(result.Inverse));
        _ = other;
    }

    [Fact]
    public void A_score_note_lands_at_its_staff_position_in_a_region() {
        var (state, ids, track, other, region) = Project();
        // The Part box at beat 3 of bar 2, on the treble staff's middle line (B4): a new region of bar 2 on the other
        // track (no region there), velocity 80, channel 1.
        var result = Result(state, ids, new AddScoreNoteCommand(other, 3840 + 1920, 960, 4));
        var added = result.State.MidiRegionsOf(other).Single();
        Assert.Equal((3840L, 3840L), (added.StartTick, added.LengthTicks));
        Assert.Equal(new(state.LastEventId + 1, 71, 80, 1, 1920, 960, 0), Assert.Single(added.Notes));
        Same(state, result.State.Apply(result.Inverse));

        // On the piano's bass staff (staff 1), the top line is A3; into the region at the tick.
        var bass = Apply(state, ids, new AddScoreNoteCommand(track, 480, 240, 8, 1, Velocity: 100));
        Assert.Contains(
            bass.MidiRegions[region.Id].Notes,
            static n => n is { Pitch: 57, StartTick: 480, Velocity: 100 }
        );

        // A drum staff maps positions to its drum map's pitches.
        var drums = Apply(state, ids, new AssignStaffStyleCommand([track], StaffStyleIds.Drums));
        drums = Apply(drums, ids, new AddScoreNoteCommand(track, 0, 240, 5));
        Assert.Contains(drums.MidiRegions[region.Id].Notes, static n => n is { Pitch: 38, StartTick: 0 });

        // A sharp forced on the bottom line (E4): F4.
        var sharp = Apply(state, ids, new AddScoreNoteCommand(track, 0, 240, 0, Accidental: Accidental.Sharp));
        Assert.Contains(
            sharp.MidiRegions[region.Id].Notes,
            static n => n is { Pitch: 65, StartTick: 0, LengthTicks: 240 }
        );
        Assert.Throws<EditException>(() => Result(state, ids, new AddScoreNoteCommand(track, 0, 240, 0, 2)));
        Assert.Throws<EditException>(() =>
            Edits.Apply(state, new AddScoreNoteCommand(track, 0, 240, 0), ids, RandomProjects.Plugins)
        );
    }

    [Fact]
    public void Importing_tracks_gives_real_ids_and_is_one_step() {
        var (state, ids, _, _, _) = Project();
        var content = new ImportedContent(
            [
                new(
                    "p1",
                    "Violin",
                    TrackKind.Instrument,
                    "style-a",
                    [
                        new(
                            "r1",
                            "x",
                            "Violin",
                            3840,
                            7680,
                            ProjectDefaults.Playback,
                            [new(1, 67, 90, 1, 0, 960, 0), new(2, 69, 90, 1, 960, 960, 0)],
                            [new(3, ControlKind.ProgramChange, 1, 0, 40, 0)],
                            false,
                            RegionNotation.Default with { DisplayQuantizeTicks = 240 }
                        )
                    ],
                    []
                ),
                new("p2", "Viola", TrackKind.Instrument, StaffStyleIds.Viola, [], [])
            ],
            [new(0, 90, TempoCurve.Step)],
            [new(0, 3, 4)],
            [new(0, 2, KeyMode.Major)],
            [new("m", "A", 3840, 0, null, "Letter A")],
            new(
                [new("style-a", "Violin", [new(Clef.Treble, 0, null, 2)], null, 0, 0, false)],
                [
                    new("s", ScoreSymbolKind.Slur, "p1", 1, EndNoteId: 2),
                    new("t", ScoreSymbolKind.TempoText, "p2", Tick: 0, Text: "Allegro")
                ],
                [new("l", 2, 1, "la", Syllabic.Single, false)],
                [new("c", "p1", 3840, new(PitchStep.D, 0), "m", null, false)],
                "Quartet",
                "Someone"
            )
        );
        var result = Result(state, ids, new ImportTracksCommand(content));
        var after = result.State;
        Assert.Equal(["Piano", "Clarinet", "Violin", "Viola"], after.Tracks.Select(static t => t.Name));
        var violin = after.Tracks[2];
        var style = Assert.Single(after.StaffStyles.Values);
        Assert.Equal(style.Id, violin.StaffStyleId);
        Assert.StartsWith("sts_", style.Id, StringComparison.Ordinal);
        Assert.Equal(StaffStyleIds.Viola, after.Tracks[3].StaffStyleId);
        var region = after.MidiRegionsOf(violin.Id).Single();
        Assert.Equal(240, region.Notation.DisplayQuantizeTicks);
        Assert.All(region.Notes, n => Assert.True(n.Id > state.LastEventId));
        var slur = after.ScoreSymbols.Values.Single(static s => s.Kind == ScoreSymbolKind.Slur);
        Assert.Equal(
            (violin.Id, region.Notes[0].Id, region.Notes[1].Id),
            (slur.TrackId, slur.NoteId!.Value, slur.EndNoteId!.Value)
        );
        Assert.Equal(
            after.Tracks[3].Id,
            after.ScoreSymbols.Values.Single(static s => s.Kind == ScoreSymbolKind.TempoText).TrackId
        );
        Assert.Equal(region.Notes[1].Id, Assert.Single(after.Lyrics.Values).NoteId);
        Assert.Equal(violin.Id, Assert.Single(after.ChordSymbols.Values).TrackId);
        Assert.Equal(("Quartet", "Someone"), (after.ScoreLayout.Title, after.ScoreLayout.Composer));
        Assert.Equal(90, after.Settings.TempoEvents![0].Bpm);
        Assert.Equal(new(3, 4), after.Settings.Signature);
        Assert.Equal(2, after.Settings.Keys![0].Tonic);
        Assert.Equal(("A", "Letter A"), after.Markers.Values.Select(static m => (m.Name, m.Text)).Single());
        Same(state, after.Apply(result.Inverse));

        // A reference to something the content does not hold is refused.
        Assert.Throws<EditException>(() =>
            Result(
                state,
                ids,
                new ImportTracksCommand(
                    content with {
                        Score = content.Score! with { Lyrics = [new("l", 99, 1, "x", Syllabic.Single, false)] }
                    }
                )
            )
        );
        Assert.Throws<EditException>(() =>
            Result(
                state,
                ids,
                new ImportTracksCommand(content with { Tracks = [content.Tracks[0] with { StaffStyleId = "nope" }] })
            )
        );
    }

    [Fact]
    public void Score_element_ids_map_back_to_notes_and_symbols() {
        Assert.Equal("n42", ScoreIds.Note(42));
        Assert.Equal("n42-3", ScoreIds.Note(42, 3));
        Assert.Equal("c7-2", ScoreIds.Chord(7, 2));
        Assert.Equal("ssym_abc", ScoreIds.Symbol("sym_abc"));
        Assert.Equal("rtrk_1-4-0-1-2", ScoreIds.Rest("trk_1", 4, 0, 1, 2));
        Assert.Equal(42, ScoreIds.NoteIdOf("n42"));
        Assert.Equal(42, ScoreIds.NoteIdOf("n42-3"));
        Assert.Null(ScoreIds.NoteIdOf("c42"));
        Assert.Null(ScoreIds.NoteIdOf("n"));
        Assert.Null(ScoreIds.NoteIdOf("nx"));
        Assert.Null(ScoreIds.NoteIdOf("n042"));
        Assert.Null(ScoreIds.NoteIdOf("n42-"));
        Assert.Null(ScoreIds.NoteIdOf("n42-x"));
        Assert.Equal("sym_abc", ScoreIds.SymbolIdOf("ssym_abc"));
        Assert.Null(ScoreIds.SymbolIdOf("s"));
        Assert.Null(ScoreIds.SymbolIdOf("n42"));
    }
}
