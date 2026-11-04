using Anode.Contracts;
using System.Globalization;
using System.Text;
using System.Xml;

namespace Anode.Notation;

/// <summary>
///     A score view as MEI 5 (docs/10-phase-9.md, workstream A; docs/notation/README.md, "MEI"): the layout as the
///     <c>scoreDef</c> (page size, margins, staff size) with the title block as <c>pgHead</c> and the footer as
///     <c>pgFoot</c>, parts as staff definitions (a part of several staves in a <c>staffGrp</c>, braced as the view says),
///     measures with their staves, layers and events (<c>note</c>, <c>chord</c>, <c>rest</c>, <c>mRest</c> for an
///     empty measure, inside <c>beam</c> and <c>tuplet</c>), articulations as <c>artic</c> in their note, lyrics as
///     <c>verse</c>/<c>syl</c>, and as control events: ties (<c>tie</c>), symbols (<c>dynam</c>, <c>hairpin</c>,
///     <c>slur</c>, <c>fermata</c>, <c>trill</c>, <c>octave</c>, <c>pedal</c>, <c>dir</c>, <c>tempo</c>, <c>reh</c>),
///     tempo marks, markers as <c>reh</c> and chord symbols as <c>harm</c>. Every element of the view carries its
///     <see cref="ScoreIds" /> as <c>xml:id</c>. Pitches are written (MEI octaves: C4 is MIDI 60); a percussion staff's
///     notes also carry their staff position as <c>loc</c>. A hidden tie is left out (MEI 5's tie has no visibility).
/// </summary>
public static class MeiWriter {
    const string Ns = "http://www.music-encoding.org/ns/mei";
    const string XmlNs = "http://www.w3.org/XML/1998/namespace";
    const string StaffName = "staff";

    /// <summary>The MEI document; the same view gives the same text, byte for byte.</summary>
    public static string Write(ScoreView view) {
        var text = new StringBuilder();
        text.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        text.Append(
            "<?xml-model href=\"https://music-encoding.org/schema/5.0/mei-all.rng\" type=\"application/xml\" schematypens=\"http://relaxng.org/ns/structure/1.0\"?>\n"
        );
        var settings = new XmlWriterSettings {
            OmitXmlDeclaration = true,
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.Replace,
            ConformanceLevel = ConformanceLevel.Document
        };
        using (var xml = XmlWriter.Create(text, settings)) {
            new Writer(xml, view).Document();
        }

        text.Append('\n');
        return text.ToString();
    }

    static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    static string D(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    sealed class Writer(XmlWriter xml, ScoreView view) {
        readonly Dictionary<string, ScoreSymbol> symbols =
            view.Symbols.ToDictionary(static s => s.Id, StringComparer.Ordinal);

        readonly Dictionary<string, LyricSyllable> lyrics =
            view.Lyrics.ToDictionary(static l => l.Id, StringComparer.Ordinal);

        readonly Dictionary<string, ChordSymbol> chords =
            view.Chords.ToDictionary(static c => c.Id, StringComparer.Ordinal);

        /// <summary>The notes whose ties are hidden.</summary>
        readonly HashSet<long> hiddenTies = view.Symbols
            .Where(static s => s.Kind == ScoreSymbolKind.TieHidden && s.NoteId is not null)
            .Select(static s => s.NoteId!.Value)
            .ToHashSet();

        /// <summary>Whether the staff being written is a percussion staff (its notes carry <c>loc</c>).</summary>
        bool percussion;

        /// <summary>Each part's first staff number (MEI staves count from 1 across the score).</summary>
        readonly int[] firstStaff = FirstStaves(view);

        public void Document() {
            xml.WriteStartElement("mei", Ns);
            xml.WriteAttributeString("meiversion", "5.0");
            Head();
            xml.WriteStartElement("music", Ns);
            xml.WriteStartElement("body", Ns);
            xml.WriteStartElement("mdiv", Ns);
            xml.WriteStartElement("score", Ns);
            ScoreDef();
            xml.WriteStartElement("section", Ns);
            for (var m = 0; m < view.Measures.Count; m++) {
                if (m > 0 && (view.Measures[m].ShowKey || view.Measures[m].ShowTime)) {
                    Change(view.Measures[m]);
                }

                Measure(m);
            }

            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteEndElement();
        }

        static int[] FirstStaves(ScoreView view) {
            var first = new int[view.Parts.Count];
            var n = 1;
            for (var p = 0; p < view.Parts.Count; p++) {
                first[p] = n;
                n += view.Parts[p].Staves.Count;
            }

            return first;
        }

        void Head() {
            var layout = view.Layout;
            xml.WriteStartElement("meiHead", Ns);
            xml.WriteStartElement("fileDesc", Ns);
            xml.WriteStartElement("titleStmt", Ns);
            xml.WriteElementString("title", Ns, layout.Title);
            if (layout.Subtitle.Length > 0) {
                xml.WriteStartElement("title", Ns);
                xml.WriteAttributeString("type", "subtitle");
                xml.WriteString(layout.Subtitle);
                xml.WriteEndElement();
            }

            if (layout.Composer.Length > 0) {
                xml.WriteStartElement("respStmt", Ns);
                xml.WriteStartElement("persName", Ns);
                xml.WriteAttributeString("role", "composer");
                xml.WriteString(layout.Composer);
                xml.WriteEndElement();
                xml.WriteEndElement();
            }

            xml.WriteEndElement();
            xml.WriteElementString("pubStmt", Ns, string.Empty);
            xml.WriteEndElement();
            xml.WriteEndElement();
        }

        void ScoreDef() {
            var layout = view.Layout;
            var first = view.Measures[0];
            xml.WriteStartElement("scoreDef", Ns);
            Meter(first.Time);
            xml.WriteAttributeString("keysig", KeySig(first.Key.Fifths));
            xml.WriteAttributeString("page.width", Mm(layout.PageWidthMm));
            xml.WriteAttributeString("page.height", Mm(layout.PageHeightMm));
            xml.WriteAttributeString("page.topmar", Mm(layout.Margins.Top));
            xml.WriteAttributeString("page.botmar", Mm(layout.Margins.Bottom));
            xml.WriteAttributeString("page.leftmar", Mm(layout.Margins.Left));
            xml.WriteAttributeString("page.rightmar", Mm(layout.Margins.Right));
            xml.WriteAttributeString("vu.height", Mm(layout.StaffHeightMm / 8));
            PageHead();
            xml.WriteStartElement("staffGrp", Ns);
            if (view.Parts.Count > 1) {
                xml.WriteAttributeString("bar.thru", "false");
            }

            for (var p = 0; p < view.Parts.Count; p++) {
                var part = view.Parts[p];
                if (part.Staves.Count > 1) {
                    xml.WriteStartElement("staffGrp", Ns);
                    xml.WriteAttributeString("symbol", part.Brace ? "brace" : "bracket");
                    xml.WriteAttributeString("bar.thru", "true");
                    Labels(part);
                    for (var s = 0; s < part.Staves.Count; s++) {
                        StaffDef(p, s, first, false);
                    }

                    xml.WriteEndElement();
                } else {
                    StaffDef(p, 0, first, true);
                }
            }

            xml.WriteEndElement();
            xml.WriteEndElement();
        }

        void PageHead() {
            var layout = view.Layout;
            if (layout.Title.Length + layout.Subtitle.Length + layout.Composer.Length + layout.Header.Length > 0) {
                xml.WriteStartElement("pgHead", Ns);
                xml.WriteAttributeString("func", "first");
                TitleBlock(layout);
                if (layout.Composer.Length > 0) {
                    Aligned(layout.Composer, "right", "bottom");
                }

                if (layout.Header.Length > 0) {
                    Aligned(layout.Header, "left", "top");
                }

                xml.WriteEndElement();
            }

            if (layout.Footer.Length > 0) {
                xml.WriteStartElement("pgFoot", Ns);
                xml.WriteAttributeString("func", "all");
                Aligned(layout.Footer, "center", "bottom");
                xml.WriteEndElement();
            }
        }

        /// <summary>The title and subtitle, centred at the top, on two lines.</summary>
        void TitleBlock(ScoreLayout layout) {
            if (layout.Title.Length + layout.Subtitle.Length == 0) {
                return;
            }

            xml.WriteStartElement("rend", Ns);
            xml.WriteAttributeString("halign", "center");
            xml.WriteAttributeString("valign", "top");
            if (layout.Title.Length > 0) {
                Rend(layout.Title, "x-large");
            }

            if (layout.Title.Length > 0 && layout.Subtitle.Length > 0) {
                xml.WriteElementString("lb", Ns, string.Empty);
            }

            if (layout.Subtitle.Length > 0) {
                Rend(layout.Subtitle, "large");
            }

            xml.WriteEndElement();
        }

        void Rend(string text, string size) {
            xml.WriteStartElement("rend", Ns);
            xml.WriteAttributeString("fontsize", size);
            xml.WriteString(text);
            xml.WriteEndElement();
        }

        void Aligned(string text, string horizontal, string vertical) {
            xml.WriteStartElement("rend", Ns);
            xml.WriteAttributeString("halign", horizontal);
            xml.WriteAttributeString("valign", vertical);
            xml.WriteString(text);
            xml.WriteEndElement();
        }

        void Labels(ScorePart part) {
            xml.WriteElementString("label", Ns, part.Name);
            xml.WriteElementString("labelAbbr", Ns, part.ShortName);
        }

        void StaffDef(int p, int staff, ScoreMeasure first, bool labels) {
            var part = view.Parts[p];
            var definition = part.Staves[staff];
            var transpose = view.Transposing ? definition.Transpose : 0;
            xml.WriteStartElement("staffDef", Ns);
            xml.WriteAttributeString("n", N(firstStaff[p] + staff));
            xml.WriteAttributeString("lines", "5");
            Clef(definition.Clef);
            var key = WrittenFifths(definition, first.Key);
            if (key != first.Key.Fifths) {
                xml.WriteAttributeString("keysig", KeySig(key));
            }

            if (transpose != 0) {
                xml.WriteAttributeString("trans.diat", N(-StaffPositions.DiatonicSteps(transpose)));
                xml.WriteAttributeString("trans.semi", N(-transpose));
            }

            if (labels) {
                Labels(part);
            }

            xml.WriteEndElement();
        }

        int WrittenFifths(Staff staff, ScoreKey concert) {
            if (staff.Clef == Contracts.Clef.Percussion) {
                return 0;
            }

            var transpose = view.Transposing ? staff.Transpose : 0;
            return StaffPositions.WrittenKey(concert, transpose).Fifths;
        }

        void Clef(Clef clef) {
            var (shape, line) = clef switch {
                Contracts.Clef.Bass or Contracts.Clef.Bass8vb => ("F", 4),
                Contracts.Clef.Alto => ("C", 3),
                Contracts.Clef.Tenor => ("C", 4),
                Contracts.Clef.Percussion => ("perc", 3),
                _ => ("G", 2)
            };
            xml.WriteAttributeString("clef.shape", shape);
            xml.WriteAttributeString("clef.line", N(line));
            if (clef is Contracts.Clef.Treble8vb or Contracts.Clef.Bass8vb or Contracts.Clef.Treble8va) {
                xml.WriteAttributeString("clef.dis", "8");
                xml.WriteAttributeString("clef.dis.place", clef == Contracts.Clef.Treble8va ? "above" : "below");
            }

            if (clef == Contracts.Clef.None) {
                xml.WriteAttributeString("clef.visible", "false");
            }
        }

        void Meter(TimeSignature time) {
            xml.WriteAttributeString("meter.count", N(time.Numerator));
            xml.WriteAttributeString("meter.unit", N(time.Denominator));
        }

        /// <summary>A key or meter change before a measure: every staff's key when transposed staves differ from concert.</summary>
        void Change(ScoreMeasure measure) {
            xml.WriteStartElement("scoreDef", Ns);
            if (measure.ShowTime) {
                Meter(measure.Time);
            }

            if (measure.ShowKey) {
                xml.WriteAttributeString("keysig", KeySig(measure.Key.Fifths));
                var staves = view.Parts.SelectMany(static p => p.Staves).ToList();
                if (staves.Any(s => WrittenFifths(s, measure.Key) != measure.Key.Fifths)) {
                    xml.WriteStartElement("staffGrp", Ns);
                    for (var s = 0; s < staves.Count; s++) {
                        xml.WriteStartElement("staffDef", Ns);
                        xml.WriteAttributeString("n", N(s + 1));
                        xml.WriteAttributeString("keysig", KeySig(WrittenFifths(staves[s], measure.Key)));
                        xml.WriteEndElement();
                    }

                    xml.WriteEndElement();
                }
            }

            xml.WriteEndElement();
        }

        void Measure(int index) {
            var measure = view.Measures[index];
            xml.WriteStartElement("measure", Ns);
            xml.WriteAttributeString("n", N(measure.Number));
            xml.WriteAttributeString("id", XmlNs, ScoreIds.Measure(measure.Number));
            if (measure.Barline != Barline.Single) {
                xml.WriteAttributeString("right", measure.Barline == Barline.Final ? "end" : "dbl");
            }

            for (var p = 0; p < view.Parts.Count; p++) {
                var part = view.Parts[p];
                for (var s = 0; s < part.Staves.Count; s++) {
                    var staff = part.Measures[index].Staves[s];
                    xml.WriteStartElement(StaffName, Ns);
                    xml.WriteAttributeString("n", N(firstStaff[p] + s));
                    percussion = part.Staves[s].Clef == Contracts.Clef.Percussion;
                    foreach (var voice in staff.Voices) {
                        xml.WriteStartElement("layer", Ns);
                        xml.WriteAttributeString("n", N(voice.Number));
                        Layer(voice.Events, measure);
                        xml.WriteEndElement();
                    }

                    xml.WriteEndElement();
                }
            }

            Controls(index);
            xml.WriteEndElement();
        }

        /// <summary>A layer's events: tuplets around their events, beams around theirs (beams never cross tuplets).</summary>
        void Layer(IReadOnlyList<ScoreEvent> events, ScoreMeasure measure) {
            if (events is [{ Kind: ScoreEventKind.Rest } only] && only.DurationTicks == measure.LengthTicks) {
                xml.WriteStartElement("mRest", Ns);
                xml.WriteAttributeString("id", XmlNs, only.Id);
                xml.WriteEndElement();
                return;
            }

            var i = 0;
            while (i < events.Count) {
                if (events[i].Duration.Tuplet is { } tuplet) {
                    var j = i;
                    while (j < events.Count && events[j].Duration.Tuplet?.Group == tuplet.Group) {
                        j++;
                    }

                    xml.WriteStartElement("tuplet", Ns);
                    xml.WriteAttributeString("num", N(tuplet.Actual));
                    xml.WriteAttributeString("numbase", N(tuplet.Normal));
                    Beams(events, i, j);
                    xml.WriteEndElement();
                    i = j;
                } else {
                    var j = i;
                    while (j < events.Count && events[j].Duration.Tuplet is null) {
                        j++;
                    }

                    Beams(events, i, j);
                    i = j;
                }
            }
        }

        void Beams(IReadOnlyList<ScoreEvent> events, int from, int to) {
            var i = from;
            while (i < to) {
                if (events[i].Beam is { } beam) {
                    var j = i;
                    while (j < to && events[j].Beam == beam) {
                        j++;
                    }

                    xml.WriteStartElement("beam", Ns);
                    for (var k = i; k < j; k++) {
                        Event(events[k]);
                    }

                    xml.WriteEndElement();
                    i = j;
                } else {
                    Event(events[i]);
                    i++;
                }
            }
        }

        void Event(ScoreEvent e) {
            switch (e.Kind) {
                case ScoreEventKind.Rest:
                    xml.WriteStartElement("rest", Ns);
                    xml.WriteAttributeString("id", XmlNs, e.Id);
                    Duration(e.Duration);
                    xml.WriteEndElement();
                    break;
                case ScoreEventKind.Note:
                    Note(e.Notes[0], e, true);
                    break;
                default:
                    xml.WriteStartElement("chord", Ns);
                    xml.WriteAttributeString("id", XmlNs, e.Id);
                    Duration(e.Duration);
                    Stem(e.Stem);
                    foreach (var note in e.Notes) {
                        Note(note, e, false);
                    }

                    Artics(e);
                    Verses(e);
                    xml.WriteEndElement();
                    break;
            }
        }

        void Note(ScoreNote note, ScoreEvent e, bool alone) {
            xml.WriteStartElement("note", Ns);
            xml.WriteAttributeString("id", XmlNs, note.Id);
            if (alone) {
                Duration(e.Duration);
            }

            xml.WriteAttributeString("pname", note.Written.Step.ToString().ToLowerInvariant());
            xml.WriteAttributeString("oct", N(note.Written.Octave));
            if (percussion) {
                xml.WriteAttributeString("loc", N(note.StaffPosition));
            }

            if (note.Accidental is { } accidental) {
                xml.WriteAttributeString(
                    "accid",
                    accidental switch {
                        Accidental.Sharp => "s",
                        Accidental.Flat => "f",
                        Accidental.DoubleSharp => "x",
                        Accidental.DoubleFlat => "ff",
                        _ => "n"
                    }
                );
            } else if (note.Written.Alter != 0) {
                xml.WriteAttributeString(
                    "accid.ges",
                    note.Written.Alter switch {
                        1 => "s",
                        -1 => "f",
                        2 => "ss",
                        _ => "ff"
                    }
                );
            }

            if (note.Notehead != Notehead.Normal) {
                xml.WriteAttributeString(
                    "head.shape",
                    note.Notehead switch {
                        Notehead.Cross => "x",
                        Notehead.CircleCross => "+",
                        Notehead.Diamond => "diamond",
                        Notehead.Triangle => "isotriangle",
                        _ => "slash"
                    }
                );
            }

            if (alone) {
                Stem(e.Stem);
                Artics(e);
                Verses(e);
            }

            xml.WriteEndElement();
        }

        void Duration(ScoreDuration duration) {
            xml.WriteAttributeString(
                "dur",
                duration.Value switch {
                    NoteValue.Breve => "breve",
                    NoteValue.Whole => "1",
                    NoteValue.Half => "2",
                    NoteValue.Quarter => "4",
                    NoteValue.Eighth => "8",
                    NoteValue.Sixteenth => "16",
                    NoteValue.ThirtySecond => "32",
                    NoteValue.SixtyFourth => "64",
                    _ => "128"
                }
            );
            if (duration.Dots > 0) {
                xml.WriteAttributeString("dots", N(duration.Dots));
            }
        }

        void Stem(StemDirection stem) {
            if (stem != StemDirection.None) {
                xml.WriteAttributeString("stem.dir", stem == StemDirection.Up ? "up" : "down");
            }
        }

        /// <summary>A note's or chord's articulations, in symbol id order.</summary>
        void Artics(ScoreEvent e) {
            foreach (var id in e.SymbolIds) {
                if (symbols.TryGetValue(id, out var symbol) && ArticOf(symbol.Kind) is { } artic) {
                    xml.WriteStartElement("artic", Ns);
                    xml.WriteAttributeString("id", XmlNs, ScoreIds.Symbol(id));
                    xml.WriteAttributeString("artic", artic);
                    Place(symbol);
                    xml.WriteEndElement();
                }
            }
        }

        static string? ArticOf(ScoreSymbolKind kind) =>
            kind switch {
                ScoreSymbolKind.Staccato => "stacc",
                ScoreSymbolKind.Staccatissimo => "stacciss",
                ScoreSymbolKind.Accent => "acc",
                ScoreSymbolKind.Marcato => "marc",
                ScoreSymbolKind.Tenuto => "ten",
                _ => null
            };

        void Verses(ScoreEvent e) {
            foreach (var id in e.LyricIds) {
                var lyric = lyrics[id];
                xml.WriteStartElement("verse", Ns);
                xml.WriteAttributeString("n", N(lyric.Verse));
                xml.WriteStartElement("syl", Ns);
                xml.WriteAttributeString("id", XmlNs, ScoreIds.Lyric(lyric.Id));
                var wordpos = lyric.Syllabic switch {
                    Syllabic.Begin => "i",
                    Syllabic.Middle => "m",
                    Syllabic.End => "t",
                    _ => null
                };
                if (wordpos is not null) {
                    xml.WriteAttributeString("wordpos", wordpos);
                }

                if (lyric.Extender) {
                    xml.WriteAttributeString("con", "u");
                } else if (lyric.Syllabic is Syllabic.Begin or Syllabic.Middle) {
                    xml.WriteAttributeString("con", "d");
                }

                xml.WriteString(lyric.Text);
                xml.WriteEndElement();
                xml.WriteEndElement();
            }
        }

        /// <summary>
        ///     A measure's control events: tempo marks and markers, then per part, staff, voice and event its symbols (in id
        ///     order), chord symbols and ties.
        /// </summary>
        void Controls(int index) {
            var measure = view.Measures[index];
            foreach (var tempo in measure.Tempos) {
                xml.WriteStartElement("tempo", Ns);
                xml.WriteAttributeString(StaffName, "1");
                xml.WriteAttributeString("tstamp", Tstamp(measure, tempo.Tick));
                xml.WriteAttributeString("place", "above");
                xml.WriteAttributeString("midi.bpm", D(tempo.Bpm));
                xml.WriteString($"♩ = {D(tempo.Bpm)}");
                xml.WriteEndElement();
            }

            foreach (var rehearsal in measure.Rehearsals) {
                xml.WriteStartElement("reh", Ns);
                xml.WriteAttributeString(StaffName, "1");
                xml.WriteAttributeString("tstamp", Tstamp(measure, rehearsal.Tick));
                xml.WriteString(rehearsal.Text);
                xml.WriteEndElement();
            }

            for (var p = 0; p < view.Parts.Count; p++) {
                var part = view.Parts[p];
                for (var s = 0; s < part.Staves.Count; s++) {
                    foreach (var e in part.Measures[index].Staves[s].Voices.SelectMany(static v => v.Events)) {
                        EventControls(e, firstStaff[p] + s, index);
                    }
                }
            }
        }

        /// <summary>An event's symbols (in id order), chord symbols and ties.</summary>
        void EventControls(ScoreEvent e, int staff, int index) {
            foreach (var id in e.SymbolIds) {
                if (symbols.TryGetValue(id, out var symbol) && ArticOf(symbol.Kind) is null) {
                    Control(symbol, e, staff, index);
                }
            }

            foreach (var id in e.ChordIds) {
                Harm(chords[id], staff, view.Measures[index]);
            }

            // A hidden tie (tieHidden) is left out: MEI 5's tie has no visibility.
            foreach (var note in e.Notes.Where(n => n.TieStart && !hiddenTies.Contains(n.NoteId))) {
                xml.WriteStartElement("tie", Ns);
                xml.WriteAttributeString(StaffName, N(staff));
                xml.WriteAttributeString("startid", "#" + note.Id);
                xml.WriteAttributeString("endid", "#" + ScoreIds.Note(note.NoteId, SegmentOf(note) + 1));
                xml.WriteEndElement();
            }
        }

        static int SegmentOf(ScoreNote note) {
            var dash = note.Id.IndexOf('-', StringComparison.Ordinal);
            return dash < 0 ? 1 : int.Parse(note.Id.AsSpan(dash + 1), CultureInfo.InvariantCulture);
        }

        void Control(ScoreSymbol symbol, ScoreEvent e, int staff, int index) {
            if (ElementOf(symbol.Kind) is not { } name) {
                return;
            }

            xml.WriteStartElement(name, Ns);
            xml.WriteAttributeString("id", XmlNs, ScoreIds.Symbol(symbol.Id));
            xml.WriteAttributeString(StaffName, N(staff));
            Anchors(symbol, e, index);
            Kind(symbol.Kind);
            Place(symbol);
            if (ScoreSymbols.WithText.Contains(symbol.Kind)) {
                xml.WriteString(symbol.Text ?? string.Empty);
            }

            xml.WriteEndElement();
        }

        static string? ElementOf(ScoreSymbolKind kind) =>
            kind switch {
                ScoreSymbolKind.Fermata => "fermata",
                ScoreSymbolKind.Trill => "trill",
                ScoreSymbolKind.Dynamic => "dynam",
                ScoreSymbolKind.Text => "dir",
                ScoreSymbolKind.TempoText => "tempo",
                ScoreSymbolKind.Rehearsal => "reh",
                ScoreSymbolKind.PedalDown or ScoreSymbolKind.PedalUp => "pedal",
                ScoreSymbolKind.Slur => "slur",
                ScoreSymbolKind.Crescendo or ScoreSymbolKind.Diminuendo => "hairpin",
                ScoreSymbolKind.OctaveUp or ScoreSymbolKind.OctaveDown => "octave",
                _ => null
            };

        /// <summary>A symbol's start and end: notes by id, positions by timestamp.</summary>
        void Anchors(ScoreSymbol symbol, ScoreEvent e, int index) {
            var measure = view.Measures[index];
            if (symbol.NoteId is { } note) {
                xml.WriteAttributeString("startid", "#" + ScoreIds.Note(note));
            } else {
                xml.WriteAttributeString("tstamp", Tstamp(measure, symbol.Tick ?? e.Tick));
            }

            if (symbol.EndNoteId is { } endNote) {
                xml.WriteAttributeString("endid", "#" + ScoreIds.Note(endNote));
            } else if (symbol.EndTick is { } endTick && ScoreSymbols.Spanning.Contains(symbol.Kind)) {
                xml.WriteAttributeString("tstamp2", Tstamp2(index, endTick));
            }
        }

        /// <summary>The attributes a kind of symbol adds: a pedal's direction, a hairpin's form, an octave line's.</summary>
        void Kind(ScoreSymbolKind kind) {
            switch (kind) {
                case ScoreSymbolKind.PedalDown or ScoreSymbolKind.PedalUp:
                    xml.WriteAttributeString("dir", kind == ScoreSymbolKind.PedalDown ? "down" : "up");
                    break;
                case ScoreSymbolKind.Crescendo or ScoreSymbolKind.Diminuendo:
                    xml.WriteAttributeString("form", kind == ScoreSymbolKind.Crescendo ? "cres" : "dim");
                    break;
                case ScoreSymbolKind.OctaveUp or ScoreSymbolKind.OctaveDown:
                    xml.WriteAttributeString("dis", "8");
                    xml.WriteAttributeString("dis.place", kind == ScoreSymbolKind.OctaveUp ? "above" : "below");
                    break;
            }
        }

        void Place(ScoreSymbol symbol) {
            if (symbol.Placement is { } placement) {
                xml.WriteAttributeString("place", placement == ScorePlacement.Above ? "above" : "below");
            }
        }

        void Harm(ChordSymbol chord, int staff, ScoreMeasure measure) {
            xml.WriteStartElement("harm", Ns);
            xml.WriteAttributeString("id", XmlNs, ScoreIds.ChordSymbol(chord.Id));
            xml.WriteAttributeString(StaffName, N(staff));
            xml.WriteAttributeString("tstamp", Tstamp(measure, chord.Tick));
            xml.WriteString(ChordText(chord));
            xml.WriteEndElement();
        }

        /// <summary>A chord symbol's text: root, quality and bass ("C", "Am7", "F♯m7♭5", "C/E").</summary>
        internal static string ChordText(ChordSymbol chord) {
            var text = Name(chord.Root) + chord.Quality;
            return chord.Bass is { } bass ? text + "/" + Name(bass) : text;

            static string Name(PitchName name) =>
                name.Step.ToString()
                + name.Alter switch {
                    1 => "♯",
                    2 => "\U0001D12A",
                    -1 => "♭",
                    -2 => "\U0001D12B",
                    _ => string.Empty
                };
        }

        /// <summary>MEI's beat timestamp: 1 + the beats (of the meter's unit) into the measure.</summary>
        static string Tstamp(ScoreMeasure measure, long tick) {
            var unit = (double)Notation.Meter.Whole / measure.Time.Denominator;
            return D(1 + (tick - measure.StartTick) / unit);
        }

        /// <summary>MEI's end timestamp: measures forward and the beat ("1m+3"); an end on a bar line is past the last beat.</summary>
        string Tstamp2(int from, long tick) {
            var to = from;
            while (to + 1 < view.Measures.Count && view.Measures[to + 1].StartTick < tick) {
                to++;
            }

            var measure = view.Measures[to];
            var beats = Math.Min(tick, measure.StartTick + measure.LengthTicks);
            return $"{N(to - from)}m+{Tstamp(measure, beats)}";
        }

        static string KeySig(int fifths) {
            if (fifths == 0) {
                return "0";
            }

            return fifths > 0 ? $"{N(fifths)}s" : $"{N(-fifths)}f";
        }

        static string Mm(double value) => D(value) + "mm";
    }
}
