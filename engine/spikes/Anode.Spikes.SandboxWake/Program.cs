using Anode.Audio.Native;
using Anode.Spikes.SandboxWake;
using System.Globalization;
using System.Text;
using System.Text.Json;

// Spike S4 (docs/spikes/S4-sandbox-wake.md). Roles:
//   host       --clock device|null [--device uid] --frames 64 --kind address|semaphore|spin --sandboxes K
//              --instances N [--biquads 4] [--burn-us 0] [--host-spin-us 0] [--sandbox-spin-us 0]
//              [--deadline-pct 80] [--rt 1] [--workgroup 0] [--warmup 3] [--seconds 15] [--label x] [--load x] [--results f.jsonl]
//   sandbox    --shm name --index k        (spawned by the host)
//   load       --threads N --seconds S     (background CPU and memory load)
//   devices
//   summarize  results.jsonl               (markdown table)

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
var role = args.Length > 0 ? args[0] : "help";
var options = Options.Parse([..args.Skip(1)]);
try {
    return role switch {
        "host" => RunHost(),
        "sandbox" => Sandbox.Run(options.Text("shm")!, options.Int("index", 0)),
        "load" => Load.Run(options.Int("threads", Environment.ProcessorCount), options.Double("seconds", 30)),
        "devices" => Devices(),
        "summarize" => Summary.Print(args[1]),
        _ => Usage()
    };
} catch (Exception e) when (e is InvalidOperationException or ArgumentException or DllNotFoundException) {
    Console.Error.WriteLine($"error: {e.Message}");
    return 1;
}

static int Usage() {
    Console.Error.WriteLine("usage: anode-s4 host|sandbox|load|devices|summarize ... (see Program.cs)");
    return 2;
}

static int Devices() {
    foreach (var d in NativeAudio.Devices()) {
        Console.WriteLine($"{d.Uid}  {d.Name}  out {d.OutputChannels}  {d.SampleRate} Hz");
    }

    return 0;
}

int RunHost() {
    var o = new HostOptions(
        options.Text("clock") ?? "null",
        options.Text("device"),
        options.Int("frames", 64),
        options.Double("rate", 48000),
        IpcWake.ParseKind(options.Text("kind") ?? "address"),
        options.Int("sandboxes", 1),
        options.Int("instances", 1),
        options.Int("biquads", 4),
        (int)(options.Double("burn-us", 0) * 1000),
        (long)(options.Double("host-spin-us", 0) * 1000),
        (long)(options.Double("sandbox-spin-us", 0) * 1000),
        options.Double("deadline-pct", 80),
        options.Int("rt", 1) != 0,
        options.Int("workgroup", 0) != 0,
        options.Double("warmup", 3),
        options.Double("seconds", 15),
        options.Text("label") ?? string.Empty,
        options.Text("load") ?? "idle",
        options.Text("results")
    );
    if (IpcWake.Supported(o.Kind) == 0) {
        throw new InvalidOperationException($"wake kind {IpcWake.KindName(o.Kind)} is not supported here");
    }

    using var host = new Host(o);
    var rate = o.SampleRate;
    nint stream = 0;
    Thread? clock = null;
    if (o.Clock == "device") {
        stream = host.OpenDevice(out var config);
        if (config.BufferFrames != o.Frames) {
            Console.Error.WriteLine($"warning: device gave {config.BufferFrames} frames");
        }

        rate = config.SampleRate;
        if (CthAudio.Start(stream) != 0) {
            throw new InvalidOperationException(CthAudio.LastErrorMessage);
        }
    } else {
        clock = host.StartNullClock();
    }

    Thread.Sleep(TimeSpan.FromSeconds(o.Warmup));
    var pids = host.SandboxPids.ToArray();
    var hostCpu0 = IpcWake.ProcessCpuNs(Environment.ProcessId);
    var sandboxCpu0 = pids.Sum(static p => IpcWake.ProcessCpuNs(p));
    var wall0 = IpcWake.Now();
    long overloads0 = 0;
    if (stream != 0) {
        CthAudio.ResetTiming(stream);
        CthAudio.Stats(stream, out var before);
        overloads0 = (long)before.Overloads;
    }

    host.StartMeasuring();
    Thread.Sleep(TimeSpan.FromSeconds(o.Seconds));
    host.StopMeasuring();
    var wall = IpcWake.Now() - wall0;
    var hostCpu = IpcWake.ProcessCpuNs(Environment.ProcessId) - hostCpu0;
    var sandboxCpu = pids.Sum(static p => IpcWake.ProcessCpuNs(p)) - sandboxCpu0;
    long overloads = 0;
    double driverMaxPct = 0;
    long driverOverBudget = 0;
    if (stream != 0) {
        CthAudio.Stats(stream, out var after);
        overloads = (long)after.Overloads - overloads0;
        unsafe {
            CthAudio.TimingData timing;
            CthAudio.GetTiming(stream, &timing);
            driverMaxPct = timing.MaxPercent;
            driverOverBudget = (long)timing.OverBudget;
        }

        _ = CthAudio.Stop(stream);
        CthAudio.Close(stream);
    } else {
        host.StopNullClock(clock!);
    }

    var line = Result(o, host, rate, wall, hostCpu, sandboxCpu, overloads, driverMaxPct, driverOverBudget);
    Console.WriteLine(Summary.Row(JsonDocument.Parse(line).RootElement));
    if (o.Results is not null) {
        File.AppendAllText(o.Results, line + "\n");
    }

    return 0;
}

static string Result(
    HostOptions o,
    Host h,
    double rate,
    long wall,
    long hostCpu,
    long sandboxCpu,
    long overloads,
    double driverMaxPct,
    long driverOverBudget
) {
    var sb = new StringBuilder("{");

    void Field(string name, object value) {
        if (sb.Length > 1) {
            sb.Append(',');
        }

        sb.Append('"').Append(name).Append("\":");
        sb.Append(
            value switch {
                string s => "\"" + JsonEncodedText.Encode(s) + "\"",
                double d => d.ToString("0.###", CultureInfo.InvariantCulture),
                bool b => b ? "true" : "false",
                _ => Convert.ToString(value, CultureInfo.InvariantCulture)
            }
        );
    }

    void Percentiles(string name, Histogram hist) {
        Field(name + "P50", hist.PercentileUs(0.5));
        Field(name + "P99", hist.PercentileUs(0.99));
        Field(name + "P999", hist.PercentileUs(0.999));
        Field(name + "Max", hist.MaxNs / 1000.0);
    }

    Field("label", o.Label);
    Field("clock", o.Clock);
    Field("frames", o.Frames);
    Field("rate", rate);
    Field("kind", IpcWake.KindName(o.Kind));
    Field("hostSpinUs", o.HostSpinNs / 1000.0);
    Field("sandboxSpinUs", o.SandboxSpinNs / 1000.0);
    Field("sandboxes", o.Sandboxes);
    Field("instances", o.Instances);
    Field("biquads", o.Biquads);
    Field("burnUs", o.BurnNs / 1000.0);
    Field("rt", o.RealTime);
    Field("rtStatus", h.SandboxRtStatus);
    Field("workgroup", o.Workgroup);
    Field("workgroupStatus", h.SandboxWorkgroupStatus);
    Field("load", o.Load);
    Field("loadAverage", IpcWake.LoadAverage);
    Field("periodUs", h.PeriodNs / 1000.0);
    Field("deadlineUs", h.DeadlineNs / 1000.0);
    Field("seconds", wall / 1e9);
    Field("callbacks", h.Callbacks);
    Field("misses", h.Misses);
    Field("missedCallbacks", h.MissedCallbacks);
    Field("busySkips", h.BusySkips);
    Field("late", h.Late);
    Field("corrupt", h.Corrupt);
    Field("clockOverruns", h.ClockOverruns);
    Percentiles("wake", h.Wake);
    Percentiles("sandboxDone", h.SandboxDone);
    Percentiles("allDone", h.AllDone);
    Percentiles("callback", h.Callback);
    Field("hostCpuPct", 100.0 * hostCpu / wall);
    Field("sandboxCpuPct", 100.0 * sandboxCpu / wall);
    Field("driverOverloads", overloads);
    Field("driverOverBudget", driverOverBudget);
    Field("driverMaxPct", driverMaxPct);
    return sb.Append('}').ToString();
}
