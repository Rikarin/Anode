using Anode.Contracts;
using Anode.Interchange.MusicXml;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using static Anode.Interchange.Tests.MusicXml.Views;

namespace Anode.Interchange.Tests.MusicXml;

/// <summary>Export a hand-made score view, validate it against the XSD, read it back: the same music.</summary>
public sealed class MusicXmlRoundTripTests {
    const string Piano = "trk_piano";

    /// <summary>
    ///     Two bars for piano: a melody with a triplet under a slur, a tie across the bar line (not drawn), a second voice,
    ///     a chord in the bass, lyrics, chord symbols, dynamics, a wedge, text, tempo text, pedal, a meter and key change,
    ///     a whole-bar rest in 3/4 and a final barline.
    /// </summary>
    internal static ScoreView PianoView() {
        var measures = new[] {
            Measure(1, 0, fifths: 1, tempos: [new(0, 100)], rehearsals: [new(0, "mrk_a", "A")]),
            Measure(2, 3840, 3, 4, -1, true, barline: Barline.Final)
        };
        var triplet = new ScoreTuplet(1, 3, 2);
        var bar1 = Bar(
            new ScoreStaffMeasure(
                Clef.Treble,
                [
                    Voice(
                        1,
                        Event(0, 960, NoteValue.Quarter, [Note(1, 72)], symbols: ["sym_accent", "sym_tempo"], lyrics: ["lyr_1"], chords: ["chd_c"]),
                        Event(960, 960, NoteValue.Quarter, [Note(2, 74)], lyrics: ["lyr_2"]),
                        Event(1920, 320, NoteValue.Eighth, [Note(3, 76)], tuplet: triplet, beam: 1, symbols: ["sym_slur"]),
                        Event(2240, 320, NoteValue.Eighth, [Note(4, 78)], tuplet: triplet, beam: 1),
                        Event(2560, 320, NoteValue.Eighth, [Note(5, 79)], tuplet: triplet, beam: 1),
                        Event(2880, 960, NoteValue.Quarter, [Note(6, 71, tieStart: true)], symbols: ["sym_tie"])
                    ),
                    Voice(
                        2,
                        Event(0, 1920, NoteValue.Half, [Note(7, 67)], stem: StemDirection.Down),
                        Rest(1920, 1920, NoteValue.Half)
                    )
                ]
            ),
            new ScoreStaffMeasure(
                Clef.Bass,
                [Voice(1, Event(0, 3840, NoteValue.Whole, [Note(8, 48), Note(9, 55)], symbols: ["sym_mf", "sym_cresc", "sym_ped"]))]
            )
        );
        var bar2 = Bar(
            Staff(
                Voice(
                    1,
                    Event(3840, 960, NoteValue.Quarter, [Note(6, 71, 2, tieStop: true)], symbols: ["sym_dolce"]),
                    Event(4800, 1440, NoteValue.Quarter, [Note(10, 69, accidental: Accidental.Natural)], 1, symbols: ["sym_fermata", "sym_sfz"], chords: ["chd_g7"]),
                    Event(6240, 480, NoteValue.Eighth, [Note(11, 67)], stem: StemDirection.Down)
                )
            ),
            Staff(Voice(1, Rest(3840, 2880, NoteValue.Whole, symbols: ["sym_pedup"])))
        );
        var part = Part(
            Piano,
            "Piano",
            [new(Clef.Treble, 0, 60, 2), new(Clef.Bass, 0, null, 1)],
            [bar1, bar2],
            StaffStyleIds.Piano,
            shortName: "Pno."
        );
        ScoreSymbol[] symbols = [
            new("sym_accent", ScoreSymbolKind.Accent, Piano, 1),
            new("sym_tempo", ScoreSymbolKind.TempoText, Piano, Tick: 0, Text: "Allegro", Staff: 0),
            new("sym_slur", ScoreSymbolKind.Slur, Piano, 3, EndNoteId: 5),
            new("sym_tie", ScoreSymbolKind.TieHidden, Piano, 6),
            new("sym_mf", ScoreSymbolKind.Dynamic, Piano, Tick: 0, Text: "mf", Staff: 1),
            new("sym_cresc", ScoreSymbolKind.Crescendo, Piano, Tick: 0, EndTick: 3840, Staff: 1),
            new("sym_ped", ScoreSymbolKind.PedalDown, Piano, Tick: 0, Staff: 1),
            new("sym_dolce", ScoreSymbolKind.Text, Piano, Tick: 3840, Text: "dolce", Placement: ScorePlacement.Below, Staff: 0),
            new("sym_fermata", ScoreSymbolKind.Fermata, Piano, 10),
            new("sym_sfz", ScoreSymbolKind.Dynamic, Piano, 10, Text: "sfz"),
            new("sym_pedup", ScoreSymbolKind.PedalUp, Piano, Tick: 3840, Staff: 1)
        ];
        LyricSyllable[] lyrics = [new("lyr_1", 1, 1, "Hel", Syllabic.Begin, false), new("lyr_2", 2, 1, "lo", Syllabic.End, true)];
        ChordSymbol[] chords = [
            new("chd_c", Piano, 0, new(PitchStep.C, 0), "", null, false),
            new("chd_g7", Piano, 4800, new(PitchStep.G, 0), "7", new(PitchStep.B, 0), true)
        ];
        return View(measures, [part], symbols, lyrics, chords);
    }

    [Fact]
    public void A_piano_score_validates_and_reads_back_with_the_same_notes() {
        var file = MusicXmlWriter.Write(PianoView(), false);
        MusicXmlSchema.AssertValid(file);

        var content = MusicXmlReader.Read(file);
        var track = Assert.Single(content.Tracks);
        Assert.Equal(("Piano", StaffStyleIds.Piano, TrackKind.Instrument), (track.Name, track.StaffStyleId, track.Kind));
        var region = Assert.Single(track.MidiRegions);
        Assert.Equal((0L, 3840L + 2880), (region.StartTick, region.LengthTicks));
        Assert.Equal(
            [
                (48, 0L, 3840L), (55, 0, 3840), (67, 0, 1920), (67, 6240, 480), (69, 4800, 1440), (71, 2880, 1920), (72, 0, 960),
                (74, 960, 960), (76, 1920, 320), (78, 2240, 320), (79, 2560, 320)
            ],
            Notes(region)
        );
    }

    [Fact]
    public void The_piano_score_keeps_its_symbols_lyrics_chords_and_project_events() {
        var content = MusicXmlReader.Read(MusicXmlWriter.Write(PianoView(), false));
        var region = content.Tracks[0].MidiRegions[0];
        var trackId = content.Tracks[0].Id;
        var at = region.Notes.ToDictionary(n => n.Id, n => (n.Pitch, n.StartTick));
        var score = content.Score!;
        Assert.All(score.Symbols, s => Assert.Equal(trackId, s.TrackId));
        Assert.Equal(
            [
                "accent@72/0", "crescendo 0-3840 staff 1", "dynamic mf 0 staff 1", "dynamic sfz@69/4800", "fermata@69/4800",
                "pedalDown 0 staff 1", "pedalUp 3840 staff 1", "slur@76/1920-79/2560",
                "tempoText Allegro 0 staff 0", "text dolce 3840 staff 0 below", "tieHidden@71/2880"
            ],
            score.Symbols.Select(s => Describe(s, at)).Order(StringComparer.Ordinal)
        );
        Assert.Equal(
            [("Hel", Syllabic.Begin, false, 72, 0L), ("lo", Syllabic.End, true, 74, 960L)],
            score.Lyrics.Select(l => (l.Text, l.Syllabic, l.Extender, at[l.NoteId].Pitch, at[l.NoteId].StartTick))
        );
        Assert.Equal(
            [(0L, "C", "", (string?)null, false), (4800, "G", "7", "B", true)],
            score.Chords.Select(c => (c.Tick, c.Root.Step.ToString(), c.Quality, c.Bass?.Step.ToString(), c.ShowGrid))
        );
        Assert.Equal(("Test Piece", "A. Composer"), (score.Title, score.Composer));
        Assert.Equal([new(0, 4, 4), new SignatureEvent(3840, 3, 4)], content.Signatures!);
        Assert.Equal([new(0, 7, KeyMode.Major), new KeyEvent(3840, 5, KeyMode.Major)], content.Keys!);
        Assert.Equal([new TempoEvent(0, 100, TempoCurve.Step)], content.TempoEvents!);
        Assert.Equal(("A", 0L), (Assert.Single(content.Markers!).Name, content.Markers![0].Tick));
        Assert.Empty(score.StaffStyles);
    }

    [Fact]
    public void Notes_take_their_velocity_from_the_dynamic_they_are_under() {
        var region = MusicXmlReader.Read(MusicXmlWriter.Write(PianoView(), false)).Tracks[0].MidiRegions[0];
        Assert.All(region.Notes, n => Assert.Equal(80, n.Velocity));
    }

    [Fact]
    public void Divisions_express_every_duration_including_tuplets_exactly() {
        var document = XDocument.Parse(Encoding.UTF8.GetString(MusicXmlWriter.Write(PianoView(), false)));
        var divisions = long.Parse(document.Descendants("divisions").Single().Value, CultureInfo.InvariantCulture);
        // A triplet eighth is a third of a quarter, an eighth a half: sixths.
        Assert.Equal(6, divisions);
        var triplets = document.Descendants("note").Where(n => n.Element("time-modification") is not null).ToList();
        Assert.Equal(3, triplets.Count);
        Assert.All(triplets, n => Assert.Equal("2", n.Element("duration")!.Value));
        Assert.Equal(["start", "stop"], document.Descendants("tuplet").Select(t => (string)t.Attribute("type")!));
        Assert.Equal(
            ["begin", "continue", "end"],
            triplets.Select(n => n.Element("beam")!.Value)
        );
    }

    [Fact]
    public void A_septuplet_of_sixteenths_gets_divisions_in_sevenths() {
        var measures = new[] { Measure(1, 0, 1, 4, barline: Barline.Final) };
        var tuplet = new ScoreTuplet(1, 7, 4);
        var events = Enumerable.Range(0, 7)
            .Select(i => Event(
                    (long)Math.Round(i * 960 / 7.0),
                    (long)Math.Round((i + 1) * 960 / 7.0) - (long)Math.Round(i * 960 / 7.0),
                    NoteValue.Sixteenth,
                    [Note(i + 1, 60 + i)],
                    tuplet: tuplet,
                    beam: 1
                )
            )
            .ToArray();
        var view = View(measures, [Part("trk_a", "Flute", [Treble], [Bar(Staff(Voice(1, events)))])]);
        var file = MusicXmlWriter.Write(view, false);
        MusicXmlSchema.AssertValid(file);
        var document = XDocument.Parse(Encoding.UTF8.GetString(file));
        Assert.Equal(0, long.Parse(document.Descendants("divisions").Single().Value, CultureInfo.InvariantCulture) % 7);
        var notes = Notes(MusicXmlReader.Read(file).Tracks[0].MidiRegions[0]);
        Assert.Equal(Enumerable.Range(0, 7).Select(i => (long)Math.Round(i * 960 / 7.0, MidpointRounding.AwayFromZero)), notes.Select(n => n.Tick));
        Assert.InRange(notes.Sum(n => n.Length), 959, 961);
    }

    [Fact]
    public void A_transposing_part_is_written_at_written_pitch_and_read_back_at_concert_pitch() {
        // B♭ clarinet: concert B♭4 (70) is written C5; concert F major is written G major.
        var measures = new[] { Measure(1, 0, fifths: -1, barline: Barline.Final) };
        var bar = Bar(Staff(Voice(1, Event(0, 3840, NoteValue.Whole, [Note(1, 70, transpose: 2)]))));
        var part = Part("trk_cl", "Clarinet in B♭", [new(Clef.Treble, 2, null, 1)], [bar], StaffStyleIds.ClarinetBb, 2);
        var file = MusicXmlWriter.Write(View(measures, [part], transposing: true), false);
        MusicXmlSchema.AssertValid(file);

        var document = XDocument.Parse(Encoding.UTF8.GetString(file));
        Assert.Equal(("C", "5"), (document.Descendants("step").Single().Value, document.Descendants("octave").Single().Value));
        Assert.Equal("1", document.Descendants("fifths").Single().Value);
        Assert.Equal(("-1", "-2"), (document.Descendants("diatonic").Single().Value, document.Descendants("chromatic").Single().Value));

        var content = MusicXmlReader.Read(file);
        Assert.Equal(StaffStyleIds.ClarinetBb, content.Tracks[0].StaffStyleId);
        Assert.Equal(70, Assert.Single(content.Tracks[0].MidiRegions[0].Notes).Pitch);
        Assert.Equal([new KeyEvent(0, 5, KeyMode.Major)], content.Keys!);
    }

    [Fact]
    public void A_concert_score_names_the_transposition_of_its_parts() {
        var measures = new[] { Measure(1, 0, barline: Barline.Final) };
        var bar = Bar(Staff(Voice(1, Event(0, 3840, NoteValue.Whole, [Note(1, 70, spelling: (PitchStep.B, -1))]))));
        var part = Part("trk_tp", "Trumpet in B♭", [new(Clef.Treble, 2, null, 1)], [bar], StaffStyleIds.TrumpetBb);
        var file = MusicXmlWriter.Write(View(measures, [part]), false);
        MusicXmlSchema.AssertValid(file);
        var document = XDocument.Parse(Encoding.UTF8.GetString(file));
        Assert.NotNull(document.Descendants("concert-score").SingleOrDefault());
        Assert.Equal("-2", document.Descendants("for-part").Descendants("chromatic").Single().Value);

        var content = MusicXmlReader.Read(file);
        Assert.Equal(StaffStyleIds.TrumpetBb, content.Tracks[0].StaffStyleId);
        Assert.Equal(70, Assert.Single(content.Tracks[0].MidiRegions[0].Notes).Pitch);
    }

    [Fact]
    public void A_drum_part_is_unpitched_with_instruments_and_noteheads() {
        var measures = new[] { Measure(1, 0, barline: Barline.Final) };
        var bar = Bar(
            Staff(
                Voice(1, Event(0, 1920, NoteValue.Half, [Note(1, 42, notehead: Notehead.Cross, staffPosition: 9)], 0, null, null, StemDirection.Up), Event(1920, 1920, NoteValue.Half, [Note(2, 42, notehead: Notehead.Cross, staffPosition: 9)])),
                Voice(2, Event(0, 3840, NoteValue.Whole, [Note(3, 36, staffPosition: -1)]))
            )
        );
        var part = Part("trk_dr", "Drums", [new(Clef.Percussion, 0, null, 2)], [bar], StaffStyleIds.Drums);
        var file = MusicXmlWriter.Write(View(measures, [part]), false);
        MusicXmlSchema.AssertValid(file);
        var document = XDocument.Parse(Encoding.UTF8.GetString(file));
        Assert.Equal(["Bass Drum 1", "Closed Hi-Hat"], document.Descendants("instrument-name").Select(e => e.Value));
        Assert.Equal(["x", "x"], document.Descendants("notehead").Select(e => e.Value));
        Assert.Equal(("G", "5"), (document.Descendants("display-step").First().Value, document.Descendants("display-octave").First().Value));

        var content = MusicXmlReader.Read(file);
        Assert.Equal(StaffStyleIds.Drums, content.Tracks[0].StaffStyleId);
        Assert.Equal([(36, 0L, 3840L), (42, 0, 1920), (42, 1920, 1920)], Notes(content.Tracks[0].MidiRegions[0]));
        Assert.All(content.Tracks[0].MidiRegions[0].Notes, n => Assert.Equal(10, n.Channel));
    }

    [Fact]
    public void Several_parts_are_bracketed_and_each_becomes_a_track() {
        var measures = new[] { Measure(1, 0, barline: Barline.Final) };
        ScorePart Single(string id, string name, int pitch, Staff staff, string style) =>
            Part(id, name, [staff], [Bar(Staff(Voice(1, Event(0, 3840, NoteValue.Whole, [Note(pitch, pitch)]))) with { Clef = staff.Clef })], style);
        var view = View(
            measures,
            [
                Single("trk_v", "Violin", 76, Treble, StaffStyleIds.Treble),
                Single("trk_va", "Viola", 60, new(Clef.Alto, 0, null, 1), StaffStyleIds.Viola),
                Single("trk_vc", "Violoncello", 48, new(Clef.Bass, 0, null, 1), StaffStyleIds.Cello),
                Single("trk_g", "Guitar", 52, new(Clef.Treble8vb, 0, null, 1), StaffStyleIds.Guitar)
            ]
        );
        var file = MusicXmlWriter.Write(view, false);
        MusicXmlSchema.AssertValid(file);
        var document = XDocument.Parse(Encoding.UTF8.GetString(file));
        Assert.Equal(["start", "stop"], document.Descendants("part-group").Select(g => (string)g.Attribute("type")!));

        var content = MusicXmlReader.Read(file);
        Assert.Equal(
            [("Violin", StaffStyleIds.Treble, 76), ("Viola", StaffStyleIds.Viola, 60), ("Violoncello", StaffStyleIds.Cello, 48), ("Guitar", StaffStyleIds.Guitar, 52)],
            content.Tracks.Select(t => (t.Name, t.StaffStyleId, t.MidiRegions[0].Notes.Single().Pitch))
        );
        Assert.Equal(4, content.Tracks.Select(t => t.Id).Distinct().Count());
        Assert.Equal(4, content.Tracks.SelectMany(t => t.MidiRegions[0].Notes).Select(n => n.Id).Distinct().Count());
    }

    [Fact]
    public void An_octave_line_and_wedges_on_notes_come_back_as_spans() {
        var measures = new[] { Measure(1, 0), Measure(2, 3840, barline: Barline.Final) };
        var bar1 = Bar(Staff(Voice(1, Event(0, 3840, NoteValue.Whole, [Note(1, 84)], symbols: ["sym_8va", "sym_dim"]))));
        var bar2 = Bar(Staff(Voice(1, Event(3840, 3840, NoteValue.Whole, [Note(2, 86)]))));
        ScoreSymbol[] symbols = [
            new("sym_8va", ScoreSymbolKind.OctaveUp, "trk_a", Tick: 0, EndTick: 7680),
            new("sym_dim", ScoreSymbolKind.Diminuendo, "trk_a", 1, EndNoteId: 2)
        ];
        var file = MusicXmlWriter.Write(View(measures, [Part("trk_a", "Flute", [Treble], [bar1, bar2])], symbols), false);
        MusicXmlSchema.AssertValid(file);
        var document = XDocument.Parse(Encoding.UTF8.GetString(file));
        Assert.Equal(["down", "stop"], document.Descendants("octave-shift").Select(o => (string)o.Attribute("type")!));

        var read = MusicXmlReader.Read(file).Score!.Symbols;
        Assert.Equal(
            [(ScoreSymbolKind.Diminuendo, 0L, 3840L), (ScoreSymbolKind.OctaveUp, 0, 7680)],
            read.Select(s => (s.Kind, s.Tick!.Value, s.EndTick!.Value)).OrderBy(s => s.Kind)
        );
    }

    [Fact]
    public void The_same_view_gives_the_same_bytes() {
        Assert.Equal(MusicXmlWriter.Write(PianoView(), false), MusicXmlWriter.Write(PianoView(), false));
        Assert.Equal(MusicXmlWriter.Write(PianoView(), true), MusicXmlWriter.Write(PianoView(), true));
    }

    [Fact]
    public void A_compressed_file_is_a_valid_container_that_reads_like_the_plain_file() {
        var mxl = MusicXmlWriter.Write(PianoView(), true);
        MusicXmlSchema.AssertValid(mxl);
        using (var zip = new ZipArchive(new MemoryStream(mxl), ZipArchiveMode.Read)) {
            Assert.Equal(["mimetype", "META-INF/container.xml", "score.musicxml"], zip.Entries.Select(e => e.FullName));
            Assert.Equal("application/vnd.recordare.musicxml", Encoding.ASCII.GetString(MusicXmlSchema.Entry(zip, "mimetype")));
            Assert.Equal(MusicXmlWriter.Write(PianoView(), false), MusicXmlSchema.Entry(zip, "score.musicxml"));
        }

        Assert.Equal(Notes(MusicXmlReader.Read(MusicXmlWriter.Write(PianoView(), false)).Tracks[0].MidiRegions[0]), Notes(MusicXmlReader.Read(mxl).Tracks[0].MidiRegions[0]));
    }

    [Fact]
    public void An_empty_score_still_validates() {
        var view = View([Measure(1, 0, barline: Barline.Final)], [Part("trk_a", "Flute", [Treble], [Bar(Staff())])], layout: ScoreLayout.Default);
        var file = MusicXmlWriter.Write(view, false);
        MusicXmlSchema.AssertValid(file);
        var content = MusicXmlReader.Read(file);
        Assert.Empty(content.Tracks[0].MidiRegions[0].Notes);
        Assert.Equal(3840, content.Tracks[0].MidiRegions[0].LengthTicks);
        Assert.Null(content.Score!.Title);
    }

    [Fact]
    public void A_view_without_parts_or_measures_is_written_as_one_empty_bar() {
        var file = MusicXmlWriter.Write(View([], []), false);
        MusicXmlSchema.AssertValid(file);
        Assert.Equal(3840, Assert.Single(MusicXmlReader.Read(file).Tracks).MidiRegions[0].LengthTicks);
    }

    internal static List<(int Pitch, long Tick, long Length)> Notes(MidiRegion region) =>
        [..region.Notes.Select(n => (n.Pitch, n.StartTick, n.LengthTicks)).Order()];

    static string Describe(ScoreSymbol s, Dictionary<long, (int Pitch, long Tick)> at) {
        var name = char.ToLowerInvariant(s.Kind.ToString()[0]) + s.Kind.ToString()[1..];
        if (s.NoteId is { } note) {
            var start = $"{name}{(s.Text is null ? "" : " " + s.Text)}@{at[note].Pitch}/{at[note].Tick}";
            return s.EndNoteId is { } end ? $"{start}-{at[end].Pitch}/{at[end].Tick}" : start;
        }

        var text = s.Text is null ? "" : " " + s.Text;
        var span = s.EndTick is { } e ? $"-{e}" : "";
        var staff = s.Staff is { } st ? $" staff {st}" : "";
        var placement = s.Placement is { } p ? " " + p.ToString().ToLowerInvariant() : "";
        return $"{name}{text} {s.Tick}{span}{staff}{placement}";
    }
}
