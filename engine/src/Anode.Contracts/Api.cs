using System.Text.Json.Serialization;

namespace Anode.Contracts;

/// <summary>What <c>GET /api/v1/system</c> answers: the server's version and its link to the engine host.</summary>
public sealed record SystemInfo(string Version, EngineStatus Engine);

/// <summary>
///     The server's link to the engine host: whether it is reachable (<c>state</c>), the host's version and audio
///     backend (<c>coreaudio</c>, <c>wasapi</c>, <c>alsa</c>, <c>null</c>) from its hello while connected, and why the
///     link is down when it is (<c>message</c>). <c>audio</c> is the device the host runs while connected, else null.
/// </summary>
public sealed record EngineStatus(
    EngineState State,
    string? HostVersion,
    string? Backend,
    string? Message,
    EngineAudio? Audio = null
);

/// <summary>
///     The stream the engine host has open, as the device settled on it (it may differ from what was asked for). The
///     latencies are in frames, as the driver reports them for the open stream with the buffer included; 0 on the null
///     backend, which has no device, and for inputs when none are open.
/// </summary>
public sealed record EngineAudio(
    int SampleRate,
    int BufferFrames,
    int InputChannels,
    int OutputChannels,
    int InputLatencyFrames,
    int OutputLatencyFrames
);

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
