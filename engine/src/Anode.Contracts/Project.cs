using System.Text.Json.Serialization;

namespace Anode.Contracts;

/// <summary>A project in the recent list.</summary>
public sealed record ProjectSummary(string Id, string Name, string Path, DateTimeOffset Modified);

/// <summary>
///     The open project (docs/01-implementation-plan.md §5). Musical positions are ticks at 960 PPQ; audio offsets,
///     lengths and fades are frames at the project's <c>sampleRate</c>. <c>revision</c> grows with every edit and is
///     the ETag; <c>dirty</c> says whether it has unsaved edits.
/// </summary>
public sealed record Project(
    string Id,
    string Name,
    string Path,
    long Revision,
    bool Dirty,
    int SampleRate,
    ProjectSettings Settings,
    ChannelStrip Master,
    IReadOnlyList<Track> Tracks,
    IReadOnlyList<AudioRegion> Regions,
    IReadOnlyList<AudioFile> AudioFiles,
    IReadOnlyList<MidiRegion> MidiRegions,
    IReadOnlyList<TakeFolder> TakeFolders,
    IReadOnlyList<Marker> Markers,
    IReadOnlyList<ArrangementMarker> Arrangement,
    string Notes,
    IReadOnlyList<MixerGroup> Groups,
    IReadOnlyList<AutomationLane> Automation
);

/// <summary>
///     Global settings. <c>tempo</c> and <c>signature</c> are the values at the project start (the first of
///     <c>tempoEvents</c> and <c>signatures</c>, which are the global tempo and signature tracks).
///     <c>endTick</c> is the project end marker. <c>mergeMidiRecording</c>: a MIDI recording over an
///     existing MIDI region adds its notes to that region instead of making a new one. <c>smartTempo</c> and
///     <c>varispeed</c> (Phase 5): the project tempo mode and Varispeed.
/// </summary>
public sealed record ProjectSettings(
    double Tempo,
    TimeSignature Signature,
    CycleRange Cycle,
    MetronomeSettings Metronome,
    long EndTick,
    bool MergeMidiRecording,
    IReadOnlyList<TempoEvent> TempoEvents,
    IReadOnlyList<SignatureEvent> Signatures,
    IReadOnlyList<KeyEvent> Keys,
    RecordingSettings Recording,
    MixerSettings Mixer,
    SmartTempoSettings SmartTempo,
    VarispeedSettings Varispeed
);

public sealed record TimeSignature(int Numerator, int Denominator);

public sealed record CycleRange(bool Enabled, long StartTick, long EndTick);

/// <summary>The click: on or off, a one-bar count-in before recording, and its level in dB.</summary>
public sealed record MetronomeSettings(bool Enabled, bool CountIn, double LevelDb);

[JsonConverter(typeof(JsonStringEnumConverter<TrackKind>))]
public enum TrackKind {
    [JsonStringEnumMemberName("audio")]
    Audio,

    /// <summary>A software instrument: MIDI regions played by <c>instrument</c>.</summary>
    [JsonStringEnumMemberName("instrument")]
    Instrument,

    /// <summary>External MIDI: MIDI regions sent to <c>midiOutput</c>.</summary>
    [JsonStringEnumMemberName("midi")]
    Midi,

    /// <summary>
    ///     A folder track stack: the tracks whose <c>parentId</c> is this track. Its strip's volume offsets theirs, its
    ///     mute and solo apply to all of them; it plays nothing itself.
    /// </summary>
    [JsonStringEnumMemberName("stack")]
    Stack,

    /// <summary>An aux channel: it plays what reaches bus <c>inputBus</c> through its strip.</summary>
    [JsonStringEnumMemberName("aux")]
    Aux,

    /// <summary>A VCA: its volume, mute and solo control the tracks whose strip names it in <c>vcaId</c>.</summary>
    [JsonStringEnumMemberName("vca")]
    Vca
}

/// <summary>
///     A track and its channel strip. <c>color</c> is a CSS colour; <c>input</c> is where an audio track records from.
///     An instrument track has an <c>instrument</c>; instrument and external MIDI tracks take MIDI from
///     <c>midiInput</c>, and an external MIDI track sends its regions to <c>midiOutput</c>. Phase 5: <c>flexOn</c> and
///     <c>flexMode</c> are its flex algorithm; <c>alternatives</c> are its inactive track alternatives, and
///     <c>alternativeId</c> names the active one (null until the track has alternatives).
/// </summary>
public sealed record Track(
    string Id,
    string Name,
    TrackKind Kind,
    string Color,
    TrackInput Input,
    bool RecordArmed,
    bool InputMonitoring,
    ChannelStrip Strip,
    InstrumentSlot? Instrument,
    MidiInputSettings MidiInput,
    MidiOutputSettings? MidiOutput,
    string? ParentId,
    bool Collapsed,
    string Notes,
    int InputBus,
    AutomationMode AutomationMode,
    string? FrozenFileId,
    bool FlexOn,
    FlexMode FlexMode,
    IReadOnlyList<TrackAlternative> Alternatives,
    string? AlternativeId
);

/// <summary>The device input: 1-based first channel (0: none); a stereo input takes that channel and the next.</summary>
public sealed record TrackInput(int Channel, bool Stereo);

/// <summary>
///     A channel strip: volume in dB (−∞ as −144), pan −1 (left) … 1 (right), mute and solo; its insert effects in
///     order; its sends to buses; where it goes (<c>output</c>: <c>master</c>, <c>bus:N</c> for bus 1…64, or
///     <c>output:N</c> for the device's stereo output pair N); the mixer groups it belongs to (1…32); and the VCA track
///     that offsets its volume, if any. The master strip uses only volume, pan, mute and its inserts.
/// </summary>
public sealed record ChannelStrip(
    double VolumeDb,
    double Pan,
    bool Mute,
    bool Solo,
    IReadOnlyList<Insert> Inserts,
    IReadOnlyList<Send> Sends,
    string Output,
    IReadOnlyList<int> Groups,
    string? VcaId
);

/// <summary>
///     A region of an audio file on a track: it starts at <c>startTick</c> and plays <c>length</c> frames of the file
///     from <c>fileOffset</c>, at <c>gainDb</c>, with fades of <c>fadeIn</c> and <c>fadeOut</c> frames whose curves run
///     from −1 (slow start, exponential-looking) through 0 (linear) to 1 (fast start, logarithmic-looking): a fade-in's
///     gain at position x (0…1) is x^(2^(−2c)), a fade-out the mirror. A muted region
///     does not play. Two regions on a track may overlap only as a crossfade: where the earlier fades out and the later
///     fades in.
/// </summary>
/// <remarks>
///     Phase 5 (docs/06-phase-5.md, <c>RegionTiming</c>): a region is <em>warped</em> when its track's flex is on, its
///     <c>follow</c> is not <c>off</c>, or it has a <c>transpose</c> (semitones), a <c>fineTune</c> (cents) or
///     <c>pitchNotes</c>; these need Flex or Follow, and the server refuses them otherwise. An unwarped region plays
///     <c>fileOffset + n</c> at <c>startTick</c> plus n frames, as before. A warped region's file frames map through
///     its base map (by <c>follow</c> and the file's <c>tempo</c>), scaled linearly between its <c>flexMarkers</c>;
///     its end tick is where its last file frame lands (derived, never stored). Fades stay in project frames.
/// </remarks>
public sealed record AudioRegion(
    string Id,
    string TrackId,
    string FileId,
    string Name,
    long StartTick,
    long FileOffset,
    long Length,
    double GainDb,
    long FadeIn,
    long FadeOut,
    double FadeInCurve,
    double FadeOutCurve,
    bool Muted,
    FlexFollow Follow,
    IReadOnlyList<FlexMarker> FlexMarkers,
    int Transpose,
    int FineTune,
    IReadOnlyList<PitchNote> PitchNotes
);

/// <summary>
///     An audio file of the project; <c>path</c> is relative to the project folder. <c>tempo</c> is its File Tempo
///     (Smart Tempo), null until analysed.
/// </summary>
public sealed record AudioFile(
    string Id,
    string Name,
    string Path,
    int SampleRate,
    int Channels,
    long Frames,
    int BitDepth,
    FileTempo? Tempo
);

public sealed record CreateProjectRequest(string Name, string? Directory, int? SampleRate);

public sealed record OpenProjectRequest(string Path);

/// <summary>Import an audio file the server can read from its own disk (the desktop app, a headless box's library).</summary>
public sealed record ImportFileRequest(string Path);
