using System.Text.Json.Serialization;

namespace Anode.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<PluginFormat>))]
public enum PluginFormat {
    /// <summary>Anode's own instruments and effects.</summary>
    [JsonStringEnumMemberName("builtin")]
    Builtin,

    [JsonStringEnumMemberName("clap")]
    Clap
}

/// <summary>
///     A plugin parameter. <c>steps</c> &gt; 0 makes it discrete (<c>steps</c> + 1 values from <c>min</c> to
///     <c>max</c>), with <c>valueNames</c> naming them when it is a choice.
/// </summary>
public sealed record ParameterInfo(
    string Id,
    string Name,
    double Min,
    double Max,
    double Default,
    string Unit,
    int Steps,
    IReadOnlyList<string>? ValueNames
);

/// <summary>
///     A plugin the engine can load. <c>fileTypes</c> (extensions such as <c>.sfz</c>, <c>.wav</c>) lists what an
///     instrument loads with <c>loadInstrumentFile</c>, empty when it loads none.
/// </summary>
public sealed record PluginInfo(
    string Id,
    string Name,
    string Vendor,
    string Version,
    PluginFormat Format,
    bool IsInstrument,
    IReadOnlyList<string> FileTypes,
    IReadOnlyList<ParameterInfo> Parameters
);

/// <summary>
///     A track's instrument: the plugin, its parameter values by parameter id (all of them), and the file it loaded
///     (a sampler's instrument), relative to the project folder when inside it. A plugin's opaque state lives in the
///     project's <c>Plugin States</c> folder.
/// </summary>
public sealed record InstrumentSlot(
    string PluginId,
    string Name,
    IReadOnlyDictionary<string, double> Parameters,
    string? File
);
