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
[JsonDerivedType(typeof(SetPunchMessage), "setPunch")]
[JsonDerivedType(typeof(FlashbackCaptureMessage), "flashbackCapture")]
[JsonDerivedType(typeof(PreviewFileMessage), "previewFile")]
[JsonDerivedType(typeof(SetChannelMessage), "setChannel")]
[JsonDerivedType(typeof(SetInsertParameterMessage), "setInsertParameter")]
[JsonDerivedType(typeof(SetSendLevelMessage), "setSendLevel")]
[JsonDerivedType(typeof(SetMixerMessage), "setMixer")]
[JsonDerivedType(typeof(RenderMessage), "render")]
[JsonDerivedType(typeof(CancelRenderMessage), "cancelRender")]
[JsonDerivedType(typeof(RenderProgressMessage), "renderProgress")]
[JsonDerivedType(typeof(RenderFinishedMessage), "renderFinished")]
[JsonDerivedType(typeof(OpenPluginEditorMessage), "openPluginEditor")]
[JsonDerivedType(typeof(ParameterChangedMessage), "parameterChanged")]
[JsonDerivedType(typeof(PluginProblemMessage), "pluginProblem")]
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

/// <summary>
///     A channel strip in engine units: linear gain (the server folds VCA offsets in), pan −1…1, mute and solo, its
///     inserts in order, its sends, and its output (0: the master, 1…64: that bus, −N: the device's output pair N).
/// </summary>
public sealed record EngineStrip(
    float Gain,
    float Pan,
    bool Mute,
    bool Solo,
    IReadOnlyList<EngineInsert>? Inserts = null,
    IReadOnlyList<EngineSend>? Sends = null,
    int Output = 0
);

/// <summary>
///     An insert effect: the plugin, bypass, parameter values by id, saved opaque state (base64), and its sidechain
///     (another engine track's post-fader signal, or a bus; −1 and 0 for none).
/// </summary>
public sealed record EngineInsert(
    string Id,
    string PluginId,
    bool Bypassed,
    IReadOnlyDictionary<string, double> Parameters,
    string? State = null,
    int SidechainTrack = -1,
    int SidechainBus = 0
);

/// <summary>A send to bus 1…64 at linear gain, with its pan, taken before or after the fader.</summary>
public sealed record EngineSend(string Id, int Bus, float Gain, float Pan, bool PreFader);

/// <summary>
///     A track's runtime settings. <paramref name="InputChannel" /> is the 0-based first device input (−1: none);
///     a stereo track takes it and the next one. An aux track plays bus <paramref name="InputBus" /> (1…64; 0 for
///     other tracks). A frozen track plays <paramref name="FreezeFile" /> (a stereo WAV of its source and inserts, from
///     the project start) instead of its regions, instrument and inserts, into its fader.
/// </summary>
public sealed record EngineTrack(
    int Id,
    int InputChannel,
    bool Stereo,
    bool RecordArmed,
    bool Monitoring,
    EngineStrip Strip,
    EngineInstrument? Instrument = null,
    EngineMidiIo? Midi = null,
    int InputBus = 0,
    string? FreezeFile = null
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
    string RecordDirectory,
    IReadOnlyList<EngineAutomationTarget>? AutomationTargets = null
) : IpcMessage;

/// <summary>
///     What automation events of target index <paramref name="Index" /> drive
///     (<see cref="Segments.PlaybackEvent.Target" />):
///     <paramref name="Kind" /> <c>volume</c> (linear gain), <c>pan</c>, <c>mute</c> (0/1), <c>send</c> (linear gain of
///     send <paramref name="Id" />), <c>insert</c> (parameter <paramref name="ParameterId" /> of insert
///     <paramref name="Id" />) or <c>instrument</c> (its parameter); on engine track <paramref name="TrackId" /> (−1: the
///     master). Sent with every model; indices are stable while the project is open.
/// </summary>
public sealed record EngineAutomationTarget(
    int Index,
    int TrackId,
    string Kind,
    string? Id = null,
    string? ParameterId = null);

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

/// <summary>
///     One recorded take: a WAV file the host finished writing. A take that wrapped (cycle recording, or a Flashback
///     Capture that looped) lists its <paramref name="Passes" /> in order; null when it is one linear pass from
///     <paramref name="StartSample" />.
/// </summary>
public sealed record RecordedTake(
    int TrackId,
    string Path,
    long StartSample,
    long Frames,
    int Channels,
    int SampleRate,
    IReadOnlyList<RecordedPass>? Passes = null);

/// <summary>
///     One pass of a take: file frames [<paramref name="FileOffset" />, + <paramref name="Frames" />) were played at
///     project samples [<paramref name="StartSample" />, + <paramref name="Frames" />).
/// </summary>
public sealed record RecordedPass(long StartSample, long FileOffset, long Frames);

/// <summary>One live MIDI message at an absolute project sample (status 0x80…0xEF).</summary>
public sealed record RecordedMidiEvent(long Sample, int Status, int Data1, int Data2);

/// <summary>The MIDI a record-armed instrument or external MIDI track received during a recording pass.</summary>
public sealed record RecordedMidiTake(
    int TrackId,
    long StartSample,
    long EndSample,
    IReadOnlyList<RecordedMidiEvent> Events);

/// <summary>
///     Host → server: recording stopped (or, with <paramref name="Flashback" />, a Flashback Capture finished); the
///     audio takes are complete files.
/// </summary>
public sealed record RecordingFinishedMessage(
    IReadOnlyList<RecordedTake> Takes,
    IReadOnlyList<RecordedMidiTake>? MidiTakes = null,
    bool Flashback = false
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

/// <summary>
///     A plugin the host can load. <paramref name="Format" /> is <c>builtin</c>, <c>clap</c>, <c>vst3</c> or
///     <c>au</c>.
/// </summary>
public sealed record EnginePlugin(
    string Id,
    string Name,
    string Vendor,
    string Version,
    string Format,
    bool IsInstrument,
    IReadOnlyList<string> FileTypes,
    IReadOnlyList<EngineParameter> Parameters,
    string Category = "",
    bool HasSidechain = false,
    bool HasEditor = false
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

/// <summary>
///     A track's instrument state, base64; null when its instrument keeps none beyond its parameters. With
///     <paramref name="InsertId" />, the state of that insert of the track (−1: the master) instead (Phase 4).
/// </summary>
public sealed record InstrumentState(int TrackId, string? State, string? InsertId = null);

public sealed record InstrumentStatesMessage(IReadOnlyList<InstrumentState> States) : IpcMessage;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
)]
[JsonSerializable(typeof(IpcMessage))]
internal sealed partial class IpcJsonContext : JsonSerializerContext;

/// <summary>
///     Server → host: autopunch. While recording, armed tracks record (and monitor their input) only between
///     <paramref name="InSample" /> and <paramref name="OutSample" />; elsewhere they play back.
/// </summary>
public sealed record SetPunchMessage(bool Enabled, long InSample, long OutSample) : IpcMessage;

/// <summary>
///     Server → host: Flashback Capture. While playing (not recording), the host keeps the last minutes of every
///     record-armed track's input and of live MIDI on armed or thru tracks; this writes what it kept since playback last
///     started (or up to now) as takes at the positions they were played, and answers with a
///     <see cref="RecordingFinishedMessage" /> whose <c>Flashback</c> is set.
/// </summary>
public sealed record FlashbackCaptureMessage : IpcMessage;

/// <summary>Server → host: play a file through the preview channel at <paramref name="Gain" /> (null path: stop).</summary>
public sealed record PreviewFileMessage(string? Path, float Gain) : IpcMessage;

/// <summary>
///     Server → host: a strip's structure changed (inserts added, removed, moved, bypassed, re-sidechained; sends;
///     output). The host keeps the instances of inserts whose id and plugin are unchanged. −1 is the master.
/// </summary>
public sealed record SetChannelMessage(int TrackId, EngineStrip Strip) : IpcMessage;

public sealed record SetInsertParameterMessage(int TrackId, string InsertId, string ParameterId, double Value)
    : IpcMessage;

public sealed record SetSendLevelMessage(int TrackId, string SendId, float Gain, float Pan) : IpcMessage;

/// <summary>
///     Server → host: plugin delay compensation (<c>all</c>, <c>tracks</c>, <c>off</c>) and low-latency mode with its
///     limit in milliseconds.
/// </summary>
public sealed record SetMixerMessage(string Pdc, bool LowLatency, double LowLatencyLimitMs) : IpcMessage;

/// <summary>
///     Server → host: render <paramref name="StartSample" />…<paramref name="EndSample" /> to 32-bit float stereo WAV,
///     offline (faster than real time, transport stopped, device output silent) unless <paramref name="Realtime" />.
///     <paramref name="Tracks" /> null: the master output to <paramref name="Path" />; otherwise one file per engine
///     track, <c>{Path}/{trackId}.wav</c>, post-fader as it reaches its output; with <paramref name="Freeze" />, the one
///     track's source and inserts before its fader. The host answers with progress and a finish.
/// </summary>
public sealed record RenderMessage(
    int JobId,
    long StartSample,
    long EndSample,
    string Path,
    IReadOnlyList<int>? Tracks = null,
    bool Freeze = false,
    bool Realtime = false
) : IpcMessage;

public sealed record CancelRenderMessage(int JobId) : IpcMessage;

public sealed record RenderProgressMessage(int JobId, double Fraction) : IpcMessage;

/// <summary>Host → server: a render finished (<paramref name="Error" /> null) or failed; the files it wrote.</summary>
public sealed record RenderFinishedMessage(int JobId, IReadOnlyList<string> Files, string? Error = null) : IpcMessage;

/// <summary>Server → host: open or close a plugin's own editor window (an insert, or the instrument when null).</summary>
public sealed record OpenPluginEditorMessage(int TrackId, string? InsertId, bool Open) : IpcMessage;

/// <summary>Host → server: a plugin's own editor changed a parameter (not automation playback).</summary>
public sealed record ParameterChangedMessage(int TrackId, string? InsertId, string ParameterId, double Value)
    : IpcMessage;

/// <summary>Host → server: a plugin crashed or failed to load and is bypassed.</summary>
public sealed record PluginProblemMessage(int TrackId, string? InsertId, string PluginId, string Message) : IpcMessage;
