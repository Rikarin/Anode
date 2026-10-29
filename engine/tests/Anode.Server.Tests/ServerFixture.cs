using Anode.Contracts;
using Anode.Server.Projects;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using Xunit.Sdk;

namespace Anode.Server.Tests;

/// <summary>
///     A Server with a throwaway data folder and, by default, an in-process engine. Pass a socket path to talk to a
///     separate anode-host over IPC instead, a token to require one on every request, and an editor folder to serve
///     the editor's web build at the root, and an analyzer to analyse audio in place of <c>Anode.Dsp</c>'s.
/// </summary>
public sealed class ServerFixture(
    string? socketPath = null,
    string? token = null,
    string? editorDirectory = null,
    IAudioAnalyzer? analyzer = null,
    IAudioEditor? editor = null
) : WebApplicationFactory<Program> {
    readonly string dataDirectory = Directory.CreateTempSubdirectory("anode-server-").FullName;

    public async Task<SystemInfo> SystemAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<SystemInfo>("/api/v1/system", TestContext.Current.CancellationToken))!;

    /// <summary>Waits up to <paramref name="seconds" /> for <paramref name="condition" />; an exception counts as "not yet".</summary>
    public static async Task Eventually(Func<Task<bool>> condition, int seconds = 30) {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        Exception? last = null;
        while (DateTime.UtcNow < deadline) {
            try {
                if (await condition()) {
                    return;
                }
            } catch (Exception e) when (e is not XunitException) {
                last = e;
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"Condition not met within {seconds} s.{(last is null ? string.Empty : $" Last error: {last}")}");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) {
        builder.UseSetting("Anode:DataDirectory", dataDirectory);
        builder.UseSetting("Anode:Engine:Mode", socketPath is null ? "inprocess" : "ipc");
        if (token is not null) {
            builder.UseSetting("Anode:Token", token);
        }

        if (socketPath is not null) {
            builder.UseSetting("Anode:Engine:SocketPath", socketPath);
        }

        if (editorDirectory is not null) {
            builder.UseSetting("Anode:EditorDirectory", editorDirectory);
        }

        if (analyzer is not null) {
            builder.ConfigureServices(services => services.AddSingleton(analyzer));
        }

        if (editor is not null) {
            builder.ConfigureServices(services => services.AddSingleton(editor));
        }

        builder.ConfigureLogging(static logging => logging.SetMinimumLevel(LogLevel.Warning));
    }

    public override async ValueTask DisposeAsync() {
        StopJobs();
        await base.DisposeAsync();
    }

    protected override void Dispose(bool disposing) {
        if (disposing) {
            StopJobs();
        }

        base.Dispose(disposing);
        if (disposing) {
            Directory.Delete(dataDirectory, true);
        }
    }

    /// <summary>
    ///     Lets background peak jobs finish before the data folder goes (the host disposes its services only after
    ///     the factory has disposed itself).
    /// </summary>
    void StopJobs() {
        try {
            Services.GetService<PeakJobs>()?.Dispose();
        } catch (ObjectDisposedException) {
            // already shut down
        }
    }
}
