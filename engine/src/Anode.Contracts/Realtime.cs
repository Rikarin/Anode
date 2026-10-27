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
public sealed record MetersEvent(IReadOnlyList<TrackMeter> Tracks, TrackMeter Master) : RealtimeEvent;

public sealed record TrackMeter(string TrackId, float PeakLeft, float PeakRight);
