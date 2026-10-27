using Anode.Audio.Native;
using System.Diagnostics;
using System.Runtime;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Anode.Spikes.DiskStream;

static class RunMode {
    public static int Run(Options o) {
        var dir = Path.GetFullPath(o.Text("dir") ?? "artifacts/spike-audio");
        var trackCount = o.Int("tracks", 128);
        var bufferFrames = o.Int("buffer", 64);
        var ringSeconds = o.Double("ring-seconds", 2);
        var readKb = o.Int("read-kb", 96);
        var threadCount = o.Int("threads", 1);
        var noCache = o.Flag("nocache");
        var bpm = o.Double("bpm", 120);
        var barFrames = (long)(4 * 60 / bpm * WavFile.SampleRate);
        var cycle = o.Flag("cycle")
            ? new Cycle(
                true,
                (o.Int("cycle-start-bar", 5) - 1) * barFrames,
                (o.Int("cycle-start-bar", 5) - 1 + o.Int("cycle-bars", 8)) * barFrames
            )
            : Cycle.Off;
        var startFrame = cycle.Enabled ? cycle.Start : (long)(o.Double("start-seconds", 10) * WavFile.SampleRate);

        // Whole frames per read, rounded to a 1024-frame multiple (3 KiB), so reads from a page boundary stay aligned.
        var readFrames = Math.Max(1024, readKb * 1024 / WavFile.BytesPerFrame / 1024 * 1024);
        var ringFrames = Math.Max(readFrames + 1024, (int)(ringSeconds * WavFile.SampleRate));

        var residency = 0.0;
        var scratch = o.Flag("prewarm") ? new byte[1 << 20] : null;
        for (var t = 0; t < trackCount; t++) {
            if (scratch is not null) {
                Prewarm(WavFile.PathOf(dir, t), scratch);
            }

            residency += Posix.Residency(WavFile.PathOf(dir, t), o.Flag("evict"));
        }

        residency /= trackCount;

        var tracks = new StreamTrack[trackCount];
        for (var t = 0; t < trackCount; t++) {
            tracks[t] = new(t, WavFile.PathOf(dir, t), ringFrames, noCache) { FillPosition = startFrame };
        }

        var mixer = new Mixer(tracks, cycle, startFrame) { AllocateForTest = o.Flag("alloc-in-callback") };
        using var streamer = new DiskStreamer(
            tracks,
            o.Flag("naive-cycle") ? Cycle.Off : cycle, // self-test: a streamer that ignores the cycle
            readFrames,
            threadCount
        );
        using IClock clock = o.Text("clock") == "device"
            ? new DeviceClock(mixer, o.Text("device"), bufferFrames)
            : new NullClock(mixer, bufferFrames, !o.Flag("no-rt"));

        // Locate: start the disk threads and wait until every ring is full, as a transport would before rolling.
        var prime = Stopwatch.StartNew();
        streamer.Start();
        var primed = false;
        while (prime.Elapsed < TimeSpan.FromSeconds(60) && !primed) {
            Thread.Sleep(1);
            primed = true;
            foreach (var t in tracks) {
                primed &= t.Ring.Free < readFrames;
            }
        }

        var primeMs = prime.Elapsed.TotalMilliseconds;

        using var hog = o.Flag("io-hog") ? new IoHog(Directory.GetFiles(dir, "track*.wav")) : null;
        clock.Start();
        Thread.Sleep(TimeSpan.FromSeconds(o.Double("warmup", 5)));
        streamer.ResetStats();
        clock.ResetStats();
        mixer.StartMeasuring();
        var process = Process.GetCurrentProcess();
        var cpu0 = process.TotalProcessorTime;
        var bytes0 = streamer.BytesRead;
        var reads0 = streamer.Reads;
        var hog0 = hog?.Bytes ?? 0;
        var gc0 = GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2);
        var wall = Stopwatch.StartNew();
        Thread.Sleep(TimeSpan.FromSeconds(o.Double("seconds", 40)));
        mixer.StopMeasuring();
        var seconds = wall.Elapsed.TotalSeconds;
        process.Refresh();
        var cpu = (process.TotalProcessorTime - cpu0).TotalSeconds / seconds;
        var bytes = streamer.BytesRead - bytes0;
        var reads = streamer.Reads - reads0;
        var hogBytes = (hog?.Bytes ?? 0) - hog0;
        var gcs = GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2) - gc0;
        var late = clock.Late;
        var nativeMax = clock.NativeMaxPercent;
        clock.Stop();

        var periodUs = bufferFrames * 1e6 / WavFile.SampleRate;
        var time = mixer.CallbackTime;
        var latency = streamer.ReadLatency();
        var result = new RunResult(
            o.Text("label") ?? "unnamed",
            clock.Name,
            trackCount,
            clock.BufferFrames,
            cycle.Enabled ? (int)((cycle.End - cycle.Start) / barFrames) : 0,
            noCache,
            residency * 100,
            ringFrames / (double)WavFile.SampleRate,
            readFrames * WavFile.BytesPerFrame / 1024,
            threadCount,
            seconds,
            primeMs,
            mixer.Callbacks,
            mixer.Underruns,
            mixer.UnderrunFrames,
            mixer.PositionErrors,
            mixer.CycleJumps,
            mixer.LowWaterFrames == int.MaxValue ? 0 : mixer.LowWaterFrames * 1000.0 / WavFile.SampleRate,
            time.Percentile(0.5),
            time.Percentile(0.99),
            time.Percentile(0.9999),
            time.MaxMicroseconds,
            time.MaxMicroseconds / periodUs * 100,
            nativeMax,
            late,
            mixer.AllocatedBytes,
            gcs,
            bytes / 1e6 / seconds,
            trackCount * WavFile.SampleRate * WavFile.BytesPerFrame / 1e6,
            reads / seconds,
            latency.Percentile(0.5) / 1000,
            latency.Percentile(0.99) / 1000,
            latency.MaxMicroseconds / 1000,
            hogBytes / 1e6 / seconds,
            cpu * 100,
            mixer.BusyTicks / (double)Stopwatch.Frequency / seconds * 100,
            GCSettings.IsServerGC ? "server" : "workstation",
            Environment.GetEnvironmentVariable("DOTNET_GCgen0size") ?? "default"
        );

        hog?.Dispose();
        streamer.Dispose(); // stop the disk threads before closing their files
        foreach (var t in tracks) {
            t.Dispose();
        }

        Console.WriteLine(result.ToConsole());
        if (o.Text("results") is { } path) {
            File.AppendAllText(
                path,
                JsonSerializer.Serialize(result, ResultJsonContext.Default.RunResult) + Environment.NewLine
            );
        }

        return result.Underruns == 0 && result.PositionErrors == 0 && result.CallbackAllocatedBytes == 0 ? 0 : 1;
    }

    /// <summary>Reads a whole file through the page cache, so the run starts warm.</summary>
    static void Prewarm(string path, byte[] scratch) {
        using var handle = File.OpenHandle(path);
        long offset = 0;
        int n;
        while ((n = RandomAccess.Read(handle, scratch, offset)) > 0) {
            offset += n;
        }
    }

    /// <summary>Page-cache residency of the files, and optionally an attempt to drop them from it.</summary>
    public static int Cache(Options o) {
        var dir = Path.GetFullPath(o.Text("dir") ?? "artifacts/spike-audio");
        var tracks = o.Int("tracks", 128);
        var residency = 0.0;
        for (var t = 0; t < tracks; t++) {
            residency += Posix.Residency(WavFile.PathOf(dir, t), o.Flag("evict"));
        }

        Console.WriteLine($"resident in page cache: {residency / tracks * 100:0.0} % of {tracks} files");
        return 0;
    }

    public static int Devices() {
        if (!NativeAudio.IsAvailable) {
            Console.WriteLine("no audio backend (build engine/native with native/build.sh)");
            return 1;
        }

        foreach (var d in NativeAudio.Devices()) {
            Console.WriteLine($"{d.Uid}  {d.Name}  out {d.OutputChannels}  {d.SampleRate} Hz");
        }

        return 0;
    }
}

sealed record RunResult(
    string Label,
    string Clock,
    int Tracks,
    int Buffer,
    int CycleBars,
    bool NoCache,
    double ResidentPercent,
    double RingSeconds,
    int ReadKb,
    int DiskThreads,
    double Seconds,
    double PrimeMs,
    long Callbacks,
    long Underruns,
    long UnderrunFrames,
    long PositionErrors,
    long CycleJumps,
    double LowWaterMs,
    double CallbackP50Us,
    double CallbackP99Us,
    double CallbackP9999Us,
    double CallbackMaxUs,
    double CallbackMaxPercent,
    double NativeMaxPercent,
    long LateOrOverloads,
    long CallbackAllocatedBytes,
    int Gcs,
    double DiskMBps,
    double NeededMBps,
    double ReadsPerSecond,
    double ReadP50Ms,
    double ReadP99Ms,
    double ReadMaxMs,
    double IoHogMBps,
    double ProcessCpuPercent,
    double CallbackCpuPercent,
    string Gc,
    string Gen0Size
) {
    public string ToConsole() =>
        $"{Label,-36} {Clock} {Tracks,3} tr  buf {Buffer}  cycle {CycleBars} bars  resident {ResidentPercent:0}%  "
        + $"prime {PrimeMs:0} ms  callbacks {Callbacks}  underruns {Underruns}  pos errors {PositionErrors}  "
        + $"jumps {CycleJumps}  low water {LowWaterMs:0} ms  callback p50 {CallbackP50Us:0.0} p99 {CallbackP99Us:0.0} "
        + $"max {CallbackMaxUs:0.0} µs ({CallbackMaxPercent:0.0}%)  native max {NativeMaxPercent:0.0}%  late {LateOrOverloads}  "
        + $"alloc {CallbackAllocatedBytes} B  GCs {Gcs}  disk {DiskMBps:0.0}/{NeededMBps:0.0} MB/s  "
        + $"{ReadsPerSecond:0} reads/s  read p50 {ReadP50Ms:0.00} p99 {ReadP99Ms:0.00} max {ReadMaxMs:0.0} ms  "
        + (IoHogMBps > 0 ? $"io hog {IoHogMBps:0} MB/s  " : string.Empty)
        + $"CPU {ProcessCpuPercent:0.0}% (callback {CallbackCpuPercent:0.0}%)";
}

[JsonSerializable(typeof(RunResult))]
sealed partial class ResultJsonContext : JsonSerializerContext;
