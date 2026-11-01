using Anode.Contracts;
using System.Xml.Linq;

namespace Anode.Interchange.MusicXml;

/// <summary>
///     One part's measures: attributes, then each staff's voices in turn (<c>backup</c> between them, <c>forward</c> over
///     gaps), with the directions and harmony placed before the event they belong to (an <c>offset</c> when their tick is
///     past the event's start), and the note-attached symbols and lyrics in each note's <c>notations</c> and
///     <c>lyric</c>s.
/// </summary>
sealed class MusicXmlPartWriter {
    readonly MusicXmlScoreWriter score;
    readonly ScorePart part;
    readonly string partId;
    readonly bool first;
    readonly bool multiStaff;

    /// <summary>Where each note's first segment is: measure, staff, voice and event indices.</summary>
    readonly Dictionary<long, Location> notes = [];

    /// <summary>Where each drawn symbol and chord symbol is.</summary>
    readonly Dictionary<string, Location> drawn = [];

    /// <summary>The directions and harmony before each event (event −1: at the measure's start).</summary>
    readonly Dictionary<Location, List<(Rational Offset, Func<Rational, XElement> Element)>> items = [];

    /// <summary>The note-attached symbols of each note, in the view's order.</summary>
    readonly Dictionary<long, List<ScoreSymbol>> noteSymbols = [];

    /// <summary>The slurs ending at each note.</summary>
    readonly Dictionary<long, List<ScoreSymbol>> slurEnds = [];

    readonly Dictionary<long, List<LyricSyllable>> noteLyrics = [];
    readonly HashSet<long> hiddenTies = [];
    readonly Dictionary<string, int> numbers = [];
    readonly HashSet<long> written = [];

    public MusicXmlPartWriter(MusicXmlScoreWriter score, ScorePart part, string partId, bool first) {
        this.score = score;
        this.part = part;
        this.partId = partId;
        this.first = first;
        multiStaff = part.Staves.Count > 1;
        Index();
        PlaceSymbols();
    }

    readonly record struct Location(int Measure, int Staff, int Voice, int Event);

    /// <summary>The MIDI pitches of a part's percussion notes, ascending: its score instruments.</summary>
    public static IReadOnlyList<int> DrumPitches(ScorePart part) {
        var pitches = new SortedSet<int>();
        foreach (var measure in part.Measures) {
            for (var s = 0; s < measure.Staves.Count && s < part.Staves.Count; s++) {
                if (part.Staves[s].Clef != Clef.Percussion) {
                    continue;
                }

                foreach (var note in measure.Staves[s]
                             .Voices.SelectMany(static v => v.Events)
                             .SelectMany(static e => e.Notes)) {
                    pitches.Add(Math.Clamp(note.Pitch, 0, 127));
                }
            }
        }

        return [..pitches];
    }

    public static string InstrumentId(string partId, int pitch) => $"{partId}-I{pitch + 1}";

    public XElement Write() {
        var element = new XElement("part", new XAttribute("id", partId));
        var measures = score.View.Measures;
        for (var m = 0; m < measures.Count; m++) {
            element.Add(Measure(m, measures[m]));
        }

        return element;
    }

    // ---- Indexing: where notes, symbols and chords are drawn.

    /// <summary>Every event of the part with its location, in writing order.</summary>
    IEnumerable<(Location At, ScoreEvent Event)> Events() {
        for (var m = 0; m < part.Measures.Count; m++) {
            var staves = part.Measures[m].Staves;
            for (var s = 0; s < staves.Count; s++) {
                foreach (var item in StaffEvents(m, s, staves[s])) {
                    yield return item;
                }
            }
        }
    }

    static IEnumerable<(Location At, ScoreEvent Event)> StaffEvents(int m, int s, ScoreStaffMeasure staff) {
        for (var v = 0; v < staff.Voices.Count; v++) {
            var events = staff.Voices[v].Events;
            for (var e = 0; e < events.Count; e++) {
                yield return (new(m, s, v, e), events[e]);
            }
        }
    }

    void Index() {
        foreach (var (at, ev) in Events()) {
            foreach (var note in ev.Notes) {
                notes.TryAdd(note.NoteId, at);
            }

            foreach (var id in ev.SymbolIds.Concat(ev.ChordIds)) {
                drawn.TryAdd(id, at);
            }
        }

        foreach (var symbol in score.View.Symbols.Where(s => s.TrackId == part.TrackId)) {
            IndexSymbol(symbol);
        }

        foreach (var lyric in score.View.Lyrics.Where(l => notes.ContainsKey(l.NoteId)).OrderBy(static l => l.Verse)) {
            Add(noteLyrics, lyric.NoteId, lyric);
        }

        Number(noteSymbols.Values.SelectMany(static s => s).Where(static s => s.Kind == ScoreSymbolKind.Slur));
    }

    /// <summary>Files a note-attached symbol under its note (a slur under its end note too, if it has one here).</summary>
    void IndexSymbol(ScoreSymbol symbol) {
        if (symbol.Kind == ScoreSymbolKind.TieHidden && symbol.NoteId is { } hidden) {
            hiddenTies.Add(hidden);
            return;
        }

        if (!OnNote(symbol) || symbol.NoteId is not { } noteId || !notes.ContainsKey(noteId)) {
            return;
        }

        if (symbol.Kind == ScoreSymbolKind.Slur) {
            if (symbol.EndNoteId is not { } end || !notes.ContainsKey(end) || end == noteId) {
                return;
            }

            Add(slurEnds, end, symbol);
        }

        Add(noteSymbols, noteId, symbol);
    }

    /// <summary>Drawn in the note's own element: articulations, ornaments, fermatas, slurs, and a note's dynamic.</summary>
    static bool OnNote(ScoreSymbol symbol) =>
        ScoreSymbols.NoteOnly.Contains(symbol.Kind)
        || symbol.Kind == ScoreSymbolKind.Slur
        || (symbol.Kind == ScoreSymbolKind.Dynamic && symbol.NoteId is not null);

    // ---- Directions and harmony: placed at events.

    /// <summary>A spanning symbol's two ends.</summary>
    readonly record struct Span(ScoreSymbol Symbol, Location Start, long StartTick, Location End, long EndTick);

    void PlaceSymbols() {
        if (first) {
            PlaceMeasureMarks();
        }

        var spans = new List<Span>();
        foreach (var symbol in score.View.Symbols.Where(s => s.TrackId == part.TrackId
                     && !OnNote(s)
                     && s.Kind != ScoreSymbolKind.TieHidden
                 )) {
            if (Start(symbol) is not { } start) {
                continue;
            }

            var startTick = symbol.Tick ?? TickOf(start);
            if (!ScoreSymbols.Spanning.Contains(symbol.Kind)) {
                PlaceDirection(symbol, start, startTick);
            } else if (End(symbol, start, startTick) is { } span) {
                spans.Add(span);
            }
        }

        Number(spans.Select(static s => s.Symbol));
        foreach (var span in spans) {
            PlaceSpan(span);
        }

        foreach (var chord in score.View.Chords.Where(c => c.TrackId == part.TrackId)) {
            var at = drawn.TryGetValue(chord.Id, out var d) ? d : Locate(0, chord.Tick);
            Place(at, chord.Tick, (o, s) => Harmony(chord, o, s));
        }
    }

    /// <summary>The project's markers (as rehearsal marks) and tempo marks, in the first part.</summary>
    void PlaceMeasureMarks() {
        foreach (var measure in score.View.Measures) {
            foreach (var rehearsal in measure.Rehearsals) {
                var text = rehearsal.Text.Length > 0 ? rehearsal.Text : "A";
                Place(
                    Locate(0, rehearsal.Tick),
                    rehearsal.Tick,
                    (o, s) => Direction(o, s, "above", null, Rehearsal(text))
                );
            }

            foreach (var tempo in measure.Tempos) {
                Place(
                    Locate(0, tempo.Tick),
                    tempo.Tick,
                    (o, s) => Direction(o, s, "above", Sound(tempo.Bpm), Metronome(tempo.Bpm))
                );
            }
        }
    }

    void PlaceDirection(ScoreSymbol symbol, Location start, long startTick) {
        if (DirectionTypes(symbol) is not { } types) {
            return;
        }

        var staff = StaffOf(symbol, start);
        var placement = Placement(symbol.Placement);
        Place(start, startTick, (o, _) => Direction(o, staff, placement, null, types));
    }

    /// <summary>Where a spanning symbol ends (its end note, else its end tick), if after its start.</summary>
    Span? End(ScoreSymbol symbol, Location start, long startTick) {
        Location? end = null;
        if (symbol.EndNoteId is { } endNote && notes.TryGetValue(endNote, out var at)) {
            end = at;
        } else if (symbol.EndTick is { } endTick) {
            end = Locate(StaffOf(symbol, start), endTick);
        }

        if (end is not { } stop) {
            return null;
        }

        var stopTick = symbol.EndTick ?? TickOf(stop);
        return stopTick > startTick ? new Span(symbol, start, startTick, stop, stopTick) : null;
    }

    void PlaceSpan(Span span) {
        var symbol = span.Symbol;
        var number = numbers[symbol.Id];
        var staff = StaffOf(symbol, span.Start);
        var placement = Placement(symbol.Placement);
        var (open, close) = symbol.Kind switch {
            ScoreSymbolKind.Crescendo => (Wedge("crescendo", number), Wedge("stop", number)),
            ScoreSymbolKind.Diminuendo => (Wedge("diminuendo", number), Wedge("stop", number)),
            // An 8va line writes the notes an octave below their pitch: MusicXML's "down".
            ScoreSymbolKind.OctaveUp => (OctaveShift("down", number), OctaveShift("stop", number)),
            _ => (OctaveShift("up", number), OctaveShift("stop", number))
        };
        Place(span.Start, span.StartTick, (o, _) => Direction(o, staff, placement, null, open));
        Place(span.End, span.EndTick, (o, _) => Direction(o, staff, placement, null, close));
    }

    /// <summary>A positioned symbol's staff: its own (within the part's), else the staff it is drawn on.</summary>
    int StaffOf(ScoreSymbol symbol, Location at) =>
        symbol.Staff is { } staff ? Math.Clamp(staff, 0, Math.Max(0, part.Staves.Count - 1)) : at.Staff;

    Location? Start(ScoreSymbol symbol) {
        if (drawn.TryGetValue(symbol.Id, out var at)) {
            return at;
        }

        if (symbol.NoteId is { } noteId) {
            return notes.TryGetValue(noteId, out var note) ? note : null;
        }

        return symbol.Tick is { } tick ? Locate(symbol.Staff ?? 0, tick) : null;
    }

    /// <summary>The event of a staff's first voice at or before a tick (event −1: none in that measure before it).</summary>
    Location Locate(int staff, long tick) {
        var measures = score.View.Measures;
        var m = 0;
        while (m + 1 < measures.Count && measures[m + 1].StartTick <= tick) {
            m++;
        }

        if (m >= part.Measures.Count) {
            return new(m, 0, 0, -1);
        }

        var staves = part.Measures[m].Staves;
        staff = Math.Clamp(staff, 0, Math.Max(0, staves.Count - 1));
        if (staves.Count == 0 || staves[staff].Voices.Count == 0) {
            return new(m, staff, 0, -1);
        }

        var events = staves[staff].Voices[0].Events;
        var index = -1;
        for (var e = 0; e < events.Count && events[e].Tick <= tick; e++) {
            index = e;
        }

        return new(m, staff, 0, index);
    }

    long TickOf(Location at) =>
        at.Event < 0
            ? score.View.Measures[at.Measure].StartTick
            : part.Measures[at.Measure].Staves[at.Staff].Voices[at.Voice].Events[at.Event].Tick;

    void Place(Location at, long tick, Func<Rational, int, XElement> element) {
        var offset = Rational.FromTicks(Math.Max(0, tick - TickOf(at)));
        var staff = at.Staff;
        Add(items, at, (offset, o => element(o, staff)));
    }

    /// <summary>
    ///     Numbers overlapping spans (slurs, wedges, octave lines) 1…16, the lowest free number first, in the order of
    ///     their starts.
    /// </summary>
    void Number(IEnumerable<ScoreSymbol> spanning) {
        var active = new List<(long End, int Number)>();
        foreach (var symbol in spanning
                     .Select(s => (Symbol: s, Start: SpanTick(s, true), End: SpanTick(s, false)))
                     .OrderBy(static s => s.Start)
                     .ThenBy(static s => s.End)) {
            if (numbers.ContainsKey(symbol.Symbol.Id)) {
                continue;
            }

            active.RemoveAll(a => a.End < symbol.Start);
            var number = FreeNumber(active);
            active.Add((symbol.End, number));
            numbers[symbol.Symbol.Id] = number;
        }
    }

    /// <summary>The lowest number (1…16) no active span has; 16 when all are taken.</summary>
    static int FreeNumber(List<(long End, int Number)> active) {
        var number = 1;
        while (number < 16 && active.Exists(a => a.Number == number)) {
            number++;
        }

        return number;
    }

    long SpanTick(ScoreSymbol symbol, bool start) {
        var tick = start ? symbol.Tick : symbol.EndTick;
        var note = start ? symbol.NoteId : symbol.EndNoteId;
        return tick ?? (note is { } id && notes.TryGetValue(id, out var at) ? TickOf(at) : 0);
    }

    // ---- Measures.

    XElement Measure(int m, ScoreMeasure measure) {
        var element = new XElement("measure", new XAttribute("number", MusicXmlScoreWriter.Format(measure.Number)));
        element.Add(Attributes(m, measure));
        var staves = m < part.Measures.Count ? part.Measures[m].Staves : [];
        for (var s = 0; s < Math.Max(1, staves.Count); s++) {
            Emit(element, new(m, s, 0, -1), Rational.Zero);
        }

        var passes = Passes(staves);
        for (var p = 0; p < passes.Count; p++) {
            var (s, v) = passes[p];
            var cursor = Voice(element, m, measure, s, v);
            if (p < passes.Count - 1 && cursor.IsPositive) {
                element.Add(new XElement("backup", new XElement("duration", score.Duration(cursor))));
            }
        }

        if (passes.Count == 0) {
            element.Add(EmptyMeasure(measure));
        }

        if (measure.Barline != Barline.Single) {
            var style = measure.Barline == Barline.Final ? "light-heavy" : "light-light";
            element.Add(new XElement("barline", new XAttribute("location", "right"), new XElement("bar-style", style)));
        }

        return element;
    }

    /// <summary>The staff and voice of every voice with events, top to bottom: the measure's passes.</summary>
    static List<(int Staff, int Voice)> Passes(IReadOnlyList<ScoreStaffMeasure> staves) {
        var passes = new List<(int Staff, int Voice)>();
        for (var s = 0; s < staves.Count; s++) {
            for (var v = 0; v < staves[s].Voices.Count; v++) {
                if (staves[s].Voices[v].Events.Count > 0) {
                    passes.Add((s, v));
                }
            }
        }

        return passes;
    }

    /// <summary>An empty measure still lasts its meter: a measure rest.</summary>
    XElement? EmptyMeasure(ScoreMeasure measure) {
        var length = Rational.FromTicks(measure.LengthTicks);
        return length.IsPositive
            ? new XElement(
                "note",
                new XElement("rest", new XAttribute("measure", "yes")),
                new XElement("duration", score.Duration(length)),
                new XElement("voice", "1")
            )
            : null;
    }

    XElement? Attributes(int m, ScoreMeasure measure) {
        var previous = m > 0 ? score.View.Measures[m - 1] : null;
        var element = new XElement("attributes");
        if (m == 0) {
            element.Add(new XElement("divisions", MusicXmlScoreWriter.Format(score.Divisions)));
        }

        if (previous is null || measure.ShowKey || previous.Key != measure.Key) {
            element.Add(Key(measure.Key));
        }

        if (previous is null || measure.ShowTime || previous.Time != measure.Time) {
            element.Add(
                new XElement(
                    "time",
                    new XElement("beats", MusicXmlScoreWriter.Format(measure.Time.Numerator)),
                    new XElement("beat-type", MusicXmlScoreWriter.Format(measure.Time.Denominator))
                )
            );
        }

        if (m == 0 && multiStaff) {
            element.Add(new XElement("staves", MusicXmlScoreWriter.Format(part.Staves.Count)));
            element.Add(new XElement("part-symbol", part.Brace ? "brace" : "bracket"));
        }

        element.Add(Clefs(m));
        if (m == 0) {
            element.Add(Transposition());
        }

        return element.HasElements ? element : null;
    }

    /// <summary>The key as written: transposed with the part.</summary>
    XElement Key(ScoreKey key) {
        var fifths = part.Transpose == 0 ? key.Fifths : MusicXmlVocabulary.TransposeFifths(key.Fifths, part.Transpose);
        return new(
            "key",
            new XElement("fifths", MusicXmlScoreWriter.Format(fifths)),
            new XElement("mode", key.Mode == KeyMode.Minor ? "minor" : "major")
        );
    }

    /// <summary>The staves' clefs in the first measure, and their changes after it.</summary>
    IEnumerable<XElement> Clefs(int m) {
        var staves = m < part.Measures.Count ? part.Measures[m].Staves : [];
        for (var s = 0; s < part.Staves.Count; s++) {
            var clef = s < staves.Count ? staves[s].Clef : null;
            if (m == 0) {
                clef ??= part.Staves[s].Clef;
            }

            if (clef is { } c) {
                yield return ClefElement(c, s);
            }
        }
    }

    /// <summary>
    ///     A written part's <c>transpose</c>; in a concert score, the transposition of the part made from it
    ///     (MusicXML 4.0's <c>for-part</c>).
    /// </summary>
    XElement? Transposition() {
        var staffTranspose = part.Staves.Count > 0 ? part.Staves[0].Transpose : 0;
        if (part.Transpose != 0) {
            return new("transpose", Transpose(part.Transpose));
        }

        return !score.View.Transposing && staffTranspose != 0
            ? new XElement("for-part", new XElement("part-transpose", Transpose(staffTranspose)))
            : null;
    }

    XElement ClefElement(Clef clef, int staff) {
        var (sign, line, octave) = MusicXmlVocabulary.ClefElement(clef);
        return new(
            "clef",
            multiStaff ? new XAttribute("number", MusicXmlScoreWriter.Format(staff + 1)) : null,
            new XElement("sign", sign),
            line is { } l ? new XElement("line", MusicXmlScoreWriter.Format(l)) : null,
            octave != 0 ? new XElement("clef-octave-change", MusicXmlScoreWriter.Format(octave)) : null
        );
    }

    static IEnumerable<XElement> Transpose(int transpose) {
        var (diatonic, chromatic, octaves) = MusicXmlVocabulary.TransposeElement(transpose);
        yield return new("diatonic", MusicXmlScoreWriter.Format(diatonic));
        yield return new("chromatic", MusicXmlScoreWriter.Format(chromatic));
        if (octaves != 0) {
            yield return new("octave-change", MusicXmlScoreWriter.Format(octaves));
        }
    }

    /// <summary>Writes one voice of a staff in a measure; returns where it ends (quarters from the measure's start).</summary>
    Rational Voice(XElement element, int m, ScoreMeasure measure, int s, int v) {
        var voice = part.Measures[m].Staves[s].Voices[v];
        var number = MusicXmlScoreWriter.Format(s * 4 + voice.Number);
        var cursor = Rational.Zero;
        for (var e = 0; e < voice.Events.Count; e++) {
            var ev = voice.Events[e];
            var position = Rational.FromTicks(ev.Tick - measure.StartTick);
            if (position > cursor && (position - cursor).ToTicks() > 0) {
                element.Add(
                    new XElement(
                        "forward",
                        new XElement("duration", score.Duration(position - cursor)),
                        new XElement("voice", number),
                        multiStaff ? new XElement("staff", MusicXmlScoreWriter.Format(s + 1)) : null
                    )
                );
                cursor = position;
            }

            Emit(element, new(m, s, v, e), Rational.Zero);
            var duration = Duration(ev);
            var measureRest = ev.Kind == ScoreEventKind.Rest
                && voice.Events.Count == 1
                && duration == Rational.FromTicks(measure.LengthTicks)
                && duration != MusicXmlVocabulary.Quarters(ev.Duration.Value) * Dotted(ev.Duration.Dots);
            if (ev.Kind == ScoreEventKind.Rest) {
                element.Add(Note(voice, e, null, 0, s, number, duration, measureRest));
            } else {
                for (var k = 0; k < ev.Notes.Count; k++) {
                    element.Add(Note(voice, e, ev.Notes[k], k, s, number, duration, false));
                }
            }

            cursor += duration;
        }

        return cursor;
    }

    void Emit(XElement element, Location at, Rational shift) {
        if (!items.TryGetValue(at, out var list)) {
            return;
        }

        foreach (var (offset, make) in list) {
            element.Add(make(offset + shift));
        }
    }

    /// <summary>
    ///     An event's exact length: its written value with dots and tuplet, unless that is not the length the view gives
    ///     (a whole-bar rest in 3/4), when the ticks are.
    /// </summary>
    static Rational Duration(ScoreEvent ev) {
        var written = MusicXmlVocabulary.Quarters(ev.Duration.Value) * Dotted(ev.Duration.Dots);
        if (ev.Duration.Tuplet is { Actual: > 0, Normal: > 0 } tuplet) {
            written *= new Rational(tuplet.Normal, tuplet.Actual);
        }

        var ticks = Rational.FromTicks(ev.DurationTicks);
        var difference = written > ticks ? written - ticks : ticks - written;
        return difference < Rational.FromTicks(1) ? written : ticks;
    }

    static Rational Dotted(int dots) {
        var power = 1L << Math.Clamp(dots, 0, 3);
        return new(2 * power - 1, power);
    }

    // ---- Notes.

    XElement Note(
        ScoreVoice voice,
        int e,
        ScoreNote? note,
        int k,
        int staff,
        string voiceNumber,
        Rational duration,
        bool measureRest
    ) {
        var ev = voice.Events[e];
        var percussion = staff < part.Staves.Count && part.Staves[staff].Clef == Clef.Percussion;
        var firstSegment = note is not null && written.Add(note.NoteId);
        var element = new XElement(
            "note",
            k > 0 ? new XElement("chord") : null,
            Sound(note, percussion, measureRest),
            new XElement("duration", score.Duration(duration)),
            note is { TieStop: true } ? new XElement("tie", new XAttribute("type", "stop")) : null,
            note is { TieStart: true } ? new XElement("tie", new XAttribute("type", "start")) : null,
            note is not null && percussion
                ? new XElement("instrument", new XAttribute("id", InstrumentId(partId, Math.Clamp(note.Pitch, 0, 127))))
                : null,
            new XElement("voice", voiceNumber)
        );
        if (!measureRest) {
            element.Add(new XElement("type", MusicXmlVocabulary.TypeName(ev.Duration.Value)));
            element.Add(
                Enumerable.Range(0, Math.Clamp(ev.Duration.Dots, 0, 3)).Select(static _ => new XElement("dot"))
            );
        }

        element.Add(Appearance(ev, note));
        if (multiStaff) {
            element.Add(new XElement("staff", MusicXmlScoreWriter.Format(staff + 1)));
        }

        if (k == 0 && note is not null) {
            element.Add(Beams(voice, e));
        }

        element.Add(Notations(voice, e, note, k, firstSegment));
        if (note is not null && firstSegment && noteLyrics.TryGetValue(note.NoteId, out var syllables)) {
            element.Add(syllables.Select(Lyric));
        }

        return element;
    }

    /// <summary>What sounds: a rest, a percussion staff's unpitched note, or a pitch.</summary>
    XElement Sound(ScoreNote? note, bool percussion, bool measureRest) {
        if (note is null) {
            return new("rest", measureRest ? new XAttribute("measure", "yes") : null);
        }

        if (!percussion) {
            return Pitch(note);
        }

        var (step, octave) = DisplayPosition(note.StaffPosition);
        return new(
            "unpitched",
            new XElement("display-step", MusicXmlVocabulary.StepName(step)),
            new XElement("display-octave", MusicXmlScoreWriter.Format(octave))
        );
    }

    /// <summary>The accidental, tuplet ratio, stem and notehead, in the schema's order.</summary>
    static IEnumerable<XElement> Appearance(ScoreEvent ev, ScoreNote? note) {
        if (note?.Accidental is { } accidental) {
            yield return new("accidental", MusicXmlVocabulary.AccidentalName(accidental));
        }

        if (ev.Duration.Tuplet is { Actual: > 0, Normal: > 0 } tuplet) {
            yield return new(
                "time-modification",
                new XElement("actual-notes", MusicXmlScoreWriter.Format(tuplet.Actual)),
                new XElement("normal-notes", MusicXmlScoreWriter.Format(tuplet.Normal))
            );
        }

        if (note is null) {
            yield break;
        }

        if (ev.Stem != StemDirection.None) {
            yield return new("stem", ev.Stem == StemDirection.Up ? "up" : "down");
        }

        if (MusicXmlVocabulary.NoteheadName(note.Notehead) is { } notehead) {
            yield return new("notehead", notehead);
        }
    }

    XElement Pitch(ScoreNote note) {
        // The written spelling, at the octave that makes it the concert pitch plus the part's transposition (so an
        // octave clef or an octave line drawn by the view does not change the pitch MusicXML carries).
        var w = note.Written;
        var target = note.Pitch + part.Transpose;
        var octave = w.Octave + (int)Math.Round((target - MusicXmlVocabulary.Midi(w.Step, w.Alter, w.Octave)) / 12.0);
        return new(
            "pitch",
            new XElement("step", MusicXmlVocabulary.StepName(w.Step)),
            w.Alter != 0 ? new XElement("alter", MusicXmlScoreWriter.Format(w.Alter)) : null,
            new XElement("octave", MusicXmlScoreWriter.Format(Math.Clamp(octave, 0, 9)))
        );
    }

    /// <summary>A percussion staff position as a treble staff's display step and octave (0, the bottom line, is E4).</summary>
    static (PitchStep Step, int Octave) DisplayPosition(int staffPosition) {
        var index = Math.Clamp(4 * 7 + 2 + staffPosition, 0, 69);
        return ((PitchStep)(index % 7), index / 7);
    }

    static IEnumerable<XElement> Beams(ScoreVoice voice, int e) {
        var ev = voice.Events[e];
        if (ev.Beam is not { } group) {
            yield break;
        }

        var levels = MusicXmlVocabulary.BeamLevels(ev.Duration.Value);
        var previous = Neighbour(voice, e, group, -1);
        var next = Neighbour(voice, e, group, 1);
        for (var level = 1; level <= levels; level++) {
            var before = previous is { } p && MusicXmlVocabulary.BeamLevels(p.Duration.Value) >= level;
            var after = next is { } n && MusicXmlVocabulary.BeamLevels(n.Duration.Value) >= level;
            var value = (before, after) switch {
                (true, true) => "continue",
                (true, false) => "end",
                (false, true) => "begin",
                _ when level == 1 => null,
                _ => previous is not null ? "backward hook" : "forward hook"
            };
            if (value is not null) {
                yield return new("beam", new XAttribute("number", MusicXmlScoreWriter.Format(level)), value);
            }
        }
    }

    /// <summary>The previous or next note of the same beam group in the voice (rests in the group are skipped).</summary>
    static ScoreEvent? Neighbour(ScoreVoice voice, int e, int group, int direction) {
        for (var i = e + direction; i >= 0 && i < voice.Events.Count; i += direction) {
            var other = voice.Events[i];
            if (other.Beam != group) {
                return null;
            }

            if (other.Kind != ScoreEventKind.Rest) {
                return other;
            }
        }

        return null;
    }

    IEnumerable<XElement> Notations(ScoreVoice voice, int e, ScoreNote? note, int k, bool firstSegment) {
        var ties = Ties(note).ToList();
        var hidden = note is not null && hiddenTies.Contains(note.NoteId);
        var attached = note is not null && firstSegment && noteSymbols.TryGetValue(note.NoteId, out var list)
            ? list
            : [];
        var children = new List<XElement>();
        if (!hidden) {
            children.AddRange(ties);
        }

        if (note is not null && firstSegment && slurEnds.TryGetValue(note.NoteId, out var ends)) {
            children.AddRange(ends.Select(s => Slur("stop", s)));
        }

        children.AddRange(attached.Where(static s => s.Kind == ScoreSymbolKind.Slur).Select(s => Slur("start", s)));
        if (k == 0) {
            children.AddRange(Tuplets(voice, e));
        }

        children.AddRange(Marks(attached));
        if (children.Count > 0) {
            yield return new("notations", children);
        }

        if (hidden && ties.Count > 0) {
            // A tie that sounds but is not drawn.
            yield return new("notations", new XAttribute("print-object", "no"), ties);
        }
    }

    static IEnumerable<XElement> Ties(ScoreNote? note) {
        if (note is { TieStop: true }) {
            yield return new("tied", new XAttribute("type", "stop"));
        }

        if (note is { TieStart: true }) {
            yield return new("tied", new XAttribute("type", "start"));
        }
    }

    /// <summary>A tuplet bracket's start on its group's first event and its stop on the last.</summary>
    static IEnumerable<XElement> Tuplets(ScoreVoice voice, int e) {
        if (voice.Events[e].Duration.Tuplet is not { } tuplet) {
            yield break;
        }

        var previous = e > 0 ? voice.Events[e - 1].Duration.Tuplet : null;
        var next = e + 1 < voice.Events.Count ? voice.Events[e + 1].Duration.Tuplet : null;
        if (previous?.Group != tuplet.Group) {
            yield return new(
                "tuplet",
                new XAttribute("type", "start"),
                new XAttribute("number", "1"),
                new XAttribute("bracket", "yes")
            );
        }

        if (next?.Group != tuplet.Group) {
            yield return new("tuplet", new XAttribute("type", "stop"), new XAttribute("number", "1"));
        }
    }

    /// <summary>A note's ornaments, articulations, dynamics and fermatas.</summary>
    static IEnumerable<XElement> Marks(List<ScoreSymbol> attached) {
        var ornaments = attached
            .Where(static s => s.Kind == ScoreSymbolKind.Trill)
            .Select(static s => new XElement("trill-mark", PlacementAttribute(s.Placement)))
            .ToList();
        if (ornaments.Count > 0) {
            yield return new("ornaments", ornaments);
        }

        var articulations = attached.Select(Articulation).OfType<XElement>().ToList();
        if (articulations.Count > 0) {
            yield return new("articulations", articulations);
        }

        foreach (var symbol in attached) {
            if (symbol is { Kind: ScoreSymbolKind.Dynamic, Text: { } text }) {
                yield return new("dynamics", PlacementAttribute(symbol.Placement), Dynamic(text));
            } else if (symbol.Kind == ScoreSymbolKind.Fermata) {
                var type = symbol.Placement == ScorePlacement.Below ? "inverted" : "upright";
                yield return new("fermata", new XAttribute("type", type));
            }
        }
    }

    static XElement? Articulation(ScoreSymbol symbol) {
        var name = symbol.Kind switch {
            ScoreSymbolKind.Accent => "accent",
            ScoreSymbolKind.Marcato => "strong-accent",
            ScoreSymbolKind.Staccato => "staccato",
            ScoreSymbolKind.Tenuto => "tenuto",
            ScoreSymbolKind.Staccatissimo => "staccatissimo",
            _ => null
        };
        return name is null ? null : new XElement(name, PlacementAttribute(symbol.Placement));
    }

    XElement Slur(string type, ScoreSymbol symbol) =>
        new(
            "slur",
            new XAttribute("type", type),
            new XAttribute("number", MusicXmlScoreWriter.Format(numbers.GetValueOrDefault(symbol.Id, 1))),
            type == "start" ? PlacementAttribute(symbol.Placement) : null
        );

    static XElement Lyric(LyricSyllable lyric) =>
        new(
            "lyric",
            new XAttribute("number", MusicXmlScoreWriter.Format(Math.Max(1, lyric.Verse))),
            new XElement(
                "syllabic",
                lyric.Syllabic switch {
                    Syllabic.Begin => "begin",
                    Syllabic.Middle => "middle",
                    Syllabic.End => "end",
                    _ => "single"
                }
            ),
            new XElement("text", MusicXmlScoreWriter.Clean(lyric.Text)),
            lyric.Extender ? new XElement("extend") : null
        );

    // ---- Directions.

    XElement Direction(Rational offset, int staff, string? placement, XElement? sound, params XElement[] types) =>
        new(
            "direction",
            placement is null ? null : new XAttribute("placement", placement),
            types.Select(static t => new XElement("direction-type", t)),
            Offset(offset, sound is not null),
            multiStaff ? new XElement("staff", MusicXmlScoreWriter.Format(staff + 1)) : null,
            sound
        );

    /// <summary>An offset from the event, if any; with a sound (a tempo), the sound moves with it.</summary>
    XElement? Offset(Rational offset, bool sound) {
        if (!offset.IsPositive) {
            return null;
        }

        return new("offset", sound ? new XAttribute("sound", "yes") : null, score.Duration(offset));
    }

    static XElement Rehearsal(string text) => new("rehearsal", MusicXmlScoreWriter.Clean(text));

    static XElement Metronome(double bpm) =>
        new(
            "metronome",
            new XAttribute("parentheses", "no"),
            new XElement("beat-unit", "quarter"),
            new XElement("per-minute", MusicXmlScoreWriter.Format(bpm))
        );

    static XElement Sound(double bpm) =>
        new("sound", new XAttribute("tempo", MusicXmlScoreWriter.Format(Math.Max(0, bpm))));

    static XElement Wedge(string type, int number) =>
        new("wedge", new XAttribute("type", type), new XAttribute("number", MusicXmlScoreWriter.Format(number)));

    static XElement OctaveShift(string type, int number) =>
        new(
            "octave-shift",
            new XAttribute("type", type),
            new XAttribute("number", MusicXmlScoreWriter.Format(number)),
            new XAttribute("size", "8")
        );

    static XElement[]? DirectionTypes(ScoreSymbol symbol) =>
        symbol.Kind switch {
            ScoreSymbolKind.Dynamic when symbol.Text is { Length: > 0 } text => [new("dynamics", Dynamic(text))],
            ScoreSymbolKind.Text when symbol.Text is { Length: > 0 } text => [
                new("words", MusicXmlScoreWriter.Clean(text))
            ],
            // Tempo text is set in bold (what the reader tells it from plain text by).
            ScoreSymbolKind.TempoText when symbol.Text is { Length: > 0 } text => [
                new("words", new XAttribute("font-weight", "bold"), MusicXmlScoreWriter.Clean(text))
            ],
            ScoreSymbolKind.Rehearsal when symbol.Text is { Length: > 0 } text => [Rehearsal(text)],
            ScoreSymbolKind.PedalDown => [Pedal("start")],
            ScoreSymbolKind.PedalUp => [Pedal("stop")],
            _ => null
        };

    static XElement Pedal(string type) =>
        new("pedal", new XAttribute("type", type), new XAttribute("line", "no"), new XAttribute("sign", "yes"));

    static XElement Dynamic(string text) =>
        ScoreSymbols.Dynamics.Contains(text)
            ? new(text)
            : new("other-dynamics", MusicXmlScoreWriter.Clean(text));

    static string? Placement(ScorePlacement? placement) =>
        placement switch {
            ScorePlacement.Above => "above",
            ScorePlacement.Below => "below",
            _ => null
        };

    static XAttribute? PlacementAttribute(ScorePlacement? placement) =>
        Placement(placement) is { } p ? new XAttribute("placement", p) : null;

    // ---- Harmony.

    XElement Harmony(ChordSymbol chord, Rational offset, int staff) =>
        new(
            "harmony",
            new XAttribute("print-frame", chord.ShowGrid ? "yes" : "no"),
            new XElement(
                "root",
                new XElement("root-step", MusicXmlVocabulary.StepName(chord.Root.Step)),
                Alter("root-alter", chord.Root.Alter)
            ),
            new XElement(
                "kind",
                new XAttribute("text", MusicXmlScoreWriter.Clean(chord.Quality)),
                MusicXmlVocabulary.KindOf(chord.Quality)
            ),
            Bass(chord.Bass),
            offset.IsPositive ? new XElement("offset", score.Duration(offset)) : null,
            multiStaff ? new XElement("staff", MusicXmlScoreWriter.Format(staff + 1)) : null
        );

    static XElement? Bass(PitchName? bass) =>
        bass is null
            ? null
            : new(
                "bass",
                new XElement("bass-step", MusicXmlVocabulary.StepName(bass.Step)),
                Alter("bass-alter", bass.Alter)
            );

    static XElement? Alter(string name, int alter) =>
        alter != 0 ? new XElement(name, MusicXmlScoreWriter.Format(alter)) : null;

    static void Add<TKey, TValue>(Dictionary<TKey, List<TValue>> map, TKey key, TValue value) where TKey : notnull {
        if (!map.TryGetValue(key, out var list)) {
            map[key] = list = [];
        }

        list.Add(value);
    }
}
