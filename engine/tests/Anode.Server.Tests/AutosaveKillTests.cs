using Anode.Contracts;
using Anode.Model.Storage;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Anode.Server.Tests;

/// <summary>
///     A real Server process killed outright while it autosaves (docs/04-phase-3.md, "Autosave and crash recovery"): no
///     shutdown runs. Every edit renames all the tracks to one generation, so a snapshot that mixed two edits would
///     show; the kill comes the moment a temporary file shows that a write is under way.
/// </summary>
public sealed class AutosaveKillTests : IDisposable {
    const int Tracks = 120;
    const int Rounds = 4;

    readonly string data = Directory.CreateTempSubdirectory("anode-autosave-kill-").FullName;

    static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(data, true);

    [Fact]
    public async Task A_killed_server_leaves_a_whole_snapshot_to_recover_and_the_saved_project_intact() {
        string? folder = null;
        string? savedDocument = null;
        var interrupted = 0;
        for (var round = 0; round < Rounds; round++) {
            using var server = await RunningServer.StartAsync(data);
            if (folder is null) {
                (folder, savedDocument) = await CreateSavedProjectAsync(server.Client);
            } else {
                var offered = Assert.Single(await RecoverableAsync(server.Client));
                Assert.Equal(folder, offered.Path);
                await AssertSameGenerationAsync(server.Client, folder);
                Assert.Equal(savedDocument, Fingerprint(folder));
            }

            using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
            var edits = EditUntilStoppedAsync(server.Client, round, stop.Token);
            var before = ProjectAutosave.Read(folder);
            await WaitForWriteUnderWayAsync(folder, before);

            server.Kill();
            await stop.CancelAsync();
            await edits;

            if (StoppedMidWrite(folder)) {
                interrupted++;
            }
        }

        Assert.True(interrupted > 0, "No kill landed during a write.");

        using var last = await RunningServer.StartAsync(data);
        await AssertSameGenerationAsync(last.Client, folder!);
        Assert.Equal(savedDocument, Fingerprint(folder!));
        (await last.Client.PostAsync("/api/v1/project/save", null, Token)).EnsureSuccessStatusCode();
        Assert.Null(ProjectAutosave.Read(folder!));
        Assert.Equal(Tracks, ProjectPackage.Load(folder!).Tracks.Count);
    }

    static async Task<(string Folder, string Saved)> CreateSavedProjectAsync(HttpClient client) {
        var project = await client.CreateProjectAsync("Killed");
        await client.EditAsync([..Enumerable.Range(0, Tracks).Select(static _ => new AddTrackCommand("g0"))]);
        (await client.PostAsync("/api/v1/project/save", null, Token)).EnsureSuccessStatusCode();
        return (project.Path, Fingerprint(project.Path));
    }

    /// <summary>Renames every track, generation after generation, until stopped or the Server is gone.</summary>
    static async Task EditUntilStoppedAsync(HttpClient client, int round, CancellationToken stop) {
        try {
            var ids = (await client.ProjectAsync()).Tracks.Select(static t => t.Id).ToArray();
            for (var generation = 1; !stop.IsCancellationRequested; generation++) {
                var name = $"g{round}.{generation}";
                await client.EditAsync([..ids.Select(id => new SetTrackCommand(id, name))]);
            }
        } catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException) {
            // killed
        }
    }

    /// <summary>Waits for a snapshot newer than <paramref name="before" /> and then for the next write to start.</summary>
    static async Task WaitForWriteUnderWayAsync(string folder, AutosaveManifest? before) {
        await ServerFixture.Eventually(
            () => Task.FromResult(ProjectAutosave.Read(folder) is { } m && m.Autosaved != before?.Autosaved),
            60
        );
        var autosave = Path.Combine(folder, ProjectPackage.AutosaveFolder);
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(30)) {
            if (TemporaryFiles(autosave).Length > 0) {
                return;
            }

            await Task.Yield();
        }

        Assert.Fail("No autosave write was seen.");
    }

    static string[] TemporaryFiles(string autosave) {
        for (var attempt = 0;; attempt++) {
            try {
                return Directory.GetFiles(autosave, "*.tmp", SearchOption.AllDirectories);
            } catch (DirectoryNotFoundException) when (attempt < 10) {
                // a slot's folder appears while we look
            }
        }
    }

    /// <summary>
    ///     Whether the kill caught a write: what it left in the slot it was filling is not what the snapshot holds, or a
    ///     temporary file remains.
    /// </summary>
    static bool StoppedMidWrite(string folder) {
        var manifest = ProjectAutosave.Read(folder);
        Assert.NotNull(manifest);
        var autosave = Path.Combine(folder, ProjectPackage.AutosaveFolder);
        return TemporaryFiles(autosave).Length > 0
            || Generations(Path.Combine(autosave, manifest.Slot == "a" ? "b" : "a", ProjectPackage.TracksFolder)) > 1;
    }

    static int Generations(string tracks) =>
        Directory.Exists(tracks)
            ? Directory.EnumerateFiles(tracks, "*.json")
                .Select(static f => File.ReadAllText(f))
                .Select(Name)
                .Distinct()
                .Count()
            : 0;

    /// <summary>The first <c>"name"</c> of a track file: the track's.</summary>
    static string Name(string json) {
        const string key = "\"name\": \"";
        var start = json.IndexOf(key, StringComparison.Ordinal) + key.Length;
        return json[start..json.IndexOf('"', start)];
    }

    /// <summary>Recovers the project; all its tracks must come from one edit.</summary>
    static async Task AssertSameGenerationAsync(HttpClient client, string folder) {
        var response = await client.PostAsJsonAsync(
            "/api/v1/projects/recover",
            new(folder),
            ContractsJsonContext.Default.OpenProjectRequest,
            Token
        );
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var project = await client.ProjectAsync();
        Assert.True(project.Dirty);
        Assert.Equal(Tracks, project.Tracks.Count);
        var names = project.Tracks.Select(static t => t.Name).Distinct().ToArray();
        Assert.True(names.Length == 1, $"A snapshot mixed edits: {string.Join(", ", names)}.");
        Assert.NotEqual("g0", names[0]);
    }

    static async Task<IReadOnlyList<RecoverableProject>> RecoverableAsync(HttpClient client) =>
        (await client.GetFromJsonAsync(
            "/api/v1/projects/recoverable",
            ContractsJsonContext.Default.IReadOnlyListRecoverableProject,
            Token
        ))!;

    /// <summary>A hash of the saved document's bytes and every file's name.</summary>
    static string Fingerprint(string folder) {
        var files = Directory.EnumerateFiles(Path.Combine(folder, ProjectPackage.TracksFolder))
            .Append(Path.Combine(folder, ProjectPackage.ProjectFileName))
            .Order(StringComparer.Ordinal);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files) {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetFileName(file)));
            hash.AppendData(File.ReadAllBytes(file));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>An Anode.Server in its own process, as the desktop app runs it, without an engine.</summary>
    sealed class RunningServer : IDisposable {
        readonly Process process;

        RunningServer(Process process, HttpClient client) {
            this.process = process;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<RunningServer> StartAsync(string data) {
            var port = FreePort();
            var start = new ProcessStartInfo("dotnet") {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            };
            foreach (var argument in new[] {
                         typeof(Program).Assembly.Location, $"--urls=http://127.0.0.1:{port}",
                         $"--Anode:DataDirectory={data}", "--Anode:Engine:Mode=inprocess",
                         "--Anode:AutosaveSeconds=0.01"
                     }) {
                start.ArgumentList.Add(argument);
            }

            var process = Process.Start(start)!;
            process.OutputDataReceived += static (_, _) => { };
            process.ErrorDataReceived += static (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            var client = new HttpClient { BaseAddress = new($"http://127.0.0.1:{port}") };
            var server = new RunningServer(process, client);
            await ServerFixture.Eventually(
                async () => {
                    Assert.False(process.HasExited, "The Server exited.");
                    return (await client.GetAsync("/api/v1/system", Token)).IsSuccessStatusCode;
                },
                60
            );
            return server;
        }

        /// <summary>SIGKILL (TerminateProcess on Windows): the process gets no chance to tidy up.</summary>
        public void Kill() {
            process.Kill();
            process.WaitForExit();
        }

        public void Dispose() {
            if (!process.HasExited) {
                Kill();
            }

            process.Dispose();
            Client.Dispose();
        }

        static int FreePort() {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }
}
