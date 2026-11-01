using Anode.Contracts;
using Anode.Interchange.MusicXml;
using static Anode.Interchange.Tests.MusicXml.MusicXmlRoundTripTests;

namespace Anode.Interchange.Tests.MusicXml;

/// <summary>Hand-written MusicXML files (Fixtures/) imported with exact expectations.</summary>
public sealed class MusicXmlFixtureTests {
    internal static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "MusicXml", "Fixtures", name));

    [Theory]
    [InlineData("string-quartet.musicxml")]
    [InlineData("piano-lyrics-chords.musicxml")]
    [InlineData("grace-notes.musicxml")]
    public void The_hand_written_4_0_fixtures_are_valid(string name) =>
        Assert.Empty(MusicXmlSchema.Errors(Fixture(name)));

    [Fact]
    public void The_string_quartet_becomes_four_tracks_with_their_notes() {
        var content = MusicXmlReader.Read(Fixture("string-quartet.musicxml"));
        Assert.Equal(
            [
                ("Violin I", StaffStyleIds.Treble), ("Violin II", StaffStyleIds.Treble), ("Viola", StaffStyleIds.Viola),
                ("Violoncello", StaffStyleIds.Cello)
            ],
            content.Tracks.Select(static t => (t.Name, t.StaffStyleId))
        );
        Assert.All(content.Tracks, static t => Assert.Equal(13440, Assert.Single(t.MidiRegions).LengthTicks));
        Assert.Equal(
            [
                (71, 10560L, 2880L), (72, 9600, 960), (74, 0, 1920), (74, 8640, 960), (76, 5760, 2880), (78, 1920, 960),
                (78, 4800, 960), (79, 4480, 320), (81, 2880, 960), (81, 4160, 320), (83, 3840, 320)
            ],
            Notes(content.Tracks[0].MidiRegions[0])
        );
        Assert.Equal(
            [(62, 10560L, 2880L), (64, 7680, 2880), (66, 5760, 1920), (67, 3840, 1920), (69, 0, 3840)],
            Notes(content.Tracks[1].MidiRegions[0])
        );
        Assert.Equal(
            [
                (55, 10560L, 2880L), (57, 9600, 960), (59, 8640, 960), (60, 7680, 960), (62, 3840, 3840),
                (64, 1920, 1920), (66, 0, 1920)
            ],
            Notes(content.Tracks[2].MidiRegions[0])
        );
        Assert.Equal(
            [(43, 3840L, 1920L), (43, 11520, 1920), (45, 5760, 1920), (48, 7680, 3840), (50, 0, 3840)],
            Notes(content.Tracks[3].MidiRegions[0])
        );

        Assert.Equal([new(0, 4, 4), new SignatureEvent(7680, 3, 4)], content.Signatures!);
        Assert.Equal([new(0, 2, KeyMode.Major), new KeyEvent(7680, 7, KeyMode.Major)], content.Keys!);
        Assert.Equal([new TempoEvent(0, 72, TempoCurve.Step)], content.TempoEvents!);
        Assert.Equal(("Little Quartet", "Anode Tests"), (content.Score!.Title, content.Score.Composer));
    }

    [Fact]
    public void The_string_quartet_keeps_its_symbols() {
        var content = MusicXmlReader.Read(Fixture("string-quartet.musicxml"));
        var tracks = content.Tracks.Select(static t => t.Id).ToList();
        var notes = content.Tracks.SelectMany(static t => t.MidiRegions[0].Notes).ToDictionary(static n => n.Id);
        string Note(long? id) => id is { } n ? $"{notes[n].Pitch}@{notes[n].StartTick}" : "-";
        Assert.Equal(
            [
                "0 dynamic p 0", "0 fermata 71@10560", "0 slur 74@0 81@2880", "0 staccato 72@9600",
                "0 staccato 74@8640",
                "0 tempoText Andante 0", "1 crescendo 0-3840", "1 dynamic f 10560", "1 dynamic p 0",
                "2 dynamic p 0", "2 slur 60@7680 57@9600", "3 dynamic p 0"
            ],
            content.Score!.Symbols.Select(s => $"{tracks.IndexOf(s.TrackId)} {Describe(s, Note)}")
                .Order(StringComparer.Ordinal)
        );
    }

    [Fact]
    public void Notes_take_the_velocity_of_their_dynamic() {
        var content = MusicXmlReader.Read(Fixture("string-quartet.musicxml"));
        var violin2 = content.Tracks[1].MidiRegions[0].Notes;
        Assert.Equal([50, 50, 50, 50, 96], violin2.OrderBy(static n => n.StartTick).Select(static n => n.Velocity));
    }

    [Fact]
    public void The_piano_piece_has_two_voices_lyrics_in_verses_and_chord_symbols() {
        var content = MusicXmlReader.Read(Fixture("piano-lyrics-chords.musicxml"));
        var track = Assert.Single(content.Tracks);
        Assert.Equal(StaffStyleIds.Piano, track.StaffStyleId);
        var region = track.MidiRegions[0];
        Assert.Equal(
            [
                (41, 3840L, 1920L), (48, 0, 3840), (48, 5760, 1920), (64, 0, 1920), (64, 1920, 1920), (64, 5760, 1920),
                (65, 3840, 1920), (72, 0, 960), (72, 960, 960), (79, 1920, 960), (79, 2880, 960), (79, 5760, 1920),
                (81, 3840, 960), (81, 4800, 960)
            ],
            Notes(region)
        );

        var at = region.Notes.ToDictionary(static n => n.Id);
        var score = content.Score!;
        Assert.Equal(
            [
                (1, "Twin", Syllabic.Begin, false, 72, 0L), (2, "How", Syllabic.Single, false, 72, 0),
                (1, "kle", Syllabic.End, false, 72, 960), (2, "I", Syllabic.Single, false, 72, 960),
                (1, "twin", Syllabic.Begin, false, 79, 1920), (1, "kle", Syllabic.End, false, 79, 2880),
                (1, "lit", Syllabic.Begin, false, 81, 3840), (1, "tle", Syllabic.End, false, 81, 4800),
                (1, "star", Syllabic.Single, true, 79, 5760)
            ],
            score.Lyrics.Select(l => (l.Verse, l.Text, l.Syllabic, l.Extender, at[l.NoteId].Pitch,
                    at[l.NoteId].StartTick)
            )
        );
        Assert.Equal(
            [
                (0L, PitchStep.C, string.Empty, (PitchStep?)null), (1920, PitchStep.A, "m7", null),
                (3840, PitchStep.F, string.Empty, null),
                (5760, PitchStep.C, string.Empty, PitchStep.E)
            ],
            score.Chords.Select(static c => (c.Tick, c.Root.Step, c.Quality, c.Bass?.Step))
        );
        Assert.All(score.Chords, c => Assert.Equal(track.Id, c.TrackId));
        var staccato = Assert.Single(score.Symbols, static s => s.Kind == ScoreSymbolKind.Staccato);
        Assert.Equal(
            (79, 2880L, ScorePlacement.Above),
            (at[staccato.NoteId!.Value].Pitch, at[staccato.NoteId.Value].StartTick, staccato.Placement)
        );
        var dynamic = Assert.Single(score.Symbols, static s => s.Kind == ScoreSymbolKind.Dynamic);
        Assert.Equal(("mp", 0L, 1), (dynamic.Text, dynamic.Tick!.Value, dynamic.Staff!.Value));
        Assert.Equal("Twinkle", score.Title);
        Assert.Null(content.TempoEvents);
        Assert.Equal([new KeyEvent(0, 0, KeyMode.Major)], content.Keys!);
    }

    [Fact]
    public void Grace_notes_are_short_notes_just_before_their_beat() {
        var content = MusicXmlReader.Read(Fixture("grace-notes.musicxml"));
        Assert.Equal(
            [(72, 0L, 960L), (72, 2880, 960), (74, 0, 120), (76, 720, 120), (77, 840, 120), (79, 960, 1920)],
            Notes(content.Tracks[0].MidiRegions[0])
        );
    }

    [Fact]
    public void A_compressed_file_is_read_through_its_container() {
        var content = MusicXmlReader.Read(Fixture("song.mxl"));
        var track = Assert.Single(content.Tracks);
        Assert.Equal(("Voice", StaffStyleIds.Treble), (track.Name, track.StaffStyleId));
        Assert.Equal([(72, 0L, 960L), (75, 960, 960)], Notes(track.MidiRegions[0]));
        Assert.Equal([new KeyEvent(0, 0, KeyMode.Minor)], content.Keys!);
        Assert.Equal([new SignatureEvent(0, 2, 4)], content.Signatures!);
        Assert.Equal(["Sing", "low"], content.Score!.Lyrics.Select(static l => l.Text));
        Assert.Equal(("m", "Song in a Container"), (Assert.Single(content.Score.Chords).Quality, content.Score.Title));
    }

    [Fact]
    public void A_3_1_file_with_a_pickup_and_a_transposing_part_reads_at_concert_pitch_on_the_bar_grid() {
        var content = MusicXmlReader.Read(Fixture("clarinet-3.1.xml"));
        var track = Assert.Single(content.Tracks);
        Assert.Equal(("Clarinet in B♭", StaffStyleIds.ClarinetBb), (track.Name, track.StaffStyleId));
        // The one-beat pickup is padded to a full 3/4 bar: its note sits on the bar's last beat.
        Assert.Equal(
            [(72, 1920L, 960L), (74, 2880, 1920), (76, 4800, 960), (77, 5760, 2880)],
            Notes(track.MidiRegions[0])
        );
        Assert.Equal(8640, track.MidiRegions[0].LengthTicks);
        Assert.Equal([new SignatureEvent(0, 3, 4)], content.Signatures!);
        Assert.Equal([new KeyEvent(0, 0, KeyMode.Major)], content.Keys!);
        Assert.Equal([new TempoEvent(0, 90, TempoCurve.Step)], content.TempoEvents!);
        var symbols = content.Score!.Symbols;
        Assert.Equal(
            [(ScoreSymbolKind.Text, (long?)2880), (ScoreSymbolKind.Slur, null)],
            symbols.Select(static s => (s.Kind, s.Tick)).OrderBy(static s => s.Kind)
        );
    }

    static string Describe(ScoreSymbol s, Func<long?, string> note) {
        var name = char.ToLowerInvariant(s.Kind.ToString()[0]) + s.Kind.ToString()[1..];
        var text = s.Text is null ? string.Empty : $" {s.Text}";
        if (s.NoteId is not null) {
            return $"{name}{text} {note(s.NoteId)}{(s.EndNoteId is null ? string.Empty : " " + note(s.EndNoteId))}";
        }

        return $"{name}{text} {s.Tick}{(s.EndTick is { } e ? $"-{e}" : string.Empty)}";
    }
}
