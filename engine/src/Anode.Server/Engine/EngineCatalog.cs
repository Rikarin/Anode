using Anode.Contracts;
using Anode.Dsp.Effects;
using Anode.Dsp.Instruments;
using Anode.Ipc;
using Anode.Model;
using Anode.Server.Realtime;
using System.Globalization;

namespace Anode.Server.Engine;

/// <summary>
///     What the engine host can play with (docs/03-phase-2.md, "Catalogue"): the plugins of its last
///     <see cref="PluginCatalogMessage" /> and the MIDI ports of its last <see cref="MidiPortsMessage" />, both empty
///     without an engine. A change publishes <see cref="PluginsChangedEvent" /> or <see cref="MidiDevicesChangedEvent" />.
///     Edits name plugins from <see cref="ForEdits" />: the host's catalogue, or Anode's built-in instruments and effects
///     while there is none.
/// </summary>
public sealed class EngineCatalog(EventHub hub) {
    readonly Lock gate = new();
    PluginInfo[] plugins = [];
    PluginCatalog forEdits = new(Builtins());
    MidiDevices devices = new([], []);

    public IReadOnlyList<PluginInfo> Plugins {
        get {
            lock (gate) {
                return plugins;
            }
        }
    }

    public MidiDevices Devices {
        get {
            lock (gate) {
                return devices;
            }
        }
    }

    public PluginCatalog ForEdits {
        get {
            lock (gate) {
                return forEdits;
            }
        }
    }

    public void SetPlugins(IReadOnlyList<EnginePlugin> reported) {
        var list = reported.Select(From).ToArray();
        lock (gate) {
            plugins = list;
            forEdits = new(list.Length > 0 ? list : Builtins());
        }

        hub.Publish(new PluginsChangedEvent());
    }

    public void SetPorts(IReadOnlyList<EngineMidiPort> ports) {
        var next = new MidiDevices(
            [..ports.Where(static p => p.Input).Select(static p => new MidiPort(p.Id, p.Name))],
            [..ports.Where(static p => p.Output).Select(static p => new MidiPort(p.Id, p.Name))]
        );
        lock (gate) {
            devices = next;
        }

        hub.Publish(new MidiDevicesChangedEvent());
    }

    /// <summary>The engine went away: nothing to list until the next one reports.</summary>
    public void Clear() {
        bool hadPlugins, hadPorts;
        lock (gate) {
            hadPlugins = plugins.Length > 0;
            hadPorts = devices.Inputs.Count + devices.Outputs.Count > 0;
            plugins = [];
            forEdits = new(Builtins());
            devices = new([], []);
        }

        if (hadPlugins) {
            hub.Publish(new PluginsChangedEvent());
        }

        if (hadPorts) {
            hub.Publish(new MidiDevicesChangedEvent());
        }
    }

    public static PluginInfo From(EnginePlugin p) =>
        new(
            p.Id,
            p.Name,
            p.Vendor,
            p.Version,
            p.Format.ToLowerInvariant() switch {
                "clap" => PluginFormat.Clap,
                "vst3" => PluginFormat.Vst3,
                "au" => PluginFormat.Au,
                _ => PluginFormat.Builtin
            },
            p.IsInstrument,
            p.FileTypes ?? [],
            [
                ..(p.Parameters ?? []).Select(static q =>
                    new ParameterInfo(q.Id, q.Name, q.Min, q.Max, q.Default, q.Unit, q.Steps, q.ValueNames)
                )
            ],
            p.IsInstrument ? "Instrument" : p.Category,
            p.HasSidechain,
            p.HasEditor
        );

    /// <summary>A built-in instrument as the engine lists it.</summary>
    public static EnginePlugin From(InstrumentDescriptor d) =>
        new(
            d.Id,
            d.Name,
            d.Vendor,
            d.Version,
            "builtin",
            true,
            d.FileTypes,
            [
                ..d.Parameters.Select(static p =>
                    new EngineParameter(
                        p.Id,
                        p.Name,
                        Widen(p.Min),
                        Widen(p.Max),
                        Widen(p.Default),
                        p.Unit,
                        p.Steps,
                        p.ValueNames
                    )
                )
            ]
        );

    /// <summary>A built-in effect as the engine lists it.</summary>
    public static EnginePlugin From(EffectDescriptor d) =>
        new(
            d.Id,
            d.Name,
            d.Vendor,
            d.Version,
            "builtin",
            false,
            [],
            [
                ..d.Parameters.Select(static p =>
                    new EngineParameter(
                        p.Id,
                        p.Name,
                        Widen(p.Min),
                        Widen(p.Max),
                        Widen(p.Default),
                        p.Unit,
                        p.Steps,
                        p.ValueNames
                    )
                )
            ],
            d.Category,
            d.HasSidechain
        );

    /// <summary>A float as the decimal it prints as (0.2f is 0.2, not 0.20000000298…).</summary>
    static double Widen(float value) =>
        double.Parse(value.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    /// <summary>Anode's built-in instruments and effects (<see cref="BuiltinInstruments.All" />, <see cref="BuiltinEffects.All" />).</summary>
    public static PluginInfo[] Builtins() => [
        ..BuiltinInstruments.All.Select(static d => From(From(d))),
        ..BuiltinEffects.All.Select(static d => From(From(d)))
    ];
}
