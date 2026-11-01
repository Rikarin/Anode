using Anode.Contracts;

namespace Anode.Notation;

/// <summary>
///     One staff of one measure of a part (docs/notation/README.md): its voices' units and rests, written as tuplets
///     and tied values, spelled with the bar's accidentals, beamed by the meter and stemmed.
/// </summary>
sealed class StaffMeasureBuilder(
    TrackNotation notation,
    int staff,
    Bar bar,
    ScoreMeasure measure,
    RegionNotation settings,
    bool transposing,
    Attachments attachments
) {
    readonly Staff definition = notation.Style.Staves[staff];

    public ScoreStaffMeasure Build() {
        var units = notation.Staves[staff].Where(u => u.Start < bar.End && u.End > bar.Start).ToList();
        var voices = units.Select(static u => u.Voice).Append(1).Distinct().Order().ToList();
        var drafts = new List<List<Draft>>();
        foreach (var voice in voices) {
            var list = Notate([..units.Where(u => u.Voice == voice).OrderBy(static u => u.Start)]);
            foreach (var draft in list) {
                draft.Voice = voice;
            }

            Beam(list);
            drafts.Add(list);
        }

        Spell(drafts.SelectMany(static d => d));
        foreach (var list in drafts) {
            Stem(list, voices.Count > 1, definition.Clef == Clef.Percussion);
        }

        var result = new List<ScoreVoice>(voices.Count);
        for (var v = 0; v < voices.Count; v++) {
            result.Add(new(voices[v], Events(drafts[v], voices[v])));
        }

        return new(measure.Number == 1 ? definition.Clef : null, result);
    }

    /// <summary>A voice's units and the rests between them as written pieces.</summary>
    List<Draft> Notate(List<Unit> units) {
        if (units.Count == 0) {
            return [new() { Tick = bar.Start, End = bar.End, Written = new(NoteValue.Whole, 0), FullMeasure = true }];
        }

        var segments = Segments(units);
        var groups = TupletGroups(segments);
        var context = SplitContext.Of(bar.Tree, bar.Time, settings);
        var drafts = new List<Draft>();
        var numbers = new Dictionary<long, int>();
        foreach (var (start, end, unit) in segments) {
            var cuts = groups.SelectMany(static g => new[] { g.Start, g.End })
                .Where(c => c > start && c < end)
                .Append(start)
                .Append(end)
                .Distinct()
                .Order()
                .ToList();
            for (var c = 0; c + 1 < cuts.Count; c++) {
                Write(cuts[c], cuts[c + 1], unit, groups, numbers, context, drafts);
            }
        }

        return drafts;
    }

    /// <summary>A voice's units clipped to the bar, and the rests between them.</summary>
    List<(long Start, long End, Unit? Unit)> Segments(List<Unit> units) {
        var segments = new List<(long Start, long End, Unit? Unit)>();
        var at = bar.Start;
        foreach (var unit in units) {
            var from = Math.Max(unit.Start, bar.Start);
            var to = Math.Min(unit.End, bar.End);
            if (from > at) {
                segments.Add((at, from, null));
            }

            segments.Add((from, to, unit));
            at = to;
        }

        if (at < bar.End) {
            segments.Add((at, bar.End, null));
        }

        return segments;
    }

    /// <summary>A piece of a segment: one triplet value inside a tuplet group, else tied values by the meter.</summary>
    void Write(
        long from,
        long to,
        Unit? unit,
        List<(long Start, long End, long Unit)> groups,
        Dictionary<long, int> numbers,
        SplitContext context,
        List<Draft> drafts
    ) {
        var group = groups.Find(g => from >= g.Start && to <= g.End);
        if (group.Unit > 0 && (to - from) * 3 % 2 == 0 && Meter.TryWrite((to - from) * 3 / 2, 0, out var tuplet)) {
            if (!numbers.TryGetValue(group.Start, out var number)) {
                number = numbers.Count + 1;
                numbers[group.Start] = number;
            }

            drafts.Add(Piece(from, to, unit, tuplet, number));
            return;
        }

        var pieces = new List<(long Start, long End, Written Written)>();
        Meter.Split(context, from, to, unit is null, pieces);
        drafts.AddRange(pieces.Select(p => Piece(p.Start, p.End, unit, p.Written, 0)));
    }

    Draft Piece(long from, long to, Unit? unit, Written written, int tuplet) {
        var draft = new Draft {
            Tick = from,
            End = to,
            Written = written,
            Tuplet = tuplet,
            Unit = unit
        };
        if (unit is null) {
            return draft;
        }

        unit.Segments++;
        draft.Segment = unit.Segments;
        draft.TieStop = from > unit.Start;
        draft.TieStart = to < unit.End;
        foreach (var note in unit.Notes) {
            draft.Notes.Add(Place(note));
        }

        draft.Notes.Sort(static (x, y) => x.Position != y.Position ? x.Position.CompareTo(y.Position) :
            x.Note.Pitch != y.Note.Pitch ? x.Note.Pitch.CompareTo(y.Note.Pitch) : x.Note.Id.CompareTo(y.Note.Id)
        );
        return draft;
    }

    /// <summary>A note's written pitch and staff position: from the drum map on a percussion staff, else spelled.</summary>
    NoteDraft Place(SourceNote note) {
        if (definition.Clef == Clef.Percussion && notation.Style.DrumMap is { } map) {
            foreach (var entry in map) {
                if (entry.Pitch == note.Pitch) {
                    return new(
                        note,
                        StaffPositions.WrittenAt(Clef.Percussion, entry.StaffPosition),
                        entry.StaffPosition,
                        entry.Notehead,
                        true
                    );
                }
            }
        }

        var placement = StaffPositions.PlacementOf(definition.Clef, WrittenKey, note.Pitch + Transpose);
        return new(note, placement.Written, placement.StaffPosition, Notehead.Normal, false);
    }

    int Transpose => transposing ? definition.Transpose : 0;

    ScoreKey WrittenKey =>
        definition.Clef == Clef.Percussion ? new(0, KeyMode.Major) : StaffPositions.WrittenKey(measure.Key, Transpose);

    /// <summary>
    ///     The tuplet groups of a voice (docs/notation/README.md, "Tuplets"): in each span drawn as triplets where the voice
    ///     has a boundary, groups of three units, the unit the coarsest (doubling the triplet grid) that every boundary
    ///     falls on; a group without a boundary inside is written straight.
    /// </summary>
    List<(long Start, long End, long Unit)> TupletGroups(List<(long Start, long End, Unit? Unit)> segments) {
        var boundaries = segments.Select(static s => s.End).ToList();
        var groups = new List<(long Start, long End, long Unit)>();
        foreach (var (start, end, triplet) in notation.Quantizer.TripletSpans(bar)) {
            var inside = boundaries.Where(b => b > start && b < end).ToList();
            if (inside.Count == 0 || inside.Exists(b => (b - start) % triplet != 0)) {
                continue;
            }

            long unit = triplet;
            while ((end - start) % (6 * unit) == 0 && inside.TrueForAll(b => (b - start) % (2 * unit) == 0)) {
                unit *= 2;
            }

            for (var g = start; g + 3 * unit <= end; g += 3 * unit) {
                if (inside.Exists(b => b > g && b < g + 3 * unit)) {
                    groups.Add((g, g + 3 * unit, unit));
                }
            }
        }

        return groups;
    }

    /// <summary>
    ///     Beams by the meter (docs/notation/README.md, "Beams"): consecutive eighths and shorter within a beat, not across
    ///     rests or tuplet boundaries; in 4/4 (and other simple meters of four beats) a half bar of plain eighths is one
    ///     beam.
    /// </summary>
    void Beam(List<Draft> drafts) {
        var group = 0;
        foreach (var (start, end) in BeamSpans(drafts)) {
            var run = new List<Draft>();
            foreach (var draft in drafts.Where(d => d.Tick >= start && d.End <= end)) {
                run = Extend(run, draft, ref group);
            }

            Flush(run, ref group);
        }
    }

    /// <summary>A run of beamable events extended by <paramref name="draft" />, or flushed and restarted.</summary>
    static List<Draft> Extend(List<Draft> run, Draft draft, ref int group) {
        var beamable = draft.Notes.Count > 0 && draft.Written.Value >= NoteValue.Eighth;
        if (beamable && (run.Count == 0 || (run[^1].End == draft.Tick && run[^1].Tuplet == draft.Tuplet))) {
            run.Add(draft);
            return run;
        }

        Flush(run, ref group);
        return beamable ? [draft] : [];
    }

    /// <summary>A run of two or more is the next beam group.</summary>
    static void Flush(List<Draft> run, ref int group) {
        if (run.Count < 2) {
            return;
        }

        group++;
        foreach (var draft in run) {
            draft.Beam = group;
        }
    }

    /// <summary>The spans beams stay within: the beats, or a half bar of plain eighths in a simple meter of four beats.</summary>
    List<(long Start, long End)> BeamSpans(List<Draft> drafts) {
        var spans = bar.Beats.Select(static b => (b.Start, b.End)).ToList();
        var simple = bar.Time.Denominator <= 4 && !Meter.IsCompound(bar.Time.Numerator, bar.Time.Denominator);
        if (!simple || bar.Tree.Children is not [{ IsBeat: false } first, { IsBeat: false } second]) {
            return spans;
        }

        foreach (var half in new[] { first, second }) {
            var inside = drafts.FindAll(d => d.Tick >= half.Start && d.End <= half.End);
            if (inside.Count > 1
                && inside.Sum(static d => d.End - d.Tick) == half.Length
                && inside.TrueForAll(static d =>
                    d.Notes.Count > 0 && d.Tuplet == 0 && d.Written == new Written(NoteValue.Eighth, 0)
                )) {
                spans.RemoveAll(s => s.Start >= half.Start && s.End <= half.End);
                spans.Add((half.Start, half.End));
            }
        }

        spans.Sort();
        return spans;
    }

    /// <summary>
    ///     Accidentals (docs/notation/README.md, "Spelling"): against the key signature, carried through the bar for the
    ///     same step and octave on the staff (every voice), cancelled with naturals; a tied continuation draws none and
    ///     changes nothing.
    /// </summary>
    void Spell(IEnumerable<Draft> drafts) {
        var key = WrittenKey;
        var state = new Dictionary<(PitchStep, int), int>();
        foreach (var (draft, note) in drafts.SelectMany(static d => d.Notes.Select(n => (Draft: d, Note: n)))
                     .OrderBy(static x => x.Draft.Tick)
                     .ThenBy(static x => x.Draft.Voice)
                     .ThenBy(static x => x.Note.Position)) {
            if (note.Mapped || draft.TieStop) {
                continue;
            }

            var written = note.Written;
            var current = state.TryGetValue((written.Step, written.Octave), out var carried)
                ? carried
                : Spelling.KeyAlter(key.Fifths, (int)written.Step);
            if (written.Alter != current) {
                note.Accidental = Spelling.AccidentalOf(written.Alter);
                state[(written.Step, written.Octave)] = written.Alter;
            }
        }
    }

    /// <summary>
    ///     Stems (docs/notation/README.md, "Stems"): with several voices odd voices up and even down; else a beam's notes or
    ///     an event's away from the note farthest from the middle line (down on a tie), a percussion staff's up; none for
    ///     rests and whole notes.
    /// </summary>
    static void Stem(List<Draft> drafts, bool voices, bool percussion) {
        foreach (var draft in drafts) {
            if (draft.Notes.Count == 0 || draft.Written.Value <= NoteValue.Whole) {
                continue;
            }

            if (voices || percussion) {
                draft.Stem = draft.Voice % 2 != 0 ? StemDirection.Up : StemDirection.Down;
                continue;
            }

            var group = draft.Beam > 0 ? drafts.Where(d => d.Beam == draft.Beam) : [draft];
            var positions = group.SelectMany(static d => d.Notes).Select(static n => n.Position).ToList();
            var above = positions.Max() - 4;
            var below = 4 - positions.Min();
            draft.Stem = above >= below ? StemDirection.Down : StemDirection.Up;
        }
    }

    List<ScoreEvent> Events(List<Draft> drafts, int voice) {
        var events = new List<ScoreEvent>(drafts.Count);
        var rests = 0;
        var track = notation.Track.Id;
        foreach (var draft in drafts) {
            var duration = new ScoreDuration(
                draft.Written.Value,
                draft.Written.Dots,
                draft.Tuplet > 0 ? new(draft.Tuplet, 3, 2) : null
            );
            if (draft.Unit is null) {
                rests++;
                events.Add(
                    new(
                        ScoreIds.Rest(track, measure.Number, staff, voice, rests),
                        ScoreEventKind.Rest,
                        draft.Tick,
                        draft.End - draft.Tick,
                        duration,
                        [],
                        StemDirection.None,
                        null,
                        Positioned(draft, voice),
                        [],
                        Chords(draft, voice)
                    )
                );
                continue;
            }

            var notes = draft.Notes.Select(n => new ScoreNote(
                        ScoreIds.Note(n.Note.Id, draft.Segment),
                        n.Note.Id,
                        n.Note.Pitch,
                        n.Written,
                        n.Position,
                        n.Accidental,
                        draft.TieStart,
                        draft.TieStop,
                        n.Head
                    )
                )
                .ToList();
            var first = draft.Segment == 1;
            var symbols = first ? draft.Notes.SelectMany(n => attachments.OfNote(n.Note.Id)) : [];
            var lyrics = first
                ? draft.Notes.SelectMany(n => attachments.LyricsOf(n.Note.Id))
                    .OrderBy(static l => l.Verse)
                    .ThenBy(static l => l.Id, StringComparer.Ordinal)
                    .Select(static l => l.Id)
                    .ToList()
                : [];
            events.Add(
                new(
                    notes.Count == 1
                        ? ScoreIds.Note(notes[0].NoteId, draft.Segment)
                        : ScoreIds.Chord(notes.Min(static n => n.NoteId), draft.Segment),
                    notes.Count == 1 ? ScoreEventKind.Note : ScoreEventKind.Chord,
                    draft.Tick,
                    draft.End - draft.Tick,
                    duration,
                    notes,
                    draft.Stem,
                    draft.Beam > 0 ? draft.Beam : null,
                    [..symbols.Concat(Positioned(draft, voice)).Distinct().Order(StringComparer.Ordinal)],
                    lyrics,
                    Chords(draft, voice)
                )
            );
        }

        return events;
    }

    List<string> Positioned(Draft draft, int voice) =>
        voice == 1
            ? [
                ..attachments.At(notation.Track.Id, staff, notation.Style.Staves.Count, draft.Tick, draft.End)
                    .Order(StringComparer.Ordinal)
            ]
            : [];

    List<string> Chords(Draft draft, int voice) =>
        voice == 1 && staff == 0 ? [..attachments.ChordsAt(notation.Track.Id, draft.Tick, draft.End)] : [];

    sealed class Draft {
        public long Tick { get; init; }
        public long End { get; init; }
        public Written Written { get; init; }
        public int Tuplet { get; init; }
        public Unit? Unit { get; init; }
        public bool FullMeasure { get; init; }
        public int Segment { get; set; }
        public bool TieStart { get; set; }
        public bool TieStop { get; set; }
        public List<NoteDraft> Notes { get; } = [];
        public int Beam { get; set; }
        public StemDirection Stem { get; set; }
        public int Voice { get; set; }
    }

    sealed class NoteDraft(SourceNote note, ScorePitch written, int position, Notehead head, bool mapped) {
        public SourceNote Note { get; } = note;
        public ScorePitch Written { get; } = written;
        public int Position { get; } = position;
        public Notehead Head { get; } = head;
        public bool Mapped { get; } = mapped;
        public Accidental? Accidental { get; set; }
    }
}
