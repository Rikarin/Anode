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
    IReadOnlyList<AudioFile> AudioFiles
);

/// <summary>Global settings. <c>endTick</c> is the project end marker.</summary>
public sealed record ProjectSettings(
    double Tempo,
    TimeSignature Signature,
    CycleRange Cycle,
    MetronomeSettings Metronome,
    long EndTick
);

public sealed record TimeSignature(int Numerator, int Denominator);

public sealed record CycleRange(bool Enabled, long StartTick, long EndTick);

/// <summary>The click: on or off, a one-bar count-in before recording, and its level in dB.</summary>
public sealed record MetronomeSettings(bool Enabled, bool CountIn, double LevelDb);

[JsonConverter(typeof(JsonStringEnumConverter<TrackKind>))]
public enum TrackKind {
    [JsonStringEnumMemberName("audio")]
    Audio
}

/// <summary>
///     A track and its channel strip. <c>color</c> is a CSS colour; <c>input</c> is where recording takes its audio
///     from.
/// </summary>
public sealed record Track(
    string Id,
    string Name,
    TrackKind Kind,
    string Color,
    TrackInput Input,
    bool RecordArmed,
    bool InputMonitoring,
    ChannelStrip Strip
);

/// <summary>The device input: 1-based first channel (0: none); a stereo input takes that channel and the next.</summary>
public sealed record TrackInput(int Channel, bool Stereo);

/// <summary>Volume in dB (−∞ as −144), pan −1 (left) … 1 (right), mute and solo.</summary>
public sealed record ChannelStrip(double VolumeDb, double Pan, bool Mute, bool Solo);

/// <summary>
///     A region of an audio file on a track: it starts at <c>startTick</c> and plays <c>length</c> frames of the file
///     from <c>fileOffset</c>, at <c>gainDb</c>, with linear fades of <c>fadeIn</c> and <c>fadeOut</c> frames.
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
    long FadeOut
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
