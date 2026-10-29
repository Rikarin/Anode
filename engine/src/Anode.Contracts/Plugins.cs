using System.Text.Json.Serialization;

namespace Anode.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<PluginFormat>))]
public enum PluginFormat {
    /// <summary>Anode's own instruments and effects.</summary>
    [JsonStringEnumMemberName("builtin")]
    Builtin,

    [JsonStringEnumMemberName("clap")]
    Clap,

    [JsonStringEnumMemberName("vst3")]
    Vst3,

    /// <summary>Audio Units (macOS).</summary>
    [JsonStringEnumMemberName("au")]
    Au
}

/// <summary>
///     A plugin parameter. <c>steps</c> &gt; 0 makes it discrete (<c>steps</c> + 1 values from <c>min</c> to
///     <c>max</c>), with <c>valueNames</c> naming them when it is a choice. <c>macro</c> (Phase 6) is its macro hint:
///     automatic Smart Controls mapping takes a plugin's hinted parameters in ascending <c>macro</c> order (null: not a
///     macro). <c>kind</c> (Phase 7) is the control that shows it: a slider when left out; a choice is a
///     <c>menu</c>, a two-valued switch a <c>checkbox</c>; a Scripter parameter has its <c>PluginParameters</c> type
///     (<c>lin</c> and <c>log</c> are sliders).
/// </summary>
public sealed record ParameterInfo(
    string Id,
    string Name,
    double Min,
    double Max,
    double Default,
    string Unit,
    int Steps,
    IReadOnlyList<string>? ValueNames,
    int? Macro = null,
    ParameterKind? Kind = null
);

/// <summary>The control a parameter shows as (Phase 7).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ParameterKind>))]
public enum ParameterKind {
    [JsonStringEnumMemberName("slider")]
    Slider,

    [JsonStringEnumMemberName("menu")]
    Menu,

    [JsonStringEnumMemberName("checkbox")]
    Checkbox,

    /// <summary>A button: 1 while pressed, else 0.</summary>
    [JsonStringEnumMemberName("momentary")]
    Momentary,

    /// <summary>A label, with no value.</summary>
    [JsonStringEnumMemberName("text")]
    Text
}

/// <summary>
///     A plugin the engine can load: an instrument (<c>category</c> <c>Instrument</c>), a MIDI effect (<c>MIDI
///     Effect</c>, Phase 6: it goes in a strip's MIDI FX slots), or an effect in <c>category</c> (EQ, Dynamics, Delay,
///     Reverb, Modulation, Distortion, Filter, Utility, Metering). <c>hasEditor</c>: it has its own editor window (desktop app
///     only; everywhere else the generic editor). <c>fileTypes</c> (extensions such as <c>.sfz</c>, <c>.wav</c>) lists
///     what an
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
    IReadOnlyList<ParameterInfo> Parameters,
    string Category,
    bool HasSidechain,
    bool HasEditor
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
