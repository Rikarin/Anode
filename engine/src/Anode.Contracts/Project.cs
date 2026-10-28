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
    string Notes
);

/// <summary>
///     Global settings. <c>tempo</c> and <c>signature</c> are the values at the project start (the first of
///     <c>tempoEvents</c> and <c>signatures</c>, which are the global tempo and signature tracks).
///     <c>endTick</c> is the project end marker. <c>mergeMidiRecording</c>: a MIDI recording over an
///     existing MIDI region adds its notes to that region instead of making a new one.
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
    RecordingSettings Recording
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
    Stack
}

/// <summary>
///     A track and its channel strip. <c>color</c> is a CSS colour; <c>input</c> is where an audio track records from.
///     An instrument track has an <c>instrument</c>; instrument and external MIDI tracks take MIDI from
///     <c>midiInput</c>, and an external MIDI track sends its regions to <c>midiOutput</c>.
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
    string Notes
);

/// <summary>The device input: 1-based first channel (0: none); a stereo input takes that channel and the next.</summary>
public sealed record TrackInput(int Channel, bool Stereo);

/// <summary>Volume in dB (−∞ as −144), pan −1 (left) … 1 (right), mute and solo.</summary>
public sealed record ChannelStrip(double VolumeDb, double Pan, bool Mute, bool Solo);

/// <summary>
///     A region of an audio file on a track: it starts at <c>startTick</c> and plays <c>length</c> frames of the file
///     from <c>fileOffset</c>, at <c>gainDb</c>, with fades of <c>fadeIn</c> and <c>fadeOut</c> frames whose curves run
///     from −1 (slow start, exponential-looking) through 0 (linear) to 1 (fast start, logarithmic-looking): a fade-in's
///     gain at position x (0…1) is x^(2^(−2c)), a fade-out the mirror. A muted region
///     does not play. Two regions on a track may overlap only as a crossfade: where the earlier fades out and the later
///     fades in.
/// </summary>
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
    bool Muted
);

/// <summary>An audio file of the project; <c>path</c> is relative to the project folder.</summary>
public sealed record AudioFile(
    string Id,
    string Name,
    string Path,
    int SampleRate,
    int Channels,
    long Frames,
    int BitDepth
);

public sealed record CreateProjectRequest(string Name, string? Directory, int? SampleRate);

public sealed record OpenProjectRequest(string Path);

/// <summary>Import an audio file the server can read from its own disk (the desktop app, a headless box's library).</summary>
public sealed record ImportFileRequest(string Path);
