using Anode.Contracts;

namespace Anode.Model.Editing;

/// <summary>
///     <c>importTracks</c> (docs/10-phase-9.md, "Import result"): what the MusicXML, AAF and Standard MIDI File imports
///     add, as one edit.
/// </summary>
public static partial class Edits {
    /// <summary>
    ///     Adds the content's tracks after the project's with their regions, staff styles and score content, giving every
    ///     provisional id a real one and rewriting the references; replaces the tempo map, signatures and keys it carries;
    ///     adds its markers; fills the layout's title and composer when they are empty.
    /// </summary>
    static List<DeltaOp> ImportTracks(ProjectState state, ImportTracksCommand c, IIdSource ids, PluginCatalog plugins) {
        var content = c.Content ?? throw new EditException("An import has content.");
        if (content.Tracks is not { } imported || state.Tracks.Count + imported.Count > MaxTracks) {
            throw new EditException($"A project holds at most {MaxTracks} tracks.");
        }

        var import = new Import(state, ids);
        if (content.TempoEvents is { } tempo) {
            import.Add(SetTempoEvents(import.State, new(tempo)));
        }

        if (content.Signatures is { } signatures) {
            import.Add(SetSignatures(import.State, new(signatures)));
        }

        if (content.Keys is { } keys) {
            import.Add(SetKeys(import.State, new(keys)));
        }

        // Staff styles first: tracks name them.
        foreach (var style in content.Score?.StaffStyles ?? []) {
            import.Style(style);
        }

        foreach (var source in imported) {
            import.Track(source, plugins);
        }

        foreach (var marker in content.Markers ?? []) {
            import.Marker(marker);
        }

        if (content.Score is { } score) {
            import.Score(score);
        }

        return import.Ops;
    }

    /// <summary>An import as it is applied: the state so far, its operations, and the provisional ids' real ones.</summary>
    sealed class Import(ProjectState state, IIdSource ids) {
        readonly Dictionary<string, string> styles = new(StringComparer.Ordinal);
        readonly Dictionary<string, string> tracks = new(StringComparer.Ordinal);
        readonly Dictionary<long, long> notes = [];
        readonly HashSet<string> regions = new(StringComparer.Ordinal);
        long next = state.LastEventId;

        public ProjectState State { get; private set; } = state;

        public List<DeltaOp> Ops { get; } = [];

        public void Add(IEnumerable<DeltaOp> step) {
            var list = step.ToList();
            State = State.Apply(list);
            Ops.AddRange(list);
        }

        public void Style(StaffStyle? style) {
            if (style?.Id is not { Length: > 0 } provisional || styles.ContainsKey(provisional)) {
                throw new EditException("Each imported staff style has its own id.");
            }

            var real = ids.Next(IdPrefix.StaffStyle);
            styles[provisional] = real;
            Add([new StaffStyleUpserted(CheckStaffStyle(style with { Id = real }, true))]);
        }

        /// <summary>A track after the project's, its style one of the content's or a factory one, with its regions.</summary>
        public void Track(ImportedTrack? source, PluginCatalog plugins) {
            if (source?.Id is not { Length: > 0 } provisional || tracks.ContainsKey(provisional)) {
                throw new EditException("Each imported track has its own id.");
            }

            var instrument = source.Kind == TrackKind.Instrument;
            if (!instrument && source.Kind != TrackKind.Audio
                || (instrument ? source.Regions?.Count : source.MidiRegions?.Count) > 0
                || (!instrument && source.StaffStyleId is not null)) {
                throw new EditException(
                    "An imported track is an instrument track (MIDI regions, a staff style) or an audio track (audio regions)."
                );
            }

            var added = AddTrack(
                State,
                new(Name(source.Name ?? string.Empty, "track name"), Kind: source.Kind),
                ids,
                plugins
            );
            var upserted = (TrackUpserted)added.Single(static o => o is TrackUpserted);
            var track = upserted.Track with { StaffStyleId = StyleId(source.StaffStyleId) };
            // Phase 10: an ADM bed's or object's format and panner (the output format and a mode the output allows
            // follow when the edit conforms, docs/11-phase-10.md).
            if (source.InputFormat is { } format) {
                CheckFormat(format);
                CheckInputFormat(track.Kind, format);
                track = track with { Strip = track.Strip with { InputFormat = format } };
            }

            if (source.Panner is not null) {
                var panner = CheckPanner(source.Panner);
                if (panner.Mode == PannerMode.Object && Objects(State) >= MaxObjects) {
                    throw new EditException($"A project has at most {MaxObjects} objects.");
                }

                track = track with { Strip = track.Strip with { Panner = panner } };
            }

            Add([..added.Select(o => o == upserted ? upserted with { Track = track } : o)]);
            tracks[provisional] = track.Id;
            foreach (var region in source.MidiRegions ?? []) {
                Add([new MidiRegionUpserted(ImportedMidi(State, region, track, ids, regions, notes, ref next))]);
            }

            foreach (var region in source.Regions ?? []) {
                Add([new RegionUpserted(ImportedAudio(State, region, track, ids, regions))]);
            }

            var targets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var lane in source.Automation ?? []) {
                if (lane?.Target is not { } target || !targets.Add(target)) {
                    throw new EditException("An imported track has at most one lane per target.");
                }

                var range = RangeOf(State, track, target, plugins);
                if (lane.Points is not { Count: > 0 and <= MaxAutomationPoints } points) {
                    throw new EditException($"An imported lane has 1…{MaxAutomationPoints} points.");
                }

                CheckPoints(points, range);
                Add([new AutomationLaneUpserted(new(ids.Next(IdPrefix.Automation), track.Id, target, [..points]))]);
            }
        }

        string? StyleId(string? id) =>
            id is null ? null
            : styles.TryGetValue(id, out var real) ? real
            : StaffStyleIds.All.Contains(id) ? id
            : throw new EditException($"No staff style '{id}' to import.");

        public void Marker(Marker? marker) {
            if (marker is null) {
                throw new EditException("An imported marker is null.");
            }

            var text = Text(marker.Text ?? string.Empty, "marker text");
            Add(
                [
                    ..AddMarker(
                            State,
                            new(marker.Name ?? string.Empty, marker.Tick, marker.LengthTicks, marker.Color, marker.Kind),
                            ids
                        )
                        .Select(o => o is MarkerUpserted m ? new MarkerUpserted(m.Marker with { Text = text }) : o)
                ]
            );
        }

        /// <summary>The symbols, lyrics and chord symbols on the real tracks and notes, and the title and composer.</summary>
        public void Score(ImportedScore score) {
            var known = NoteTracks(State, false);
            var ops = new List<DeltaOp>();
            ops.AddRange(
                (score.Symbols ?? []).Select(s => new ScoreSymbolUpserted(CheckSymbol(State, known, Symbol(s))))
            );
            var verses = new HashSet<(long, int)>();
            ops.AddRange((score.Lyrics ?? []).Select(l => new LyricUpserted(Lyric(l, verses))));
            ops.AddRange(
                (score.Chords ?? []).Select(h => new ChordSymbolUpserted(
                        CheckChord(
                            State,
                            (h ?? throw new EditException("An imported chord symbol is null.")) with {
                                Id = ids.Next(IdPrefix.ChordSymbol), TrackId = TrackId(h.TrackId)
                            }
                        )
                    )
                )
            );
            var layout = State.ScoreLayout;
            var filled = layout with {
                Title = Filled(layout.Title, score.Title), Composer = Filled(layout.Composer, score.Composer)
            };
            if (filled != layout) {
                ops.Add(new ScoreLayoutChanged(filled));
            }

            Add(ops);
        }

        ScoreSymbol Symbol(ScoreSymbol? symbol) =>
            (symbol ?? throw new EditException("An imported score symbol is null.")) with {
                Id = ids.Next(IdPrefix.ScoreSymbol),
                TrackId = TrackId(symbol.TrackId),
                NoteId = NoteId(symbol.NoteId),
                EndNoteId = NoteId(symbol.EndNoteId)
            };

        /// <summary>An imported syllable on its real note: one per note and verse (1…8), 1…200 characters.</summary>
        LyricSyllable Lyric(LyricSyllable? lyric, HashSet<(long, int)> verses) {
            if (lyric is null) {
                throw new EditException("An imported syllable is null.");
            }

            var note = NoteId(lyric.NoteId)!.Value;
            if (lyric.Verse is < 1 or > MaxVerses || !verses.Add((note, lyric.Verse))) {
                throw new EditException($"A note has one syllable in each verse (1…{MaxVerses}).");
            }

            return lyric.Text is { Length: > 0 and <= ScoreSymbols.MaxTextLength } && Enum.IsDefined(lyric.Syllabic)
                ? lyric with { Id = ids.Next(IdPrefix.Lyric), NoteId = note }
                : throw new EditException($"A syllable has 1…{ScoreSymbols.MaxTextLength} characters.");
        }

        string TrackId(string? id) =>
            tracks.TryGetValue(id ?? string.Empty, out var real)
                ? real
                : throw new EditException($"No imported track '{id}'.");

        long? NoteId(long? id) =>
            id is not { } provisional ? null
            : notes.TryGetValue(provisional, out var real) ? real
            : throw new EditException($"No imported note {provisional}.");

        /// <summary>A layout text: the project's, or the imported one (cut to length) when the project's is empty.</summary>
        static string Filled(string current, string? imported) =>
            current.Length > 0 || imported is null
                ? current
                : imported.Length <= ScoreSymbols.MaxTextLength
                    ? imported
                    : imported[..ScoreSymbols.MaxTextLength];
    }

    /// <summary>An imported MIDI region on <paramref name="track" />, with real ids for it, its notes and controllers.</summary>
    static MidiRegion ImportedMidi(
        ProjectState state,
        MidiRegion source,
        Track track,
        IIdSource ids,
        HashSet<string> regionIds,
        Dictionary<long, long> noteIds,
        ref long next
    ) {
        if (source is null || source.Id is not { Length: > 0 } || !regionIds.Add(source.Id)) {
            throw new EditException("Each imported region has its own id.");
        }

        CheckTick(source.StartTick, "region start");
        CheckLength(source.LengthTicks, "region");
        var playback = source.Playback ?? ProjectDefaults.Playback;
        if (playback.GrooveId is { } groove && !state.Grooves.ContainsKey(groove)) {
            playback = playback with { GrooveId = null };
        }

        var region = source with {
            Id = ids.Next(IdPrefix.MidiRegion),
            TrackId = track.Id,
            Name = source.Name is { } name ? Name(name, "region name") : track.Name,
            Playback = playback,
            Notes = [],
            Controls = [],
            Notation = CheckNotation(source.Notation ?? ProjectDefaults.Notation)
        };
        var notes = source.Notes ?? [];
        var controls = source.Controls ?? [];
        CheckCapacity(region, notes.Count + controls.Count);
        var importedNotes = new List<Note>(notes.Count);
        foreach (var note in notes) {
            if (note is null || noteIds.ContainsKey(note.Id)) {
                throw new EditException("Each imported note has its own id.");
            }

            var real = note with { Id = ++next };
            CheckNote(region, real, true);
            noteIds[note.Id] = real.Id;
            importedNotes.Add(real);
        }

        var importedControls = new List<ControlEvent>(controls.Count);
        foreach (var control in controls) {
            var real = (control ?? throw new EditException("An imported controller event is null.")) with {
                Id = ++next
            };
            CheckControl(region, real);
            importedControls.Add(real);
        }

        return region with { Notes = Sorted(importedNotes), Controls = Sorted(importedControls) };
    }

    /// <summary>An imported audio region on <paramref name="track" />: of a file of the project, plain (no flex).</summary>
    static AudioRegion ImportedAudio(
        ProjectState state,
        AudioRegion source,
        Track track,
        IIdSource ids,
        HashSet<string> regionIds
    ) {
        if (source is null || source.Id is not { Length: > 0 } || !regionIds.Add(source.Id)) {
            throw new EditException("Each imported region has its own id.");
        }

        var file = FileOf(state, source.FileId ?? string.Empty);
        CheckTick(source.StartTick, "region start");
        CheckSpan(file, source.FileOffset, source.Length);
        if (!(source.GainDb is >= MinRegionGainDb and <= MaxRegionGainDb)) {
            throw new EditException(
                $"Region gain {source.GainDb} dB is outside {MinRegionGainDb}…{MaxRegionGainDb} dB."
            );
        }

        if (source.FadeIn < 0 || source.FadeOut < 0 || source.FadeIn + source.FadeOut > source.Length) {
            throw new EditException("Fades are at least 0 frames and together no longer than the region.");
        }

        CheckCurve(source.FadeInCurve);
        CheckCurve(source.FadeOutCurve);
        return source with {
            Id = ids.Next(IdPrefix.Region),
            TrackId = track.Id,
            Name = source.Name is { } name ? Name(name, "region name") : file.Name,
            Follow = FlexFollow.Off,
            FlexMarkers = [],
            Transpose = 0,
            FineTune = 0,
            PitchNotes = []
        };
    }
}
