using System.Text.Json.Serialization;

namespace Anode.Ipc;

/// <summary>
///     A control message between anode-server and anode-host. Bulk data (playback models, peaks) will travel through
///     shared memory instead (docs/adr/0006-bulk-ipc.md); frames stay small.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(PingMessage), "ping")]
[JsonDerivedType(typeof(PongMessage), "pong")]
[JsonDerivedType(typeof(ErrorMessage), "error")]
public abstract record IpcMessage;

/// <summary>
///     The first message each side sends. The server's carries its version; the host answers with its own and its
///     audio backend.
/// </summary>
public sealed record HelloMessage(string Version, string? Backend = null) : IpcMessage;

public sealed record PingMessage(long Id) : IpcMessage;

public sealed record PongMessage(long Id) : IpcMessage;

/// <summary>A request the peer could not carry out.</summary>
public sealed record ErrorMessage(string Message) : IpcMessage;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
)]
[JsonSerializable(typeof(IpcMessage))]
internal sealed partial class IpcJsonContext : JsonSerializerContext;
