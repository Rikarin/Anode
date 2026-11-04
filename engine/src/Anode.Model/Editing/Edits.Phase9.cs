using Anode.Contracts;
using Anode.Model.Time;

namespace Anode.Model.Editing;

/// <summary>
///     Phase 9 (docs/10-phase-9.md): the stored score content (staff styles, region notation, symbols, lyrics, chord
///     symbols, score sets, the layout), the score edits (<c>moveScoreNotes</c>, <c>addScoreNote</c>, converted with
///     the notation engine's staff positions through <see cref="IScoreNotation" />) and the interchange imports' result
///     (<c>importTracks</c>, in <c>Edits.ImportTracks.cs</c>). Note-attached symbols and lyrics follow their notes
///     (<see cref="Detached" />).
/// </summary>
public static partial class Edits {
    /// <summary>The most staves a staff style has.</summary>
    public const int MaxStaves = 4;

    /// <summary>A staff transposes −48…48 semitones.</summary>
    public const int MaxStaffTranspose = 48;

    /// <summary>The most verses of lyrics.</summary>
    public const int MaxVerses = 8;

    /// <summary>A whole note in ticks: display quantise grids divide it.</summary>
    public const int WholeNoteTicks = TempoMap.Ppq * 4;

    /// <summary>The most dots a value takes before it is tied.</summary>
    public const int MaxDots = 3;

    /// <summary>A staff position (and a drum map's) is −30…38: well beyond the ledger lines either way.</summary>
    public const int MaxStaffPosition = 38;

    public const int MinStaffPosition = -30;

    /// <summary>The longest id a client chooses (staff styles, score sets).</summary>
    public const int MaxClientIdLength = 64;

    /// <summary>The page is 50…2000 mm a side.</summary>
    public const double MinPageMm = 50;

    public const double MaxPageMm = 2000;

    /// <summary>The staff height is 2…20 mm.</summary>
    public const double MinStaffMm = 2;

    public const double MaxStaffMm = 20;

    /// <summary>Score edits move notes at most this many staff steps.</summary>
    public const int MaxScoreSteps = 127;

    /// <summary>A new score note's velocity when the command gives none.</summary>
    public const int ScoreNoteVelocity = 80;

    /// <summary>The Phase 9 commands; null for every other command.</summary>
    static IReadOnlyList<DeltaOp>? Phase9Ops(
        ProjectState state,
        EditCommand command,
        IIdSource ids,
        PluginCatalog plugins,
        IScoreNotation notation
    ) =>
        command switch {
            SetStaffStyleCommand c => [new StaffStyleUpserted(CheckStaffStyle(c.Style, true))],
            DeleteStaffStyleCommand c => DeleteStaffStyle(state, c),
            AssignStaffStyleCommand c => AssignStaffStyle(state, c),
            SetRegionNotationCommand c => SetRegionNotation(state, c),
            AddScoreSymbolsCommand c => AddScoreSymbols(state, c, ids),
            EditScoreSymbolsCommand c => EditScoreSymbols(state, c),
            DeleteScoreSymbolsCommand c => [
                ..Existing(state.ScoreSymbols, c.SymbolIds, "score symbol")
                    .Select(static id => new ScoreSymbolRemoved(id))
            ],
            SetLyricsCommand c => SetLyrics(state, c, ids),
            AddChordSymbolsCommand c => AddChordSymbols(state, c, ids),
            EditChordSymbolsCommand c => EditChordSymbols(state, c),
            DeleteChordSymbolsCommand c => [
                ..Existing(state.ChordSymbols, c.ChordIds, "chord symbol")
                    .Select(static id => new ChordSymbolRemoved(id))
            ],
            SetScoreSetCommand c => [new ScoreSetUpserted(CheckScoreSet(state, c.Set))],
            DeleteScoreSetCommand c => state.ScoreSets.ContainsKey(c.SetId ?? string.Empty)
                ? [new ScoreSetRemoved(c.SetId!)]
                : throw new EditException($"No score set '{c.SetId}'."),
            SetScoreLayoutCommand c => [new ScoreLayoutChanged(CheckLayout(state, c.Layout))],
            MoveScoreNotesCommand c => MoveScoreNotes(state, c, notation),
            AddScoreNoteCommand c => AddScoreNote(state, c, ids, notation),
            ImportTracksCommand c => ImportTracks(state, c, ids, plugins),
            _ => null
        };

    // ---- Staff styles.

    /// <summary>
    ///     A staff style checked (<c>setStaffStyle</c>'s rules); with <paramref name="clientId" /> its id is one a client
    ///     may choose (not a factory id). Returns it with its name trimmed.
    /// </summary>
    public static StaffStyle CheckStaffStyle(StaffStyle? style, bool clientId) {
        if (style is null) {
            throw new EditException("A staff style is required.");
        }

        if (clientId) {
            CheckClientId(style.Id, "staff style", true);
        }

        var name = Name(style.Name ?? string.Empty, "staff style name");
        if (style.Staves is not { Count: >= 1 and <= MaxStaves } staves) {
            throw new EditException($"A staff style has 1…{MaxStaves} staves.");
        }

        for (var i = 0; i < staves.Count; i++) {
            CheckStaff(staves, i);
        }

        CheckGrid(style.DisplayQuantizeTicks, "display quantise");
        CheckGrid(style.TripletQuantizeTicks, "triplet quantise");
        if (style.DrumMap is { } map) {
            if (!staves.Any(static s => s.Clef == Clef.Percussion)) {
                throw new EditException("Only a style with a percussion staff has a drum map.");
            }

            CheckDrumMap(map);
        }

        return style with {
            Name = name, Staves = [..staves], DrumMap = style.DrumMap is null ? null : [..style.DrumMap]
        };
    }

    /// <summary>
    ///     Staff <paramref name="i" /> of a style: a clef, a transposition, voices, and its split point below the one
    ///     above.
    /// </summary>
    static void CheckStaff(IReadOnlyList<Staff> staves, int i) {
        var staff = staves[i] ?? throw new EditException("A staff is null.");
        if (!Enum.IsDefined(staff.Clef)) {
            throw new EditException($"Unknown clef {staff.Clef}.");
        }

        if (staff.Transpose is < -MaxStaffTranspose or > MaxStaffTranspose) {
            throw new EditException(
                $"A staff transposes −{MaxStaffTranspose}…{MaxStaffTranspose} semitones, not {staff.Transpose}."
            );
        }

        if (staff.Voices is < 1 or > 4) {
            throw new EditException($"A staff has 1…4 voices, not {staff.Voices}.");
        }

        if (i == staves.Count - 1 != staff.SplitPitch is null) {
            throw new EditException("Every staff but the last has a split pitch; the last has none.");
        }

        if (staff.SplitPitch is { } split && (split is < 0 or > 127 || (i > 0 && split >= staves[i - 1].SplitPitch))) {
            throw new EditException("Split pitches are 0…127, descending from the top staff.");
        }
    }

    static void CheckDrumMap(IReadOnlyList<DrumMapEntry> map) {
        if (map.Count > 128) {
            throw new EditException("A drum map maps at most 128 pitches.");
        }

        var pitches = new HashSet<int>();
        foreach (var entry in map) {
            if (entry is null || entry.Pitch is < 0 or > 127 || !pitches.Add(entry.Pitch)) {
                throw new EditException("A drum map maps each pitch (0…127) once.");
            }

            CheckStaffPosition(entry.StaffPosition);
            if (!Enum.IsDefined(entry.Notehead)) {
                throw new EditException($"Unknown notehead {entry.Notehead}.");
            }

            if (entry.Name is not { Length: <= MaxNameLength }) {
                throw new EditException($"A drum map entry's name has at most {MaxNameLength} characters.");
            }
        }
    }

    static void CheckStaffPosition(int position) {
        if (position is < MinStaffPosition or > MaxStaffPosition) {
            throw new EditException(
                $"A staff position is {MinStaffPosition}…{MaxStaffPosition} (0 the bottom line), not {position}."
            );
        }
    }

    /// <summary>A display quantise grid: 0 (none) or a divisor of a whole note.</summary>
    static void CheckGrid(int ticks, string what) {
        if (ticks < 0 || (ticks > 0 && WholeNoteTicks % ticks != 0)) {
            throw new EditException(
                $"A {what} grid is 0 or divides a whole note ({WholeNoteTicks} ticks), not {ticks}."
            );
        }
    }

    /// <summary>An id a client chooses: 1…64 letters, digits, '_', '-' or '.'; a factory prefix only when allowed.</summary>
    static void CheckClientId(string? id, string what, bool notFactory) {
        if (id is not { Length: > 0 and <= MaxClientIdLength }
            || !id.All(static c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')) {
            throw new EditException($"A {what} id has 1…{MaxClientIdLength} letters, digits, '_', '-' or '.'.");
        }

        if (notFactory && id.StartsWith(StaffStyleIds.FactoryPrefix, StringComparison.Ordinal)) {
            throw new EditException($"'{StaffStyleIds.FactoryPrefix}' ids are the factory styles'.");
        }
    }

    static List<DeltaOp> DeleteStaffStyle(ProjectState state, DeleteStaffStyleCommand c) {
        if (!state.StaffStyles.ContainsKey(c.StyleId ?? string.Empty)) {
            throw new EditException($"No staff style '{c.StyleId}'.");
        }

        var ops = TracksChanged(state, t => t.StaffStyleId == c.StyleId, static t => t with { StaffStyleId = null });
        ops.Add(new StaffStyleRemoved(c.StyleId!));
        return ops;
    }

    /// <summary>Whether <paramref name="styleId" /> is a style a track may name: a factory one or one of the project's.</summary>
    public static bool StyleExists(ProjectState state, string styleId) =>
        StaffStyleIds.All.Contains(styleId) || state.StaffStyles.ContainsKey(styleId);

    static List<DeltaOp> AssignStaffStyle(ProjectState state, AssignStaffStyleCommand c) {
        if (c.TrackIds is not { Count: > 0 }) {
            throw new EditException("Name at least one track.");
        }

        if (c.StyleId is { } style && !StyleExists(state, style)) {
            throw new EditException($"No staff style '{style}'.");
        }

        var ops = new List<DeltaOp>();
        foreach (var id in c.TrackIds.Distinct(StringComparer.Ordinal)) {
            var track = TrackOf(state, id ?? string.Empty);
            if (!IsMidiKind(track.Kind)) {
                throw new EditException($"Track '{track.Name}' is not an instrument or MIDI track: it has no staff.");
            }

            ops.Add(new TrackUpserted(track with { StaffStyleId = c.StyleId }, state.IndexOfTrack(track.Id)));
        }

        return ops;
    }

    // ---- Region notation.

    /// <summary>A region's notation checked: grids 0 or dividing a whole note, 0…3 dots.</summary>
    public static RegionNotation CheckNotation(RegionNotation notation) {
        CheckGrid(notation.DisplayQuantizeTicks, "display quantise");
        CheckGrid(notation.TripletQuantizeTicks, "triplet quantise");
        return notation.MaxDots is < 0 or > MaxDots
            ? throw new EditException($"A value has 0…{MaxDots} dots, not {notation.MaxDots}.")
            : notation;
    }

    static List<DeltaOp> SetRegionNotation(ProjectState state, SetRegionNotationCommand c) {
        if (c.RegionIds is not { Count: > 0 }) {
            throw new EditException("Name at least one MIDI region.");
        }

        var ops = new List<DeltaOp>();
        foreach (var id in c.RegionIds.Distinct(StringComparer.Ordinal)) {
            var region = MidiRegionOf(state, id);
            var n = region.Notation;
            var notation = CheckNotation(
                new(
                    c.DisplayQuantizeTicks ?? n.DisplayQuantizeTicks,
                    c.TripletQuantizeTicks ?? n.TripletQuantizeTicks,
                    c.Interpretation ?? n.Interpretation,
                    c.NoOverlap ?? n.NoOverlap,
                    c.Syncopation ?? n.Syncopation,
                    c.MaxDots ?? n.MaxDots
                )
            );
            ops.Add(new MidiRegionUpserted(region with { Notation = notation }));
        }

        return ops;
    }

    // ---- Symbols.

    static List<DeltaOp> AddScoreSymbols(ProjectState state, AddScoreSymbolsCommand c, IIdSource ids) {
        if (c.Symbols is not { Count: > 0 and <= MaxBatch } symbols) {
            throw new EditException($"Add 1…{MaxBatch} score symbols.");
        }

        var notes = NoteTracks(state, false);
        return [
            ..symbols.Select(static s => s ?? throw new EditException("A score symbol is null."))
                .Select(s => new ScoreSymbolUpserted(
                        CheckSymbol(
                            state,
                            notes,
                            new(
                                ids.Next(IdPrefix.ScoreSymbol),
                                s.Kind,
                                s.TrackId,
                                s.NoteId,
                                s.Tick,
                                s.EndNoteId,
                                s.EndTick,
                                s.Text,
                                s.Placement,
                                s.Staff
                            )
                        )
                    )
                )
        ];
    }

    static List<DeltaOp> EditScoreSymbols(ProjectState state, EditScoreSymbolsCommand c) {
        if (c.Symbols is not { Count: > 0 and <= MaxBatch } symbols) {
            throw new EditException($"Change 1…{MaxBatch} score symbols.");
        }

        var notes = NoteTracks(state, false);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ops = new List<DeltaOp>();
        foreach (var symbol in symbols) {
            if (symbol is null || !state.ScoreSymbols.TryGetValue(symbol.Id ?? string.Empty, out var old)) {
                throw new EditException($"No score symbol '{symbol?.Id}'.");
            }

            if (!seen.Add(old.Id)) {
                throw new EditException($"Score symbol '{symbol.Id}' is changed twice.");
            }

            if (symbol.TrackId != old.TrackId) {
                throw new EditException("A score symbol stays on its track.");
            }

            ops.Add(new ScoreSymbolUpserted(CheckSymbol(state, notes, symbol)));
        }

        return ops;
    }

    /// <summary>
    ///     A score symbol checked against the project: its track holds MIDI, its anchors are the ones its kind takes
    ///     (<see cref="ScoreSymbols" />) and its notes are the track's, its text is one its kind takes.
    /// </summary>
    static ScoreSymbol CheckSymbol(
        ProjectState state,
        IReadOnlyDictionary<long, (string Track, long Tick)> notes,
        ScoreSymbol s
    ) {
        if (!Enum.IsDefined(s.Kind)) {
            throw new EditException($"Unknown score symbol kind {s.Kind}.");
        }

        var track = MidiTrackOf(state, s.TrackId ?? string.Empty);
        CheckAnchorKinds(s);
        CheckAnchors(notes, s, track);
        if (s.Placement is { } placement && !Enum.IsDefined(placement)) {
            throw new EditException($"Unknown placement {placement}.");
        }

        if (s.Staff is { } staff && (s.NoteId is not null || staff is < 0 or >= MaxStaves)) {
            throw new EditException(
                $"A positioned symbol's staff is 0…{MaxStaves - 1}; a note's symbol takes its note's."
            );
        }

        return s with { Text = SymbolText(s) };
    }

    /// <summary>A symbol's notes are its track's, the end one later; its ticks are in the project, the end one later.</summary>
    static void CheckAnchors(IReadOnlyDictionary<long, (string Track, long Tick)> notes, ScoreSymbol s, Track track) {
        if (s.NoteId is { } noteId) {
            var start = NoteOfTrack(notes, noteId, track);
            if (s.EndNoteId is { } endId && (endId == noteId || NoteOfTrack(notes, endId, track).Tick < start.Tick)) {
                throw new EditException($"A {Kind(s.Kind)} ends at a later note.");
            }

            return;
        }

        CheckTick(s.Tick!.Value, "symbol");
        if (s.EndTick is { } endTick && (endTick <= s.Tick || endTick > MaxTick)) {
            throw new EditException($"A {Kind(s.Kind)} ends after it starts, inside the project.");
        }
    }

    /// <summary>A symbol's anchors are those its kind takes: a note or a tick, an end of the same kind when it spans.</summary>
    static void CheckAnchorKinds(ScoreSymbol s) {
        var onNote = s.NoteId is not null;
        if (onNote == s.Tick is not null) {
            throw new EditException($"A {Kind(s.Kind)} is on a note or at a tick (one of them).");
        }

        if (!onNote && (ScoreSymbols.NoteOnly.Contains(s.Kind) || s.Kind == ScoreSymbolKind.Slur)) {
            throw new EditException($"A {Kind(s.Kind)} is on a note (a slur spans notes).");
        }

        var ends = (s.EndNoteId is not null, s.EndTick is not null);
        if (!ScoreSymbols.Spanning.Contains(s.Kind)) {
            if (ends != (false, false)) {
                throw new EditException($"A {Kind(s.Kind)} has no end.");
            }
        } else if (ends != (onNote, !onNote)) {
            throw new EditException($"A {Kind(s.Kind)} ends at the same kind of anchor it starts at.");
        }
    }

    /// <summary>A symbol's text: one of 1…200 characters for the kinds that take one (a dynamic's a marking), else none.</summary>
    static string? SymbolText(ScoreSymbol s) {
        if (!ScoreSymbols.WithText.Contains(s.Kind)) {
            return s.Text is null ? null : throw new EditException($"A {Kind(s.Kind)} has no text.");
        }

        var text = s.Text is { Length: > 0 and <= ScoreSymbols.MaxTextLength } t && !string.IsNullOrWhiteSpace(t)
            ? t
            : throw new EditException($"A {Kind(s.Kind)} has a text of 1…{ScoreSymbols.MaxTextLength} characters.");
        return s.Kind != ScoreSymbolKind.Dynamic || ScoreSymbols.Dynamics.Contains(text)
            ? text
            : throw new EditException($"'{text}' is not a dynamic ({string.Join(", ", ScoreSymbols.Dynamics)}).");
    }

    static string Kind(ScoreSymbolKind kind) => kind.ToString().ToLowerInvariant();

    static (string Track, long Tick) NoteOfTrack(
        IReadOnlyDictionary<long, (string Track, long Tick)> notes,
        long noteId,
        Track track
    ) =>
        notes.TryGetValue(noteId, out var note) && note.Track == track.Id
            ? note
            : throw new EditException($"Track '{track.Name}' has no note {noteId}.");

    /// <summary>
    ///     Every note of the project's MIDI regions: its track and project tick; with <paramref name="alternatives" />
    ///     also the notes of tracks' other alternatives (where a note waits while its alternative is not selected).
    /// </summary>
    static Dictionary<long, (string Track, long Tick)> NoteTracks(ProjectState state, bool alternatives) {
        var notes = new Dictionary<long, (string, long)>();
        foreach (var region in state.MidiRegions.Values) {
            AddNotes(notes, region, region.TrackId, true);
        }

        if (alternatives) {
            foreach (var track in state.Tracks) {
                foreach (var region in track.Alternatives.SelectMany(static a => a.MidiRegions)) {
                    AddNotes(notes, region, track.Id, false);
                }
            }
        }

        return notes;
    }

    static void AddNotes(Dictionary<long, (string, long)> notes, MidiRegion region, string trackId, bool replace) {
        foreach (var note in region.Notes) {
            if (replace) {
                notes[note.Id] = (trackId, region.StartTick + note.StartTick);
            } else {
                notes.TryAdd(note.Id, (trackId, region.StartTick + note.StartTick));
            }
        }
    }

    // ---- Lyrics.

    static List<DeltaOp> SetLyrics(ProjectState state, SetLyricsCommand c, IIdSource ids) {
        if (c.Verse is < 1 or > MaxVerses) {
            throw new EditException($"A verse is 1…{MaxVerses}, not {c.Verse}.");
        }

        if (c.Syllables is not { Count: > 0 and <= MaxBatch } syllables) {
            throw new EditException($"Set 1…{MaxBatch} syllables.");
        }

        var notes = NoteTracks(state, false);
        var existing = state.Lyrics.Values.Where(l => l.Verse == c.Verse).ToDictionary(static l => l.NoteId);
        var seen = new HashSet<long>();
        var ops = new List<DeltaOp>();
        foreach (var change in syllables) {
            if (change is null || !notes.ContainsKey(change.NoteId) || !seen.Add(change.NoteId)) {
                throw new EditException($"No note {change?.NoteId}, or it has two syllables in the verse.");
            }

            if (Syllable(change, c.Verse, existing.GetValueOrDefault(change.NoteId), ids) is { } op) {
                ops.Add(op);
            }
        }

        return ops;
    }

    /// <summary>
    ///     A note's syllable change: an upsert keeping the note's syllable's id, a removal for an empty text, or nothing
    ///     (an empty text where it has none).
    /// </summary>
    static DeltaOp? Syllable(LyricChange change, int verse, LyricSyllable? old, IIdSource ids) {
        if (change.Text is not { Length: <= ScoreSymbols.MaxTextLength } text) {
            throw new EditException($"A syllable has at most {ScoreSymbols.MaxTextLength} characters.");
        }

        if (text.Length == 0) {
            return old is null ? null : new LyricRemoved(old.Id);
        }

        return Enum.IsDefined(change.Syllabic)
            ? new LyricUpserted(
                new(old?.Id ?? ids.Next(IdPrefix.Lyric), change.NoteId, verse, text, change.Syllabic, change.Extender)
            )
            : throw new EditException($"Unknown syllabic {change.Syllabic}.");
    }

    // ---- Chord symbols.

    static List<DeltaOp> AddChordSymbols(ProjectState state, AddChordSymbolsCommand c, IIdSource ids) {
        if (c.Chords is not { Count: > 0 and <= MaxBatch } chords) {
            throw new EditException($"Add 1…{MaxBatch} chord symbols.");
        }

        return [
            ..chords.Select(static n => n ?? throw new EditException("A chord symbol is null."))
                .Select(n => new ChordSymbolUpserted(
                        CheckChord(
                            state,
                            new(
                                ids.Next(IdPrefix.ChordSymbol),
                                n.TrackId,
                                n.Tick,
                                n.Root,
                                n.Quality,
                                n.Bass,
                                n.ShowGrid
                            )
                        )
                    )
                )
        ];
    }

    static List<DeltaOp> EditChordSymbols(ProjectState state, EditChordSymbolsCommand c) {
        if (c.Chords is not { Count: > 0 and <= MaxBatch } chords) {
            throw new EditException($"Change 1…{MaxBatch} chord symbols.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ops = new List<DeltaOp>();
        foreach (var chord in chords) {
            if (chord is null || !state.ChordSymbols.ContainsKey(chord.Id ?? string.Empty) || !seen.Add(chord.Id!)) {
                throw new EditException($"No chord symbol '{chord?.Id}' (or it is changed twice).");
            }

            ops.Add(new ChordSymbolUpserted(CheckChord(state, chord)));
        }

        return ops;
    }

    static ChordSymbol CheckChord(ProjectState state, ChordSymbol chord) {
        _ = MidiTrackOf(state, chord.TrackId ?? string.Empty);
        CheckTick(chord.Tick, "chord symbol");
        CheckPitchName(chord.Root ?? throw new EditException("A chord symbol has a root."));
        if (chord.Bass is { } bass) {
            CheckPitchName(bass);
        }

        return chord.Quality is { Length: <= ScoreSymbols.MaxTextLength }
            ? chord
            : throw new EditException($"A chord quality has at most {ScoreSymbols.MaxTextLength} characters.");
    }

    static void CheckPitchName(PitchName name) {
        if (!Enum.IsDefined(name.Step) || name.Alter is < -2 or > 2) {
            throw new EditException("A pitch name has a step and an alteration −2…2.");
        }
    }

    // ---- Score sets and layout.

    static ScoreSet CheckScoreSet(ProjectState state, ScoreSet? set) {
        if (set is null) {
            throw new EditException("A score set is required.");
        }

        CheckClientId(set.Id, "score set", false);
        var name = Name(set.Name ?? string.Empty, "score set name");
        if (set.TrackIds is not { } trackIds || trackIds.Count > MaxTracks) {
            throw new EditException($"A score set lists 0…{MaxTracks} tracks.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in trackIds) {
            var track = MidiTrackOf(state, id ?? string.Empty);
            if (!seen.Add(track.Id)) {
                throw new EditException($"Track '{track.Name}' is in the set twice.");
            }
        }

        return set with { Name = name, TrackIds = [..trackIds] };
    }

    /// <summary>A layout checked (<c>setScoreLayout</c>'s rules).</summary>
    public static ScoreLayout CheckLayout(ProjectState state, ScoreLayout? layout) {
        if (layout?.Margins is not { } margins) {
            throw new EditException("A score layout has its page and margins.");
        }

        CheckPage(layout, margins);
        if ((string?[])[layout.Title, layout.Subtitle, layout.Composer, layout.Header, layout.Footer] is var texts
            && texts.Any(static t => t is not { Length: <= ScoreSymbols.MaxTextLength })) {
            throw new EditException($"A layout's texts have at most {ScoreSymbols.MaxTextLength} characters.");
        }

        if (layout.InstrumentNames is not { } names) {
            throw new EditException("A score layout lists its instrument names.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names) {
            var track = TrackOf(state, name?.TrackId ?? string.Empty);
            if (!seen.Add(track.Id)) {
                throw new EditException($"Track '{track.Name}' has one instrument name.");
            }

            if (name!.Full is not { Length: <= MaxNameLength } || name.Short is not { Length: <= MaxNameLength }) {
                throw new EditException($"Instrument names have at most {MaxNameLength} characters.");
            }
        }

        return layout with { InstrumentNames = [..names] };
    }

    /// <summary>The page 50…2000 mm a side, margins of 0 or more leaving half of it, staves 2…20 mm high.</summary>
    static void CheckPage(ScoreLayout layout, ScoreMargins margins) {
        if (!(layout.PageWidthMm is >= MinPageMm and <= MaxPageMm)
            || !(layout.PageHeightMm is >= MinPageMm and <= MaxPageMm)) {
            throw new EditException($"A page is {MinPageMm}…{MaxPageMm} mm a side.");
        }

        if (!(Math.Min(Math.Min(margins.Top, margins.Bottom), Math.Min(margins.Left, margins.Right)) >= 0)
            || !(margins.Top + margins.Bottom <= layout.PageHeightMm / 2)
            || !(margins.Left + margins.Right <= layout.PageWidthMm / 2)) {
            throw new EditException("Margins are 0 or more and leave at least half the page.");
        }

        if (!(layout.StaffHeightMm is >= MinStaffMm and <= MaxStaffMm)) {
            throw new EditException($"A staff is {MinStaffMm}…{MaxStaffMm} mm high.");
        }
    }

    // ---- Score edits.

    /// <summary>The style a track uses: one of the project's, else a factory one, else the default.</summary>
    public static StaffStyle StyleOf(ProjectState state, Track track, IScoreNotation notation) {
        var id = track.StaffStyleId ?? StaffStyleIds.Default;
        if (state.StaffStyles.TryGetValue(id, out var own)) {
            return own;
        }

        return notation.FactoryStyle(id)
            ?? notation.FactoryStyle(StaffStyleIds.Default)
            ?? throw new EditException("Score edits need the notation engine.");
    }

    /// <summary>The staff of <paramref name="style" /> a note of <paramref name="pitch" /> goes to (by split points).</summary>
    public static int StaffOf(StaffStyle style, int pitch) {
        for (var i = 0; i < style.Staves.Count; i++) {
            if (style.Staves[i].SplitPitch is not { } split || pitch >= split) {
                return i;
            }
        }

        return style.Staves.Count - 1;
    }

    /// <summary>
    ///     <c>moveScoreNotes</c>: each note's pitch moved by staff steps in the written key at its position (its staff's
    ///     transposition undone) and its start by <c>ticks</c>, as <c>editNotes</c> would change them.
    /// </summary>
    static DeltaOp[] MoveScoreNotes(ProjectState state, MoveScoreNotesCommand c, IScoreNotation notation) {
        var region = MidiRegionOf(state, c.RegionId);
        var chosen = Known(region.Notes.Select(static n => n.Id), c.NoteIds, "note", region.Id);
        if (c.Steps is < -MaxScoreSteps or > MaxScoreSteps) {
            throw new EditException($"Notes move at most {MaxScoreSteps} staff steps.");
        }

        if (c.Ticks is < -MaxTick or > MaxTick) {
            throw new EditException($"Notes move at most {MaxTick} ticks.");
        }

        if (c.Accidental is { } forced && !Enum.IsDefined(forced)) {
            throw new EditException($"Unknown accidental {forced}.");
        }

        var track = TrackOf(state, region.TrackId);
        var style = c.Steps != 0 || c.Accidental is not null ? StyleOf(state, track, notation) : null;
        var notes = region.Notes.Select(n => {
                if (!chosen.Contains(n.Id)) {
                    return n;
                }

                var pitch = n.Pitch;
                if (style is not null) {
                    var staff = style.Staves[StaffOf(style, n.Pitch)];
                    pitch = notation.Step(
                        n.Pitch,
                        c.Steps,
                        staff.Transpose,
                        KeyAt(state, region.StartTick + n.StartTick),
                        c.Accidental
                    );
                }

                var moved = n with { Pitch = pitch, StartTick = n.StartTick + c.Ticks };
                CheckNote(region, moved, c.Ticks != 0);
                return moved;
            }
        );
        return [new MidiRegionUpserted(region with { Notes = Sorted(notes) })];
    }

    /// <summary>
    ///     <c>addScoreNote</c>: the note at the staff position under the staff's clef (a drum map's pitch on a
    ///     percussion staff), into the region named, else the track's region at the tick, else a new region of whole bars.
    /// </summary>
    static DeltaOp[] AddScoreNote(ProjectState state, AddScoreNoteCommand c, IIdSource ids, IScoreNotation notation) {
        var track = MidiTrackOf(state, c.TrackId ?? string.Empty);
        CheckTick(c.Tick, "note");
        CheckLength(c.LengthTicks, "note");
        CheckStaffPosition(c.StaffPosition);
        if (c.Accidental is { } forced && !Enum.IsDefined(forced)) {
            throw new EditException($"Unknown accidental {forced}.");
        }

        var velocity = c.Velocity ?? ScoreNoteVelocity;
        var style = StyleOf(state, track, notation);
        if (c.Staff < 0 || c.Staff >= style.Staves.Count) {
            throw new EditException($"Staff {c.Staff} is not one of the style's 0…{style.Staves.Count - 1}.");
        }

        var pitch = ScoreNotePitch(style.Staves[c.Staff], style, KeyAt(state, c.Tick), c, notation);
        var region = ScoreNoteRegion(state, track, c, ids);
        CheckCapacity(region, 1);
        var note = new Note(state.LastEventId + 1, pitch, velocity, 1, c.Tick - region.StartTick, c.LengthTicks, 0);
        CheckNote(region, note, true);
        return [new MidiRegionUpserted(region with { Notes = Sorted([..region.Notes, note]) })];
    }

    /// <summary>A score note's pitch: the drum map's at its position on a percussion staff, else the clef's.</summary>
    static int ScoreNotePitch(
        Staff staff,
        StaffStyle style,
        KeyEvent key,
        AddScoreNoteCommand c,
        IScoreNotation notation
    ) {
        if (staff.Clef != Clef.Percussion) {
            return notation.PitchAt(staff.Clef, staff.Transpose, key, c.StaffPosition, c.Accidental);
        }

        // A position the drum map does not name reads as on a treble staff.
        var mapped = style.DrumMap?.Where(e => e.StaffPosition == c.StaffPosition)
            .OrderBy(static e => e.Pitch)
            .FirstOrDefault();
        return mapped?.Pitch ?? notation.PitchAt(Clef.Treble, staff.Transpose, key, c.StaffPosition, c.Accidental);
    }

    /// <summary>The region a score note goes into: the one named, else the track's at the tick, else a new one of whole bars.</summary>
    static MidiRegion ScoreNoteRegion(ProjectState state, Track track, AddScoreNoteCommand c, IIdSource ids) {
        if (c.RegionId is { } regionId) {
            var named = MidiRegionOf(state, regionId);
            if (named.TrackId != track.Id
                || c.Tick < named.StartTick
                || c.Tick >= named.StartTick + named.LengthTicks) {
                throw new EditException($"Tick {c.Tick} is not in region '{named.Name}' of track '{track.Name}'.");
            }

            return named;
        }

        if (state.MidiRegionsOf(track.Id)
                .LastOrDefault(r => r.StartTick <= c.Tick && c.Tick < r.StartTick + r.LengthTicks)
            is { } at) {
            return at;
        }

        var bars = state.SignatureMap;
        var start = bars.BarLineAtOrBefore(c.Tick);
        var end = bars.BarLineAtOrAfter(c.Tick + c.LengthTicks);
        CheckTick(end, "region end");
        return new(
            ids.Next(IdPrefix.MidiRegion),
            track.Id,
            track.Name,
            start,
            Math.Max(end - start, SignatureMap.BarTicks(bars.At(start))),
            ProjectDefaults.Playback,
            [],
            [],
            false,
            ProjectDefaults.Notation,
            PitchSourceMode.Off,
            null
        );
    }

    // ---- Attachment.

    /// <summary>
    ///     The operations that keep the score content attached after an edit (docs/10-phase-9.md, "Attachment"): a
    ///     note-attached symbol or lyric whose note is gone goes with it (a note in a track's other alternative still
    ///     counts), a note-attached symbol follows its note to another track, a slur or wedge whose notes are no longer
    ///     on one track goes, and a removed track leaves the symbols, chord symbols, score sets and instrument names. Empty
    ///     when everything is attached; <see cref="Step" /> applies them after every edit's operations.
    /// </summary>
    public static List<DeltaOp> Detached(ProjectState state) {
        var ops = new List<DeltaOp>();
        if (state.ScoreSymbols.Count == 0
            && state.Lyrics.Count == 0
            && state.ChordSymbols.Count == 0
            && state.ScoreSets.Count == 0
            && state.ScoreLayout.InstrumentNames.Count == 0) {
            return ops;
        }

        var tracks = state.Tracks.Select(static t => t.Id).ToHashSet(StringComparer.Ordinal);
        Dictionary<long, (string Track, long Tick)>? notes = null;

        string? TrackOfNote(long id) =>
            (notes ??= NoteTracks(state, true)).TryGetValue(id, out var note) ? note.Track : null;

        foreach (var symbol in state.ScoreSymbols.Values) {
            if (DetachedSymbol(symbol, tracks, TrackOfNote) is { } op) {
                ops.Add(op);
            }
        }

        foreach (var lyric in state.Lyrics.Values) {
            if (TrackOfNote(lyric.NoteId) is null) {
                ops.Add(new LyricRemoved(lyric.Id));
            }
        }

        ops.AddRange(TrackItemsLeft(state, tracks));
        return ops;
    }

    /// <summary>What a removed track leaves: its chord symbols go, and it leaves the score sets and instrument names.</summary>
    static IEnumerable<DeltaOp> TrackItemsLeft(ProjectState state, HashSet<string> tracks) {
        foreach (var chord in state.ChordSymbols.Values.Where(c => !tracks.Contains(c.TrackId))) {
            yield return new ChordSymbolRemoved(chord.Id);
        }

        foreach (var set in state.ScoreSets.Values.Where(s => !s.TrackIds.All(tracks.Contains))) {
            yield return new ScoreSetUpserted(set with { TrackIds = [..set.TrackIds.Where(tracks.Contains)] });
        }

        var names = state.ScoreLayout.InstrumentNames;
        if (!names.All(n => tracks.Contains(n.TrackId))) {
            yield return new ScoreLayoutChanged(
                state.ScoreLayout with { InstrumentNames = [..names.Where(n => tracks.Contains(n.TrackId))] }
            );
        }
    }

    /// <summary>
    ///     What keeps a symbol attached: its removal when its track or a note it is on is gone (or its notes are on two
    ///     tracks), its move to its note's track, else nothing.
    /// </summary>
    static DeltaOp? DetachedSymbol(ScoreSymbol symbol, HashSet<string> tracks, Func<long, string?> trackOfNote) {
        if (!tracks.Contains(symbol.TrackId)) {
            return new ScoreSymbolRemoved(symbol.Id);
        }

        if (symbol.NoteId is not { } noteId) {
            return null;
        }

        var at = trackOfNote(noteId);
        var end = symbol.EndNoteId is { } endId ? trackOfNote(endId) : at;
        if (at is null || end != at) {
            return new ScoreSymbolRemoved(symbol.Id);
        }

        return at == symbol.TrackId ? null : new ScoreSymbolUpserted(symbol with { TrackId = at });
    }

    /// <summary>Whether an operation can detach score content (it removes or moves tracks, regions or notes).</summary>
    static bool MayDetach(DeltaOp op) => op is TrackRemoved or TrackUpserted or MidiRegionUpserted or MidiRegionRemoved;

    /// <summary>The ids named, each an item of <paramref name="items" />, once.</summary>
    static List<string> Existing<T>(IReadOnlyDictionary<string, T> items, IReadOnlyList<string>? named, string what) {
        if (named is not { Count: > 0 }) {
            throw new EditException($"Name at least one {what}.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        foreach (var id in named) {
            if (!items.ContainsKey(id ?? string.Empty)) {
                throw new EditException($"No {what} '{id}'.");
            }

            if (seen.Add(id!)) {
                list.Add(id!);
            }
        }

        return list;
    }
}
