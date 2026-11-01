using Anode.Contracts;
using Anode.Model;
using System.Globalization;
using System.Numerics;
using System.Xml.Linq;

namespace Anode.Interchange.MusicXml;

/// <summary>
///     Reads one part, measure by measure, keeping positions as exact fractions of a quarter: notes (chords, voices,
///     backup and forward, ties merged into one note, grace notes as 1/32 notes just before their principal note, cue
///     notes skipped), concert pitch from <c>transpose</c>, the attributes (divisions, key, time, staves, clefs,
///     transposition), and the directions, notations, lyrics and harmony as score content of the track. A short first
///     measure marked <c>implicit</c> (a pickup) is padded at its start to a full bar, so bars stay on the project's
///     grid.
/// </summary>
sealed class MusicXmlPartReader {
    /// <summary>A grace note's length: a 1/32 note.</summary>
    static readonly Rational GraceLength = new(1, 8);

    readonly MusicXmlScoreReader score;
    readonly XElement part;
    readonly MusicXmlScoreReader.PartInfo info;
    readonly string trackId;

    readonly List<Building> notes = [];
    readonly Dictionary<int, Building> openTies = [];
    readonly Dictionary<int, (long NoteId, ScorePlacement? Placement)> openSlurs = [];
    readonly Dictionary<(string Kind, int Number), (ScoreSymbolKind Kind, Rational Start, int? Staff, ScorePlacement? Placement)> openSpans = [];
    readonly Dictionary<string, List<(int Pitch, int Staff, bool Chord)>> graces = [];
    readonly Dictionary<int, Clef> clefs = [];
    readonly Dictionary<int, HashSet<string>> voices = [];
    readonly HashSet<(ScoreSymbolKind, long)> noteSymbols = [];
    readonly HashSet<(long, int)> noteLyrics = [];
    readonly List<(Rational At, int Velocity)> dynamics = [];
    readonly List<SignatureEvent> signatures = [];
    readonly List<KeyEvent> keys = [];

    Rational divisions = new(1, 1);
    Rational measureStart = Rational.Zero;
    Rational cursor = Rational.Zero;
    Rational highWater = Rational.Zero;
    Rational lastStart = Rational.Zero;

    /// <summary>Where a padded pickup's music starts (0 without one).</summary>
    Rational pickup = Rational.Zero;
    int beats = 4;
    int beatType = 4;
    int staves = 1;
    int transpose;
    int? forPartTranspose;
    bool? brace;
    bool keySeen;
    int fifths;
    KeyMode mode = KeyMode.Major;

    public MusicXmlPartReader(MusicXmlScoreReader score, XElement part, MusicXmlScoreReader.PartInfo info, string trackId) {
        this.score = score;
        this.part = part;
        this.info = info;
        this.trackId = trackId;
    }

    /// <summary>A note being read: ties extend it.</summary>
    sealed class Building(long id, int pitch, Rational start, Rational end, int staff, int? velocity, int channel) {
        public long Id { get; } = id;

        public int Pitch { get; } = pitch;

        public Rational Start { get; } = start;

        public Rational End { get; set; } = end;

        public int Staff { get; } = staff;

        public int? Velocity { get; } = velocity;

        public int Channel { get; } = channel;
    }

    /// <summary>What the score reader needs from a part.</summary>
    public sealed record Result(
        IReadOnlyList<Note> Notes,
        long LengthTicks,
        IReadOnlyList<SignatureEvent> Signatures,
        IReadOnlyList<KeyEvent>? Keys,
        IReadOnlyList<Clef> Clefs,
        int Transpose,
        IReadOnlyList<int?> LowestPitches,
        IReadOnlyList<int> Voices,
        bool? Brace
    );

    public Result Read() {
        var measures = part.Elements("measure").ToList();
        (int Beats, int BeatType)? lastSignature = null;
        for (var m = 0; m < measures.Count; m++) {
            var measure = measures[m];
            if (m == 0) {
                measureStart = pickup = Pickup(measure);
            }

            cursor = Rational.Zero;
            highWater = Rational.Zero;
            lastStart = measureStart;
            var keyBefore = (keySeen, fifths, mode);
            foreach (var element in measure.Elements()) {
                switch (element.Name.LocalName) {
                    case "attributes":
                        Attributes(element);
                        break;
                    case "note":
                        NoteElement(element);
                        break;
                    case "backup":
                        cursor -= Duration(element.Element("duration"), true);
                        if (cursor < Rational.Zero) {
                            cursor = Rational.Zero;
                        }

                        break;
                    case "forward":
                        cursor += Duration(element.Element("duration"), true);
                        highWater = Rational.Max(highWater, cursor);
                        break;
                    case "direction":
                        Direction(element);
                        break;
                    case "harmony":
                        Harmony(element);
                        break;
                    case "sound":
                        Tempo(element, At(Rational.Zero));
                        break;
                }
            }

            // A padded pickup's bar starts at the score's start, before the measure's own start.
            var signature = SignatureLength();
            var implicitMeasure = (string?)measure.Attribute("implicit") == "yes";
            var length = implicitMeasure && highWater.IsPositive ? highWater : Rational.Max(highWater, signature);
            var barStart = m == 0 ? Rational.Zero : measureStart;
            var barLength = measureStart + length - barStart;
            var startTick = barStart.ToTicks();
            var (num, den) = barLength == signature ? (beats, beatType) : Irregular(barLength) ?? (beats, beatType);
            if (lastSignature != (num, den)) {
                signatures.Add(new(startTick, num, den));
                lastSignature = (num, den);
            }

            if (keySeen && (m == 0 || keyBefore != (keySeen, fifths, mode))) {
                // Keys in the project are at concert pitch.
                var concert = transpose == 0 ? fifths : MusicXmlVocabulary.TransposeFifths(fifths, -transpose);
                var key = new KeyEvent(startTick, MusicXmlVocabulary.Tonic(concert, mode), mode);
                if (keys.Count == 0 || keys[^1].Tonic != key.Tonic || keys[^1].Mode != key.Mode) {
                    keys.Add(key);
                }
            }

            measureStart += length;
        }

        foreach (var voice in graces.Keys.ToList()) {
            FlushGraces(voice, measureStart);
        }

        return Finish(measures.Count == 0 ? 0 : measureStart.ToTicks());
    }

    /// <summary>The padding before a pickup: the meter's length less the first measure's, if it is short and implicit.</summary>
    Rational Pickup(XElement measure) {
        if ((string?)measure.Attribute("implicit") != "yes") {
            return Rational.Zero;
        }

        // Scan the measure's length with its own attributes (they are read again when the measure is).
        var scanDivisions = divisions;
        var (scanBeats, scanBeatType) = (beats, beatType);
        Rational position = Rational.Zero, high = Rational.Zero;
        foreach (var element in measure.Elements()) {
            switch (element.Name.LocalName) {
                case "attributes":
                    if (element.Element("divisions") is { } d) {
                        scanDivisions = PositiveDecimal(d.Value, "divisions");
                    }

                    if (element.Element("time") is { } time && Beats(time) is { } b) {
                        (scanBeats, scanBeatType) = b;
                    }

                    break;
                case "note" when element.Element("grace") is null && element.Element("chord") is null:
                case "forward":
                    position += Divisions(element.Element("duration"), scanDivisions, element.Name.LocalName == "forward");
                    high = Rational.Max(high, position);
                    break;
                case "backup":
                    position -= Divisions(element.Element("duration"), scanDivisions, true);
                    break;
            }
        }

        var bar = new Rational(scanBeats * 4L, scanBeatType);
        return high.IsPositive && high < bar ? bar - high : Rational.Zero;
    }

    Rational SignatureLength() => new(beats * 4L, beatType);

    /// <summary>A meter for an irregular bar's length, in the current beat unit or a smaller one.</summary>
    (int, int)? Irregular(Rational length) {
        for (var den = beatType; den <= 64; den *= 2) {
            var units = length * new Rational(den, 4);
            if (units.Denominator == 1 && units.Numerator is > 0 and <= 128) {
                return ((int)units.Numerator, den);
            }
        }

        return null;
    }

    Rational At(Rational offset) => Rational.Max(Rational.Zero, measureStart + cursor + offset);

    // ---- Attributes.

    void Attributes(XElement attributes) {
        if (attributes.Element("divisions") is { } d) {
            divisions = PositiveDecimal(d.Value, "divisions");
        }

        if (attributes.Element("key") is { } key && Integer(key.Element("fifths")?.Value) is { } f) {
            keySeen = true;
            fifths = Math.Clamp(f, -7, 7);
            mode = key.Element("mode")?.Value.Trim() == "minor" ? KeyMode.Minor : KeyMode.Major;
        }

        if (attributes.Element("time") is { } time && Beats(time) is { } b) {
            (beats, beatType) = b;
        }

        if (Integer(attributes.Element("staves")?.Value) is { } count) {
            staves = Math.Clamp(count, 1, 4);
        }

        if (attributes.Element("part-symbol")?.Value.Trim() is { } symbol) {
            brace = symbol == "brace";
        }

        foreach (var clef in attributes.Elements("clef")) {
            var number = Math.Clamp(Integer((string?)clef.Attribute("number")) ?? 1, 1, 4) - 1;
            clefs.TryAdd(
                number,
                MusicXmlVocabulary.ParseClef(
                    clef.Element("sign")?.Value,
                    Integer(clef.Element("line")?.Value),
                    Integer(clef.Element("clef-octave-change")?.Value) ?? 0
                )
            );
        }

        if (attributes.Element("transpose") is { } t) {
            transpose = WrittenAboveConcert(t);
        }

        if (attributes.Element("for-part")?.Element("part-transpose") is { } forPart) {
            forPartTranspose = WrittenAboveConcert(forPart);
        }
    }

    /// <summary>A <c>transpose</c> (written to sounding) as the staff's transposition (written above concert).</summary>
    static int WrittenAboveConcert(XElement transpose) =>
        -((Integer(transpose.Element("chromatic")?.Value) ?? 0) + (12 * (Integer(transpose.Element("octave-change")?.Value) ?? 0)));

    static (int, int)? Beats(XElement time) {
        // "3+2" beats add up; a senza-misura time has none.
        var beats = time.Element("beats")?.Value;
        var type = Integer(time.Element("beat-type")?.Value);
        if (beats is null || type is not (1 or 2 or 4 or 8 or 16 or 32 or 64)) {
            return null;
        }

        var sum = 0;
        foreach (var part in beats.Split('+')) {
            if (Integer(part) is not { } n || n <= 0) {
                return null;
            }

            sum += n;
        }

        return sum is > 0 and <= 128 ? (sum, type.Value) : null;
    }

    // ---- Notes.

    void NoteElement(XElement note) {
        var voice = note.Element("voice")?.Value.Trim() ?? "1";
        var staff = Math.Clamp(Integer(note.Element("staff")?.Value) ?? 1, 1, 4) - 1;
        var chord = note.Element("chord") is not null;
        if (note.Element("grace") is not null) {
            if (Pitch(note) is { } gracePitch) {
                Add(graces, voice, (gracePitch, staff, chord));
            }

            return;
        }

        var duration = Duration(note.Element("duration"), false);
        var start = chord ? lastStart : At(Rational.Zero);
        if (!chord) {
            lastStart = start;
            cursor += duration;
            highWater = Rational.Max(highWater, cursor);
        }

        if (note.Element("rest") is not null || note.Element("cue") is not null) {
            return;
        }

        var pitch = Pitch(note) ?? throw new FormatException("A note without a pitch.");
        if (!voices.TryGetValue(staff, out var staffVoices)) {
            voices[staff] = staffVoices = [];
        }

        staffVoices.Add(voice);
        if (!chord) {
            FlushGraces(voice, start);
        }

        var segment = openTies.Remove(pitch, out var open) && Near(open.End, start) ? open : null;
        if (segment is null) {
            var velocity = Number((string?)note.Attribute("dynamics")) is { } percent
                ? (int?)Math.Clamp((int)Math.Round(percent * 90 / 100), 1, 127)
                : null;
            segment = new(score.NextNoteId(), pitch, start, start + duration, staff, velocity, Unpitched(note) ? 10 : 1);
            notes.Add(segment);
        } else {
            segment.End = start + duration;
        }

        var tieStart = note.Elements("tie").Any(t => (string?)t.Attribute("type") == "start")
            || note.Elements("notations").Elements("tied").Any(t => (string?)t.Attribute("type") == "start");
        if (tieStart) {
            openTies[pitch] = segment;
        }

        foreach (var notations in note.Elements("notations")) {
            Notations(notations, segment.Id, start);
        }

        foreach (var lyric in note.Elements("lyric")) {
            Lyric(lyric, segment.Id);
        }
    }

    static bool Unpitched(XElement note) => note.Element("unpitched") is not null;

    /// <summary>A note's concert MIDI pitch, or null for a rest.</summary>
    int? Pitch(XElement note) {
        if (note.Element("pitch") is { } pitch) {
            var step = MusicXmlVocabulary.ParseStep(pitch.Element("step")?.Value)
                ?? throw new FormatException("A pitch without a step.");
            var alter = (int)Math.Round(Number(pitch.Element("alter")?.Value) ?? 0);
            var octave = Integer(pitch.Element("octave")?.Value) ?? throw new FormatException("A pitch without an octave.");
            return Math.Clamp(MusicXmlVocabulary.Midi(step, alter, octave) - transpose, 0, 127);
        }

        if (note.Element("unpitched") is { } unpitched) {
            if ((string?)note.Element("instrument")?.Attribute("id") is { } instrument
                && info.Unpitched.TryGetValue(instrument, out var key)) {
                return key;
            }

            if (MusicXmlVocabulary.ParseStep(unpitched.Element("display-step")?.Value) is { } step
                && Integer(unpitched.Element("display-octave")?.Value) is { } octave) {
                return Math.Clamp(MusicXmlVocabulary.Midi(step, 0, octave), 0, 127);
            }

            return 38;
        }

        return null;
    }

    /// <summary>Grace notes become 1/32 notes ending where their principal note starts (not before the score's start).</summary>
    void FlushGraces(string voice, Rational principal) {
        if (!graces.Remove(voice, out var pending) || pending.Count == 0) {
            return;
        }

        var slots = pending.Count(g => !g.Chord);
        var slot = -1;
        foreach (var (pitch, staff, chord) in pending) {
            if (!chord || slot < 0) {
                slot++;
            }

            var start = principal - (GraceLength * new Rational(slots - slot, 1));
            if (start < Rational.Zero) {
                start = Rational.Zero;
            }

            notes.Add(new(score.NextNoteId(), pitch, start, start + GraceLength, staff, null, 1));
        }
    }

    void Notations(XElement notations, long noteId, Rational at) {
        var hidden = (string?)notations.Attribute("print-object") == "no";
        foreach (var child in notations.Elements()) {
            switch (child.Name.LocalName) {
                case "tied" when hidden && (string?)child.Attribute("type") == "start":
                    NoteSymbol(ScoreSymbolKind.TieHidden, noteId, null);
                    break;
                case "slur":
                    Slur(child, noteId);
                    break;
                case "articulations":
                    foreach (var articulation in child.Elements()) {
                        var kind = articulation.Name.LocalName switch {
                            "accent" => ScoreSymbolKind.Accent,
                            "strong-accent" => ScoreSymbolKind.Marcato,
                            "staccato" => ScoreSymbolKind.Staccato,
                            "tenuto" => ScoreSymbolKind.Tenuto,
                            "staccatissimo" or "spiccato" => ScoreSymbolKind.Staccatissimo,
                            "detached-legato" => ScoreSymbolKind.Tenuto,
                            _ => (ScoreSymbolKind?)null
                        };
                        if (kind is { } k) {
                            NoteSymbol(k, noteId, Placement(articulation));
                        }

                        if (articulation.Name.LocalName == "detached-legato") {
                            NoteSymbol(ScoreSymbolKind.Staccato, noteId, Placement(articulation));
                        }
                    }

                    break;
                case "ornaments":
                    foreach (var ornament in child.Elements("trill-mark")) {
                        NoteSymbol(ScoreSymbolKind.Trill, noteId, Placement(ornament));
                    }

                    break;
                case "fermata":
                    NoteSymbol(
                        ScoreSymbolKind.Fermata,
                        noteId,
                        (string?)child.Attribute("type") == "inverted" ? ScorePlacement.Below : null
                    );
                    break;
                case "dynamics":
                    foreach (var text in DynamicTexts(child)) {
                        if (ScoreSymbols.Dynamics.Contains(text)) {
                            AddSymbol(new(score.Ids.Next(IdPrefix.ScoreSymbol), ScoreSymbolKind.Dynamic, trackId, noteId, Text: text, Placement: Placement(child)));
                            Level(at, text);
                        }
                    }

                    break;
            }
        }
    }

    void NoteSymbol(ScoreSymbolKind kind, long noteId, ScorePlacement? placement) {
        if (noteSymbols.Add((kind, noteId))) {
            AddSymbol(new(score.Ids.Next(IdPrefix.ScoreSymbol), kind, trackId, noteId, Placement: placement));
        }
    }

    void Slur(XElement slur, long noteId) {
        var number = Integer((string?)slur.Attribute("number")) ?? 1;
        switch ((string?)slur.Attribute("type")) {
            case "start":
                openSlurs[number] = (noteId, Placement(slur));
                break;
            case "stop" when openSlurs.Remove(number, out var open) && open.NoteId != noteId:
                AddSymbol(
                    new(
                        score.Ids.Next(IdPrefix.ScoreSymbol),
                        ScoreSymbolKind.Slur,
                        trackId,
                        open.NoteId,
                        EndNoteId: noteId,
                        Placement: open.Placement
                    )
                );
                break;
        }
    }

    void Lyric(XElement lyric, long noteId) {
        var texts = new List<string>();
        foreach (var element in lyric.Elements()) {
            if (element.Name.LocalName == "text") {
                texts.Add(element.Value);
            } else if (element.Name.LocalName == "elision") {
                texts.Add(element.Value.Length > 0 ? element.Value : "‿");
            }
        }

        if (MusicXmlScoreReader.Text(string.Concat(texts)) is not { } text) {
            return;
        }

        var verse = Verse((string?)lyric.Attribute("number"));
        if (!noteLyrics.Add((noteId, verse))) {
            return;
        }

        var syllabic = lyric.Element("syllabic")?.Value.Trim() switch {
            "begin" => Syllabic.Begin,
            "middle" => Syllabic.Middle,
            "end" => Syllabic.End,
            _ => Syllabic.Single
        };
        var extend = lyric.Element("extend") is { } e && (string?)e.Attribute("type") is null or "start" or "continue";
        score.Lyrics.Add(new(score.Ids.Next(IdPrefix.Lyric), noteId, verse, text, syllabic, extend));
    }

    /// <summary>A lyric's verse from its number ("1", "verse2", "part1verse3": the last digits; 1 when none).</summary>
    static int Verse(string? number) {
        if (number is null) {
            return 1;
        }

        var end = number.Length;
        while (end > 0 && !char.IsAsciiDigit(number[end - 1])) {
            end--;
        }

        var start = end;
        while (start > 0 && char.IsAsciiDigit(number[start - 1])) {
            start--;
        }

        return Integer(number[start..end]) is { } verse ? Math.Clamp(verse, 1, 99) : 1;
    }

    // ---- Directions and harmony.

    void Direction(XElement direction) {
        var at = At(Offset(direction));
        var staff = StaffOf(direction);
        var placement = Placement(direction);
        var tempo = direction.Element("sound") is { } sound && Number((string?)sound.Attribute("tempo")) is not null;
        var types = direction.Elements("direction-type").SelectMany(t => t.Elements()).ToList();
        var hasMetronome = types.Any(t => t.Name.LocalName == "metronome");
        foreach (var type in types) {
            switch (type.Name.LocalName) {
                case "dynamics":
                    foreach (var text in DynamicTexts(type)) {
                        var kind = ScoreSymbols.Dynamics.Contains(text) ? ScoreSymbolKind.Dynamic : ScoreSymbolKind.Text;
                        Positioned(kind, at, staff, placement ?? Placement(type), text);
                        Level(at, text);
                    }

                    break;
                case "words":
                    if (MusicXmlScoreReader.Text(type.Value) is { } words) {
                        var bold = (string?)type.Attribute("font-weight") == "bold";
                        Positioned(bold || tempo || hasMetronome ? ScoreSymbolKind.TempoText : ScoreSymbolKind.Text, at, staff, placement, words);
                    }

                    break;
                case "rehearsal":
                    if (MusicXmlScoreReader.Text(type.Value) is { } mark) {
                        score.AddMarker(at.ToTicks(), mark);
                    }

                    break;
                case "metronome" when !tempo:
                    if (Metronome(type) is { } bpm) {
                        score.AddTempo(TempoTick(at), bpm);
                    }

                    break;
                case "pedal":
                    switch ((string?)type.Attribute("type")) {
                        case "start":
                            Positioned(ScoreSymbolKind.PedalDown, at, staff, placement, null);
                            break;
                        case "stop":
                            Positioned(ScoreSymbolKind.PedalUp, at, staff, placement, null);
                            break;
                        case "change":
                            Positioned(ScoreSymbolKind.PedalUp, at, staff, placement, null);
                            Positioned(ScoreSymbolKind.PedalDown, at, staff, placement, null);
                            break;
                    }

                    break;
                case "wedge":
                    Span(
                        "wedge",
                        type,
                        (string?)type.Attribute("type") switch {
                            "crescendo" => ScoreSymbolKind.Crescendo,
                            "diminuendo" => ScoreSymbolKind.Diminuendo,
                            _ => null
                        },
                        at,
                        staff,
                        placement
                    );
                    break;
                case "octave-shift":
                    // MusicXML's "down" is an 8va line: the notes are written below their pitch.
                    Span(
                        "octave",
                        type,
                        (string?)type.Attribute("type") switch {
                            "down" => ScoreSymbolKind.OctaveUp,
                            "up" => ScoreSymbolKind.OctaveDown,
                            _ => null
                        },
                        at,
                        staff,
                        placement
                    );
                    break;
            }
        }

        if (direction.Element("sound") is { } s) {
            Tempo(s, at);
        }
    }

    void Tempo(XElement sound, Rational at) {
        if (Number((string?)sound.Attribute("tempo")) is { } bpm) {
            score.AddTempo(TempoTick(at), bpm);
        }
    }

    /// <summary>A tempo at a pickup's start holds from the score's start.</summary>
    long TempoTick(Rational at) => at <= pickup ? 0 : at.ToTicks();

    /// <summary>A metronome mark's tempo in quarters per minute (null: not a beat unit and a number).</summary>
    static double? Metronome(XElement metronome) {
        if (MusicXmlVocabulary.QuartersOfType(metronome.Element("beat-unit")?.Value) is not { } unit
            || Number(metronome.Element("per-minute")?.Value) is not { } perMinute) {
            return null;
        }

        var dots = metronome.Elements("beat-unit-dot").Count();
        var factor = 2 - Math.Pow(0.5, dots);
        return perMinute * unit.Numerator / unit.Denominator * factor;
    }

    void Span(string family, XElement element, ScoreSymbolKind? kind, Rational at, int? staff, ScorePlacement? placement) {
        var number = Integer((string?)element.Attribute("number")) ?? 1;
        if (kind is { } k) {
            openSpans[(family, number)] = (k, at, staff, placement);
            return;
        }

        if ((string?)element.Attribute("type") == "stop" && openSpans.Remove((family, number), out var open)) {
            var (start, end) = (open.Start.ToTicks(), at.ToTicks());
            if (end > start) {
                AddSymbol(
                    new(
                        score.Ids.Next(IdPrefix.ScoreSymbol),
                        open.Kind,
                        trackId,
                        Tick: start,
                        EndTick: end,
                        Placement: open.Placement,
                        Staff: open.Staff
                    )
                );
            }
        }
    }

    void Positioned(ScoreSymbolKind kind, Rational at, int? staff, ScorePlacement? placement, string? text) =>
        AddSymbol(
            new(score.Ids.Next(IdPrefix.ScoreSymbol), kind, trackId, Tick: at.ToTicks(), Text: text, Placement: placement, Staff: staff)
        );

    void Harmony(XElement harmony) {
        var root = harmony.Element("root");
        if (root is null || MusicXmlVocabulary.ParseStep(root.Element("root-step")?.Value) is not { } step) {
            return;
        }

        var kind = harmony.Element("kind");
        if (kind?.Value.Trim() == "none") {
            return;
        }

        var quality = kind?.Attribute("text") is { } text
            ? MusicXmlScoreReader.Text(text.Value) ?? string.Empty
            : MusicXmlVocabulary.QualityOf(kind?.Value.Trim() ?? "major");
        var bass = harmony.Element("bass") is { } b && MusicXmlVocabulary.ParseStep(b.Element("bass-step")?.Value) is { } bassStep
            ? new PitchName(bassStep, Alter(b.Element("bass-alter")))
            : null;
        var grid = (string?)harmony.Attribute("print-frame") == "yes" || harmony.Element("frame") is not null;
        score.Chords.Add(
            new(
                score.Ids.Next(IdPrefix.ChordSymbol),
                trackId,
                At(Offset(harmony)).ToTicks(),
                new(step, Alter(root.Element("root-alter"))),
                quality,
                bass,
                grid
            )
        );
    }

    static int Alter(XElement? alter) => Math.Clamp((int)Math.Round(Number(alter?.Value) ?? 0), -2, 2);

    /// <summary>A direction's or harmony's offset from the current position, in quarters (it may be negative).</summary>
    Rational Offset(XElement element) =>
        element.Element("offset") is { } offset
            ? Signed(offset.Value) * new Rational(divisions.Denominator, divisions.Numerator)
            : Rational.Zero;

    int? StaffOf(XElement element) =>
        staves > 1 && Integer(element.Element("staff")?.Value) is { } staff ? Math.Clamp(staff, 1, staves) - 1 : null;

    static ScorePlacement? Placement(XElement element) =>
        (string?)element.Attribute("placement") switch {
            "above" => ScorePlacement.Above,
            "below" => ScorePlacement.Below,
            _ => null
        };

    static IEnumerable<string> DynamicTexts(XElement dynamics) =>
        dynamics.Elements()
            .Select(d => d.Name.LocalName == "other-dynamics" ? MusicXmlScoreReader.Text(d.Value) : d.Name.LocalName)
            .OfType<string>();

    void Level(Rational at, string dynamic) {
        if (MusicXmlVocabulary.Velocity(dynamic) is { } velocity) {
            dynamics.Add((at, velocity));
        }
    }

    void AddSymbol(ScoreSymbol symbol) => score.Symbols.Add(symbol);

    // ---- The result.

    Result Finish(long lengthTicks) {
        var levels = dynamics.OrderBy(d => d.At).ToList();
        var result = new List<Note>();
        foreach (var note in notes.OrderBy(n => n.Start).ThenBy(n => n.Pitch).ThenBy(n => n.Id)) {
            var start = note.Start.ToTicks();
            var velocity = note.Velocity ?? LevelAt(levels, note.Start) ?? 80;
            result.Add(new(note.Id, note.Pitch, velocity, note.Channel, start, Math.Max(1, note.End.ToTicks() - start), 0));
        }

        var count = Math.Clamp(Math.Max(staves, clefs.Count == 0 ? 1 : clefs.Keys.Max() + 1), 1, 4);
        var staffClefs = new List<Clef>();
        var lowest = new List<int?>();
        var voiceCounts = new List<int>();
        for (var s = 0; s < count; s++) {
            staffClefs.Add(clefs.TryGetValue(s, out var clef) ? clef : s == 0 ? Clef.Treble : Clef.Bass);
            lowest.Add(notes.Where(n => n.Staff == s).Select(n => (int?)n.Pitch).Min());
            voiceCounts.Add(voices.TryGetValue(s, out var v) ? v.Count : 1);
        }

        return new(
            result,
            lengthTicks,
            signatures.Count > 0 ? signatures : [new(0, 4, 4)],
            keys.Count > 0 ? keys : null,
            staffClefs,
            transpose != 0 ? transpose : forPartTranspose ?? 0,
            lowest,
            voiceCounts,
            brace
        );
    }

    static int? LevelAt(List<(Rational At, int Velocity)> levels, Rational at) {
        int? velocity = null;
        foreach (var level in levels) {
            if (level.At > at) {
                break;
            }

            velocity = level.Velocity;
        }

        return velocity;
    }

    // ---- Numbers.

    Rational Duration(XElement? duration, bool allowZero) => Divisions(duration, divisions, allowZero);

    static Rational Divisions(XElement? duration, Rational divisions, bool allowZero) {
        if (duration is null) {
            return allowZero ? Rational.Zero : throw new FormatException("A note without a duration.");
        }

        var value = Signed(duration.Value);
        if (value < Rational.Zero) {
            throw new FormatException($"A negative duration ({duration.Value}).");
        }

        return value * new Rational(divisions.Denominator, divisions.Numerator);
    }

    /// <summary>Two times within a tick of each other (a tie across a rounded tuplet still joins).</summary>
    static bool Near(Rational a, Rational b) => Math.Abs(a.ToTicks() - b.ToTicks()) <= 1;

    static Rational PositiveDecimal(string text, string what) {
        var value = Signed(text);
        return value.IsPositive ? value : throw new FormatException($"The {what} must be positive ({text}).");
    }

    /// <summary>A decimal number as an exact fraction.</summary>
    static Rational Signed(string text) {
        if (!decimal.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) {
            throw new FormatException($"Not a number: '{text}'.");
        }

        var bits = decimal.GetBits(decimal.Round(value, 6));
        var scale = (bits[3] >> 16) & 0xFF;
        var mantissa = new BigInteger((uint)bits[0]) | (new BigInteger((uint)bits[1]) << 32) | (new BigInteger((uint)bits[2]) << 64);
        if (bits[3] < 0) {
            mantissa = -mantissa;
        }

        return new((long)mantissa, (long)BigInteger.Pow(10, scale));
    }

    internal static int? Integer(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    static double? Number(string? text) =>
        double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? value
            : null;

    static void Add<TKey, TValue>(Dictionary<TKey, List<TValue>> map, TKey key, TValue value) where TKey : notnull {
        if (!map.TryGetValue(key, out var list)) {
            map[key] = list = [];
        }

        list.Add(value);
    }
}
