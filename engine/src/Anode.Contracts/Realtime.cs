using System.Text.Json.Serialization;

namespace Anode.Contracts;

/// <summary>A message on the <c>/ws</c> WebSocket. The first one a client receives is <see cref="ConnectedEvent" />.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ConnectedEvent), "connected")]
[JsonDerivedType(typeof(EngineEvent), "engine")]
[JsonDerivedType(typeof(ResyncEvent), "resync")]
[JsonDerivedType(typeof(ProjectOpenedEvent), "projectOpened")]
[JsonDerivedType(typeof(ProjectClosedEvent), "projectClosed")]
[JsonDerivedType(typeof(ProjectSavedEvent), "projectSaved")]
[JsonDerivedType(typeof(ProjectChangedEvent), "projectChanged")]
[JsonDerivedType(typeof(TransportEvent), "transport")]
[JsonDerivedType(typeof(MetersEvent), "meters")]
[JsonDerivedType(typeof(MidiActivityEvent), "midiActivity")]
[JsonDerivedType(typeof(PluginsChangedEvent), "pluginsChanged")]
[JsonDerivedType(typeof(MidiDevicesChangedEvent), "midiDevicesChanged")]
[JsonDerivedType(typeof(JobEvent), "job")]
[JsonDerivedType(typeof(PluginProblemEvent), "pluginProblem")]
[JsonDerivedType(typeof(AnalysisEvent), "analysis")]
[JsonDerivedType(typeof(CellEvent), "cell")]
[JsonDerivedType(typeof(ScriptLogEvent), "scriptLog")]
[JsonDerivedType(typeof(SlotParametersEvent), "parameters")]
[JsonDerivedType(typeof(StepInputEvent), "stepInput")]
public abstract record RealtimeEvent;

/// <summary>The socket is open; the client fetches the state it needs now.</summary>
public sealed record ConnectedEvent(string Version) : RealtimeEvent;

/// <summary>The server's link to the engine host changed.</summary>
public sealed record EngineEvent(EngineStatus Engine) : RealtimeEvent;

/// <summary>The client fell too far behind and missed events; it refetches what it shows.</summary>
public sealed record ResyncEvent : RealtimeEvent;

/// <summary>A project was opened (or created); clients fetch it.</summary>
public sealed record ProjectOpenedEvent(ProjectSummary Project, long Revision) : RealtimeEvent;

public sealed record ProjectClosedEvent : RealtimeEvent;

public sealed record ProjectSavedEvent(ProjectSummary Project, long Revision) : RealtimeEvent;

/// <summary>An edit, undo or redo (from any client, or a finished recording) changed the project.</summary>
public sealed record ProjectChangedEvent(ProjectDelta Delta) : RealtimeEvent;

/// <summary>
///     The playhead, about 30 times a second while it moves and once when it stops. Clients extrapolate between
///     frames from <c>positionSamples</c> and <c>sampleRate</c>.
/// </summary>
public sealed record TransportEvent(TransportStatus Transport) : RealtimeEvent;

/// <summary>Peak levels (linear, 0…1+) since the previous frame, per track id and for the master.</summary>
/// <remarks>
///     <c>readings</c> (Phase 4) are what inserts report for the generic editor (a compressor's gain reduction, a
///     meter's level), since the previous frame; left out when there are none.
/// </remarks>
public sealed record MetersEvent(
    IReadOnlyList<TrackMeter> Tracks,
    TrackMeter Master,
    IReadOnlyList<InsertReading>? Readings = null
) : RealtimeEvent;

/// <summary>An insert's reading (<c>IAudioEffect.Reading</c>); <c>trackId</c> null is the master.</summary>
public sealed record InsertReading(string? TrackId, string InsertId, double Value);

public sealed record TrackMeter(string TrackId, float PeakLeft, float PeakRight);

/// <summary>
///     Live MIDI the engine received (from a device or <c>/midi/live</c>), batched up to 30 times a second, for
///     keyboards and activity lights; <c>trackId</c> is the track that played it, if any.
/// </summary>
public sealed record MidiActivityEvent(IReadOnlyList<MidiActivity> Events) : RealtimeEvent;

public sealed record MidiActivity(string? TrackId, string? Device, int Status, int Data1, int Data2);

/// <summary>The plugin catalogue changed (a rescan finished); clients refetch <c>/plugins</c>.</summary>
public sealed record PluginsChangedEvent : RealtimeEvent;

/// <summary>A MIDI device was connected or removed; clients refetch <c>/midi/devices</c>.</summary>
public sealed record MidiDevicesChangedEvent : RealtimeEvent;

/// <summary>A job changed state or progressed (at most a few times a second).</summary>
public sealed record JobEvent(Job Job) : RealtimeEvent;

/// <summary>A plugin crashed or failed to load; the engine bypasses it. <c>insertId</c> null is the instrument.</summary>
public sealed record PluginProblemEvent(string? TrackId, string? InsertId, string PluginId, string Message)
    : RealtimeEvent;

/// <summary>A file's analysis changed (it was built or rebuilt); clients refetch <c>/project/files/{fileId}/analysis</c>.</summary>
public sealed record AnalysisEvent(string FileId) : RealtimeEvent;

/// <summary>
///     A Live Loops cell changed state in the launcher, at project sample <c>sample</c> (for queued and stopping, the
///     boundary it waits for).
/// </summary>
public sealed record CellEvent(string CellId, CellState State, long Sample) : RealtimeEvent;

/// <summary>
///     Phase 7: a Scripter slot's <c>Trace</c> output (and its errors), in order, at most 50 lines a second per slot;
///     <c>dropped</c> counts the lines left out since the previous event.
/// </summary>
public sealed record ScriptLogEvent(string TrackId, string SlotId, IReadOnlyList<string> Lines, int Dropped)
    : RealtimeEvent;

/// <summary>
///     Phase 7: a MIDI FX slot's run-time parameter list changed (a script was compiled): the slot's
///     <c>runtimeParameters</c> are now <c>parameters</c> (the project delta that stores them follows as well).
/// </summary>
public sealed record SlotParametersEvent(string TrackId, string SlotId, IReadOnlyList<ParameterInfo> Parameters)
    : RealtimeEvent;

/// <summary>Phase 7: the step input state changed (a step advanced it, or a client changed it).</summary>
public sealed record StepInputEvent(StepInputState State) : RealtimeEvent;
