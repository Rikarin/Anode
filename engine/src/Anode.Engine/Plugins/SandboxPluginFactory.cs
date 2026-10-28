using Anode.Dsp.Effects;
using Anode.Dsp.Instruments;
using Anode.Ipc;
using Anode.Plugins;
using System.Collections.Concurrent;

namespace Anode.Engine.Plugins;

/// <summary>
///     The engine's plugin factory over workstream E's <see cref="PluginHost" />: VST3, AU and CLAP effects and
///     instruments in <c>anode-plugin</c> sandboxes (ADR-0003). It lists the host's catalogue, creates instances for
///     the ids the catalogue knows, relays the host's <see cref="PluginHost.ParameterChanged" /> and
///     <see cref="PluginHost.Problem" /> events (background threads) to each instance's
///     <see cref="IPluginCallbacks" /> by owner, marks each device cycle (<see cref="PluginHost.BeginCycle" />) and
///     switches the host to <see cref="ProcessingMode.Offline" /> during offline renders.
/// </summary>
public sealed class SandboxPluginFactory : IPluginFactory, IAudioCycleObserver, IDisposable {
    readonly ConcurrentDictionary<PluginOwner, IPluginCallbacks> callbacks = new();

    public SandboxPluginFactory(PluginHost host) {
        Host = host;
        host.ParameterChanged += OnParameterChanged;
        host.Problem += OnProblem;
    }

    public PluginHost Host { get; }

    public IReadOnlyList<EnginePlugin> Catalog => Host.CatalogEntries();

    /// <summary>Rescans the plugin folders (blocking; the engine calls it on a background thread).</summary>
    public void Scan() => Host.Catalog.Scan();

    public bool Handles(string pluginId) => Host.Knows(pluginId);

    public IAudioEffect? CreateEffect(string pluginId, PluginContext context) =>
        Host.CreateEffect(pluginId, Owner(context));

    public IInstrument? CreateInstrument(string pluginId, PluginContext context) =>
        Host.CreateInstrument(pluginId, Owner(context));

    public void BeginCycle(long hostTimeNs, int frames) => Host.BeginCycle();

    public void EndCycle() { }

    public void SetOffline(bool offline) =>
        Host.Processing = offline ? ProcessingMode.Offline : ProcessingMode.RealTime;

    PluginOwner Owner(PluginContext context) {
        var owner = new PluginOwner(context.TrackId, context.InsertId);
        callbacks[owner] = context.Callbacks; // the newest instance of a slot reports
        return owner;
    }

    void OnParameterChanged(PluginOwner owner, string parameterId, double value) {
        if (callbacks.TryGetValue(owner, out var target)) {
            target.ParameterChanged(parameterId, value);
        }
    }

    void OnProblem(PluginProblem problem) {
        if (callbacks.TryGetValue(problem.Owner, out var target)) {
            target.Problem(problem.Message);
        }
    }

    public void Dispose() {
        Host.ParameterChanged -= OnParameterChanged;
        Host.Problem -= OnProblem;
        Host.Dispose();
    }
}
