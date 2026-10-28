using Anode.Dsp.Effects;
using Anode.Dsp.Instruments;
using Anode.Ipc;
using Anode.Plugins.Hosting;
using Anode.Plugins.Native;
using Anode.Plugins.Sandbox;

namespace Anode.Plugins;

/// <summary>What <see cref="PluginHost" /> needs to know.</summary>
public sealed class PluginHostOptions {
    /// <summary>Anode's data directory: the catalogue's cache and blocklist live in <c>plugins/</c> under it.</summary>
    public string DataDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Anode");

    /// <summary>
    ///     The <c>anode-plugin</c> executable (or its <c>.dll</c>, run with <c>dotnet exec</c>). Default: next to this
    ///     assembly (where the host's build and publish put it).
    /// </summary>
    public string? SandboxExecutable { get; set; }

    public SandboxMode Mode { get; set; } = SandboxMode.Grouped;

    /// <summary>At most this many grouped sandboxes (0: performance cores − 2, at least 1); further vendors share.</summary>
    public int MaxSandboxes { get; set; }

    public int SlotsPerSandbox { get; set; } = 32;

    /// <summary>The largest block a hosted plugin is handed (offline renders split larger ones).</summary>
    public int MaxFrames { get; set; } = 4096;

    /// <summary>A sandboxed block's deadline, as a fraction of the device period (S4: 80 %).</summary>
    public double DeadlineFraction { get; set; } = 0.8;

    /// <summary>A sandbox that has not returned a block (or a request) for this long is killed as hung.</summary>
    public TimeSpan HangTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long loading one plugin may take.</summary>
    public TimeSpan LoadTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>VST3 folders (null: <c>VST3_PATH</c> and the platform's standard folders).</summary>
    public IReadOnlyList<string>? Vst3Folders { get; set; }

    /// <summary>CLAP folders (null: <c>CLAP_PATH</c> and the platform's standard folders).</summary>
    public IReadOnlyList<string>? ClapFolders { get; set; }

    /// <summary>Scan the installed Audio Units (macOS).</summary>
    public bool ScanAudioUnits { get; set; } = OperatingSystem.IsMacOS();

    /// <summary>How long scanning one item may take before it is blocklisted as hung.</summary>
    public TimeSpan ScanTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     The desktop app's process, whose main window editor windows go over (<see cref="EditorPlacement.AppWindow" />).
    ///     Default: this process's parent (the desktop app starts the host as a sidecar).
    /// </summary>
    public int AppProcessId { get; set; } = ParentProcessId();

    static int ParentProcessId() => OperatingSystem.IsWindows() ? 0 : Libc.GetParentPid();

    /// <summary>Diagnostics (sandbox output, plugins that failed to scan).</summary>
    public Action<string>? Log { get; set; }
}

/// <summary>
///     The factory the engine creates third-party plugins with (docs/05-phase-4.md, workstream E): VST3, AU and CLAP
///     effects and instruments, run in grouped <c>anode-plugin</c> sandboxes (ADR-0003, spike S4) and adapted to
///     <see cref="IAudioEffect" /> and <see cref="IInstrument" />.
///     <para>
///         Threads: <see cref="CreateEffect" />, <see cref="CreateInstrument" />, the instances' <c>Prepare</c>, state
///         and editor calls, <see cref="ConfigureDevice" /> and disposal on control threads; <see cref="BeginCycle" />
///         and the instances' <c>Process</c>, <c>SetParameter</c> and <c>Reset</c> on the audio thread.
///         <see cref="ParameterChanged" /> and <see cref="Problem" /> are raised on background threads.
///     </para>
/// </summary>
public sealed class PluginHost : IDisposable {
    readonly PluginHostOptions options;
    readonly Lock gate = new();
    readonly List<SandboxProcess> sandboxes = [];
    readonly Dictionary<SandboxProcess, Dictionary<int, PluginInstance>> instances = [];
    readonly List<PluginInstance> inHost = [];
    readonly string executable;
    int sandboxCount;
    long cycleStart;
    long devicePeriodNs;
    nint workgroup;
    int workgroupGeneration;
    bool disposed;

    public PluginHost(PluginHostOptions options) {
        this.options = options;
        executable = options.SandboxExecutable ?? DefaultExecutable();
        Catalog = new(options, executable);
    }

    /// <summary>The catalogue: what the last scan (or the cache) found.</summary>
    public PluginCatalog Catalog { get; }

    public PluginHostOptions Options => options;

    internal Action<string>? Log => options.Log;

    internal int MaxFrames => options.MaxFrames;

    /// <summary>Real time (deadline, bypass on a miss) or offline (wait).</summary>
    public ProcessingMode Processing { get; set; } = ProcessingMode.RealTime;

    /// <summary>A plugin's own editor changed a parameter: (owner, parameter id, value).</summary>
    public event Action<PluginOwner, string, double>? ParameterChanged;

    /// <summary>A plugin crashed, hung or failed to load, and is bypassed.</summary>
    public event Action<PluginProblem>? Problem;

    /// <summary>The catalogue as the host reports it to the server.</summary>
    public IReadOnlyList<EnginePlugin> CatalogEntries() => [..Catalog.Entries.Select(static e => e.ToEngine())];

    /// <summary>Rescans (see <see cref="PluginCatalog.Scan" />) on a background thread.</summary>
    public Task<IReadOnlyList<PluginEntry>> ScanAsync(bool force = false, CancellationToken cancellation = default) =>
        Task.Run(() => Catalog.Scan(force, cancellation), cancellation);

    /// <summary>True when <paramref name="pluginId" /> is in the catalogue.</summary>
    public bool Knows(string pluginId) => Catalog.Find(pluginId) is not null;

    /// <summary>
    ///     A new, unprepared effect for <paramref name="pluginId" />, or null when the catalogue has no such effect. A
    ///     plugin whose sandbox cannot start comes back failed (bypassed) and reports a <see cref="Problem" />.
    /// </summary>
    public IAudioEffect? CreateEffect(string pluginId, PluginOwner owner) {
        var entry = Catalog.Find(pluginId);
        return entry is null || entry.Description.IsInstrument ? null : new HostedEffect(CreateInstance(entry, owner));
    }

    /// <summary>A new, unprepared instrument for <paramref name="pluginId" />, or null (see <see cref="CreateEffect" />).</summary>
    public IInstrument? CreateInstrument(string pluginId, PluginOwner owner) {
        var entry = Catalog.Find(pluginId);
        return entry is null || !entry.Description.IsInstrument
            ? null
            : new HostedInstrument(CreateInstance(entry, owner));
    }

    /// <summary>
    ///     The device the engine runs: its period sets the blocks' deadline and the sandboxes' real-time policy, and
    ///     on macOS its IO workgroup is published for the sandboxes' audio threads to join (S4). Null device: no
    ///     workgroup (the null clock).
    /// </summary>
    public void ConfigureDevice(double sampleRate, int bufferFrames, string? deviceUid, bool joinWorkgroup = true) {
        lock (gate) {
            Volatile.Write(ref devicePeriodNs, (long)(bufferFrames * 1e9 / sampleRate));
            if (workgroup != 0) {
                IpcWake.WorkgroupUnpublish(workgroup);
                workgroup = 0;
            }

            var generation = 0;
            if (joinWorkgroup && deviceUid is not null && OperatingSystem.IsMacOS()) {
                var g = workgroupGeneration + 1;
                if (IpcWake.WorkgroupPublish(
                        deviceUid.Length == 0 ? null : deviceUid,
                        SharedLayout.WorkgroupService(Environment.ProcessId, g),
                        out workgroup
                    )
                    == 0) {
                    workgroupGeneration = g;
                    generation = g;
                } else {
                    workgroup = 0;
                    options.Log?.Invoke("plugin sandboxes: no device workgroup (" + IpcWake.WorkgroupLastError + ")");
                }
            }

            foreach (var box in sandboxes) {
                Configure(box, generation);
            }
        }
    }

    unsafe void Configure(SandboxProcess box, int generation) {
        if (box.IsDead) {
            return;
        }

        var h = box.Header;
        h->PeriodNs = Volatile.Read(ref devicePeriodNs);
        h->Workgroup = generation;
        Interlocked.Increment(ref h->ConfigGeneration);
        IpcWake.Signal(box.Go);
    }

    /// <summary>The audio thread starts a device cycle: blocks of this cycle share its deadline.</summary>
    public void BeginCycle() => Volatile.Write(ref cycleStart, IpcWake.Now());

    /// <summary>The deadline for a block of <paramref name="frames" /> at <paramref name="sampleRate" />.</summary>
    internal long Deadline(int frames, double sampleRate) {
        var now = IpcWake.Now();
        if (Processing == ProcessingMode.Offline) {
            return now + options.HangTimeout.Ticks * 100;
        }

        var period = Volatile.Read(ref devicePeriodNs);
        if (period <= 0) {
            period = (long)(frames * 1e9 / Math.Max(sampleRate, 1));
        }

        var start = Volatile.Read(ref cycleStart);
        if (start == 0 || now - start > period) {
            start = now; // no cycle marked (tests, a caller that does not): this block's own budget
        }

        return start + (long)(period * options.DeadlineFraction);
    }

    /// <summary>The live sandboxes: (group, pid, instances), for diagnostics and tests.</summary>
    public IReadOnlyList<(string Group, int Pid, int Instances)> Sandboxes {
        get {
            lock (gate) {
                return [..sandboxes.Where(static s => !s.IsDead).Select(static s => (s.Group, s.Pid, s.SlotsInUse))];
            }
        }
    }

    internal unsafe (int RtStatus, int WorkgroupStatus, long Wakes)? SandboxStatus(int pid) {
        lock (gate) {
            var box = sandboxes.Find(s => s.Pid == pid);
            return box is null ? null : (box.Header->RtStatus, box.Header->WorkgroupStatus, box.Header->Wakes);
        }
    }

    PluginInstance CreateInstance(PluginEntry entry, PluginOwner owner) {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (options.Mode == SandboxMode.InHost) {
            var local = new PluginInstance(this, entry, owner, new InHostBackend(entry), null);
            lock (gate) {
                inHost.Add(local);
            }

            return local;
        }

        SandboxProcess box;
        int slot;
        try {
            (box, slot) = AcquireSlot(entry);
        } catch (InvalidOperationException e) {
            var failed = new PluginInstance(this, entry, owner, null, "could not start its sandbox: " + e.Message);
            RaiseProblem(new(owner, entry.PluginId, failed.Failure!));
            return failed;
        }

        var instance = new PluginInstance(
            this,
            entry,
            owner,
            new SandboxBackend(box, slot, entry, options.LoadTimeout),
            null
        );
        lock (gate) {
            instances[box][slot] = instance;
        }

        return instance;
    }

    (SandboxProcess, int) AcquireSlot(PluginEntry entry) {
        var group = options.Mode == SandboxMode.PerInstance
            ? $"instance-{Interlocked.Increment(ref sandboxCount)}"
            : entry.Description.Vendor.Length > 0
                ? entry.Description.Vendor
                : entry.PluginId;
        lock (gate) {
            var live = sandboxes.Where(static s => !s.IsDead).ToList();
            foreach (var box in live.Where(s => s.Group == group)) {
                var slot = box.AllocateSlot();
                if (slot >= 0) {
                    return (box, slot);
                }
            }

            var cap = options.MaxSandboxes > 0 ? options.MaxSandboxes : Math.Max(1, PerformanceCores() - 2);
            if (options.Mode == SandboxMode.Grouped && live.Count >= cap) {
                // Over the cap: share the least busy sandbox.
                foreach (var box in live.OrderBy(static s => s.SlotsInUse)) {
                    var slot = box.AllocateSlot();
                    if (slot >= 0) {
                        return (box, slot);
                    }
                }
            }

            var started = StartSandbox(group);
            return (started, started.AllocateSlot());
        }
    }

    SandboxProcess StartSandbox(string group) {
        var box = SandboxProcess.Start(
            executable,
            group,
            Interlocked.Increment(ref sandboxCount),
            options.SlotsPerSandbox,
            options.MaxFrames,
            Volatile.Read(ref devicePeriodNs),
            workgroup != 0 ? workgroupGeneration : 0,
            options.HangTimeout,
            options.Log
        );
        box.Died += OnSandboxDied;
        box.EventReceived += (slot, e) => OnSandboxEvent(box, slot, e);
        sandboxes.Add(box);
        instances[box] = [];
        return box;
    }

    void OnSandboxEvent(SandboxProcess box, int slot, SandboxReply e) {
        PluginInstance? instance;
        lock (gate) {
            instance = instances.TryGetValue(box, out var slots) && slots.TryGetValue(slot, out var i) ? i : null;
        }

        switch (e.Event) {
            case "param":
                instance?.OnEdited(e.Index, e.Value);
                break;
            case "latency":
                instance?.OnLatency(e.Latency);
                break;
            case "editorClosed":
                instance?.EditorWindow = 0;

                break;
        }
    }

    void OnSandboxDied(SandboxProcess box) {
        List<PluginInstance> affected;
        lock (gate) {
            affected = instances.TryGetValue(box, out var slots) ? [..slots.Values] : [];
        }

        options.Log?.Invoke($"anode-plugin {box.Pid} ({box.Group}) {box.DeathReason}");
        foreach (var instance in affected) {
            instance.Fail(box.DeathReason ?? "stopped");
        }
    }

    internal void Release(PluginInstance instance, PluginBackend? backend = null) {
        SandboxProcess? idle = null;
        lock (gate) {
            inHost.Remove(instance);
            if (disposed) {
                return; // the host already stopped (and unmapped) every sandbox
            }

            if (backend is SandboxBackend sb && instances.TryGetValue(sb.Box, out var slots)) {
                slots.Remove(sb.Slot);
                sb.Box.FreeSlot(sb.Slot);
                // An empty grouped sandbox stays for the vendor's next plugin (replacing an insert, reopening a
                // project); an empty per-instance or crashed one goes (a crashed one is unmapped only now, when no
                // adapter reads it any more).
                if (slots.Count == 0 && (sb.Box.IsDead || options.Mode == SandboxMode.PerInstance)) {
                    idle = sb.Box;
                    instances.Remove(sb.Box);
                    sandboxes.Remove(sb.Box);
                }
            }
        }

        idle?.Dispose();
    }

    internal void RaiseParameterChanged(PluginOwner owner, string parameterId, double value) =>
        ParameterChanged?.Invoke(owner, parameterId, value);

    internal void RaiseProblem(PluginProblem problem) => Problem?.Invoke(problem);

    /// <summary>
    ///     Stops every sandbox. Instances still alive are bypassed from then on: their <c>Process</c> leaves the block
    ///     dry (instruments silent), and their <c>Dispose</c> is harmless. Stop processing them before disposing the
    ///     host: a block in flight on another thread while the sandboxes go away is not safe.
    /// </summary>
    public void Dispose() {
        List<SandboxProcess> all;
        List<PluginInstance> outstanding;
        lock (gate) {
            if (disposed) {
                return;
            }

            disposed = true;
            all = [..sandboxes];
            outstanding = [..inHost, ..instances.Values.SelectMany(static s => s.Values)];
            sandboxes.Clear();
            instances.Clear();
            inHost.Clear();
        }

        foreach (var instance in outstanding) {
            instance.Abandon();
        }

        foreach (var box in all) {
            box.Died -= OnSandboxDied;
            box.Dispose();
        }

        if (workgroup != 0) {
            IpcWake.WorkgroupUnpublish(workgroup);
            workgroup = 0;
        }
    }

    static string DefaultExecutable() {
        var dir = AppContext.BaseDirectory;
        var native = Path.Combine(dir, OperatingSystem.IsWindows() ? "anode-plugin.exe" : "anode-plugin");
        return File.Exists(native) ? native : Path.Combine(dir, "anode-plugin.dll");
    }

    static int PerformanceCores() => OperatingSystem.IsWindows() ? Environment.ProcessorCount : Libc.PerformanceCores();
}
