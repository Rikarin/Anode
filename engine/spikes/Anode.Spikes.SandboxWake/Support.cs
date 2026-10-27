using System.Globalization;
using System.Text.Json;

namespace Anode.Spikes.SandboxWake;

/// <summary><c>--name value</c> pairs.</summary>
sealed class Options {
    readonly Dictionary<string, string> values = new(StringComparer.Ordinal);

    public static Options Parse(string[] args) {
        var o = new Options();
        for (var i = 0; i < args.Length; i++) {
            if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length) {
                o.values[args[i][2..]] = args[++i];
            }
        }

        return o;
    }

    public string? Text(string name) => values.TryGetValue(name, out var v) ? v : null;

    public int Int(string name, int fallback) =>
        values.TryGetValue(name, out var v) ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;

    public double Double(string name, double fallback) =>
        values.TryGetValue(name, out var v) ? double.Parse(v, CultureInfo.InvariantCulture) : fallback;
}

/// <summary>
///     Background load at default priority: each thread alternates arithmetic with streaming through 16 MB, so it
///     competes for cores, caches and memory bandwidth.
/// </summary>
static class Load {
    public static int Run(int threads, double seconds) {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        var workers = new Thread[threads];
        for (var t = 0; t < threads; t++) {
            workers[t] = new Thread(() => {
                    var a = new long[2 * 1024 * 1024];
                    var b = new long[a.Length];
                    long acc = 1;
                    while (DateTime.UtcNow < until) {
                        for (var i = 0; i < 2_000_000; i++) {
                            acc = acc * 6364136223846793005L + 1442695040888963407L;
                        }

                        Array.Copy(a, b, a.Length);
                        a[acc & (a.Length - 1)] = acc;
                    }

                    GC.KeepAlive(b);
                }
            ) { IsBackground = true };
            workers[t].Start();
        }

        foreach (var w in workers) {
            w.Join();
        }

        return 0;
    }
}

/// <summary>Turns result lines into the markdown rows of docs/spikes/S4-sandbox-wake.md.</summary>
static class Summary {
    public const string Head =
        "| Label | Clock | Frames | Kind | Load (load avg) | K | N | Burn µs | Wake p50/p99/p99.9/max µs | All done p50/p99/p99.9/max µs | Callback max µs | Misses / sandbox cycles | Missed cb | Host CPU % | Sandbox CPU % | Driver overloads |\n"
        + "|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|";

    public static int Print(string path) {
        Console.WriteLine(Head);
        foreach (var line in File.ReadLines(path)) {
            if (line.Length > 0) {
                using var doc = JsonDocument.Parse(line);
                Console.WriteLine(Row(doc.RootElement));
            }
        }

        return 0;
    }

    public static string Row(JsonElement r) {
        string N(string name, string format = "0.#") =>
            r.GetProperty(name).GetDouble().ToString(format, CultureInfo.InvariantCulture);

        string Q(string name) => $"{N(name + "P50")} / {N(name + "P99")} / {N(name + "P999")} / {N(name + "Max")}";
        var kind = r.GetProperty("kind").GetString();
        var spin = r.GetProperty("hostSpinUs").GetDouble() + r.GetProperty("sandboxSpinUs").GetDouble() > 0
            ? $"+spin {N("hostSpinUs", "0")}/{N("sandboxSpinUs", "0")}"
            : string.Empty;
        var rt = r.GetProperty("rt").GetBoolean()
            ? r.GetProperty("rtStatus").GetInt32() == 0 ? string.Empty : " (rt refused)"
            : " (no rt)";
        if (r.TryGetProperty("workgroup", out var wg) && wg.GetBoolean()) {
            rt += r.GetProperty("workgroupStatus").GetInt32() == 0 ? " +wg" : " (wg refused)";
        }

        var clock = r.GetProperty("clock").GetString() == "device" ? "device" : "null";
        var callbacks = r.GetProperty("callbacks").GetInt64();
        var drv = clock == "device"
            ? r.GetProperty("driverOverloads").GetInt64().ToString(CultureInfo.InvariantCulture)
            : "—";
        return $"| {r.GetProperty("label").GetString()} | {clock} | {N("frames", "0")} | {kind}{spin}{rt} | {r.GetProperty("load").GetString()} ({N("loadAverage", "0")}) "
            + $"| {N("sandboxes", "0")} | {N("instances", "0")} | {N("burnUs", "0")} | {Q("wake")} | {Q("allDone")} | {N("callbackMax")} "
            + $"| {r.GetProperty("misses").GetInt64()} / {callbacks * r.GetProperty("sandboxes").GetInt64()} | {r.GetProperty("missedCallbacks").GetInt64()} "
            + $"| {N("hostCpuPct", "0")} | {N("sandboxCpuPct", "0")} | {drv} |";
    }
}
