using Anode.Plugins.Sandbox;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Anode.Plugins;

/// <summary>
///     The third-party plugins the engine can create: VST3 bundles, Audio Units and CLAP bundles, described by the
///     out-of-process scanner (<c>anode-plugin scan</c>), which instantiates each plugin to read its parameters, buses
///     and editor. A plugin that crashes or hangs the scanner is blocklisted (<c>plugins/blocklist.json</c> in the data
///     directory) and skipped until it changes; what was found is cached (<c>plugins/cache.json</c>), so a rescan only
///     opens what is new or changed.
/// </summary>
public sealed class PluginCatalog {
    readonly PluginHostOptions options;
    readonly string executable;
    readonly Lock gate = new();
    readonly Lock scanning = new();
    readonly List<PluginEntry> registered = [];
    IReadOnlyList<PluginEntry> entries = [];

    internal PluginCatalog(PluginHostOptions options, string executable) {
        this.options = options;
        this.executable = executable;
        var cache = ReadCache();
        entries = Build(cache.Items.Select(static i => (i.Key, (IReadOnlyList<PluginDescription>)i.Plugins)));
    }

    string Folder => Path.Combine(options.DataDirectory, "plugins");
    string CachePath => Path.Combine(Folder, "cache.json");
    string BlocklistPath => Path.Combine(Folder, "blocklist.json");

    /// <summary>Every plugin found (the cache's until the first scan), then any added with <see cref="Register" />.</summary>
    public IReadOnlyList<PluginEntry> Entries {
        get {
            lock (gate) {
                return [..entries, ..registered];
            }
        }
    }

    /// <summary>The items skipped because they crashed or hung a scan: (item, reason).</summary>
    public IReadOnlyList<(string Item, string Reason)> Blocklist =>
        [..ReadBlocklist().Items.Select(static i => (i.Key, i.Reason))];

    public PluginEntry? Find(string pluginId) {
        lock (gate) {
            return entries.FirstOrDefault(e => e.PluginId == pluginId)
                ?? registered.FirstOrDefault(e => e.PluginId == pluginId);
        }
    }

    /// <summary>Adds an entry without scanning (tests, tools).</summary>
    public void Register(PluginEntry entry) {
        lock (gate) {
            registered.Add(entry);
        }
    }

    /// <summary>
    ///     Scans the plugin folders and Audio Units: items unchanged since the cache are reused, blocklisted ones
    ///     skipped (both unless <paramref name="force" />), the rest described by <c>anode-plugin scan</c>
    ///     processes. Blocking; one scan at a time.
    /// </summary>
    public IReadOnlyList<PluginEntry> Scan(bool force = false, CancellationToken cancellation = default) {
        lock (scanning) {
            var cache = ReadCache();
            var blocklist = ReadBlocklist();
            var items = Items(cancellation);
            var results = new List<(string Key, IReadOnlyList<PluginDescription> Plugins)>();
            var todo = new List<(string Key, string Stamp)>();
            foreach (var (key, stamp) in items) {
                var cached = cache.Items.FirstOrDefault(i => i.Key == key);
                var blocked = blocklist.Items.FirstOrDefault(i => i.Key == key);
                if (!force && cached is not null && cached.Stamp == stamp) {
                    results.Add((key, cached.Plugins));
                } else if (!force && blocked is not null && blocked.Stamp == stamp) {
                    options.Log?.Invoke($"plugin scan: skipping {key} (blocklisted: {blocked.Reason})");
                } else {
                    todo.Add((key, stamp));
                }
            }

            var stamps = todo.ToDictionary(static t => t.Key, static t => t.Stamp);
            foreach (var (key, plugins, failure) in Describe([..todo.Select(static t => t.Key)], cancellation)) {
                blocklist.Items.RemoveAll(i => i.Key == key);
                if (failure is { Crashed: true }) {
                    blocklist.Items.Add(new() { Key = key, Stamp = stamps[key], Reason = failure.Value.Reason });
                    options.Log?.Invoke($"plugin scan: {key} blocklisted ({failure.Value.Reason})");
                } else if (failure is { } f) {
                    options.Log?.Invoke($"plugin scan: {key}: {f.Reason}");
                } else {
                    results.Add((key, plugins));
                }
            }

            var newCache = new PluginCacheFile {
                Items = [
                    ..results.Select(r => new PluginCacheItem {
                            Key = r.Key, Stamp = items.First(i => i.Key == r.Key).Stamp, Plugins = [..r.Plugins]
                        }
                    )
                ]
            };
            Write(CachePath, JsonSerializer.Serialize(newCache, SandboxJson.Default.PluginCacheFile));
            Write(BlocklistPath, JsonSerializer.Serialize(blocklist, SandboxJson.Default.PluginBlocklistFile));
            var built = Build(results);
            lock (gate) {
                entries = built;
            }

            return Entries;
        }
    }

    static List<PluginEntry> Build(IEnumerable<(string Key, IReadOnlyList<PluginDescription> Plugins)> items) {
        var list = new List<PluginEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, plugins) in items) {
            var colon = key.IndexOf(':', StringComparison.Ordinal);
            var kind = key[..colon];
            var target = key[(colon + 1)..];
            var format = kind switch {
                "vst3" => HostedFormat.Vst3,
                "au" => HostedFormat.Au,
                _ => HostedFormat.Clap
            };
            foreach (var d in plugins) {
                var id = PluginEntry.MakeId(format, d.Id);
                if (seen.Add(id)) {
                    list.Add(new(id, format, format == HostedFormat.Au ? string.Empty : target, d));
                }
            }
        }

        return list;
    }

    // ---- What to scan ------------------------------------------------------------------------------------------------

    List<(string Key, string Stamp)> Items(CancellationToken cancellation) {
        var items = new List<(string, string)>();
        foreach (var bundle in Bundles(options.Vst3Folders ?? Vst3Folders(), ".vst3")) {
            items.Add(("vst3:" + bundle, Stamp(bundle)));
        }

        foreach (var bundle in Bundles(options.ClapFolders ?? ClapFolders(), ".clap")) {
            items.Add(("clap:" + bundle, Stamp(bundle)));
        }

        if (options.ScanAudioUnits && OperatingSystem.IsMacOS()) {
            // Listing the components instantiates nothing; it still runs in a child, as everything plugin-related does.
            foreach (var (_, plugins, failure) in Describe(["au:*"], cancellation)) {
                if (failure is { } f) {
                    options.Log?.Invoke("plugin scan: could not list Audio Units: " + f.Reason);
                    continue;
                }

                foreach (var d in plugins) {
                    items.Add(("au:" + d.Id, d.Version));
                }
            }
        }

        return items;
    }

    /// <summary>Where VST3 plugins live: <c>VST3_PATH</c> first, then the platform's standard folders.</summary>
    public static IReadOnlyList<string> Vst3Folders() {
        var folders = FromEnvironment("VST3_PATH");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS()) {
            folders.Add(Path.Combine(home, "Library", "Audio", "Plug-Ins", "VST3"));
            folders.Add("/Library/Audio/Plug-Ins/VST3");
        } else if (OperatingSystem.IsWindows()) {
            folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "VST3"));
        } else {
            folders.Add(Path.Combine(home, ".vst3"));
            folders.Add("/usr/lib/vst3");
            folders.Add("/usr/local/lib/vst3");
        }

        return folders;
    }

    /// <summary>Where CLAP plugins live: <c>CLAP_PATH</c> first, then the platform's standard folders.</summary>
    public static IReadOnlyList<string> ClapFolders() {
        var folders = FromEnvironment("CLAP_PATH");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS()) {
            folders.Add(Path.Combine(home, "Library", "Audio", "Plug-Ins", "CLAP"));
            folders.Add("/Library/Audio/Plug-Ins/CLAP");
        } else if (OperatingSystem.IsWindows()) {
            folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "CLAP"));
        } else {
            folders.Add(Path.Combine(home, ".clap"));
            folders.Add("/usr/lib/clap");
        }

        return folders;
    }

    static List<string> FromEnvironment(string variable) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } path
            ? [..path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)]
            : [];

    /// <summary>The bundles (macOS) or files with <paramref name="extension" /> under the folders, recursively.</summary>
    static IEnumerable<string> Bundles(IEnumerable<string> folders, string extension) {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var folder in folders) {
            if (!Directory.Exists(folder)) {
                continue;
            }

            var pending = new Stack<string>([folder]);
            while (pending.TryPop(out var dir)) {
                string[] children;
                try {
                    children = Directory.GetFileSystemEntries(dir);
                } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                    continue;
                }

                Array.Sort(children, StringComparer.Ordinal);
                foreach (var child in children) {
                    if (child.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) {
                        if (seen.Add(Path.GetFullPath(child))) {
                            yield return Path.GetFullPath(child);
                        }
                    } else if (Directory.Exists(child)) {
                        pending.Push(child);
                    }
                }
            }
        }
    }

    /// <summary>A bundle's stamp: the latest modification time of the bundle and its binary folder.</summary>
    static string Stamp(string bundle) {
        var times = new List<DateTime> {
            Directory.Exists(bundle) ? Directory.GetLastWriteTimeUtc(bundle) : File.GetLastWriteTimeUtc(bundle)
        };
        foreach (var inner in new[] {
                     "Contents/MacOS", "Contents/Info.plist", "Contents/x86_64-linux", "Contents/x86_64-win"
                 }) {
            var p = Path.Combine(bundle, inner);
            if (Directory.Exists(p)) {
                times.Add(Directory.GetLastWriteTimeUtc(p));
                times.AddRange(Directory.GetFiles(p).Select(File.GetLastWriteTimeUtc));
            } else if (File.Exists(p)) {
                times.Add(File.GetLastWriteTimeUtc(p));
            }
        }

        return times.Max().Ticks.ToString(CultureInfo.InvariantCulture);
    }

    // ---- The scanner processes -----------------------------------------------------------------------------------------

    readonly record struct ScanFailure(bool Crashed, string Reason);

    /// <summary>
    ///     Describes <paramref name="items" /> in <c>anode-plugin scan</c> processes: one process for all of them, a new
    ///     one after any item that crashes or hangs it (that item is reported as crashed).
    /// </summary>
    List<(string Key, IReadOnlyList<PluginDescription> Plugins, ScanFailure? Failure)> Describe(
        IReadOnlyList<string> items,
        CancellationToken cancellation
    ) {
        var results = new List<(string, IReadOnlyList<PluginDescription>, ScanFailure?)>();
        var remaining = new Queue<string>(items);
        while (remaining.Count > 0 && !cancellation.IsCancellationRequested) {
            var batch = remaining.ToList();
            var done = new HashSet<string>(StringComparer.Ordinal);
            var failed = RunScanner(
                batch,
                (item, plugins, error) => {
                    done.Add(item);
                    results.Add(error is null ? (item, plugins, null) : (item, [], new ScanFailure(false, error)));
                },
                out var current,
                out var reason,
                cancellation
            );
            remaining = new(batch.Where(i => !done.Contains(i)));
            if (!failed) {
                break;
            }

            if (current is not null) {
                results.Add((current, [], new ScanFailure(true, reason)));
                remaining = new(remaining.Where(i => i != current));
            } else {
                // The scanner itself failed before any item: report them all.
                foreach (var item in remaining) {
                    results.Add((item, [], new ScanFailure(false, reason)));
                }

                break;
            }
        }

        return results;
    }

    /// <summary>Runs one scanner; true when it died or hung (on <paramref name="current" />, if any).</summary>
    bool RunScanner(
        IReadOnlyList<string> items,
        Action<string, IReadOnlyList<PluginDescription>, string?> found,
        out string? current,
        out string reason,
        CancellationToken cancellation
    ) {
        current = null;
        reason = string.Empty;
        var dll = executable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(dll ? "dotnet" : executable) {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8
        };
        if (dll) {
            start.ArgumentList.Add("exec");
            start.ArgumentList.Add(executable);
        }

        start.ArgumentList.Add("scan");
        foreach (var item in items) {
            start.ArgumentList.Add(item);
        }

        using var process = Process.Start(start);
        if (process is null) {
            reason = "could not start " + executable;
            return true;
        }

        process.ErrorDataReceived += (_, e) => {
            if (e.Data is { Length: > 0 } line) {
                options.Log?.Invoke("plugin scan: " + line);
            }
        };
        process.BeginErrorReadLine();
        var lines = new System.Collections.Concurrent.BlockingCollection<string?>();
        var reader = new Thread(() => {
                try {
                    while (process.StandardOutput.ReadLine() is { } line) {
                        lines.Add(line, cancellation);
                    }

                    lines.Add(null, cancellation);
                } catch (Exception e) when (e is IOException or OperationCanceledException) {
                    // ended, or nobody listens any more
                }
            }
        ) { IsBackground = true, Name = "plugin scan reader" };
        reader.Start();
        var since = Stopwatch.StartNew();
        while (true) {
            if (cancellation.IsCancellationRequested) {
                Kill(process);
                reason = "cancelled";
                return false;
            }

            if (!lines.TryTake(out var line, 100, CancellationToken.None)) {
                if (current is not null && since.Elapsed > options.ScanTimeout) {
                    Kill(process);
                    reason = $"hung (no answer within {options.ScanTimeout.TotalSeconds:0} s)";
                    return true;
                }

                continue;
            }

            if (line is null) {
                process.WaitForExit();
                if (current is null) {
                    return false; // finished every item
                }

                var code = process.ExitCode;
                reason = code > 128 ? $"crashed the scanner (signal {code - 128})" : $"stopped the scanner ({code})";
                return true;
            }

            SandboxReply? e;
            try {
                e = SandboxJson.ReadReply(line);
            } catch (JsonException) {
                continue;
            }

            if (e?.Event == "begin") {
                current = e.Item;
                since.Restart();
            } else if (e?.Event == "scanned" && e.Item is { } item) {
                IReadOnlyList<PluginDescription> plugins = [];
                var error = e.Error ?? "failed";
                if (e.Ok && e.Plugins is { } json) {
                    try {
                        plugins = SandboxJson.ReadDescriptions(json);
                        error = null;
                    } catch (JsonException x) {
                        error = "unreadable description: " + x.Message;
                    }
                }

                found(item, plugins, error);
                current = null;
            }
        }
    }

    static void Kill(Process process) {
        try {
            process.Kill();
            process.WaitForExit(2000);
        } catch (InvalidOperationException) {
            // gone
        }
    }

    // ---- Files ---------------------------------------------------------------------------------------------------------

    PluginCacheFile ReadCache() => Read(CachePath, SandboxJson.Default.PluginCacheFile) ?? new();

    PluginBlocklistFile ReadBlocklist() => Read(BlocklistPath, SandboxJson.Default.PluginBlocklistFile) ?? new();

    static T? Read<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) where T : class {
        try {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), type) : null;
        } catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) {
            return null;
        }
    }

    void Write(string path, string json) {
        try {
            Directory.CreateDirectory(Folder);
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, true);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            options.Log?.Invoke($"plugin catalogue: could not write {path}: {e.Message}");
        }
    }
}
