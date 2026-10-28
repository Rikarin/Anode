namespace Anode.Plugins;

/// <summary>Where hosted plugins run (docs/adr/0003-plugin-hosting.md).</summary>
public enum SandboxMode {
    /// <summary>One <c>anode-plugin</c> process per vendor, capped by core count (the default).</summary>
    Grouped,

    /// <summary>One process per instance (for crash-prone plugins; ~10–20 µs per process per cycle).</summary>
    PerInstance,

    /// <summary>In the engine's own process: no isolation, no editor windows (opt-in, and the tests' reference).</summary>
    InHost
}

/// <summary>How hosted plugins are waited for.</summary>
public enum ProcessingMode {
    /// <summary>The device's cycle: each block has a deadline at a fraction of the period; a miss is bypassed.</summary>
    RealTime,

    /// <summary>Offline rendering: blocks are waited for (up to the hang timeout), never bypassed for being slow.</summary>
    Offline
}

/// <summary>Who an instance belongs to, as the IPC messages name it: a track's insert, or its instrument (no insert).</summary>
public readonly record struct PluginOwner(int TrackId, string? InsertId);

/// <summary>A plugin that crashed, hung or failed to load, and is bypassed (<c>pluginProblem</c>).</summary>
public sealed record PluginProblem(PluginOwner Owner, string PluginId, string Message);

/// <summary>
///     Where a plugin's editor window goes (spike S6, docs/spikes/S6-plugin-windows.md).
///     <paramref name="ParentWindow" />: a window number (CGWindowID) to order the editor directly above; 0 for none;
///     <see cref="AppWindow" /> (the default) for the desktop app's main window, which the sandbox finds by the app's
///     process (<see cref="PluginHostOptions.AppProcessId" />), so no handle has to travel through the host and the
///     server. (<paramref name="X" />, <paramref name="Y" />): the top-left corner in screen points from the main
///     screen's top-left; negative places it over the parent's top-left corner, or centres it without one.
///     <paramref name="Floating" />: a floating utility panel (above every normal window) rather than a normal window.
/// </summary>
public sealed record EditorPlacement(long ParentWindow = EditorPlacement.AppWindow, int X = -1, int Y = -1, bool Floating = true) {
    /// <summary>The desktop app's main window.</summary>
    public const long AppWindow = -1;
}

/// <summary>Counters of one hosted instance, for diagnostics and tests.</summary>
public readonly record struct HostedPluginStats(
    long Completed,
    long Misses,
    long BusySkips,
    long LastRoundTripNs,
    int SandboxPid
);

/// <summary>What the engine can ask of a hosted plugin besides <c>IAudioEffect</c> / <c>IInstrument</c>.</summary>
public interface IHostedPlugin {
    PluginOwner Owner { get; }

    /// <summary>Anode's plugin id (<c>vst3:…</c>, <c>au:…</c>, <c>clap:…</c>).</summary>
    string PluginId { get; }

    /// <summary>It has its own editor window (otherwise the generic editor).</summary>
    bool HasEditor { get; }

    /// <summary>True once it crashed, hung or failed to load: it is bypassed (effects pass their input; instruments are silent).</summary>
    bool IsFailed { get; }

    /// <summary>Why it failed, or null.</summary>
    string? Failure { get; }

    HostedPluginStats Stats { get; }

    /// <summary>Opens or closes the plugin's own editor window (control thread); the window number, 0 when closed, negative on failure.</summary>
    int OpenEditor(bool open, EditorPlacement? placement = null);

    /// <summary>The open editor's window number, or 0.</summary>
    int EditorWindow { get; }
}
