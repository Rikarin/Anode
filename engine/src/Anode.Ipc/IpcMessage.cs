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
[JsonDerivedType(typeof(PluginCatalogMessage), "pluginCatalog")]
[JsonDerivedType(typeof(ScanPluginsMessage), "scanPlugins")]
[JsonDerivedType(typeof(MidiPortsMessage), "midiPorts")]
[JsonDerivedType(typeof(SetInstrumentMessage), "setInstrument")]
[JsonDerivedType(typeof(SetParameterMessage), "setParameter")]
[JsonDerivedType(typeof(SetMidiIoMessage), "setMidiIo")]
[JsonDerivedType(typeof(SetMidiThruMessage), "setMidiThru")]
[JsonDerivedType(typeof(LiveMidiMessage), "liveMidi")]
[JsonDerivedType(typeof(MidiActivityMessage), "midiActivity")]
[JsonDerivedType(typeof(RequestInstrumentStatesMessage), "requestInstrumentStates")]
[JsonDerivedType(typeof(InstrumentStatesMessage), "instrumentStates")]
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
    EngineStrip Strip,
    EngineInstrument? Instrument = null,
    EngineMidiIo? Midi = null
);

/// <summary>
///     An instrument for a track: the plugin, its parameter values by parameter id, the file it loads (absolute path)
///     and its saved opaque state (base64), each when it has one.
/// </summary>
public sealed record EngineInstrument(
    string PluginId,
    IReadOnlyDictionary<string, double> Parameters,
    string? File = null,
    string? State = null
);

/// <summary>
///     A track's MIDI: live input from <paramref name="InputDevice" /> (null: every device) on
///     <paramref name="InputChannel" /> (0: all, else 1…16); an external MIDI track sends its events to
///     <paramref name="OutputDevice" /> on <paramref name="OutputChannel" /> (1…16).
/// </summary>
public sealed record EngineMidiIo(string? InputDevice, int InputChannel, string? OutputDevice, int OutputChannel);

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
///     <see cref="Segments.TrackEntry.MetronomeTrack" /> in the model) and at what linear gain, and whether a recording
///     started from a stop gets a one-bar count-in (<paramref name="CountIn" />, the project's
///     <c>MetronomeSettings.CountIn</c>). The host times the count-in from the model's clicks and sounds it at
///     <paramref name="Gain" /> even when the metronome itself is off.
/// </summary>
public sealed record SetMetronomeMessage(bool Enabled, float Gain, bool CountIn = false) : IpcMessage;

/// <summary>One recorded take: a WAV file the host finished writing.</summary>
public sealed record RecordedTake(
    int TrackId,
    string Path,
    long StartSample,
    long Frames,
    int Channels,
    int SampleRate);

/// <summary>One live MIDI message at an absolute project sample (status 0x80…0xEF).</summary>
public sealed record RecordedMidiEvent(long Sample, int Status, int Data1, int Data2);

/// <summary>The MIDI a record-armed instrument or external MIDI track received during a recording pass.</summary>
public sealed record RecordedMidiTake(
    int TrackId,
    long StartSample,
    long EndSample,
    IReadOnlyList<RecordedMidiEvent> Events);

/// <summary>Host → server: recording stopped; the audio takes are complete files.</summary>
public sealed record RecordingFinishedMessage(
    IReadOnlyList<RecordedTake> Takes,
    IReadOnlyList<RecordedMidiTake>? MidiTakes = null
) : IpcMessage;

/// <summary>A plugin parameter as the engine numbers it.</summary>
public sealed record EngineParameter(
    string Id,
    string Name,
    double Min,
    double Max,
    double Default,
    string Unit,
    int Steps,
    IReadOnlyList<string>? ValueNames = null
);

/// <summary>A plugin the host can load. <paramref name="Format" /> is <c>builtin</c> or <c>clap</c>.</summary>
public sealed record EnginePlugin(
    string Id,
    string Name,
    string Vendor,
    string Version,
    string Format,
    bool IsInstrument,
    IReadOnlyList<string> FileTypes,
    IReadOnlyList<EngineParameter> Parameters
);

/// <summary>Host → server: every plugin it can load; sent after the hello and after each scan.</summary>
public sealed record PluginCatalogMessage(IReadOnlyList<EnginePlugin> Plugins) : IpcMessage;

/// <summary>Server → host: rescan the CLAP folders; the host answers with a new catalogue.</summary>
public sealed record ScanPluginsMessage : IpcMessage;

public sealed record EngineMidiPort(string Id, string Name, bool Input, bool Output);

/// <summary>Host → server: the MIDI ports; sent after the hello and whenever they change.</summary>
public sealed record MidiPortsMessage(IReadOnlyList<EngineMidiPort> Ports) : IpcMessage;

/// <summary>Server → host: give a track this instrument (null removes it).</summary>
public sealed record SetInstrumentMessage(int TrackId, EngineInstrument? Instrument) : IpcMessage;

public sealed record SetParameterMessage(int TrackId, string ParameterId, double Value) : IpcMessage;

public sealed record SetMidiIoMessage(int TrackId, EngineMidiIo Midi) : IpcMessage;

/// <summary>Server → host: the track that hears live MIDI when no instrument track is record-armed (−1: none).</summary>
public sealed record SetMidiThruMessage(int TrackId) : IpcMessage;

/// <summary>One raw MIDI message.</summary>
public sealed record EngineMidiEvent(int Status, int Data1, int Data2);

/// <summary>Server → host: play these messages on a track now (the on-screen keyboard).</summary>
public sealed record LiveMidiMessage(int TrackId, IReadOnlyList<EngineMidiEvent> Events) : IpcMessage;

/// <summary>Live MIDI the host received; <paramref name="TrackId" /> −1 when no track played it.</summary>
public sealed record MidiActivityEntry(int TrackId, string? Device, int Status, int Data1, int Data2);

/// <summary>Host → server: live MIDI since the last batch, at most 30 batches a second.</summary>
public sealed record MidiActivityMessage(IReadOnlyList<MidiActivityEntry> Events) : IpcMessage;

/// <summary>Server → host: send every instrument's opaque state (before a save).</summary>
public sealed record RequestInstrumentStatesMessage : IpcMessage;

/// <summary>A track's instrument state, base64; null when its instrument keeps none beyond its parameters.</summary>
public sealed record InstrumentState(int TrackId, string? State);

public sealed record InstrumentStatesMessage(IReadOnlyList<InstrumentState> States) : IpcMessage;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
)]
[JsonSerializable(typeof(IpcMessage))]
internal sealed partial class IpcJsonContext : JsonSerializerContext;
