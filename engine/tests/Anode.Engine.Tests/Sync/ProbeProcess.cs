using Anode.Audio.Native;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Anode.Engine.Tests;

/// <summary>
///     anode-midiprobe as the golden path drives it: a separate process in serve mode, commands as JSON lines on its
///     stdin, answers as JSON lines on its stdout (engine/tools/Anode.MidiProbe/README.md).
/// </summary>
sealed class ProbeProcess : IAsyncDisposable {
    readonly Process process;
    readonly Dictionary<int, TaskCompletionSource<JsonObject>> waiting = [];
    readonly Lock gate = new();
    readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int ids;

    ProbeProcess(Process process) {
        this.process = process;
        process.OutputDataReceived += (_, e) => Line(e.Data);
        process.ErrorDataReceived += (_, e) => {
            if (e.Data is not null) {
                Log.Add(e.Data);
            }
        };
    }

    /// <summary>Whether the probe can run here (CoreMIDI, the native shim built).</summary>
    public static bool Supported => OperatingSystem.IsMacOS() && NativeMidiBackend.IsAvailable;

    public List<string> Log { get; } = [];

    static string Path =>
        System.IO.Path.Combine(
            AppContext.BaseDirectory.Replace(
                System.IO.Path.Combine("tests", "Anode.Engine.Tests"),
                System.IO.Path.Combine("tools", "Anode.MidiProbe"),
                StringComparison.Ordinal
            ),
            "anode-midiprobe.dll"
        );

    public static async Task<ProbeProcess> StartAsync(CancellationToken cancellationToken) {
        Assert.True(File.Exists(Path), $"anode-midiprobe not built at {Path}");
        var process = new Process {
            StartInfo = new("dotnet", ["exec", Path, "serve"]) {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        var probe = new ProbeProcess(process);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await probe.ready.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        return probe;
    }

    void Line(string? line) {
        if (line is null || JsonNode.Parse(line) is not JsonObject message) {
            return;
        }

        if (message["event"]?.GetValue<string>() == "ready") {
            ready.TrySetResult();
            return;
        }

        if (message["id"]?.GetValue<int>() is { } id) {
            TaskCompletionSource<JsonObject>? answer;
            lock (gate) {
                waiting.Remove(id, out answer);
            }

            answer?.TrySetResult(message);
        }
    }

    /// <summary>Sends a command; returns the answer, which must be ok.</summary>
    public async Task<JsonObject> Command(JsonObject command, int timeoutMs = 20_000) {
        var id = Interlocked.Increment(ref ids);
        command["id"] = id;
        var answer = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate) {
            waiting[id] = answer;
        }

        await process.StandardInput.WriteLineAsync(
            command.ToJsonString().AsMemory(),
            TestContext.Current.CancellationToken
        );
        await process.StandardInput.FlushAsync(TestContext.Current.CancellationToken);
        var result = await answer.Task.WaitAsync(
            TimeSpan.FromMilliseconds(timeoutMs),
            TestContext.Current.CancellationToken
        );
        Assert.True(result["ok"]?.GetValue<bool>() == true, $"{command.ToJsonString()} → {result.ToJsonString()}");
        return result;
    }

    public Task<JsonObject> Command(string cmd, int timeoutMs = 20_000) =>
        Command(new JsonObject { ["cmd"] = cmd }, timeoutMs);

    /// <summary>The hex of every message received, in order.</summary>
    public async Task<List<string>> Received(bool clear = false) {
        var answer = await Command(new JsonObject { ["cmd"] = "received", ["clear"] = clear });
        return [..answer["messages"]!.AsArray().Select(static m => m!["hex"]!.GetValue<string>())];
    }

    public async ValueTask DisposeAsync() {
        try {
            await Command("quit", 5_000);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        } catch (Exception e) when (e is TimeoutException or InvalidOperationException or IOException) {
            // killed below
        }

        if (!process.HasExited) {
            process.Kill(true);
        }

        process.Dispose();
    }
}
