using System.Text.Json;
using System.Text.Json.Serialization;

namespace Anode.Plugins.Sandbox;

/// <summary>
///     Host → sandbox, one JSON object per line on the sandbox's stdin. <see cref="Op" />: <c>load</c>, <c>unload</c>,
///     <c>saveState</c>, <c>loadState</c>, <c>values</c>, <c>editor</c>, <c>configure</c>, <c>ping</c>, <c>quit</c>.
/// </summary>
internal sealed class SandboxRequest {
    public int Id { get; set; }
    public string Op { get; set; } = "";
    public int Slot { get; set; }
    public int Format { get; set; }
    public string? Path { get; set; }
    public string? Plugin { get; set; }
    public double SampleRate { get; set; }
    public int MaxFrames { get; set; }
    public bool Sidechain { get; set; }
    public string? State { get; set; }
    public int[]? Indices { get; set; }
    public double[]? Values { get; set; }
    public bool Open { get; set; }
    public string? Title { get; set; }
    public long Parent { get; set; }
    public int X { get; set; } = -1;
    public int Y { get; set; } = -1;
    public bool Floating { get; set; }
}

/// <summary>
///     Sandbox → host, one JSON object per line on the sandbox's protocol descriptor: a reply (<see cref="Id" /> &gt; 0)
///     or an event (<see cref="Event" />: <c>param</c>, <c>latency</c>, <c>editorClosed</c>, <c>log</c>).
/// </summary>
internal sealed class SandboxReply {
    public int Id { get; set; }
    public string? Event { get; set; }
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public int Slot { get; set; }
    public string? Description { get; set; }
    public int Latency { get; set; }
    public string? State { get; set; }
    public double[]? Values { get; set; }
    public int Index { get; set; }
    public double Value { get; set; }
    public int Window { get; set; }
    public string? Plugins { get; set; }
    public string? Item { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true
)]
[JsonSerializable(typeof(SandboxRequest))]
[JsonSerializable(typeof(SandboxReply))]
[JsonSerializable(typeof(List<PluginDescription>))]
[JsonSerializable(typeof(PluginDescription))]
[JsonSerializable(typeof(PluginCacheFile))]
[JsonSerializable(typeof(PluginBlocklistFile))]
internal sealed partial class SandboxJson : JsonSerializerContext {
    public static string Write(SandboxRequest r) => JsonSerializer.Serialize(r, Default.SandboxRequest);

    public static string Write(SandboxReply r) => JsonSerializer.Serialize(r, Default.SandboxReply);

    public static SandboxRequest? ReadRequest(string line) => JsonSerializer.Deserialize(line, Default.SandboxRequest);

    public static SandboxReply? ReadReply(string line) => JsonSerializer.Deserialize(line, Default.SandboxReply);

    public static List<PluginDescription> ReadDescriptions(string json) =>
        JsonSerializer.Deserialize(json, Default.ListPluginDescription) ?? [];

    public static PluginDescription? ReadDescription(string json) =>
        JsonSerializer.Deserialize(json, Default.PluginDescription);
}
