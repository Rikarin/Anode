using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace Anode.Spikes.BulkIpc;

/// <summary>Options of one measured scenario.</summary>
sealed record Scenario(
    string Name,
    long Events,
    SegmentBackend Backend,
    bool Incremental,
    bool Checksum,
    bool Verify,
    bool Lock,
    bool Pooled = false
) {
    public string Label => $"{(Backend == SegmentBackend.PosixShm ? "shm" : "file")}: {Name}";
}

/// <summary>
///     The anode-server side: spawns the host role, then for each scenario writes models into segments, names them in
///     a frame, and times every step from the host's replies. Prints Markdown tables.
/// </summary>
sealed class ServerRole : IDisposable {
    readonly Stream stream;
    readonly Socket socket;
    readonly Process host;
    readonly string directory;
    readonly string prefix = $"anode-s3-{Environment.ProcessId}";
    readonly int tracks;
    readonly long periodTicks;
    long nextSegment;
    SharedSegment[] pool = [];
    int poolNext;
    long version;

    ServerRole(Socket socket, Process host, string directory, int tracks, long periodTicks) {
        this.periodTicks = periodTicks;
        this.socket = socket;
        this.host = host;
        this.directory = directory;
        this.tracks = tracks;
        stream = new NetworkStream(socket, false);
    }

    public static int Run(Options options) {
        Layout.AssertSizes();
        var sizes = options.Longs("sizes", [10_000, 100_000, 1_000_000, 5_000_000]);
        var swaps = options.Int("swaps", 100);
        var warmup = options.Int("warmup", 3);
        var tracks = options.Int("tracks", 128);
        var frames = options.Int("frames", 64);
        var rate = options.Int("rate", 48_000);
        var directory = options.Text("dir") ?? SharedSegment.DefaultDirectory();

        var socketPath = Path.Combine(Path.GetTempPath(), $"anode-s3-{Environment.ProcessId}.sock");
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
        var host = Process.Start(
                new ProcessStartInfo(
                    self,
                    ["host", "--socket", socketPath, "--frames", $"{frames}", "--rate", $"{rate}"]
                ) { UseShellExecute = false }
            )
            ?? throw new InvalidOperationException("Could not start the host role.");

        var socket = Connect(socketPath);
        using var server = new ServerRole(socket, host, directory, tracks, Stopwatch.Frequency * frames / rate);
        Console.WriteLine(
            $"# S3 bulk IPC: {Environment.OSVersion}, {Environment.ProcessorCount} cores, "
            + $"{(Debugger.IsAttached ? "debugger" : System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported ? "JIT" : "NativeAOT")}, "
            + $"page {Environment.SystemPageSize} B, segments in {(OperatingSystem.IsWindows() ? "named mappings" : directory)}"
        );
        Thread.Sleep(100);
        var realtime = server.Request<StatsReply>(new StatsRequest()).Realtime;
        Console.WriteLine(
            $"Audio thread: {frames} frames at {rate} Hz ({frames * 1000.0 / rate:0.000} ms), "
            + $"{(realtime ? "Mach time-constraint thread" : "spinning, highest normal priority")}, {tracks} tracks, "
            + $"{swaps} swaps per scenario after {warmup} warm-up swaps."
        );
        Console.WriteLine();

        // macOS has no tmpfs: its file backend is APFS-backed, so POSIX shm is the primary backend there and the file
        // backend is measured alongside for comparison.
        var primary = OperatingSystem.IsMacOS() ? SegmentBackend.PosixShm : SegmentBackend.File;
        var scenarios = new List<Scenario>();
        foreach (var size in sizes) {
            scenarios.Add(new("whole, checksum + verify", size, primary, false, true, true, false));
        }

        foreach (var size in sizes.Where(static s => s >= 1_000_000)) {
            scenarios.Add(new("whole, prefault only", size, primary, false, false, false, false));
        }

        scenarios.Add(new("whole, prefault + mlock", 1_000_000, primary, false, false, false, true));
        foreach (var size in sizes.Where(static s => s >= 1_000_000)) {
            scenarios.Add(new("per-track, checksum + verify", size, primary, true, true, true, false));
            scenarios.Add(new("per-track, prefault only", size, primary, true, false, false, false));
        }

        foreach (var size in sizes.Where(static s => s >= 1_000_000)) {
            scenarios.Add(new("whole, pooled, checksum + verify", size, primary, false, true, true, false, true));
            scenarios.Add(new("whole, pooled, prefault only", size, primary, false, false, false, false, true));
        }

        if (primary != SegmentBackend.File) {
            foreach (var size in sizes.Where(static s => s >= 1_000_000)) {
                scenarios.Add(new("whole, checksum + verify", size, SegmentBackend.File, false, true, true, false));
                scenarios.Add(new("whole, prefault only", size, SegmentBackend.File, false, false, false, false));
            }
        }

        var timing = new StringBuilder();
        var health = new StringBuilder();
        var breakdown = new StringBuilder();
        timing.AppendLine(
            "| Scenario | Events | Segment MB | Write | Frame → host | Map | Layout + prefault | Verify | mlock | Host work (frame → published) | Publish → first use | **Frame → first use** | **End to end** | First callback on new model (µs) | Release |"
        );
        timing.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        breakdown.AppendLine(
            "| Scenario | Events | Create + map | Copy | Checksum | Cleanup after ack (unmap + unlink) |"
        );
        breakdown.AppendLine("|---|---|---|---|---|---|");
        health.AppendLine(
            "| Scenario | Events | Callbacks | Late | Over 10 % of budget (host idle / mapping / publishing / unmapping) | Worst callback (µs) | Audio-thread bytes | Host GCs | Mapped after | Host RSS MB (before → after) | Host fds (before → after) | Names left |"
        );
        health.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");

        var models = new Dictionary<long, CompiledModel>();
        foreach (var scenario in scenarios) {
            if (!models.TryGetValue(scenario.Events, out var model)) {
                model = CompiledModel.Generate(tracks, scenario.Events, 42);
                models[scenario.Events] = model;
            }

            Console.Error.WriteLine($"running {scenario.Label}, {scenario.Events:N0} events…");
            server.RunScenario(scenario, model, swaps, warmup, timing, breakdown, health);
        }

        Console.WriteLine("## Timing per swap, ms: p50 / p95 / max");
        Console.WriteLine();
        Console.WriteLine(timing);
        Console.WriteLine("## Server write breakdown, ms: p50 / max");
        Console.WriteLine();
        Console.WriteLine(breakdown);
        Console.WriteLine("## Host health over each scenario");
        Console.WriteLine();
        Console.WriteLine(health);
        Framing.Write(server.stream, new QuitRequest());
        host.WaitForExit(10_000);
        return 0;
    }

    static Socket Connect(string path) {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 10;
        while (true) {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try {
                socket.Connect(new UnixDomainSocketEndPoint(path));
                return socket;
            } catch (SocketException) when (Stopwatch.GetTimestamp() < deadline) {
                socket.Dispose();
                Thread.Sleep(20);
            }
        }
    }

    void RunScenario(
        Scenario scenario,
        CompiledModel model,
        int swaps,
        int warmup,
        StringBuilder timing,
        StringBuilder breakdown,
        StringBuilder health
    ) {
        var before = Request<StatsReply>(new ResetRequest());
        if (scenario.Pooled) {
            // Two slots: the one published, and the one the host released at the previous swap.
            var capacity = model.Bytes(0, tracks);
            pool = [
                SharedSegment.Create(scenario.Backend, directory, prefix, ++nextSegment, capacity),
                SharedSegment.Create(scenario.Backend, directory, prefix, ++nextSegment, capacity)
            ];
        }

        var samples = new List<Sample>();
        string? initial = null;
        if (scenario.Incremental) {
            // The initial load: every track its own segment, published in one swap.
            var start = Stopwatch.GetTimestamp();
            SegmentLoaded? last = null;
            version++;
            for (var t = 0; t < tracks; t++) {
                last = Swap(model, t, scenario, t == tracks - 1, start).Loaded;
            }

            initial = $"initial load of {tracks} segments: {Ms(start, last!.FirstUseTicks):0.0} ms";
        }

        var random = new Random(7);
        for (var s = 0; s < warmup + swaps; s++) {
            var track = random.Next(tracks);
            version++;
            model.Edit(track, version);
            // Edits arrive at any phase of the audio callback: a random pause decorrelates the swaps from it (the
            // previous swap ended right after a callback).
            var pause = Stopwatch.GetTimestamp() + (long)(random.NextDouble() * periodTicks);
            while (Stopwatch.GetTimestamp() < pause) {
                Thread.SpinWait(20);
            }

            var sample = Swap(model, scenario.Incremental ? track : -1, scenario, true, Stopwatch.GetTimestamp());
            if (s >= warmup) {
                samples.Add(sample);
            }
        }

        var after = Request<StatsReply>(new StatsRequest());
        if (scenario.Pooled) {
            Request<StatsReply>(new ResetRequest());
            foreach (var segment in pool) {
                segment.Dispose();
            }

            pool = [];
        }

        var label = scenario.Label + (initial is null ? string.Empty : $" ({initial})");
        var mb = (scenario.Incremental ? model.Bytes(0, 1) : model.Bytes(0, tracks)) / 1048576.0;
        timing.AppendLine(
            $"| {label} | {scenario.Events:N0} | {mb:0.##} | {Stat(samples, static x => x.Write)} | {Stat(samples, static x => x.Ipc)} "
            + $"| {Stat(samples, static x => x.Map)} | {Stat(samples, static x => x.Prefault)} | {Stat(samples, static x => x.Verify)} "
            + $"| {Stat(samples, static x => x.Lock)} | {Stat(samples, static x => x.HostWork)} | {Stat(samples, static x => x.FirstUse)} | **{Stat(samples, static x => x.FrameToUse)}** "
            + $"| **{Stat(samples, static x => x.EndToEnd)}** | {Stat(samples, static x => x.FirstRender * 1000)} | {Stat(samples, static x => x.Release)} |"
        );
        breakdown.AppendLine(
            $"| {scenario.Label} | {scenario.Events:N0} | {Stat2(samples, static x => x.Create)} | {Stat2(samples, static x => x.Copy)} "
            + $"| {Stat2(samples, static x => x.Checksum)} | {Stat2(samples, static x => x.Cleanup)} |"
        );
        var leftovers = scenario.Backend == SegmentBackend.PosixShm
            ? SharedSegment.UnlinkFailures
            : Directory.Exists(directory) && !OperatingSystem.IsWindows()
                ? Directory.GetFiles(directory, prefix + "-*").Length
                : 0;
        health.AppendLine(
            $"| {scenario.Label} | {scenario.Events:N0} | {after.Callbacks:N0} | {after.LateCallbacks} | {after.SlowCallbacks} ({after.SlowByPhase}) "
            + $"| {after.MaxCallbackMicros:0.0} | {after.AudioThreadAllocatedBytes} "
            + $"| {after.Gen0 - before.Gen0}/{after.Gen1 - before.Gen1}/{after.Gen2 - before.Gen2} "
            + $"| {after.MappedSegments} ({after.MappedBytes / 1048576.0:0.#} MB) "
            + $"| {before.WorkingSet / 1048576.0:0} → {after.WorkingSet / 1048576.0:0} | {before.OpenHandles} → {after.OpenHandles} | {leftovers} |"
        );
    }

    sealed record Sample(
        SegmentLoaded Loaded,
        double Write,
        double Create,
        double Copy,
        double Checksum,
        double Cleanup,
        double Ipc,
        double Map,
        double Prefault,
        double Verify,
        double Lock,
        double HostWork,
        double FirstUse,
        double FrameToUse,
        double EndToEnd,
        double FirstRender,
        double Release
    );

    Sample Swap(CompiledModel model, int track, Scenario scenario, bool commit, long start) {
        var id = ++nextSegment;
        var written = model.Write(
            scenario.Backend,
            directory,
            prefix,
            id,
            track < 0 ? 0 : track,
            track < 0 ? tracks : 1,
            version,
            scenario.Checksum,
            scenario.Pooled ? pool[poolNext ^= 1] : null
        );
        var writeEnd = Stopwatch.GetTimestamp();
        var sent = Stopwatch.GetTimestamp();
        var loaded = Request<SegmentLoaded>(
            new LoadSegment(
                id,
                written.Segment.Name,
                written.Segment.Length,
                version,
                track,
                commit,
                scenario.Verify,
                scenario.Lock,
                sent,
                scenario.Pooled
            )
        );
        // The host has its own mapping now: the server's view and the name can go.
        var cleanup = Stopwatch.GetTimestamp();
        if (!scenario.Pooled) {
            written.Segment.Dispose();
        }

        var cleaned = Stopwatch.GetTimestamp();
        if (loaded.Error is not null) {
            throw new InvalidOperationException($"host: {loaded.Error}");
        }

        return new(
            loaded,
            Ms(start, writeEnd),
            written.CreateMs,
            written.CopyMs,
            written.ChecksumMs,
            Ms(cleanup, cleaned),
            Ms(sent, loaded.ReceivedTicks),
            Ms(loaded.ReceivedTicks, loaded.MappedTicks),
            Ms(loaded.MappedTicks, loaded.PrefaultedTicks),
            Ms(loaded.PrefaultedTicks, loaded.VerifiedTicks),
            Ms(loaded.VerifiedTicks, loaded.LockedTicks),
            commit ? Ms(sent, loaded.PublishedTicks) : 0,
            commit ? Ms(loaded.PublishedTicks, loaded.FirstUseTicks) : 0,
            commit ? Ms(sent, loaded.FirstUseTicks) : 0,
            commit ? Ms(start, loaded.FirstUseTicks) : 0,
            commit ? Ms(0, loaded.FirstRenderTicks) : 0,
            commit ? Ms(loaded.FirstUseTicks, loaded.ReleasedTicks) : 0
        );
    }

    T Request<T>(SpikeMessage message) where T : SpikeMessage {
        Framing.Write(stream, message);
        return Framing.Read(stream) as T ?? throw new InvalidDataException($"Expected {typeof(T).Name}.");
    }

    static double Ms(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;

    static string Stat(List<Sample> samples, Func<Sample, double> pick) {
        var values = samples.Select(pick).Order().ToArray();
        return values.Length == 0
            ? "—"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{Percentile(values, 0.5):0.00} / {Percentile(values, 0.95):0.00} / {values[^1]:0.00}"
            );
    }

    static string Stat2(List<Sample> samples, Func<Sample, double> pick) {
        var values = samples.Select(pick).Order().ToArray();
        return values.Length == 0
            ? "—"
            : string.Create(CultureInfo.InvariantCulture, $"{Percentile(values, 0.5):0.00} / {values[^1]:0.00}");
    }

    static double Percentile(double[] sorted, double p) =>
        sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];

    public void Dispose() {
        stream.Dispose();
        socket.Dispose();
        if (!host.HasExited) {
            host.Kill();
        }

        host.Dispose();
    }
}
