using System.Text.Json.Serialization;

namespace Anode.Contracts;

/// <summary>What <c>GET /api/v1/system</c> answers: the server's version and its link to the engine host.</summary>
public sealed record SystemInfo(string Version, EngineStatus Engine);

/// <summary>
///     The server's link to the engine host: whether it is reachable (<c>state</c>), the host's version and audio
///     backend (<c>coreaudio</c>, <c>wasapi</c>, <c>alsa</c>, <c>null</c>) from its hello while connected, and why the
///     link is down when it is (<c>message</c>).
/// </summary>
public sealed record EngineStatus(EngineState State, string? HostVersion, string? Backend, string? Message);

[JsonConverter(typeof(JsonStringEnumConverter<EngineState>))]
public enum EngineState {
    /// <summary>Not connected to a host (yet, or any more); the server keeps retrying.</summary>
    [JsonStringEnumMemberName("disconnected")]
    Disconnected,

    /// <summary>Connected to a separate anode-host over IPC.</summary>
    [JsonStringEnumMemberName("connected")]
    Connected,

    /// <summary>Running an in-process engine without audio (tests and UI development).</summary>
    [JsonStringEnumMemberName("inprocess")]
    InProcess
}
