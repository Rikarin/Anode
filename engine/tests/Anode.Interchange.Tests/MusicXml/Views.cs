using Anode.Contracts;

namespace Anode.Interchange.Tests.MusicXml;

/// <summary>
///     Small hand-made score views (until workstream A's notation engine and ScoreVectors.json arrive): measures, parts
///     and events with sensible defaults, pitches spelled with sharps unless told otherwise.
/// </summary>
static class Views {
    static readonly (PitchStep Step, int Alter)[] Sharps = [
        (PitchStep.C, 0), (PitchStep.C, 1), (PitchStep.D, 0), (PitchStep.D, 1), (PitchStep.E, 0), (PitchStep.F, 0),
        (PitchStep.F, 1), (PitchStep.G, 0), (PitchStep.G, 1), (PitchStep.A, 0), (PitchStep.A, 1), (PitchStep.B, 0)
    ];

    public static readonly ScoreLayout Layout =
        ScoreLayout.Default with { Title = "Test Piece", Composer = "A. Composer" };

    public static ScoreView View(
        IReadOnlyList<ScoreMeasure> measures,
        IReadOnlyList<ScorePart> parts,
        IReadOnlyList<ScoreSymbol>? symbols = null,
        IReadOnlyList<LyricSyllable>? lyrics = null,
        IReadOnlyList<ChordSymbol>? chords = null,
        bool transposing = false,
        ScoreLayout? layout = null
    ) =>
        new(
            null,
            null,
            ScoreViewMode.Page,
            transposing,
            layout ?? Layout,
            measures,
            parts,
            symbols ?? [],
            lyrics ?? [],
            chords ?? []
        );

    public static ScoreMeasure Measure(
        int number,
        long start,
        int numerator = 4,
        int denominator = 4,
        int fifths = 0,
        bool show = false,
        IReadOnlyList<ScoreTempoMark>? tempos = null,
        IReadOnlyList<ScoreRehearsal>? rehearsals = null,
        Barline barline = Barline.Single,
        KeyMode mode = KeyMode.Major
    ) =>
        new(
            number,
            start,
            960L * 4 * numerator / denominator,
            new(numerator, denominator),
            new(fifths, mode),
            show || number == 1,
            show || number == 1,
            tempos ?? [],
            rehearsals ?? [],
            barline
        );

    public static ScorePart Part(
        string trackId,
        string name,
        IReadOnlyList<Staff> staves,
        IReadOnlyList<ScorePartMeasure> measures,
        string styleId = StaffStyleIds.Treble,
        int transpose = 0,
        string? shortName = null
    ) =>
        new(
            trackId,
            name,
            shortName ?? name[..Math.Min(3, name.Length)] + ".",
            styleId,
            transpose,
            staves.Count > 1,
            staves,
            measures
        );

    public static readonly Staff Treble = new(Clef.Treble, 0, null, 2);

    public static ScorePartMeasure Bar(params ScoreStaffMeasure[] staves) => new(staves);

    public static ScoreStaffMeasure Staff(params ScoreVoice[] voices) => new(null, voices);

    public static ScoreVoice Voice(int number, params ScoreEvent[] events) => new(number, events);

    public static ScoreEvent Rest(
        long tick,
        long ticks,
        NoteValue value,
        int dots = 0,
        ScoreTuplet? tuplet = null,
        IReadOnlyList<string>? symbols = null
    ) =>
        new(
            $"r{tick}",
            ScoreEventKind.Rest,
            tick,
            ticks,
            new(value, dots, tuplet),
            [],
            StemDirection.None,
            null,
            symbols ?? [],
            [],
            []
        );

    public static ScoreEvent Event(
        long tick,
        long ticks,
        NoteValue value,
        ScoreNote[] notes,
        int dots = 0,
        ScoreTuplet? tuplet = null,
        int? beam = null,
        StemDirection stem = StemDirection.Up,
        IReadOnlyList<string>? symbols = null,
        IReadOnlyList<string>? lyrics = null,
        IReadOnlyList<string>? chords = null
    ) =>
        new(
            notes.Length > 1 ? ScoreIds.Chord(notes[0].NoteId) : notes[0].Id,
            notes.Length > 1 ? ScoreEventKind.Chord : ScoreEventKind.Note,
            tick,
            ticks,
            new(value, dots, tuplet),
            notes,
            value is NoteValue.Whole or NoteValue.Breve ? StemDirection.None : stem,
            beam,
            symbols ?? [],
            lyrics ?? [],
            chords ?? []
        );

    /// <summary>A note at concert <paramref name="pitch" />, written <paramref name="transpose" /> above, spelled with sharps.</summary>
    public static ScoreNote Note(
        long noteId,
        int pitch,
        int segment = 1,
        bool tieStart = false,
        bool tieStop = false,
        int transpose = 0,
        Accidental? accidental = null,
        Notehead notehead = Notehead.Normal,
        int staffPosition = 0,
        (PitchStep Step, int Alter)? spelling = null
    ) {
        var written = pitch + transpose;
        var (step, alter) = spelling ?? Sharps[written % 12];
        var octave = (written - alter) / 12 - 1;
        return new(
            ScoreIds.Note(noteId, segment),
            noteId,
            pitch,
            new(step, alter, octave),
            staffPosition,
            accidental,
            tieStart,
            tieStop,
            notehead
        );
    }
}
