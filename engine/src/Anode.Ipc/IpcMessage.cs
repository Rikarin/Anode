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
[JsonDerivedType(typeof(InsertReadingsMessage), "insertReadings")]
[JsonDerivedType(typeof(LaunchCellsMessage), "launchCells")]
[JsonDerivedType(typeof(StopCellsMessage), "stopCells")]
[JsonDerivedType(typeof(CellStateMessage), "cellState")]
[JsonDerivedType(typeof(SetPreviewSyncMessage), "setPreviewSync")]
[JsonDerivedType(typeof(SlotParametersMessage), "slotParameters")]
[JsonDerivedType(typeof(ScriptLogMessage), "scriptLog")]
[JsonDerivedType(typeof(SetScriptMessage), "setScript")]
[JsonDerivedType(typeof(SetStepInputForwardMessage), "setStepInputForward")]
[JsonDerivedType(typeof(StepInputMidiMessage), "stepInputMidi")]
[JsonDerivedType(typeof(PreviewRangeMessage), "previewRange")]
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
///     <paramref name="MidiEffects" /> (Phase 6) are its MIDI FX slots in order, as inserts (no sidechain): the host
///     runs them on the track's block MIDI (regions, live and step input) before the instrument, a bypassed slot
///     passing it through; <see cref="SetChannelMessage" /> keeps the instances whose id and plugin are unchanged, as
///     for inserts.
/// </summary>
public sealed record EngineStrip(
    float Gain,
    float Pan,
    bool Mute,
    bool Solo,
    IReadOnlyList<EngineInsert>? Inserts = null,
    IReadOnlyList<EngineSend>? Sends = null,
    int Output = 0,
    IReadOnlyList<EngineInsert>? MidiEffects = null
);

/// <summary>
///     An insert effect: the plugin, bypass, parameter values by id, saved opaque state (base64), and its sidechain
///     (another engine track's post-fader signal, or a bus; −1 and 0 for none). <paramref name="Source" /> (Phase 7) is
///     a Scripter MIDI FX slot's script (the contract's <c>Insert.source</c>): the host compiles it on the control
///     thread when it creates the instance, and answers with a <see cref="SlotParametersMessage" />.
/// </summary>
public sealed record EngineInsert(
    string Id,
    string PluginId,
    bool Bypassed,
    IReadOnlyDictionary<string, double> Parameters,
    string? State = null,
    int SidechainTrack = -1,
    int SidechainBus = 0,
    string? File = null,
    string? Source = null
);

/// <summary>A send to bus 1…64 at linear gain, with its pan, taken before or after the fader.</summary>
public sealed record EngineSend(string Id, int Bus, float Gain, float Pan, bool PreFader);

/// <summary>
///     A track's runtime settings. <paramref name="InputChannel" /> is the 0-based first device input (−1: none);
///     a stereo track takes it and the next one. An aux track plays bus <paramref name="InputBus" /> (1…64; 0 for
///     other tracks). A frozen track plays <paramref name="FreezeFile" /> (a stereo WAV of its source and inserts, from
///     the project start) instead of its regions, instrument and inserts, into its fader.
///     <para>
///         Phase 6: a Drum Machine Designer track's <paramref name="NoteRoutes" /> send the live MIDI it receives to
///         its pad tracks by note (a note with no route is dropped; its regions' notes the server routes itself). A
///         track whose <paramref name="LiveLoopsActive" /> is on plays its playing cell and not its segment slice.
///     </para>
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
    string? FreezeFile = null,
    IReadOnlyList<EngineNoteRoute>? NoteRoutes = null,
    bool LiveLoopsActive = false
);

/// <summary>
///     A Drum Machine Designer route: live MIDI note <paramref name="Note" /> goes to engine track
///     <paramref name="TrackId" /> (a pad), played there as the pad's base note.
/// </summary>
public sealed record EngineNoteRoute(int Note, int TrackId);

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
/// <remarks>
///     Format 4 (Phase 6, docs/07-phase-6.md): a whole model carries the tempo points (the compiled tempo map the
///     engine computes every block's transport context from) and every Live Loops cell; a per-track segment carries no
///     tempo points (the model's stay) and the cells of the tracks it holds, which replace those tracks' cells. Cell
///     indices, like automation target indices, are stable while the project is open.
/// </remarks>
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
///     <paramref name="Id" />), <c>midiEffect</c> (parameter <paramref name="ParameterId" /> of MIDI FX slot
///     <paramref name="Id" />, Phase 6) or <c>instrument</c> (its parameter); on engine track
///     <paramref name="TrackId" /> (−1: the master). Sent with every model; indices are stable while the project is
///     open. (Smart Control automation reaches the engine as automation of the parameters it maps.)
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

/// <summary>A plugin parameter as the engine numbers it; <paramref name="Macro" /> is its macro hint (Phase 6).</summary>
public sealed record EngineParameter(
    string Id,
    string Name,
    double Min,
    double Max,
    double Default,
    string Unit,
    int Steps,
    IReadOnlyList<string>? ValueNames = null,
    int? Macro = null
);

/// <summary>
///     A plugin the host can load. <paramref name="Format" /> is <c>builtin</c>, <c>clap</c>, <c>vst3</c> or
///     <c>au</c>. A MIDI effect (Phase 6) is not an instrument and has <paramref name="Category" />
///     <c>MIDI Effect</c>.
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
/// <remarks>
///     Bounce in place (Phase 5, docs/06-phase-5.md) renders as a freeze, for every track of
///     <paramref name="Tracks" />: each isolated (its source alone, no sends), through its inserts unless
///     <paramref name="BypassInserts" />, and through its fader and pan too when <paramref name="IncludeVolumePan" />
///     (both apply only with <paramref name="Freeze" />); the server extends the range by the tail it wants.
/// </remarks>
public sealed record RenderMessage(
    int JobId,
    long StartSample,
    long EndSample,
    string Path,
    IReadOnlyList<int>? Tracks = null,
    bool Freeze = false,
    bool Realtime = false,
    bool IncludeVolumePan = false,
    bool BypassInserts = false
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

/// <summary>An insert's reading (<c>IAudioEffect.Reading</c>: a compressor's gain reduction in dB, a meter's level).</summary>
public sealed record InsertReadingEntry(int TrackId, string InsertId, float Value);

/// <summary>
///     Host → server: the inserts' readings (track −1: the master; inserts without one are left out), at most 15 times a
///     second while any insert has one.
/// </summary>
public sealed record InsertReadingsMessage(IReadOnlyList<InsertReadingEntry> Readings) : IpcMessage;

/// <summary>A Live Loops quantise (the contract's <c>LaunchQuantize</c>): the boundary a launch or stop waits for.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EngineQuantize>))]
public enum EngineQuantize {
    [JsonStringEnumMemberName("off")]
    Off,

    [JsonStringEnumMemberName("sixteenth")]
    Sixteenth,

    [JsonStringEnumMemberName("eighth")]
    Eighth,

    [JsonStringEnumMemberName("quarter")]
    Quarter,

    [JsonStringEnumMemberName("half")]
    Half,

    [JsonStringEnumMemberName("bar")]
    Bar,

    [JsonStringEnumMemberName("twoBars")]
    TwoBars,

    [JsonStringEnumMemberName("fourBars")]
    FourBars
}

/// <summary>A cell's state in the launcher (the contract's <c>CellState</c>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EngineCellState>))]
public enum EngineCellState {
    [JsonStringEnumMemberName("queued")]
    Queued,

    [JsonStringEnumMemberName("playing")]
    Playing,

    [JsonStringEnumMemberName("stopping")]
    Stopping,

    [JsonStringEnumMemberName("stopped")]
    Stopped
}

/// <summary>
///     Launch cell <paramref name="Cell" /> (its index in the segment's cell table) at the next
///     <paramref name="Quantize" /> boundary.
/// </summary>
public sealed record CellLaunch(int Cell, EngineQuantize Quantize);

/// <summary>
///     Stop the cell playing on engine track <paramref name="TrackId" /> at the next <paramref name="Quantize" />
///     boundary.
/// </summary>
public sealed record CellStop(int TrackId, EngineQuantize Quantize);

/// <summary>
///     Server → host: launch cells and stop others, as one decision (a scene launch: its cells, and stops on the
///     tracks with none in it; the server resolves scenes and each cell's quantise). The host starts and stops them
///     sample-accurately at their boundaries, computed from the segment's tempo points; a request within 1 ms of a
///     boundary takes the next one. A launch replaces the track's playing cell at that boundary; a retrigger cell
///     launched while playing restarts.
/// </summary>
public sealed record LaunchCellsMessage(IReadOnlyList<CellLaunch> Cells, IReadOnlyList<CellStop>? Stops = null)
    : IpcMessage;

/// <summary>Server → host: stop cells, each at its quantise boundary.</summary>
public sealed record StopCellsMessage(IReadOnlyList<CellStop> Stops) : IpcMessage;

/// <summary>
///     Host → server: cell <paramref name="Cell" /> (its index in the cell table) changed state at project sample
///     <paramref name="Sample" /> (for queued and stopping, the boundary it waits for); pushed from the control
///     thread as the launcher changes, never from the audio callback.
/// </summary>
public sealed record CellStateMessage(int Cell, EngineCellState State, long Sample) : IpcMessage;

/// <summary>
///     Server → host: how the preview channel plays the next <see cref="PreviewFileMessage" /> (and the current one):
///     at <paramref name="TempoRatio" /> of its own speed (the project tempo over the loop's), transposed by
///     <paramref name="TransposeSemitones" />, rendered with the Stretch renderer on the disk thread; with
///     <paramref name="StartOnBar" />, it starts on the next bar while the transport plays. A ratio of 1 and no
///     transpose play the file as it is (the default).
/// </summary>
public sealed record SetPreviewSyncMessage(double TempoRatio, int TransposeSemitones, bool StartOnBar) : IpcMessage;

/// <summary>
///     Host → server (Phase 7): MIDI FX slot <paramref name="SlotId" /> of engine track <paramref name="TrackId" /> has
///     a new parameter list (a script compiled, or failed to and has none), defined at run time. Parameters are
///     addressed by id; <see cref="SetInsertParameterMessage" /> and automation reach them as any parameter's. Sent
///     from the control thread after each compile, before the swapped-in script runs.
/// </summary>
public sealed record SlotParametersMessage(int TrackId, string SlotId, IReadOnlyList<EngineParameter> Parameters)
    : IpcMessage;

/// <summary>
///     Host → server (Phase 7): a Scripter slot's <c>Trace</c> lines (and error lines), from the script's trace ring,
///     at most 50 lines a second per slot; <paramref name="Dropped" /> counts the lines left out since the previous
///     message. Budget overruns and errors also go as <see cref="PluginProblemMessage" /> (with the line).
/// </summary>
public sealed record ScriptLogMessage(int TrackId, string SlotId, IReadOnlyList<string> Lines, int Dropped)
    : IpcMessage;

/// <summary>
///     Server → host (Phase 7): a Scripter slot's new script. The host compiles it on the control thread and swaps it in
///     between blocks without a gap (the old one runs until then; on a compile error the slot is bypassed and reports
///     a <see cref="PluginProblemMessage" />), then answers with a <see cref="SlotParametersMessage" />.
/// </summary>
public sealed record SetScriptMessage(int TrackId, string SlotId, string Source) : IpcMessage;

/// <summary>
///     Server → host (Phase 7): while <paramref name="On" />, the host forwards live MIDI note input to the server as
///     <see cref="StepInputMidiMessage" /> (MIDI step input), and still plays it on the thru track.
/// </summary>
public sealed record SetStepInputForwardMessage(bool On) : IpcMessage;

/// <summary>
///     Host → server (Phase 7): one step from the MIDI keyboard while forwarding is on: the keys that went down since
///     the previous step, sent when the first of them is released (several keys down before any goes up make a chord),
///     in the order they went down, with the highest of their velocities.
/// </summary>
public sealed record StepInputMidiMessage(IReadOnlyList<int> Pitches, int Velocity) : IpcMessage;

/// <summary>
///     Server → host (Phase 7): play frames <paramref name="StartFrame" />…<paramref name="EndFrame" /> (exclusive) of
///     a file through the preview channel at <paramref name="Gain" />, looping the range when <paramref name="Loop" />,
///     until a <see cref="PreviewFileMessage" /> (a null path stops it) or another preview replaces it.
/// </summary>
public sealed record PreviewRangeMessage(string Path, long StartFrame, long EndFrame, bool Loop, float Gain)
    : IpcMessage;
