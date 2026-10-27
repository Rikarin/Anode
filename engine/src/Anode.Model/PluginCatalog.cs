using Anode.Contracts;
using System.Collections.Immutable;

namespace Anode.Model;

/// <summary>
///     The plugins edits may name (docs/03-phase-2.md, "Instrument tracks"): what the engine host reported in its
///     catalogue, or the built-in instruments while no host has. A new instrument's parameters start at the defaults
///     listed here.
/// </summary>
public sealed class PluginCatalog {
    readonly Dictionary<string, PluginInfo> byId;

    public PluginCatalog(IReadOnlyList<PluginInfo> plugins) {
        Plugins = plugins;
        byId = new(StringComparer.Ordinal);
        foreach (var plugin in plugins) {
            byId.TryAdd(plugin.Id, plugin);
        }
    }

    /// <summary>The instrument a new instrument track gets (Anode.Dsp's <c>BuiltinInstruments.DefaultId</c>).</summary>
    public const string DefaultInstrumentId = "anode.synth";

    public static PluginCatalog Empty { get; } = new([]);

    public IReadOnlyList<PluginInfo> Plugins { get; }

    public PluginInfo? Find(string id) => byId.GetValueOrDefault(id);

    /// <summary>Every parameter of <paramref name="plugin" /> at its default, by id.</summary>
    public static ImmutableSortedDictionary<string, double> Defaults(PluginInfo plugin) =>
        ImmutableSortedDictionary.CreateRange(
            StringComparer.Ordinal,
            plugin.Parameters.DistinctBy(static p => p.Id).Select(static p => KeyValuePair.Create(p.Id, p.Default))
        );

    /// <summary>Parameter values in canonical (ordinal) order, as the model keeps them.</summary>
    public static ImmutableSortedDictionary<string, double> Sorted(IReadOnlyDictionary<string, double>? values) =>
        values switch {
            ImmutableSortedDictionary<string, double> sorted when sorted.KeyComparer == StringComparer.Ordinal =>
                sorted,
            null => ImmutableSortedDictionary.Create<string, double>(StringComparer.Ordinal),
            _ => ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal, values)
        };
}
