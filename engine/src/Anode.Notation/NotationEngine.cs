using Anode.Contracts;
using Anode.Model;

namespace Anode.Notation;

/// <summary>
///     What to render: score set <c>SetId</c> (null: every track that holds MIDI, in track order), or the part of track
///     <c>PartTrackId</c> alone (written at transposed pitch), in the linear or page view
///     (<c>GET /project/score?set=&amp;part=&amp;view=</c>).
/// </summary>
public sealed record ScoreRequest(
    string? SetId = null,
    string? PartTrackId = null,
    ScoreViewMode Mode = ScoreViewMode.Linear);

/// <summary>
///     MIDI to notation, as Logic's score does it (docs/10-phase-9.md, workstream A; the rules: docs/notation/README.md).
///     The engine decides what is musically fixed (which notes, durations, ties, tuplets, voices, spelling, measures);
///     Verovio engraves (docs/adr/0011-notation-rendering.md).
/// </summary>
public static class NotationEngine {
    /// <summary>The length of a default short instrument name (<see cref="DefaultShortName" />).</summary>
    public const int ShortNameLength = 5;

    /// <summary>
    ///     The score view of <paramref name="state" /> for <paramref name="request" />: each track's MIDI regions under its
    ///     staff style (<see cref="FactoryStaffStyles.Resolve" />) and their <see cref="RegionNotation" />, measured by the
    ///     project's signatures, spelled by its keys, with its tempo marks and markers as rehearsal marks, and the stored
    ///     symbols, lyrics and chord symbols of <paramref name="score" /> (the project's <c>score</c>; passed in until the
    ///     model stores it). Deterministic: the same input gives the same view. Throws <see cref="ArgumentException" />
    ///     for a set or track that does not exist (or a part track outside the set).
    /// </summary>
    public static ScoreView Render(ProjectState state, ProjectScore score, ScoreRequest request) {
        var tracks = TracksOf(state, score, request);
        var transposing = request.PartTrackId is not null || score.Layout.TransposingScore;
        var bars = new Bars(state.SignatureMap);
        var notations = tracks.Select(t => TrackNotation.Build(
                    state,
                    t,
                    FactoryStaffStyles.Resolve(score, t.StaffStyleId),
                    bars
                )
            )
            .ToList();
        var trackIds = tracks.Select(static t => t.Id).ToHashSet(StringComparer.Ordinal);
        var notesInView = notations
            .SelectMany(static n => n.Staves.SelectMany(static s => s).SelectMany(static u => u.Notes))
            .Select(static n => n.Id)
            .ToHashSet();
        var symbols = score.Symbols.Where(s => trackIds.Contains(s.TrackId) && Drawn(s, notesInView))
            .OrderBy(static s => s.Id, StringComparer.Ordinal)
            .ToList();
        var lyrics = score.Lyrics.Where(l => notesInView.Contains(l.NoteId))
            .OrderBy(static l => l.Id, StringComparer.Ordinal)
            .ToList();
        var chords = score.Chords.Where(c => trackIds.Contains(c.TrackId) && c.Tick >= 0)
            .OrderBy(static c => c.Tick)
            .ThenBy(static c => c.Id, StringComparer.Ordinal)
            .ToList();
        var last = notations.Select(static n => n.LastTick)
            .Concat(
                symbols.Where(static s => s.NoteId is null)
                    .SelectMany(static s => new[] { s.Tick ?? -1, (s.EndTick ?? 0) - 1 })
            )
            .Concat(chords.Select(static c => c.Tick))
            .DefaultIfEmpty(-1)
            .Max();
        var lastBar = last < 0 ? 0 : bars.At(last).Index;
        var measures = Measures(state, bars, lastBar);
        var attachments = new Attachments(symbols, lyrics, chords);
        var parts = notations.Select(n => Part(n, score.Layout, measures, bars, transposing, attachments)).ToList();
        return new(
            request.SetId,
            request.PartTrackId,
            request.Mode,
            transposing,
            score.Layout,
            measures,
            parts,
            symbols,
            lyrics,
            chords
        );
    }

    static List<Track> TracksOf(ProjectState state, ProjectScore score, ScoreRequest request) {
        List<Track> tracks;
        ScoreSet? set = null;
        if (request.SetId is { } setId) {
            set = score.Sets.FirstOrDefault(s => s.Id == setId)
                ?? throw new ArgumentException($"No score set {setId}.", nameof(request));
            tracks = [..set.TrackIds.Select(state.FindTrack).OfType<Track>()];
        } else {
            var withMidi = state.MidiRegions.Values.Select(static r => r.TrackId).ToHashSet(StringComparer.Ordinal);
            tracks = [..state.Tracks.Where(t => withMidi.Contains(t.Id))];
        }

        if (request.PartTrackId is { } partId) {
            var track = state.FindTrack(partId) ?? throw new ArgumentException($"No track {partId}.", nameof(request));
            if (set is not null && !set.TrackIds.Contains(partId)) {
                throw new ArgumentException($"Track {partId} is not in score set {set.Id}.", nameof(request));
            }

            tracks = [track];
        }

        return tracks;
    }

    /// <summary>A symbol is drawn when every note it is anchored to is in the view.</summary>
    static bool Drawn(ScoreSymbol symbol, HashSet<long> notes) =>
        (symbol.NoteId is not { } start || notes.Contains(start))
        && (symbol.EndNoteId is not { } end || notes.Contains(end))
        && (symbol.NoteId is not null || symbol.Tick is >= 0);

    static List<ScoreMeasure> Measures(ProjectState state, Bars bars, long lastBar) {
        var keys = state.Settings.Keys.OrderBy(static k => k.Tick).ToList();
        var tempos = state.Settings.TempoEvents is { Count: > 0 } events
            ? events.OrderBy(static t => t.Tick).ToList()
            : [new(0, state.Settings.Tempo, TempoCurve.Step)];
        var markers = state.Markers.Values.OrderBy(static m => m.Tick)
            .ThenBy(static m => m.Id, StringComparer.Ordinal)
            .ToList();
        var drafts = new List<ScoreMeasure>();
        for (var i = 0L; i <= lastBar; i++) {
            var bar = bars.Of(i);
            var key = keys.LastOrDefault(k => bars.Map.BarLineAtOrAfter(k.Tick) <= bar.Start) is { } k
                ? Spelling.KeyOf(k)
                : new(0, KeyMode.Major);
            var previous = drafts.Count > 0 ? drafts[^1] : null;
            drafts.Add(
                new(
                    (int)i + 1,
                    bar.Start,
                    bar.Length,
                    bar.Time,
                    key,
                    previous is null || previous.Time != bar.Time,
                    previous is null || previous.Key != key,
                    TempoMarks(tempos, bar),
                    [
                        ..markers.Where(m => m.Tick >= bar.Start && m.Tick < bar.End)
                            .Select(static m => new ScoreRehearsal(m.Tick, m.Id, m.Name))
                    ],
                    Barline.Single
                )
            );
        }

        for (var i = 0; i < drafts.Count; i++) {
            drafts[i] = drafts[i] with {
                Barline = i == drafts.Count - 1 ? Barline.Final : BarlineBefore(drafts[i + 1])
            };
        }

        return drafts;
    }

    /// <summary>The tempo marks of a bar: where the tempo changes value, and at the start.</summary>
    static List<ScoreTempoMark> TempoMarks(List<TempoEvent> tempos, Bar bar) {
        var marks = new List<ScoreTempoMark>();
        for (var t = 0; t < tempos.Count; t++) {
            var tempo = tempos[t];
            var changes = t == 0 || tempos[t - 1].Bpm != tempo.Bpm;
            if (changes && tempo.Tick >= bar.Start && tempo.Tick < bar.End) {
                marks.Add(new(tempo.Tick, tempo.Bpm));
            }
        }

        return marks;
    }

    /// <summary>A double barline before a meter or key change or a marker on the bar line, else a single one.</summary>
    static Barline BarlineBefore(ScoreMeasure next) =>
        next.ShowTime || next.ShowKey || next.Rehearsals.Any(r => r.Tick == next.StartTick)
            ? Barline.Double
            : Barline.Single;

    static ScorePart Part(
        TrackNotation notation,
        ScoreLayout layout,
        List<ScoreMeasure> measures,
        Bars bars,
        bool transposing,
        Attachments attachments
    ) {
        var track = notation.Track;
        var style = notation.Style;
        var name = layout.InstrumentNames.FirstOrDefault(n => n.TrackId == track.Id);
        var partMeasures = new List<ScorePartMeasure>(measures.Count);
        foreach (var measure in measures) {
            var bar = bars.Of(measure.Number - 1);
            var settings = SettingsOf(notation, bar);
            var staves = new List<ScoreStaffMeasure>(style.Staves.Count);
            for (var s = 0; s < style.Staves.Count; s++) {
                var builder = new StaffMeasureBuilder(notation, s, bar, measure, settings, transposing, attachments);
                staves.Add(builder.Build());
            }

            partMeasures.Add(new(staves));
        }

        return new(
            track.Id,
            name is { Full.Length: > 0 } ? name.Full : track.Name,
            name is { Short.Length: > 0 } ? name.Short : DefaultShortName(track.Name),
            style.Id,
            transposing ? style.Staves[0].Transpose : 0,
            style.Brace && style.Staves.Count > 1,
            style.Staves,
            partMeasures
        );
    }

    /// <summary>
    ///     A track's short instrument name when the layout gives none (<see cref="ScoreLayout.InstrumentNames" /> lists
    ///     only the tracks whose names differ from these defaults, as the editor does): the track's name cut to
    ///     <see cref="ShortNameLength" /> characters, never inside a surrogate pair.
    /// </summary>
    public static string DefaultShortName(string trackName) {
        if (trackName.Length <= ShortNameLength) {
            return trackName;
        }

        return trackName[..(char.IsHighSurrogate(trackName[ShortNameLength - 1]) ? ShortNameLength - 1 : ShortNameLength)];
    }

    /// <summary>
    ///     A measure's splitting settings (syncopation, dots): the region of its first note, else the region over its start,
    ///     else the track's first region, else the defaults.
    /// </summary>
    static RegionNotation SettingsOf(TrackNotation notation, Bar bar) {
        var first = notation.Staves.SelectMany(static s => s)
            .Where(u => u.Start >= bar.Start && u.Start < bar.End)
            .OrderBy(static u => u.Start)
            .ThenBy(static u => u.Notes[0].Id)
            .FirstOrDefault();
        if (first is not null) {
            return first.Notation;
        }

        return notation.Regions.Find(r => r.StartTick <= bar.Start && r.StartTick + r.LengthTicks > bar.Start)
                ?.Notation
            ?? notation.Regions.FirstOrDefault()?.Notation
            ?? RegionNotation.Default;
    }
}

/// <summary>The stored items of the view, indexed for drawing at events.</summary>
sealed class Attachments {
    readonly Dictionary<long, List<string>> byNote = [];
    readonly Dictionary<long, List<LyricSyllable>> lyricsByNote = [];
    readonly Dictionary<string, List<ScoreSymbol>> positioned = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<ChordSymbol>> chords = new(StringComparer.Ordinal);

    public Attachments(List<ScoreSymbol> symbols, List<LyricSyllable> lyrics, List<ChordSymbol> chordSymbols) {
        foreach (var symbol in symbols) {
            if (symbol.NoteId is { } note) {
                Add(byNote, note, symbol.Id);
            } else {
                Add(positioned, symbol.TrackId, symbol);
            }
        }

        foreach (var lyric in lyrics) {
            Add(lyricsByNote, lyric.NoteId, lyric);
        }

        foreach (var chord in chordSymbols) {
            Add(chords, chord.TrackId, chord);
        }
    }

    public IEnumerable<string> OfNote(long noteId) => byNote.TryGetValue(noteId, out var ids) ? ids : [];

    public IEnumerable<LyricSyllable> LyricsOf(long noteId) => lyricsByNote.TryGetValue(noteId, out var l) ? l : [];

    /// <summary>The positioned symbols of a track's staff (of <paramref name="staves" />) in [from, to).</summary>
    public IEnumerable<string> At(string trackId, int staff, int staves, long from, long to) =>
        positioned.TryGetValue(trackId, out var symbols)
            ? symbols.Where(s => StaffOf(s, staves) == staff && s.Tick >= from && s.Tick < to).Select(static s => s.Id)
            : [];

    public IEnumerable<string> ChordsAt(string trackId, long from, long to) =>
        chords.TryGetValue(trackId, out var list)
            ? list.Where(c => c.Tick >= from && c.Tick < to).Select(static c => c.Id)
            : [];

    /// <summary>A positioned symbol's staff: its own, else the bottom staff for pedals and the top for the rest.</summary>
    public static int StaffOf(ScoreSymbol symbol, int staves) =>
        Math.Clamp(
            symbol.Staff ?? (symbol.Kind is ScoreSymbolKind.PedalDown or ScoreSymbolKind.PedalUp ? staves - 1 : 0),
            0,
            staves - 1
        );

    static void Add<TKey, TValue>(Dictionary<TKey, List<TValue>> into, TKey key, TValue value) where TKey : notnull {
        if (!into.TryGetValue(key, out var list)) {
            list = [];
            into[key] = list;
        }

        list.Add(value);
    }
}
