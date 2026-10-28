using Anode.Dsp.Effects;
using Anode.Dsp.Instruments;
using Anode.Ipc;

namespace Anode.Plugins;

/// <summary>The plugin formats Anode hosts out of process.</summary>
public enum HostedFormat {
    Vst3 = 1,
    Au = 2,
    Clap = 3
}

/// <summary>A parameter as <c>an_plug</c> describes it (values in the plugin's own range; VST3 normalised 0..1).</summary>
public sealed record PluginParameterDescription(
    string Id,
    string Name,
    double Min,
    double Max,
    double Default,
    string Unit,
    int Steps,
    IReadOnlyList<string>? ValueNames = null
);

/// <summary>
///     What a plugin class is, as <c>an_plug</c> (and so the scanner) describes it: its format-local id (a VST3 class
///     id, an AU's <c>type.subtype.manufacturer</c>, a CLAP id), names, category, buses, editor, latency and parameters.
/// </summary>
public sealed record PluginDescription(
    string Id,
    string Name,
    string Vendor,
    string Version,
    string Format,
    string Category,
    bool IsInstrument,
    bool HasSidechain,
    bool HasEditor,
    int Latency,
    IReadOnlyList<PluginParameterDescription> Parameters
) {
    public IReadOnlyList<InstrumentParameter> InstrumentParameters() =>
        [
            ..Parameters.Select((p, i) => new InstrumentParameter(
                    i,
                    p.Id,
                    p.Name,
                    (float)p.Min,
                    (float)p.Max,
                    (float)p.Default,
                    p.Unit,
                    p.Steps,
                    p.ValueNames
                )
            )
        ];
}

/// <summary>
///     A plugin in the catalogue: Anode's plugin id (<c>vst3:&lt;class id&gt;</c>, <c>au:&lt;type.subtype.manu&gt;</c>,
///     and for CLAP its own id, as Phase 2 stored it in projects), where it lives (a bundle; empty for AU) and what it is.
/// </summary>
public sealed record PluginEntry(string PluginId, HostedFormat Format, string Path, PluginDescription Description) {
    /// <summary>Anode's id for a format-local id.</summary>
    public static string MakeId(HostedFormat format, string nativeId) =>
        format switch {
            HostedFormat.Vst3 => "vst3:" + nativeId,
            HostedFormat.Au => "au:" + nativeId,
            _ => nativeId
        };

    public static string FormatName(HostedFormat format) =>
        format switch {
            HostedFormat.Vst3 => "vst3",
            HostedFormat.Au => "au",
            _ => "clap"
        };

    /// <summary>As the host reports it in <see cref="PluginCatalogMessage" />.</summary>
    public EnginePlugin ToEngine() {
        var d = Description;
        return new(
            PluginId,
            d.Name,
            d.Vendor,
            d.Version,
            FormatName(Format),
            d.IsInstrument,
            [],
            [..d.Parameters.Select(p => new EngineParameter(p.Id, p.Name, p.Min, p.Max, p.Default, p.Unit, p.Steps, p.ValueNames))],
            d.IsInstrument ? "" : d.Category,
            d.HasSidechain,
            d.HasEditor
        );
    }

    public EffectDescriptor EffectDescriptor() {
        var d = Description;
        return new(PluginId, d.Name, d.Vendor, d.Version, d.Category, d.InstrumentParameters(), d.HasSidechain);
    }

    public InstrumentDescriptor InstrumentDescriptor() {
        var d = Description;
        return new(PluginId, d.Name, d.Vendor, d.Version, d.InstrumentParameters(), []);
    }
}

/// <summary><c>&lt;DataDirectory&gt;/plugins/cache.json</c>: what the last scans found, per scanned item.</summary>
internal sealed class PluginCacheFile {
    public int Version { get; set; } = 1;
    public List<PluginCacheItem> Items { get; set; } = [];
}

/// <summary>One scanned item (a bundle, or one AU component) and its stamp (modification time or AU version).</summary>
internal sealed class PluginCacheItem {
    public string Key { get; set; } = "";
    public string Stamp { get; set; } = "";
    public List<PluginDescription> Plugins { get; set; } = [];
}

/// <summary>
///     <c>&lt;DataDirectory&gt;/plugins/blocklist.json</c>: items whose scan crashed or hung. They are skipped until
///     their stamp changes or the user removes them (a forced rescan retries them).
/// </summary>
internal sealed class PluginBlocklistFile {
    public int Version { get; set; } = 1;
    public List<PluginBlocklistItem> Items { get; set; } = [];
}

internal sealed class PluginBlocklistItem {
    public string Key { get; set; } = "";
    public string Stamp { get; set; } = "";
    public string Reason { get; set; } = "";
}
