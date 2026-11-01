using System.Globalization;
using System.Text.Json.Serialization;

namespace Anode.Contracts;

/// <summary>A staff's clef (docs/10-phase-9.md). The octave clefs read an octave below (8vb) or above (8va) the plain one.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Clef>))]
public enum Clef {
    [JsonStringEnumMemberName("treble")]
    Treble,

    [JsonStringEnumMemberName("bass")]
    Bass,

    /// <summary>C clef on the middle line (viola).</summary>
    [JsonStringEnumMemberName("alto")]
    Alto,

    /// <summary>C clef on the fourth line.</summary>
    [JsonStringEnumMemberName("tenor")]
    Tenor,

    /// <summary>Treble with an 8 below: sounds an octave below the treble clef's reading (guitar, tenor voice).</summary>
    [JsonStringEnumMemberName("treble8vb")]
    Treble8vb,

    [JsonStringEnumMemberName("treble8va")]
    Treble8va,

    /// <summary>Bass with an 8 below (bass guitar, contrabass).</summary>
    [JsonStringEnumMemberName("bass8vb")]
    Bass8vb,

    /// <summary>The neutral clef: positions come from the style's drum map.</summary>
    [JsonStringEnumMemberName("percussion")]
    Percussion,

    /// <summary>No clef drawn; read as treble.</summary>
    [JsonStringEnumMemberName("none")]
    None
}

/// <summary>A notehead shape (a drum map's; every other note is <c>normal</c>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Notehead>))]
public enum Notehead {
    [JsonStringEnumMemberName("normal")]
    Normal,

    /// <summary>An x (hi-hat, cymbals).</summary>
    [JsonStringEnumMemberName("cross")]
    Cross,

    [JsonStringEnumMemberName("circleCross")]
    CircleCross,

    [JsonStringEnumMemberName("diamond")]
    Diamond,

    [JsonStringEnumMemberName("triangle")]
    Triangle,

    [JsonStringEnumMemberName("slash")]
    Slash
}

/// <summary>A stem's direction; <c>none</c> for rests, whole notes and longer.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StemDirection>))]
public enum StemDirection {
    [JsonStringEnumMemberName("none")]
    None,

    [JsonStringEnumMemberName("up")]
    Up,

    [JsonStringEnumMemberName("down")]
    Down
}

/// <summary>
///     One staff of a staff style. <c>transpose</c> is in semitones, written = concert + transpose (B♭ clarinet +2, F
///     horn +7, alto sax +9); an octave clef's octave is the clef's, not part of it. With several staves (top to
///     bottom), a note goes to the first staff whose <c>splitPitch</c> (a concert MIDI pitch) it reaches; the last
///     staff's is null and takes the rest (one staff: null). <c>voices</c> (1…4) is the most voices the notation engine
///     makes on this staff from overlapping notes (1: overlaps become chords or are cut, as <c>noOverlap</c> says).
/// </summary>
public sealed record Staff(Clef Clef, int Transpose, int? SplitPitch, int Voices);

/// <summary>
///     A percussion staff's mapping of a MIDI pitch to a staff position (diatonic steps above the bottom line: 0 the
///     bottom line, 8 the top), a notehead and the instrument's name (for MusicXML and the staff style editor).
/// </summary>
public sealed record DrumMapEntry(int Pitch, int StaffPosition, Notehead Notehead, string Name);

/// <summary>
///     A staff style (Logic's): one to four staves, the drum map of a percussion style (null otherwise; a note it does
///     not map is drawn at its pitch's position on a treble staff), the display quantise its tracks' regions use by
///     default (<see cref="RegionNotation" />: a straight grid in ticks and a triplet grid, 0 for none), and whether its
///     staves are joined by a brace (piano) rather than a bracket. Factory styles (<see cref="StaffStyleIds" />) are the
///     notation engine's data; a project's own are in <c>ProjectScore.staffStyles</c>, with ids chosen by the client
///     (not starting <c>factory.</c>).
/// </summary>
public sealed record StaffStyle(
    string Id,
    string Name,
    IReadOnlyList<Staff> Staves,
    IReadOnlyList<DrumMapEntry>? DrumMap,
    int DisplayQuantizeTicks,
    int TripletQuantizeTicks,
    bool Brace
);

/// <summary>
///     The factory staff styles' ids (data in <c>Anode.Notation.FactoryStaffStyles</c>). A track's
///     <c>staffStyleId</c> null means <see cref="Default" />.
/// </summary>
public static class StaffStyleIds {
    /// <summary>The prefix of every factory id; a project's own styles may not use it.</summary>
    public const string FactoryPrefix = "factory.";

    public const string Treble = "factory.treble";
    public const string Bass = "factory.bass";

    /// <summary>A grand staff, treble and bass, split at C3 (60), braced.</summary>
    public const string Piano = "factory.piano";

    /// <summary>One percussion staff with a General MIDI drum map.</summary>
    public const string Drums = "factory.drums";

    /// <summary>Treble 8vb.</summary>
    public const string Guitar = "factory.guitar";

    /// <summary>Bass 8vb.</summary>
    public const string BassGuitar = "factory.bassGuitar";

    /// <summary>Treble, written a major second above concert (+2).</summary>
    public const string ClarinetBb = "factory.clarinetBb";

    /// <summary>Treble, +2.</summary>
    public const string TrumpetBb = "factory.trumpetBb";

    /// <summary>Treble, a perfect fifth above concert (+7).</summary>
    public const string HornF = "factory.hornF";

    /// <summary>Treble, a major sixth above concert (+9).</summary>
    public const string AltoSax = "factory.altoSax";

    /// <summary>Treble, a major ninth above concert (+14).</summary>
    public const string TenorSax = "factory.tenorSax";

    /// <summary>Alto clef.</summary>
    public const string Viola = "factory.viola";

    /// <summary>Bass clef.</summary>
    public const string Cello = "factory.cello";

    /// <summary>A track without a staff style of its own uses this one.</summary>
    public const string Default = Piano;

    /// <summary>Every factory id, in the order the library lists them.</summary>
    public static IReadOnlyList<string> All { get; } = [
        Treble, Bass, Piano, Drums, Guitar, BassGuitar, ClarinetBb, TrumpetBb, HornF, AltoSax, TenorSax, Viola, Cello
    ];
}

/// <summary>
///     A MIDI region's score settings (Logic's region parameters in the Score Editor's inspector): the display
///     quantise, a straight grid in ticks (240 a 1/16; 0: the track's staff style's grids) and a triplet grid
///     (160 a 1/16 triplet; 0: no triplets; used when the straight grid is not 0); <c>interpretation</c> shortens notes
///     to tidy rests and lengthens them over short gaps; <c>noOverlap</c> cuts a note where the next one starts (else
///     overlaps make voices or chords); <c>syncopation</c> shows syncopated notes undivided rather than tied across the
///     beat; <c>maxDots</c> (0…3) the most dots a value has before it is tied instead. Display quantise moves where
///     notes are drawn, never the notes.
/// </summary>
public sealed record RegionNotation(
    int DisplayQuantizeTicks,
    int TripletQuantizeTicks,
    bool Interpretation,
    bool NoOverlap,
    bool Syncopation,
    int MaxDots
) {
    /// <summary>
    ///     A new region's (Logic's defaults): the staff style's grids, interpretation and no overlap on, syncopation off,
    ///     one dot.
    /// </summary>
    public static RegionNotation Default { get; } = new(0, 0, true, true, false, 1);
}

/// <summary>What a score symbol is (docs/10-phase-9.md, "Symbols").</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ScoreSymbolKind>))]
public enum ScoreSymbolKind {
    // Articulations and ornaments: on a note (noteId).
    [JsonStringEnumMemberName("staccato")]
    Staccato,

    [JsonStringEnumMemberName("staccatissimo")]
    Staccatissimo,

    [JsonStringEnumMemberName("accent")]
    Accent,

    /// <summary>Strong accent (^).</summary>
    [JsonStringEnumMemberName("marcato")]
    Marcato,

    [JsonStringEnumMemberName("tenuto")]
    Tenuto,

    [JsonStringEnumMemberName("fermata")]
    Fermata,

    [JsonStringEnumMemberName("trill")]
    Trill,

    /// <summary>The ties from this note are not drawn.</summary>
    [JsonStringEnumMemberName("tieHidden")]
    TieHidden,

    // At a position (tick), or on a note.
    /// <summary>A dynamic mark; <c>text</c> is one of <c>ScoreSymbols.Dynamics</c> (ppp … fff, sf, sfz, fp…).</summary>
    [JsonStringEnumMemberName("dynamic")]
    Dynamic,

    /// <summary>Free text (<c>text</c>).</summary>
    [JsonStringEnumMemberName("text")]
    Text,

    /// <summary>A tempo or expression text above the staff (<c>text</c>: "Allegro", "rit."); not the tempo itself.</summary>
    [JsonStringEnumMemberName("tempoText")]
    TempoText,

    /// <summary>A rehearsal mark (<c>text</c>: "A"); the project's markers are shown as rehearsal marks too.</summary>
    [JsonStringEnumMemberName("rehearsal")]
    Rehearsal,

    [JsonStringEnumMemberName("pedalDown")]
    PedalDown,

    [JsonStringEnumMemberName("pedalUp")]
    PedalUp,

    // Spanning two notes (noteId and endNoteId) or two positions (tick and endTick).
    /// <summary>From a note to another (both notes).</summary>
    [JsonStringEnumMemberName("slur")]
    Slur,

    /// <summary>A hairpin opening.</summary>
    [JsonStringEnumMemberName("crescendo")]
    Crescendo,

    /// <summary>A hairpin closing.</summary>
    [JsonStringEnumMemberName("diminuendo")]
    Diminuendo,

    /// <summary>An 8va line: written an octave below where it sounds.</summary>
    [JsonStringEnumMemberName("octaveUp")]
    OctaveUp,

    /// <summary>An 8vb line.</summary>
    [JsonStringEnumMemberName("octaveDown")]
    OctaveDown
}

/// <summary>Above or below the staff (or the note).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ScorePlacement>))]
public enum ScorePlacement {
    [JsonStringEnumMemberName("above")]
    Above,

    [JsonStringEnumMemberName("below")]
    Below
}

/// <summary>
///     A score symbol of a track. Attached to a note (<c>noteId</c>, a note of one of the track's MIDI regions): it
///     follows the note and goes with it when the note is deleted (coming back on undo). Positioned: <c>tick</c> (a
///     project tick) on the track's staff <c>staff</c> (0 the top staff of its style; null: the notation engine's
///     choice). Spanning kinds end at <c>endNoteId</c> or <c>endTick</c>, the same kind of anchor as the start.
///     <c>text</c> for dynamic, text, tempo text and rehearsal; <c>placement</c> null is the engine's choice. Which kinds
///     take which anchors: <c>ScoreSymbols</c>.
/// </summary>
public sealed record ScoreSymbol(
    string Id,
    ScoreSymbolKind Kind,
    string TrackId,
    long? NoteId = null,
    long? Tick = null,
    long? EndNoteId = null,
    long? EndTick = null,
    string? Text = null,
    ScorePlacement? Placement = null,
    int? Staff = null
);

/// <summary>The rules of score symbols that commands check.</summary>
public static class ScoreSymbols {
    /// <summary>The kinds attached to a note only (<c>noteId</c>).</summary>
    public static IReadOnlySet<ScoreSymbolKind> NoteOnly { get; } = new HashSet<ScoreSymbolKind> {
        ScoreSymbolKind.Staccato,
        ScoreSymbolKind.Staccatissimo,
        ScoreSymbolKind.Accent,
        ScoreSymbolKind.Marcato,
        ScoreSymbolKind.Tenuto,
        ScoreSymbolKind.Fermata,
        ScoreSymbolKind.Trill,
        ScoreSymbolKind.TieHidden
    };

    /// <summary>The kinds with an end (<c>endNoteId</c> or <c>endTick</c>, after the start); a slur spans notes only.</summary>
    public static IReadOnlySet<ScoreSymbolKind> Spanning { get; } = new HashSet<ScoreSymbolKind> {
        ScoreSymbolKind.Slur,
        ScoreSymbolKind.Crescendo,
        ScoreSymbolKind.Diminuendo,
        ScoreSymbolKind.OctaveUp,
        ScoreSymbolKind.OctaveDown
    };

    /// <summary>The kinds that need <c>text</c>.</summary>
    public static IReadOnlySet<ScoreSymbolKind> WithText { get; } = new HashSet<ScoreSymbolKind> {
        ScoreSymbolKind.Dynamic, ScoreSymbolKind.Text, ScoreSymbolKind.TempoText, ScoreSymbolKind.Rehearsal
    };

    /// <summary>A dynamic's text: one of these.</summary>
    public static IReadOnlyList<string> Dynamics { get; } = [
        "pppp", "ppp", "pp", "p", "mp", "mf", "f", "ff", "fff", "ffff", "sf", "sfz", "sffz", "sfp", "fp", "fz", "rf",
        "rfz"
    ];

    /// <summary>The longest text a symbol, lyric syllable or chord quality holds.</summary>
    public const int MaxTextLength = 200;
}

/// <summary>A syllable's place in its word (MusicXML's and MEI's).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Syllabic>))]
public enum Syllabic {
    /// <summary>A word of one syllable.</summary>
    [JsonStringEnumMemberName("single")]
    Single,

    /// <summary>The first syllable of a word; a hyphen follows.</summary>
    [JsonStringEnumMemberName("begin")]
    Begin,

    [JsonStringEnumMemberName("middle")]
    Middle,

    /// <summary>The last syllable of a word of several.</summary>
    [JsonStringEnumMemberName("end")]
    End
}

/// <summary>
///     A lyric syllable on a note, in verse <c>verse</c> (1…): at most one per note and verse. <c>extender</c> draws
///     the line after a word's last syllable held over several notes. It follows its note and goes with it.
/// </summary>
public sealed record LyricSyllable(string Id, long NoteId, int Verse, string Text, Syllabic Syllabic, bool Extender);

/// <summary>A diatonic step, as MusicXML and MEI spell pitches.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PitchStep>))]
public enum PitchStep {
    [JsonStringEnumMemberName("c")]
    C,

    [JsonStringEnumMemberName("d")]
    D,

    [JsonStringEnumMemberName("e")]
    E,

    [JsonStringEnumMemberName("f")]
    F,

    [JsonStringEnumMemberName("g")]
    G,

    [JsonStringEnumMemberName("a")]
    A,

    [JsonStringEnumMemberName("b")]
    B
}

/// <summary>A spelled pitch class: a step and an alteration in semitones (−2…2: double flat … double sharp).</summary>
public sealed record PitchName(PitchStep Step, int Alter);

/// <summary>
///     A chord symbol on a track at project tick <c>tick</c>: its root, its quality as written after the root ("" major,
///     "m", "7", "maj7", "m7", "dim", "m7b5", "aug", "sus4", "6", "9"…; MusicXML's <c>kind</c> is derived from it),
///     an optional bass ("C/E"), and whether a chord grid (guitar diagram) is drawn with it.
/// </summary>
public sealed record ChordSymbol(
    string Id,
    string TrackId,
    long Tick,
    PitchName Root,
    string Quality,
    PitchName? Bass,
    bool ShowGrid
);

/// <summary>A score set (Logic's): a named selection of tracks, in score order; parts are extracted from it.</summary>
public sealed record ScoreSet(string Id, string Name, IReadOnlyList<string> TrackIds);

/// <summary>A track's instrument names in the score (full on the first system, short on the others).</summary>
public sealed record InstrumentName(string TrackId, string Full, string Short);

/// <summary>Page margins in millimetres.</summary>
public sealed record ScoreMargins(double Top, double Bottom, double Left, double Right);

/// <summary>
///     The score's layout: the page in millimetres (portrait A4 by default), its margins, the staff height (the five
///     lines, mm), the title block, a header and footer printed on every page ("" for none), whether the full score
///     shows transposing instruments at written pitch (parts always do), and instrument names that differ from the
///     track's name (a track without one shows its name, full and short).
/// </summary>
public sealed record ScoreLayout(
    double PageWidthMm,
    double PageHeightMm,
    ScoreMargins Margins,
    double StaffHeightMm,
    string Title,
    string Subtitle,
    string Composer,
    string Header,
    string Footer,
    bool TransposingScore,
    IReadOnlyList<InstrumentName> InstrumentNames
) {
    /// <summary>A new project's: A4 portrait, 15 mm margins, 7 mm staves, no texts, concert pitch, track names.</summary>
    public static ScoreLayout Default { get; } = new(
        210,
        297,
        new(15, 15, 15, 15),
        7,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        false,
        []
    );
}

/// <summary>
///     The project's stored score content (<c>Project.score</c>; docs/10-phase-9.md): its own staff styles, the
///     symbols, lyrics and chord symbols, the score sets and the layout. Everything else in a score is derived from the
///     MIDI each time (the score is a view).
/// </summary>
public sealed record ProjectScore(
    IReadOnlyList<StaffStyle> StaffStyles,
    IReadOnlyList<ScoreSymbol> Symbols,
    IReadOnlyList<LyricSyllable> Lyrics,
    IReadOnlyList<ChordSymbol> Chords,
    IReadOnlyList<ScoreSet> Sets,
    ScoreLayout Layout
) {
    /// <summary>A new project's: nothing stored, the default layout.</summary>
    public static ProjectScore Empty { get; } = new([], [], [], [], [], ScoreLayout.Default);
}

// ---- The rendered score: the notation engine's output (docs/adr/0011-notation-rendering.md).

/// <summary>The Score Editor's view: one long system (linear) or pages (page; the page breaks are Verovio's).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ScoreViewMode>))]
public enum ScoreViewMode {
    [JsonStringEnumMemberName("linear")]
    Linear,

    [JsonStringEnumMemberName("page")]
    Page
}

/// <summary>What a score event is.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ScoreEventKind>))]
public enum ScoreEventKind {
    [JsonStringEnumMemberName("note")]
    Note,

    /// <summary>Notes of one voice that start and end together.</summary>
    [JsonStringEnumMemberName("chord")]
    Chord,

    [JsonStringEnumMemberName("rest")]
    Rest
}

/// <summary>A note value (the undotted, untupled duration).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<NoteValue>))]
public enum NoteValue {
    [JsonStringEnumMemberName("breve")]
    Breve,

    [JsonStringEnumMemberName("whole")]
    Whole,

    [JsonStringEnumMemberName("half")]
    Half,

    [JsonStringEnumMemberName("quarter")]
    Quarter,

    [JsonStringEnumMemberName("eighth")]
    Eighth,

    [JsonStringEnumMemberName("sixteenth")]
    Sixteenth,

    [JsonStringEnumMemberName("thirtySecond")]
    ThirtySecond,

    [JsonStringEnumMemberName("sixtyFourth")]
    SixtyFourth,

    [JsonStringEnumMemberName("hundredTwentyEighth")]
    HundredTwentyEighth
}

/// <summary>A written accidental.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Accidental>))]
public enum Accidental {
    [JsonStringEnumMemberName("natural")]
    Natural,

    [JsonStringEnumMemberName("sharp")]
    Sharp,

    [JsonStringEnumMemberName("flat")]
    Flat,

    [JsonStringEnumMemberName("doubleSharp")]
    DoubleSharp,

    [JsonStringEnumMemberName("doubleFlat")]
    DoubleFlat
}

/// <summary>A measure's closing barline.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Barline>))]
public enum Barline {
    [JsonStringEnumMemberName("single")]
    Single,

    /// <summary>Before a key or meter change, and at a marker.</summary>
    [JsonStringEnumMemberName("double")]
    Double,

    /// <summary>The last measure.</summary>
    [JsonStringEnumMemberName("final")]
    Final
}

/// <summary>A key signature: sharps (positive) or flats (negative), −7…7, and the mode.</summary>
public sealed record ScoreKey(int Fifths, KeyMode Mode);

/// <summary>A spelled written pitch: step, alteration (−2…2) and octave in scientific numbering (C4 is MIDI 60).</summary>
public sealed record ScorePitch(PitchStep Step, int Alter, int Octave);

/// <summary>
///     A tuplet an event belongs to: <c>actual</c> notes in the time of <c>normal</c> (3:2 a triplet); the events of a
///     voice with the same <c>group</c> (1… within the measure's voice) form one bracket.
/// </summary>
public sealed record ScoreTuplet(int Group, int Actual, int Normal);

/// <summary>A written duration: the note value, its dots (0…3) and its tuplet, if any.</summary>
public sealed record ScoreDuration(NoteValue Value, int Dots, ScoreTuplet? Tuplet);

/// <summary>
///     A note of a score event. <c>id</c> is its element id in the MEI (<see cref="ScoreIds.Note" />: the note's first
///     segment <c>n{noteId}</c>, a tied continuation <c>n{noteId}-{k}</c>). <c>pitch</c> is the concert MIDI pitch;
///     <c>written</c> the spelled pitch drawn (transposed when the view is); <c>staffPosition</c> its diatonic steps above
///     the bottom line (0 the bottom line, 8 the top; negative below); <c>accidental</c> the one drawn (null: none);
///     <c>tieStart</c> it is tied to the same note in the voice's next event, <c>tieStop</c> tied from the previous.
/// </summary>
public sealed record ScoreNote(
    string Id,
    long NoteId,
    int Pitch,
    ScorePitch Written,
    int StaffPosition,
    Accidental? Accidental,
    bool TieStart,
    bool TieStop,
    Notehead Notehead
);

/// <summary>
///     A note, chord or rest of a voice. <c>id</c> is its element id (<see cref="ScoreIds" />: a note's is its note's,
///     a chord's <c>c{lowest noteId}</c> with the segment suffix, a rest's <see cref="ScoreIds.Rest" />). <c>tick</c> is
///     where it is drawn (a project tick, display-quantised) and <c>durationTicks</c> its written length (tuplets
///     included); <c>notes</c> low to high (empty for a rest); <c>beam</c> the beam group (1… within the measure's
///     voice; events with the same group are beamed; null: none); <c>symbolIds</c>, <c>lyricIds</c> and
///     <c>chordIds</c> the stored items drawn at it (note-attached ones on the note's first segment; positioned ones on
///     the event at or before their tick).
/// </summary>
public sealed record ScoreEvent(
    string Id,
    ScoreEventKind Kind,
    long Tick,
    long DurationTicks,
    ScoreDuration Duration,
    IReadOnlyList<ScoreNote> Notes,
    StemDirection Stem,
    int? Beam,
    IReadOnlyList<string> SymbolIds,
    IReadOnlyList<string> LyricIds,
    IReadOnlyList<string> ChordIds
);

/// <summary>
///     A voice of a staff in a measure (1… within the staff, 1 the upper), its events in time order filling the
///     measure.
/// </summary>
public sealed record ScoreVoice(int Number, IReadOnlyList<ScoreEvent> Events);

/// <summary>
///     A staff in a measure: a clef change at its start (in the first measure, the staff's clef; else null) and its
///     voices.
/// </summary>
public sealed record ScoreStaffMeasure(Clef? Clef, IReadOnlyList<ScoreVoice> Voices);

/// <summary>A part's measure: one entry per staff of the part, top to bottom.</summary>
public sealed record ScorePartMeasure(IReadOnlyList<ScoreStaffMeasure> Staves);

/// <summary>
///     A track's part in the view: its names (from the layout, else the track's), its staff style and staves, the
///     transposition applied to its written pitches here (the staves' when the view is transposing, else 0), whether
///     its staves are braced, and one measure per <c>ScoreView.measures</c>, in order.
/// </summary>
public sealed record ScorePart(
    string TrackId,
    string Name,
    string ShortName,
    string StaffStyleId,
    int Transpose,
    bool Brace,
    IReadOnlyList<Staff> Staves,
    IReadOnlyList<ScorePartMeasure> Measures
);

/// <summary>A tempo mark: the project's tempo at a tick where it changes (and at the start).</summary>
public sealed record ScoreTempoMark(long Tick, double Bpm);

/// <summary>A project marker shown as a rehearsal mark at its tick.</summary>
public sealed record ScoreRehearsal(long Tick, string MarkerId, string Text);

/// <summary>
///     A measure, common to every part: its bar number (1…), start and length in project ticks, meter and key, whether
///     the meter and key are drawn here (the first measure and changes), its tempo marks and rehearsal marks, and its
///     closing barline.
/// </summary>
public sealed record ScoreMeasure(
    int Number,
    long StartTick,
    long LengthTicks,
    TimeSignature Time,
    ScoreKey Key,
    bool ShowTime,
    bool ShowKey,
    IReadOnlyList<ScoreTempoMark> Tempos,
    IReadOnlyList<ScoreRehearsal> Rehearsals,
    Barline Barline
);

/// <summary>
///     The rendered score (docs/adr/0011-notation-rendering.md): what the notation engine derives from the MIDI, the
///     staff styles, the regions' notation settings, the project's meter, key, tempo and markers, and the stored score
///     content, for a score set (<c>setId</c>; null: every track that holds MIDI, in track order) or one track's part
///     (<c>partTrackId</c>). <c>transposing</c>: written pitches include the staves' transposition (a part, or the full
///     score with <c>layout.transposingScore</c>). Measures cover the project start to the last bar with notes (at
///     least one). <c>symbols</c>, <c>lyrics</c> and <c>chords</c> are the stored items drawn in it. The same project
///     and request give the same view, byte for byte.
/// </summary>
public sealed record ScoreView(
    string? SetId,
    string? PartTrackId,
    ScoreViewMode Mode,
    bool Transposing,
    ScoreLayout Layout,
    IReadOnlyList<ScoreMeasure> Measures,
    IReadOnlyList<ScorePart> Parts,
    IReadOnlyList<ScoreSymbol> Symbols,
    IReadOnlyList<LyricSyllable> Lyrics,
    IReadOnlyList<ChordSymbol> Chords
);

/// <summary>
///     <c>GET /project/score</c>'s answer: the view and its MEI 5 text for Verovio, whose <c>xml:id</c>s are the
///     <see cref="ScoreIds" /> of the view's elements.
/// </summary>
public sealed record ScoreDocument(ScoreView View, string Mei);

/// <summary>
///     The element ids of the rendered score (MEI <c>xml:id</c>s, kept in Verovio's SVG), so the editor maps an element
///     back to the model without the server (mirrored by <c>@anode/notation</c>'s <c>noteIdOf</c> and
///     <c>symbolIdOf</c>). Prefixes: <c>n</c> note, <c>c</c> chord, <c>r</c> rest, <c>s</c> symbol, <c>l</c> lyric,
///     <c>h</c> chord symbol (harmony), <c>m</c> measure. A note drawn as tied segments has <c>n{noteId}</c> on the first
///     and <c>n{noteId}-{k}</c> on the k-th (k ≥ 2).
/// </summary>
public static class ScoreIds {
    public static string Note(long noteId, int segment = 1) =>
        segment <= 1 ? $"n{N(noteId)}" : $"n{N(noteId)}-{N(segment)}";

    public static string Chord(long lowestNoteId, int segment = 1) =>
        segment <= 1 ? $"c{N(lowestNoteId)}" : $"c{N(lowestNoteId)}-{N(segment)}";

    /// <summary>
    ///     A rest: its track, measure number, staff (0…), voice (1…) and index among the voice's rests in the measure
    ///     (1…).
    /// </summary>
    public static string Rest(string trackId, int measure, int staff, int voice, int index) =>
        $"r{trackId}-{N(measure)}-{N(staff)}-{N(voice)}-{N(index)}";

    public static string Symbol(string symbolId) => $"s{symbolId}";

    public static string Lyric(string lyricId) => $"l{lyricId}";

    public static string ChordSymbol(string chordId) => $"h{chordId}";

    public static string Measure(int number) => $"m{N(number)}";

    /// <summary>The note id of a note element (<c>n…</c>), else null.</summary>
    public static long? NoteIdOf(string elementId) {
        if (elementId is not ['n', .. var rest]) {
            return null;
        }

        // n{noteId} or n{noteId}-{segment}: digits without a leading zero, then optionally a dash and digits.
        var dash = rest.IndexOf('-', StringComparison.Ordinal);
        var digits = dash < 0 ? rest : rest[..dash];
        var segment = dash < 0 ? "0" : rest[(dash + 1)..];
        return digits is [not '0', ..]
            && long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            && segment.Length > 0
            && segment.All(char.IsAsciiDigit)
                ? id
                : null;
    }

    /// <summary>The symbol id of a symbol element (<c>s…</c>), else null.</summary>
    public static string? SymbolIdOf(string elementId) => elementId is ['s', _, ..] ? elementId[1..] : null;

    static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
}
