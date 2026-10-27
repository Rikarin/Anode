using System.Text.Json.Serialization;

namespace Anode.Contracts;

/// <summary>A message on the <c>/ws</c> WebSocket. The first one a client receives is <see cref="ConnectedEvent" />.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ConnectedEvent), "connected")]
[JsonDerivedType(typeof(EngineEvent), "engine")]
[JsonDerivedType(typeof(ResyncEvent), "resync")]
public abstract record RealtimeEvent;

/// <summary>The socket is open; the client fetches the state it needs now.</summary>
public sealed record ConnectedEvent(string Version) : RealtimeEvent;

/// <summary>The server's link to the engine host changed.</summary>
public sealed record EngineEvent(EngineStatus Engine) : RealtimeEvent;

/// <summary>The client fell too far behind and missed events; it refetches what it shows.</summary>
public sealed record ResyncEvent : RealtimeEvent;
