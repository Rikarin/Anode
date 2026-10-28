using Anode.Dsp.Effects;
using Anode.Dsp.Instruments;
using Anode.Engine.Plugins.Clap;
using Anode.Ipc;

namespace Anode.Engine.Plugins;

/// <summary>
///     The plugins the engine can load: Anode's built-in instruments (<see cref="BuiltinInstruments" />) and effects
///     (<see cref="BuiltinEffects" />), plus any registered with <see cref="AddBuiltin" /> and
///     <see cref="AddBuiltinEffect" /> (tests, tools); the CLAP instruments found by <see cref="ScanClap" />; and
///     whatever the third-party <see cref="Factory" /> hosts (VST3, AU; docs/05-phase-4.md, workstream E). It answers
///     the catalogue the host sends the server (built-ins first, then CLAP, then the factory's) and creates instances by
///     plugin id.
/// </summary>
public sealed class PluginRegistry {
    readonly Lock gate = new();
    readonly List<(InstrumentDescriptor Descriptor, Func<IInstrument> Create)> extras = [];
    readonly List<(EffectDescriptor Descriptor, Func<IAudioEffect> Create)> effects = [];
    IPluginFactory factory = NullPluginFactory.Instance;
    TaskCompletionSource idle = Done();
    IReadOnlyList<ClapEntry> clap = [];
    int scans;

    /// <summary>Where diagnostics go (plugins that failed to load, plugin log messages).</summary>
    public Action<string>? Log { get; set; }

    /// <summary>A CLAP plugin found by a scan: its file and what it reported.</summary>
    public sealed record ClapEntry(
        string Path,
        ClapDescriptor Descriptor,
        IReadOnlyList<InstrumentParameter> Parameters);

    /// <summary>The CLAP plugins of the last scan.</summary>
    public IReadOnlyList<ClapEntry> Clap {
        get {
            lock (gate) {
                return clap;
            }
        }
    }

    /// <summary>Adds an instrument the engine treats as built in (tests, tools).</summary>
    public void AddBuiltin(InstrumentDescriptor descriptor, Func<IInstrument> create) {
        lock (gate) {
            extras.Add((descriptor, create));
        }
    }

    /// <summary>Adds an effect the engine treats as built in (tests, tools).</summary>
    public void AddBuiltinEffect(EffectDescriptor descriptor, Func<IAudioEffect> create) {
        lock (gate) {
            effects.Add((descriptor, create));
        }
    }

    /// <summary>Third-party plugin hosting (default: none).</summary>
    public IPluginFactory Factory {
        get => Volatile.Read(ref factory);
        set => Volatile.Write(ref factory, value ?? NullPluginFactory.Instance);
    }

    /// <summary>Every plugin, as the host reports it in <see cref="PluginCatalogMessage" />.</summary>
    public IReadOnlyList<EnginePlugin> Catalog() {
        var list = new List<EnginePlugin>();
        foreach (var d in BuiltinInstruments.All) {
            list.Add(Builtin(d));
        }

        foreach (var d in BuiltinEffects.All) {
            list.Add(Builtin(d));
        }

        lock (gate) {
            foreach (var (d, _) in extras) {
                list.Add(Builtin(d));
            }

            foreach (var (d, _) in effects) {
                list.Add(Builtin(d));
            }

            foreach (var entry in clap) {
                var d = entry.Descriptor;
                list.Add(
                    new(d.Id, d.Name, d.Vendor, d.Version, "clap", d.IsInstrument, [], [..entry.Parameters.Select(Map)])
                );
            }
        }

        var known = new HashSet<string>(list.Select(static p => p.Id), StringComparer.Ordinal);
        foreach (var p in Factory.Catalog) {
            if (known.Add(p.Id)) {
                list.Add(p);
            }
        }

        return list;
    }

    /// <summary>
    ///     A new, unprepared effect <paramref name="pluginId" /> for the insert <paramref name="context" /> names, or
    ///     null when there is none: a built-in, else the factory's. Control thread.
    /// </summary>
    public IAudioEffect? CreateEffect(string pluginId, PluginContext context) {
        if (BuiltinEffects.Create(pluginId) is { } builtin) {
            return builtin;
        }

        Func<IAudioEffect>? create = null;
        lock (gate) {
            foreach (var (d, f) in effects) {
                if (d.Id == pluginId) {
                    create = f;
                }
            }
        }

        if (create is not null) {
            return create();
        }

        var hosted = Factory;
        return hosted.Handles(pluginId) ? hosted.CreateEffect(pluginId, context) : null;
    }

    /// <summary>
    ///     A new, unprepared instance of plugin <paramref name="pluginId" />, or null when there is none. Control
    ///     thread; a CLAP instance is created on the plugin thread.
    /// </summary>
    public IInstrument? Create(string pluginId) => Create(pluginId, null);

    /// <summary>
    ///     As <see cref="Create(string)" />, for the track <paramref name="context" /> names; ids that are neither built
    ///     in nor found by the CLAP scan go to the <see cref="Factory" />.
    /// </summary>
    public IInstrument? Create(string pluginId, PluginContext? context) {
        if (BuiltinInstruments.Create(pluginId) is { } builtin) {
            return builtin;
        }

        if (!Knows(pluginId)) {
            Task pending;
            lock (gate) {
                pending = idle.Task;
            }

            pending.Wait(ScanWait); // a project loaded while the host still scans: its CLAP plugins are on their way
        }

        Func<IInstrument>? create = null;
        ClapEntry? found = null;
        lock (gate) {
            foreach (var (d, factory) in extras) {
                if (d.Id == pluginId) {
                    create = factory;
                }
            }

            found = clap.FirstOrDefault(e => e.Descriptor.Id == pluginId);
        }

        if (create is not null) {
            return create();
        }

        if (found is not null) {
            return ClapInstrument.Create(found.Path, pluginId, Log);
        }

        var hosted = Factory;
        return context is not null && hosted.Handles(pluginId) ? hosted.CreateInstrument(pluginId, context) : null;
    }

    /// <summary>Scans the CLAP folders (<see cref="ScanClap" />), then has the factory rescan its formats.</summary>
    public void ScanAll(IEnumerable<string>? clapFolders = null) {
        BeginScan();
        try {
            Scan(clapFolders);
            try {
                Factory.Scan();
            } catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException) {
                Log?.Invoke($"plugin scan: {e.Message}");
            }
        } finally {
            EndScan();
        }
    }

    /// <summary>
    ///     Scans <paramref name="folders" /> (default: <see cref="ClapFolders" />) for CLAP plugins on the plugin thread,
    ///     creating each once to read its parameters, and replaces the CLAP part of the catalogue. A plugin that fails
    ///     to load is logged and left out.
    /// </summary>
    public void ScanClap(IEnumerable<string>? folders = null) {
        BeginScan();
        try {
            Scan(folders);
        } finally {
            EndScan();
        }
    }

    /// <summary>
    ///     A scan is about to run: until the matching <see cref="EndScan" />, creating a plugin the catalogue does not
    ///     know yet waits for it.
    /// </summary>
    public void BeginScan() {
        lock (gate) {
            if (scans++ == 0) {
                idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    public void EndScan() {
        lock (gate) {
            if (--scans == 0) {
                idle.TrySetResult();
            }
        }
    }

    static TaskCompletionSource Done() {
        var done = new TaskCompletionSource();
        done.SetResult();
        return done;
    }

    /// <summary>How long creating an unknown plugin waits for a scan in progress.</summary>
    public static TimeSpan ScanWait { get; set; } = TimeSpan.FromSeconds(60);

    bool Knows(string pluginId) {
        if (Factory.Handles(pluginId)) {
            return true;
        }

        lock (gate) {
            return extras.Exists(e => e.Descriptor.Id == pluginId) || clap.Any(e => e.Descriptor.Id == pluginId);
        }
    }

    void Scan(IEnumerable<string>? folders) {
        var found = new List<ClapEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in ClapFiles(folders ?? ClapFolders())) {
            try {
                var module = PluginThread.Shared.Invoke(() => ClapModule.Load(file));
                foreach (var descriptor in module.Descriptors) {
                    if (!seen.Add(descriptor.Id)) {
                        continue; // the first folder wins, as CLAP_PATH comes first
                    }

                    using var instance = ClapInstrument.Create(file, descriptor.Id, Log);
                    found.Add(new(file, descriptor, instance.Descriptor.Parameters));
                }
            } catch (Exception e) when (e is IOException
                                            or BadImageFormatException
                                            or DllNotFoundException
                                            or EntryPointNotFoundException
                                            or UnauthorizedAccessException) {
                Log?.Invoke($"CLAP {file}: {e.Message}");
            }
        }

        lock (gate) {
            clap = found;
        }
    }

    /// <summary>
    ///     Where CLAP plugins live: <c>CLAP_PATH</c> first, then the platform's standard folders (docs/03-phase-2.md,
    ///     "Catalogue").
    /// </summary>
    public static IReadOnlyList<string> ClapFolders() {
        var folders = new List<string>();
        if (Environment.GetEnvironmentVariable("CLAP_PATH") is { Length: > 0 } path) {
            folders.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS()) {
            folders.Add(Path.Combine(home, "Library", "Audio", "Plug-Ins", "CLAP"));
            folders.Add("/Library/Audio/Plug-Ins/CLAP");
        } else if (OperatingSystem.IsWindows()) {
            folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "CLAP"));
            folders.Add(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs",
                    "Common",
                    "CLAP"
                )
            );
        } else {
            folders.Add(Path.Combine(home, ".clap"));
            folders.Add("/usr/lib/clap");
        }

        return folders;
    }

    /// <summary>The <c>.clap</c> files (on macOS, bundles) under <paramref name="folders" />, recursively.</summary>
    public static IEnumerable<string> ClapFiles(IEnumerable<string> folders) {
        foreach (var folder in folders) {
            if (!Directory.Exists(folder)) {
                continue;
            }

            var pending = new Stack<string>([folder]);
            while (pending.TryPop(out var dir)) {
                foreach (var entry in Entries(dir)) {
                    var isClap = entry.EndsWith(".clap", StringComparison.OrdinalIgnoreCase);
                    var isFolder = Directory.Exists(entry);
                    if (isClap && isFolder == OperatingSystem.IsMacOS()) {
                        yield return entry; // a bundle on macOS, a shared library elsewhere
                    } else if (isFolder) {
                        pending.Push(entry);
                    }
                }
            }
        }
    }

    static string[] Entries(string dir) {
        try {
            var entries = Directory.GetFileSystemEntries(dir);
            Array.Sort(entries, StringComparer.Ordinal);
            return entries;
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            return [];
        }
    }

    static EnginePlugin Builtin(InstrumentDescriptor d) =>
        new(d.Id, d.Name, d.Vendor, d.Version, "builtin", true, d.FileTypes, [..d.Parameters.Select(Map)]);

    static EnginePlugin Builtin(EffectDescriptor d) =>
        new(
            d.Id,
            d.Name,
            d.Vendor,
            d.Version,
            "builtin",
            false,
            [],
            [..d.Parameters.Select(Map)],
            d.Category,
            d.HasSidechain
        );

    static EngineParameter Map(InstrumentParameter p) =>
        new(p.Id, p.Name, p.Min, p.Max, p.Default, p.Unit, p.Steps, p.ValueNames);
}
