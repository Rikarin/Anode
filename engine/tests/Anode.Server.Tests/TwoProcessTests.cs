using Anode.Contracts;
using System.Diagnostics;
using System.Text;

namespace Anode.Server.Tests;

/// <summary>
///     The production layout (docs/adr/0001-process-model.md): anode-host in its own process, the Server talking to it
///     over a Unix domain socket.
/// </summary>
public sealed class TwoProcessTests : IAsyncDisposable {
    readonly StringBuilder hostOutput = new();

    // Unix socket paths are limited to ~104 bytes on macOS.
    readonly string socketPath = Path.Combine(Path.GetTempPath(), $"an-{Guid.NewGuid().ToString("n")[..8]}.sock");

    Process? host;

    public async ValueTask DisposeAsync() {
        await StopHostAsync();
        File.Delete(socketPath);
        lock (hostOutput) {
            TestContext.Current.TestOutputHelper?.WriteLine("anode-host output:\n" + hostOutput);
        }
    }

    [Fact]
    public async Task Server_connects_to_a_separate_host_and_reconnects_after_it_restarts() {
        StartHost();
        await using var server = new ServerFixture(socketPath);
        using var client = server.CreateClient();
        await ServerFixture.Eventually(
            async () => (await server.SystemAsync(client)).Engine.State == EngineState.Connected,
            60
        );
        var engine = (await server.SystemAsync(client)).Engine;
        Assert.Equal("null", engine.Backend);
        Assert.False(string.IsNullOrEmpty(engine.HostVersion));

        await StopHostAsync();
        await ServerFixture.Eventually(async () =>
            (await server.SystemAsync(client)).Engine.State == EngineState.Disconnected
        );
        StartHost();
        await ServerFixture.Eventually(
            async () => (await server.SystemAsync(client)).Engine.State == EngineState.Connected,
            60
        );
    }

    void StartHost() {
        var dll = Path.Combine(
            AppContext.BaseDirectory.Replace(
                Path.Combine("tests", "Anode.Server.Tests"),
                Path.Combine("src", "Anode.Host"),
                StringComparison.Ordinal
            ),
            "anode-host.dll"
        );
        Assert.True(File.Exists(dll), $"anode-host not built at {dll}");
        host = new() {
            StartInfo = new("dotnet", ["exec", dll, "--serve", "--null-audio", "--socket", socketPath]) {
                RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        host.OutputDataReceived += (_, e) => Capture(e.Data);
        host.ErrorDataReceived += (_, e) => Capture(e.Data);
        host.Start();
        host.BeginOutputReadLine();
        host.BeginErrorReadLine();
    }

    void Capture(string? line) {
        if (line is not null) {
            lock (hostOutput) {
                hostOutput.AppendLine(line);
            }
        }
    }

    async Task StopHostAsync() {
        if (host is null) {
            return;
        }

        if (!host.HasExited) {
            host.Kill(true);
        }

        await host.WaitForExitAsync(TestContext.Current.CancellationToken);
        host.Dispose();
        host = null;
    }
}
