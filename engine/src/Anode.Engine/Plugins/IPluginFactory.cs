using Anode.Dsp.Effects;
using Anode.Dsp.Instruments;
using Anode.Ipc;

namespace Anode.Engine.Plugins;

/// <summary>
///     The seam third-party plugin hosting (docs/05-phase-4.md, workstream E) plugs into: the engine asks it for the
///     instances of every plugin id that is not built in (VST3, AU, and CLAP effects or instruments if E hosts them), and
///     it adds those plugins to the host's catalogue. The engine runs what it returns exactly as it runs built-in
///     effects and instruments: <see cref="IAudioEffect" /> in insert slots, <see cref="IInstrument" /> on instrument
///     tracks, with the same threading contract (create, prepare, state and dispose on the control thread; process,
///     parameters and reset on the audio thread, without allocating, locking or blocking beyond a sandbox's deadline).
///     <para>
///         The engine works without one (<see cref="NullPluginFactory" />): unknown ids then fail to load and are
///         reported with <see cref="PluginProblemMessage" />.
///     </para>
///     <para>
///         <b>Callbacks.</b> Each instance gets a <see cref="PluginContext" /> naming where it sits (engine track,
///         insert id or the instrument) and the <see cref="IPluginCallbacks" /> it reports through: a parameter its own
///         editor changed (the host relays it as <see cref="ParameterChangedMessage" />), a problem (a crash or a failed
///         load; the host relays <see cref="PluginProblemMessage" />; the instance must already be passing audio
///         through, or silent for an instrument, when it says so), or a change of its latency (the engine then
///         recomputes delay compensation). Callbacks may come from any thread except the audio thread.
///     </para>
///     <para>
///         <b>Editors.</b> An instance that has its own editor window implements <see cref="IPluginEditor" />; the host
///         calls it for <see cref="OpenPluginEditorMessage" /> on the control thread.
///     </para>
///     <para>
///         <b>Cycles.</b> A factory that also implements <see cref="IAudioCycleObserver" /> hears the start and end of
///         every audio callback on the audio thread (to time sandbox deadlines from the callback's start, or to wake
///         sandboxes early).
///     </para>
/// </summary>
public interface IPluginFactory {
    /// <summary>The plugins it can create, as the host lists them (formats <c>vst3</c>, <c>au</c>, <c>clap</c>).</summary>
    IReadOnlyList<EnginePlugin> Catalog { get; }

    /// <summary>Rescans (the server's <c>scanPlugins</c>); blocking, on a background thread. Catalog changes after it.</summary>
    void Scan();

    /// <summary>Whether <paramref name="pluginId" /> is one of its plugins.</summary>
    bool Handles(string pluginId);

    /// <summary>A new, unprepared effect instance, or null when the id is not an effect it hosts. Control thread.</summary>
    IAudioEffect? CreateEffect(string pluginId, PluginContext context);

    /// <summary>A new, unprepared instrument instance, or null when the id is not an instrument it hosts. Control thread.</summary>
    IInstrument? CreateInstrument(string pluginId, PluginContext context);
}

/// <summary>Where an instance sits and how it reports back.</summary>
/// <param name="TrackId">The engine track (−1: the master).</param>
/// <param name="InsertId">The insert slot's id; null for a track's instrument.</param>
/// <param name="PluginId">The plugin.</param>
/// <param name="SampleRate">The engine's rate (also passed to <c>Prepare</c>).</param>
/// <param name="MaxFrames">The largest block the engine processes (also passed to <c>Prepare</c>).</param>
/// <param name="Callbacks">Where the instance reports parameter changes, problems and latency changes.</param>
public sealed record PluginContext(
    int TrackId,
    string? InsertId,
    string PluginId,
    float SampleRate,
    int MaxFrames,
    IPluginCallbacks Callbacks
);

/// <summary>How a hosted instance reports back to the engine. Any thread but the audio thread.</summary>
public interface IPluginCallbacks {
    /// <summary>The plugin's own editor changed parameter <paramref name="parameterId" /> (not automation playback).</summary>
    void ParameterChanged(string parameterId, double value);

    /// <summary>The plugin crashed or failed and is now bypassed (an instrument: silent).</summary>
    void Problem(string message);

    /// <summary>The plugin's <c>LatencySamples</c> changed; the engine recomputes delay compensation.</summary>
    void LatencyChanged();
}

/// <summary>An instance with its own editor window.</summary>
public interface IPluginEditor {
    /// <summary>Opens or closes the plugin's own window (control thread); false when it has none or cannot.</summary>
    bool SetEditorOpen(bool open);
}

/// <summary>A factory that hears every audio callback (audio thread; no allocation, lock or blocking).</summary>
public interface IAudioCycleObserver {
    /// <summary>A callback of <paramref name="frames" /> frames started at <paramref name="hostTimeNs" /> (monotonic).</summary>
    void BeginCycle(long hostTimeNs, int frames);

    /// <summary>The callback's processing is done.</summary>
    void EndCycle();
}

/// <summary>No third-party plugins: the engine with built-in effects and instruments (and its in-process CLAP) only.</summary>
public sealed class NullPluginFactory : IPluginFactory {
    public static NullPluginFactory Instance { get; } = new();

    public IReadOnlyList<EnginePlugin> Catalog => [];

    public void Scan() { }

    public bool Handles(string pluginId) => false;

    public IAudioEffect? CreateEffect(string pluginId, PluginContext context) => null;

    public IInstrument? CreateInstrument(string pluginId, PluginContext context) => null;
}
