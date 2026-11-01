namespace Anode.Contracts;

// Interchange (docs/10-phase-9.md): MusicXML, AAF and Standard MIDI File export and import, as jobs. An export writes
// files as a bounce does (fileName in directory, default <project>/Exports; the job's files are what it wrote); an
// import reads a file from the server's disk (path) or an upload (the .../upload endpoints), copies its media into the
// project, and applies an importTracks command: one undo step.

/// <summary>
///     MusicXML 4.0 export (<c>POST /project/export/musicxml</c>, job <c>musicXmlExport</c>): score set
///     <c>setId</c> (null: every MIDI track) as a partwise score, or one track's part (<c>partTrackId</c>);
///     <c>compressed</c> writes <c>.mxl</c> (with <c>META-INF/container.xml</c>), else <c>.musicxml</c>, the extension
///     added to <c>fileName</c> when missing.
/// </summary>
public sealed record MusicXmlExportRequest(
    string FileName,
    string? Directory = null,
    string? SetId = null,
    string? PartTrackId = null,
    bool Compressed = false
);

/// <summary>
///     MusicXML import (<c>POST /project/import/musicxml</c>, job <c>musicXmlImport</c>): a <c>.musicxml</c>,
///     <c>.xml</c> or <c>.mxl</c> on the server's disk. Each part becomes an instrument track with a MIDI region, a
///     staff style for its clefs and transposition, and its symbols, lyrics and chord symbols; into an empty project
///     (no tracks) its tempo, meter and key changes replace the project's.
/// </summary>
public sealed record MusicXmlImportRequest(string Path);

/// <summary>
///     AAF export (<c>POST /project/export/aaf</c>, job <c>aafExport</c>): the audio tracks <c>trackIds</c> (null: every
///     audio track with regions) as one composition at the project's sample rate, with clips, offsets, gain, fades,
///     crossfades as transitions and the markers; <c>embedMedia</c> puts the audio in the file, else it is written as WAV
///     files in a folder beside it (<c>&lt;fileName&gt; Media</c>) that the file links to. <c>.aaf</c> added when missing.
/// </summary>
public sealed record AafExportRequest(
    string FileName,
    string? Directory = null,
    IReadOnlyList<string>? TrackIds = null,
    bool EmbedMedia = true
);

/// <summary>
///     AAF import (<c>POST /project/import/aaf</c>, job <c>aafImport</c>): each audio slot of the composition becomes an
///     audio track with its clips as regions (positions to the sample), embedded or linked media copied into the
///     project, and its markers.
/// </summary>
public sealed record AafImportRequest(string Path);

/// <summary>
///     Standard MIDI File export (<c>POST /project/export/midi</c>, job <c>midiExport</c>) at 960 PPQ: <c>format</c> 0
///     (one track) or 1 (a tempo track, then one per track); the tempo map, signatures, keys, markers, track names, notes,
///     controllers, pitch bend and program changes of the MIDI tracks <c>trackIds</c> (null: every track with MIDI
///     regions), or of region <c>regionId</c> alone. <c>.mid</c> added when missing.
/// </summary>
public sealed record MidiExportRequest(
    string FileName,
    string? Directory = null,
    int Format = 1,
    IReadOnlyList<string>? TrackIds = null,
    string? RegionId = null
);

/// <summary>
///     Standard MIDI File import as a job (<c>POST /project/import/midi</c>, job <c>midiImport</c>): with
///     <c>importTempo</c> the file's tempo map and signatures replace the project's; with <c>importMarkers</c> its
///     markers are added; each file track with events becomes an instrument track, or with <c>merge</c> all of them one.
///     (<c>POST /project/files/import-midi</c>, Phase 3, stays: it imports at once, with the tempo, one track per file
///     track.)
/// </summary>
public sealed record MidiImportRequest(
    string Path,
    bool ImportTempo = true,
    bool ImportMarkers = true,
    bool Merge = false
);

/// <summary>
///     A track an import adds. Its <c>id</c>, and every id in its regions (their <c>trackId</c> is ignored; note and
///     controller ids too), are provisional: unique within the <see cref="ImportedContent" />, replaced by the model.
///     <c>kind</c> is <c>instrument</c> (MIDI regions, the default synth) or <c>audio</c> (audio regions, whose
///     <c>fileId</c> is a file of the project: the job adds the media first). <c>staffStyleId</c> is a factory style or
///     one of the content's score staff styles (null: the default).
/// </summary>
public sealed record ImportedTrack(
    string Id,
    string Name,
    TrackKind Kind,
    string? StaffStyleId,
    IReadOnlyList<MidiRegion> MidiRegions,
    IReadOnlyList<AudioRegion> Regions
);

/// <summary>
///     The score content an import adds: staff styles (provisional ids), symbols, lyrics and chord symbols (provisional
///     ids, referring to provisional track and note ids), and the title and composer (set in the layout when given and
///     the project's are empty).
/// </summary>
public sealed record ImportedScore(
    IReadOnlyList<StaffStyle> StaffStyles,
    IReadOnlyList<ScoreSymbol> Symbols,
    IReadOnlyList<LyricSyllable> Lyrics,
    IReadOnlyList<ChordSymbol> Chords,
    string? Title = null,
    string? Composer = null
);

/// <summary>
///     What an import adds, as the interchange readers make it: tracks (after the project's), the tempo map, signatures
///     and keys that replace the project's (null: kept), markers to add (provisional ids), and score content.
/// </summary>
public sealed record ImportedContent(
    IReadOnlyList<ImportedTrack> Tracks,
    IReadOnlyList<TempoEvent>? TempoEvents = null,
    IReadOnlyList<SignatureEvent>? Signatures = null,
    IReadOnlyList<KeyEvent>? Keys = null,
    IReadOnlyList<Marker>? Markers = null,
    ImportedScore? Score = null
);
