using Anode.Dsp.Instruments;
using Anode.Engine.Plugins.Clap;
using Anode.Ipc;

namespace Anode.Engine.Plugins;

/// <summary>
///     The instruments the engine can load: Anode's built-ins (<see cref="BuiltinInstruments" />, plus any registered
///     with <see cref="AddBuiltin" />, as tests do) and the CLAP plugins found by <see cref="ScanClap" />. It answers
///     the catalogue the host sends the server (built-ins first, then CLAP) and creates instances by plugin id.
/// </summary>
public sealed class PluginRegistry {
    readonly Lock gate = new();
    readonly List<(InstrumentDescriptor Descriptor, Func<IInstrument> Create)> extras = [];
    TaskCompletionSource idle = Done();
    IReadOnlyList<ClapEntry> clap = [];
    int scans;

    /// <summary>Where diagnostics go (plugins that failed to load, plugin log messages).</summary>
    public Action<string>? Log { get; set; }

    /// <summary>A CLAP plugin found by a scan: its file and what it reported.</summary>
    public sealed record ClapEntry(string Path, ClapDescriptor Descriptor, IReadOnlyList<InstrumentParameter> Parameters);

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

    /// <summary>Every plugin, as the host reports it in <see cref="PluginCatalogMessage" />.</summary>
    public IReadOnlyList<EnginePlugin> Catalog() {
        var list = new List<EnginePlugin>();
        foreach (var d in BuiltinInstruments.All) {
            list.Add(Builtin(d));
        }

        lock (gate) {
            foreach (var (d, _) in extras) {
                list.Add(Builtin(d));
            }

            foreach (var entry in clap) {
                var d = entry.Descriptor;
                list.Add(
                    new(d.Id, d.Name, d.Vendor, d.Version, "clap", d.IsInstrument, [], [..entry.Parameters.Select(Map)])
                );
            }
        }

        return list;
    }

    /// <summary>
    ///     A new, unprepared instance of plugin <paramref name="pluginId" />, or null when there is none. Control
    ///     thread; a CLAP instance is created on the plugin thread.
    /// </summary>
    public IInstrument? Create(string pluginId) {
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

        return found is null ? null : ClapInstrument.Create(found.Path, pluginId, Log);
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
            } catch (Exception e) when (e is IOException or BadImageFormatException or DllNotFoundException
                                            or EntryPointNotFoundException or UnauthorizedAccessException) {
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
            folders.Add(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "CLAP")
            );
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
                string[] entries;
                try {
                    entries = Directory.GetFileSystemEntries(dir);
                } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                    continue;
                }

                Array.Sort(entries, StringComparer.Ordinal);
                foreach (var entry in entries) {
                    var isClap = entry.EndsWith(".clap", StringComparison.OrdinalIgnoreCase);
                    if (Directory.Exists(entry)) {
                        if (isClap && OperatingSystem.IsMacOS()) {
                            yield return entry;
                        } else {
                            pending.Push(entry);
                        }
                    } else if (isClap && !OperatingSystem.IsMacOS()) {
                        yield return entry;
                    }
                }
            }
        }
    }

    static EnginePlugin Builtin(InstrumentDescriptor d) =>
        new(d.Id, d.Name, d.Vendor, d.Version, "builtin", true, d.FileTypes, [..d.Parameters.Select(Map)]);

    static EngineParameter Map(InstrumentParameter p) =>
        new(p.Id, p.Name, p.Min, p.Max, p.Default, p.Unit, p.Steps, p.ValueNames);
}
