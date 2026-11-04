using System.Text.Json.Serialization;

namespace Anode.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<JobKind>))]
public enum JobKind {
    [JsonStringEnumMemberName("bounce")]
    Bounce,

    [JsonStringEnumMemberName("freeze")]
    Freeze,

    /// <summary>Pitch analysis of a file (Flex Pitch); an <c>analysis</c> event follows when it is cached.</summary>
    [JsonStringEnumMemberName("analyze")]
    Analyze,

    [JsonStringEnumMemberName("bounceInPlace")]
    BounceInPlace,

    /// <summary>Selection-based processing.</summary>
    [JsonStringEnumMemberName("process")]
    Process,

    /// <summary>Phase 7: an Audio File Editor operation (<c>AudioEditOperation</c>), writing a new version of a file.</summary>
    [JsonStringEnumMemberName("audioEdit")]
    AudioEdit,

    /// <summary>Phase 8: the Stem Splitter (<c>StemSplitRequest</c>, docs/adr/0010-stem-splitter.md).</summary>
    [JsonStringEnumMemberName("stemSplit")]
    StemSplit,

    /// <summary>Phase 9: MusicXML export (<c>MusicXmlExportRequest</c>, docs/10-phase-9.md).</summary>
    [JsonStringEnumMemberName("musicXmlExport")]
    MusicXmlExport,

    /// <summary>Phase 9: MusicXML import (<c>MusicXmlImportRequest</c>); applies an <c>importTracks</c> command.</summary>
    [JsonStringEnumMemberName("musicXmlImport")]
    MusicXmlImport,

    /// <summary>Phase 9: AAF export (<c>AafExportRequest</c>).</summary>
    [JsonStringEnumMemberName("aafExport")]
    AafExport,

    /// <summary>Phase 9: AAF import (<c>AafImportRequest</c>); copies the media, then applies <c>importTracks</c>.</summary>
    [JsonStringEnumMemberName("aafImport")]
    AafImport,

    /// <summary>Phase 9: Standard MIDI File export (<c>MidiExportRequest</c>).</summary>
    [JsonStringEnumMemberName("midiExport")]
    MidiExport,

    /// <summary>Phase 9: Standard MIDI File import with options (<c>MidiImportRequest</c>); applies <c>importTracks</c>.</summary>
    [JsonStringEnumMemberName("midiImport")]
    MidiImport,

    /// <summary>Phase 10: ADM BWF export (<c>AdmExportRequest</c>, docs/11-phase-10.md).</summary>
    [JsonStringEnumMemberName("admExport")]
    AdmExport,

    /// <summary>Phase 10: ADM BWF import (<c>AdmImportRequest</c>); copies the media, then applies <c>importTracks</c>.</summary>
    [JsonStringEnumMemberName("admImport")]
    AdmImport,

    /// <summary>
    ///     Phase 10: movie import (<c>MovieImportRequest</c>); probes, copies or references it, then applies
    ///     <c>setMovie</c>.
    /// </summary>
    [JsonStringEnumMemberName("movieImport")]
    MovieImport,

    /// <summary>Phase 10: Import Audio from Movie (<c>MovieAudioImportRequest</c>); applies <c>importTracks</c>.</summary>
    [JsonStringEnumMemberName("movieAudioImport")]
    MovieAudioImport,

    /// <summary>Phase 10: Export Audio to Movie (<c>MovieAudioExportRequest</c>); its file is the new movie.</summary>
    [JsonStringEnumMemberName("movieAudioExport")]
    MovieAudioExport,

    /// <summary>Phase 10: Detect Scene Cuts (<c>SceneDetectRequest</c>); applies <c>addSceneMarkers</c>.</summary>
    [JsonStringEnumMemberName("sceneDetect")]
    SceneDetect,

    /// <summary>Phase 10: movie thumbnails (<c>ThumbnailsRequest</c>); its files are the JPEGs, cached per movie.</summary>
    [JsonStringEnumMemberName("thumbnails")]
    Thumbnails,

    /// <summary>
    ///     Phase 11: Chord ID (<c>ChordIdRequest</c>, docs/12-phase-11.md); applies <c>applyChordId</c> (the chord track
    ///     and the regions' keys, one undo step).
    /// </summary>
    [JsonStringEnumMemberName("chordId")]
    ChordId,

    /// <summary>
    ///     Phase 11: the Mastering Assistant's analysis (<c>MasteringAnalysisRequest</c>) over an offline render of the
    ///     master before it; applies <c>setMasteringAnalysis</c>.
    /// </summary>
    [JsonStringEnumMemberName("masteringAnalysis")]
    MasteringAnalysis,

    /// <summary>
    ///     Phase 11: Convert to MIDI or to a pattern (<c>SessionPlayerConvertRequest</c>); applies
    ///     <c>convertSessionPlayerRegions</c>.
    /// </summary>
    [JsonStringEnumMemberName("sessionPlayerConvert")]
    SessionPlayerConvert,

    /// <summary>Save As (<c>SaveProjectAsRequest</c>): writes a whole package elsewhere and goes on working there.</summary>
    [JsonStringEnumMemberName("saveAs")]
    SaveAs,

    /// <summary>Save a Copy (<c>SaveProjectAsRequest</c>): writes a whole package elsewhere; the open project stays.</summary>
    [JsonStringEnumMemberName("saveCopy")]
    SaveCopy,

    /// <summary>A change of the project's sample rate (<c>ChangeSampleRateRequest</c>); it reopens at the new rate.</summary>
    [JsonStringEnumMemberName("sampleRateChange")]
    SampleRateChange
}

[JsonConverter(typeof(JsonStringEnumConverter<JobState>))]
public enum JobState {
    [JsonStringEnumMemberName("queued")]
    Queued,

    [JsonStringEnumMemberName("running")]
    Running,

    [JsonStringEnumMemberName("done")]
    Done,

    [JsonStringEnumMemberName("failed")]
    Failed,

    [JsonStringEnumMemberName("cancelled")]
    Cancelled
}

/// <summary>A long-running job; <c>progress</c> 0…1; <c>files</c> are what it wrote (absolute paths), when done.</summary>
public sealed record Job(
    string Id,
    JobKind Kind,
    JobState State,
    double Progress,
    string? Message,
    IReadOnlyList<string> Files
);

[JsonConverter(typeof(JsonStringEnumConverter<BounceSource>))]
public enum BounceSource {
    /// <summary>The master output.</summary>
    [JsonStringEnumMemberName("mix")]
    Mix,

    /// <summary>One file per track in <c>trackIds</c> (post-fader, as it reaches its output).</summary>
    [JsonStringEnumMemberName("stems")]
    Stems
}

[JsonConverter(typeof(JsonStringEnumConverter<AudioFormat>))]
public enum AudioFormat {
    [JsonStringEnumMemberName("wav")]
    Wav,

    [JsonStringEnumMemberName("aiff")]
    Aiff
}

[JsonConverter(typeof(JsonStringEnumConverter<DitherMode>))]
public enum DitherMode {
    [JsonStringEnumMemberName("none")]
    None,

    /// <summary>Triangular (TPDF) dither.</summary>
    [JsonStringEnumMemberName("tpdf")]
    Tpdf,

    /// <summary>TPDF with first-order noise shaping.</summary>
    [JsonStringEnumMemberName("shaped")]
    Shaped
}

[JsonConverter(typeof(JsonStringEnumConverter<NormalizeMode>))]
public enum NormalizeMode {
    [JsonStringEnumMemberName("off")]
    Off,

    /// <summary>Peak to <c>peakCeilingDb</c> (true peak, 4× oversampled).</summary>
    [JsonStringEnumMemberName("peak")]
    Peak,

    /// <summary>Integrated loudness (EBU R128) to <c>loudnessLufs</c>, limited to <c>peakCeilingDb</c> true peak.</summary>
    [JsonStringEnumMemberName("loudness")]
    Loudness
}

/// <summary>
///     Bounce (export): the range (ticks; left out: the cycle when it is on, else the project start to its end), the
///     source, and the file: format, bit depth (16, 24, or 32 for float), sample rate (left out: the project's), dither
///     (applied below 32 bits), normalisation. <c>realtime</c> plays the bounce through the device instead of rendering
///     offline; <c>addToProject</c> adds the result to the project's audio files. Phase 10: a surround mix is one
///     WAVE_FORMAT_EXTENSIBLE file in the master's format; <c>monitoring</c> (a mix only) bounces what the monitoring
///     sends the device instead (the binaural render or the down-mix, before the speaker map), as ADR-0012 allows a
///     bounce that asks for it.
/// </summary>
public sealed record BounceRequest(
    string FileName,
    string? Directory = null,
    long? StartTick = null,
    long? EndTick = null,
    BounceSource Source = BounceSource.Mix,
    IReadOnlyList<string>? TrackIds = null,
    AudioFormat Format = AudioFormat.Wav,
    int BitDepth = 24,
    int? SampleRate = null,
    DitherMode Dither = DitherMode.Tpdf,
    NormalizeMode Normalize = NormalizeMode.Off,
    double LoudnessLufs = -14,
    double PeakCeilingDb = -1,
    bool Realtime = false,
    bool AddToProject = false,
    bool Monitoring = false
);

[JsonConverter(typeof(JsonStringEnumConverter<BounceInPlaceDestination>))]
public enum BounceInPlaceDestination {
    /// <summary>A new audio track under each source track, with the bounced regions.</summary>
    [JsonStringEnumMemberName("newTrack")]
    NewTrack,

    /// <summary>The bounced regions replace the source regions on their own track (audio tracks only).</summary>
    [JsonStringEnumMemberName("replace")]
    Replace
}

[JsonConverter(typeof(JsonStringEnumConverter<BounceSourceAction>))]
public enum BounceSourceAction {
    [JsonStringEnumMemberName("leave")]
    Leave,

    [JsonStringEnumMemberName("mute")]
    Mute,

    [JsonStringEnumMemberName("delete")]
    Delete
}

/// <summary>
///     Bounce in place: tracks (<c>trackIds</c>, their whole range) or regions (<c>regionIds</c>, theirs) rendered
///     isolated by the host through <c>render</c>, through their inserts unless <c>bypassEffects</c>, with fader and pan
///     only when <c>includeVolumePan</c>, plus the effects' tail when <c>includeTail</c>; normalised as a bounce. When
///     the job finishes, the server adds the files and, in one undo step, adds a new track (or replaces the regions) and
///     leaves, mutes or deletes the source. <c>name</c> names the new track or regions (left out: the source's, plus
///     " Bounce").
/// </summary>
public sealed record BounceInPlaceRequest(
    IReadOnlyList<string>? TrackIds = null,
    IReadOnlyList<string>? RegionIds = null,
    BounceInPlaceDestination Destination = BounceInPlaceDestination.NewTrack,
    BounceSourceAction Source = BounceSourceAction.Mute,
    bool BypassEffects = false,
    bool IncludeTail = true,
    bool IncludeVolumePan = false,
    NormalizeMode Normalize = NormalizeMode.Off,
    string? Name = null
);

/// <summary>A built-in effect of a processing chain, with the parameters it is set to (the rest at their defaults).</summary>
public sealed record ProcessEffect(string PluginId, IReadOnlyDictionary<string, double>? Parameters = null);

/// <summary>
///     Selection-based processing: runs <c>chain</c> (built-in effects only, in order) in-process over the regions'
///     audio between <c>startTick</c> and <c>endTick</c> (left out: the regions' own ends), with the chain's tail when
///     <c>includeTail</c>. When the job finishes, the server writes a new file per region and, in one undo step,
///     replaces that part of each region with a new region of it.
/// </summary>
public sealed record ProcessRequest(
    IReadOnlyList<string> RegionIds,
    IReadOnlyList<ProcessEffect> Chain,
    long? StartTick = null,
    long? EndTick = null,
    bool IncludeTail = false
);

/// <summary>A stem the Stem Splitter separates (docs/adr/0010-stem-splitter.md).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StemKind>))]
public enum StemKind {
    [JsonStringEnumMemberName("vocals")]
    Vocals,

    [JsonStringEnumMemberName("drums")]
    Drums,

    [JsonStringEnumMemberName("bass")]
    Bass,

    /// <summary>Everything the other stems asked for do not take.</summary>
    [JsonStringEnumMemberName("other")]
    Other,

    /// <summary>Six-stem model only.</summary>
    [JsonStringEnumMemberName("guitar")]
    Guitar,

    /// <summary>Six-stem model only.</summary>
    [JsonStringEnumMemberName("piano")]
    Piano
}

/// <summary>
///     Phase 8, the Stem Splitter (<c>POST /project/regions/{regionId}/split-stems</c>, an audio region): the stems to
///     make, each once (left out: vocals, drums, bass and other). With guitar or piano among them the six-stem model
///     runs, else the four-stem one; a stem not asked for is not written. The job writes one file per stem over the
///     region's span into the project's <c>Audio Files</c>, then, in one undo step (<c>addStems</c>), adds a track per
///     stem below the region's track, in the order of <see cref="StemKind" />, with a region at the source region's
///     position, and mutes the source region unless <c>keepSource</c>.
/// </summary>
public sealed record StemSplitRequest(IReadOnlyList<StemKind>? Stems = null, bool KeepSource = false);
