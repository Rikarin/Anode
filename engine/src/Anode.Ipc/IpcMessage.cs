using System.Text.Json.Serialization;

namespace Anode.Ipc;

/// <summary>
///     A control message between anode-server and anode-host. Frames stay small: bulk data (compiled playback models)
///     travels through shared-memory segments (<see cref="Segments.SegmentWriter" />, docs/adr/0006-bulk-ipc.md), and
///     meters and the playhead through the host's telemetry region (<see cref="Telemetry.TelemetryHeader" />).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(PingMessage), "ping")]
[JsonDerivedType(typeof(PongMessage), "pong")]
[JsonDerivedType(typeof(ErrorMessage), "error")]
[JsonDerivedType(typeof(LoadModelMessage), "loadModel")]
[JsonDerivedType(typeof(ModelLoadedMessage), "modelLoaded")]
[JsonDerivedType(typeof(SetStripMessage), "setStrip")]
[JsonDerivedType(typeof(SetTrackIoMessage), "setTrackIo")]
[JsonDerivedType(typeof(TransportMessage), "transport")]
[JsonDerivedType(typeof(SetCycleMessage), "setCycle")]
[JsonDerivedType(typeof(SetMetronomeMessage), "setMetronome")]
[JsonDerivedType(typeof(RecordingFinishedMessage), "recordingFinished")]
public abstract record IpcMessage;

/// <summary>
///     The first message each side sends. The server's carries only its version; the host answers with its own, its
///     audio backend, the device it runs (rate, buffer, channels) and the name of its telemetry region.
/// </summary>
public sealed record HelloMessage(
    string Version,
    string? Backend = null,
    int SampleRate = 0,
    int BufferFrames = 0,
    int InputChannels = 0,
    int OutputChannels = 0,
    string? TelemetryName = null
) : IpcMessage;

public sealed record PingMessage(long Id) : IpcMessage;

public sealed record PongMessage(long Id) : IpcMessage;

/// <summary>A request the peer could not carry out; <paramref name="Request" /> names it when known.</summary>
public sealed record ErrorMessage(string Message, string? Request = null) : IpcMessage;

/// <summary>Where a shared-memory segment is: <see cref="Segments.SharedSegment.Name" />, length and model version.</summary>
public sealed record SegmentRef(string Name, long Length, long Version);

/// <summary>An audio file the host may stream: its engine id, absolute path and format.</summary>
public sealed record EngineFile(int Id, string Path, int Channels, int SampleRate, long Frames);

/// <summary>A channel strip in engine units: linear gain, pan −1…1.</summary>
public sealed record EngineStrip(float Gain, float Pan, bool Mute, bool Solo);

/// <summary>
///     A track's runtime settings. <paramref name="InputChannel" /> is the 0-based first device input (−1: none);
///     a stereo track takes it and the next one.
/// </summary>
public sealed record EngineTrack(
    int Id,
    int InputChannel,
    bool Stereo,
    bool RecordArmed,
    bool Monitoring,
    EngineStrip Strip
);

/// <summary>
///     Server → host: swap in compiled playback data. With <paramref name="Replace" /> the segment is the whole model
///     (tracks it does not hold are removed, <paramref name="Tracks" /> is every track); without, it patches the tracks
///     it holds and <paramref name="Tracks" /> lists only changed ones. <paramref name="Files" /> are files the host does
///     not know yet. <paramref name="RecordDirectory" /> is where recorded takes go. The host answers
///     <see cref="ModelLoadedMessage" /> once it has mapped the segment and the model it replaces is no longer in use,
///     and the server then disposes the segment.
/// </summary>
public sealed record LoadModelMessage(
    SegmentRef Segment,
    bool Replace,
    IReadOnlyList<EngineTrack> Tracks,
    IReadOnlyList<EngineFile> Files,
    EngineStrip Master,
    string RecordDirectory
) : IpcMessage;

public sealed record ModelLoadedMessage(long Version) : IpcMessage;

/// <summary>Server → host: a channel strip changed. <paramref name="TrackId" /> −1 is the master.</summary>
public sealed record SetStripMessage(int TrackId, EngineStrip Strip) : IpcMessage;

/// <summary>Server → host: a track's input, record arm or input monitoring changed.</summary>
public sealed record SetTrackIoMessage(int TrackId, int InputChannel, bool Stereo, bool RecordArmed, bool Monitoring)
    : IpcMessage;

[JsonConverter(typeof(JsonStringEnumConverter<TransportAction>))]
public enum TransportAction {
    [JsonStringEnumMemberName("play")]
    Play,

    [JsonStringEnumMemberName("stop")]
    Stop,

    [JsonStringEnumMemberName("record")]
    Record,

    [JsonStringEnumMemberName("locate")]
    Locate
}

/// <summary>
///     Server → host: play, stop, record (from <paramref name="PositionSamples" />, or the current position when
///     negative) or locate.
/// </summary>
public sealed record TransportMessage(TransportAction Action, long PositionSamples = -1) : IpcMessage;

/// <summary>Server → host: the cycle (loop) range, in project samples.</summary>
public sealed record SetCycleMessage(bool Enabled, long StartSample, long EndSample) : IpcMessage;

/// <summary>
///     Server → host: whether the metronome sounds (its clicks are events of track
///     <see cref="Segments.TrackEntry.MetronomeTrack" /> in the model) and at what linear gain.
/// </summary>
public sealed record SetMetronomeMessage(bool Enabled, float Gain) : IpcMessage;

/// <summary>One recorded take: a WAV file the host finished writing.</summary>
public sealed record RecordedTake(
    int TrackId,
    string Path,
    long StartSample,
    long Frames,
    int Channels,
    int SampleRate);

/// <summary>Host → server: recording stopped; the takes are complete files.</summary>
public sealed record RecordingFinishedMessage(IReadOnlyList<RecordedTake> Takes) : IpcMessage;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
)]
[JsonSerializable(typeof(IpcMessage))]
internal sealed partial class IpcJsonContext : JsonSerializerContext;
