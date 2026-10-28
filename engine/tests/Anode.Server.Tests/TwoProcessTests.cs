using Anode.Contracts;
using System.Diagnostics;
using System.Net;
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

    [Fact]
    public async Task Server_opens_imports_plays_and_records_on_the_null_device() {
        var media = Directory.CreateTempSubdirectory("anode-two-process-").FullName;
        try {
            StartHost();
            await using var server = new ServerFixture(socketPath);
            using var client = server.CreateClient();
            await using var events = await EventStream.ConnectAsync(server);
            await ServerFixture.Eventually(
                async () => (await server.SystemAsync(client)).Engine.State == EngineState.Connected,
                60
            );

            var project = await client.CreateProjectAsync("Two Process");
            var file = await client.ImportAsync(TestWav.Write(media, "Loop.wav", frames: 96_000));
            await client.EditAsync(new AddTrackCommand("Playback"), new AddTrackCommand("Record"));
            var tracks = (await client.ProjectAsync()).Tracks;
            await client.EditAsync(
                new AddRegionCommand(tracks[0].Id, file.Id, 0),
                new SetTrackCommand(tracks[1].Id, RecordArmed: true, Input: new(1, false))
            );

            await client.TransportAsync(TransportCommand.Play, 0);
            await events.WaitAsync<TransportEvent>(
                static e => e.Transport is { State: TransportState.Playing, PositionSamples: > 24_000 },
                30
            );
            await events.WaitAsync<MetersEvent>(
                e => e.Tracks.Any(t => t.TrackId == tracks[0].Id && t.PeakLeft > 0),
                30
            );
            await client.TransportAsync(TransportCommand.Stop);
            await events.WaitAsync<TransportEvent>(static e => e.Transport.State == TransportState.Stopped, 30);

            await client.TransportAsync(TransportCommand.Record, 960 * 4);
            await events.WaitAsync<TransportEvent>(
                static e => e.Transport is { State: TransportState.Recording, PositionSamples: > 120_000 },
                30
            );
            await client.TransportAsync(TransportCommand.Stop);
            var recorded = await events.WaitAsync<ProjectChangedEvent>(
                e => e.Delta.Ops.OfType<RegionUpserted>().Any(r => r.Region.TrackId == tracks[1].Id),
                30
            );
            var take = recorded.Delta.Ops.OfType<AudioFileUpserted>().Single().File;
            Assert.Equal(24, take.BitDepth);
            Assert.True(File.Exists(Path.Combine(project.Path, take.Path)));
            Assert.Equal(960 * 4, recorded.Delta.Ops.OfType<RegionUpserted>().Single().Region.StartTick);
        } finally {
            Directory.Delete(media, true);
        }
    }

    [Fact]
    public async Task Cycle_records_takes_punches_in_and_captures_a_flashback_on_the_real_host() {
        // The null device's inputs carry a tone, so every recording has material.
        StartHost("--input-tone");
        await using var server = new ServerFixture(socketPath);
        using var client = server.CreateClient();
        await using var events = await EventStream.ConnectAsync(server);
        await ServerFixture.Eventually(
            async () => (await server.SystemAsync(client)).Engine.State == EngineState.Connected,
            60
        );
        await client.CreateProjectAsync("Phase 3", 48_000);
        await client.EditAsync(new AddTrackCommand("Vocal"));
        var track = (await client.ProjectAsync()).Tracks.Single();
        const long Bar = 960 * 4; // 2 s at 120 BPM: 96,000 samples
        await client.EditAsync(
            new SetTrackCommand(track.Id, RecordArmed: true, Input: new(1, false)),
            new SetCycleCommand(true, 0, Bar)
        );

        // Cycle recording: about two and a half passes make a take folder of three takes, comped on the last.
        await client.TransportAsync(TransportCommand.Record, 0);
        await events.WaitAsync<TransportEvent>(static e => e.Transport.State == TransportState.Recording, 30);
        await Task.Delay(5_200, TestContext.Current.CancellationToken);
        await client.TransportAsync(TransportCommand.Stop);
        var cycled = await events.WaitAsync<ProjectChangedEvent>(
            static e => e.Delta.Ops.OfType<TakeFolderUpserted>().Any(),
            30
        );
        var folder = cycled.Delta.Ops.OfType<TakeFolderUpserted>().Single().Folder;
        Assert.InRange(folder.Takes.Count, 2, 4);
        Assert.Equal((0L, Bar), (folder.StartTick, folder.LengthTicks));
        Assert.Equal(folder.Takes[^1].Id, Assert.Single(folder.Comp).TakeId);

        // Autopunch: recording from bar 1 keeps only bar 3.
        await client.EditAsync(
            new SetCycleCommand(false),
            new SetRecordingCommand(Autopunch: true, PunchInTick: 2 * Bar, PunchOutTick: 3 * Bar)
        );
        await client.TransportAsync(TransportCommand.Record, 0);
        await events.WaitAsync<TransportEvent>(
            static e => e.Transport is { State: TransportState.Recording, PositionTick: > 3 * Bar + 480 },
            30
        );
        await client.TransportAsync(TransportCommand.Stop);
        var punched = await events.WaitAsync<ProjectChangedEvent>(
            static e => e.Delta.Ops.OfType<RegionUpserted>().Any(),
            30
        );
        var region = punched.Delta.Ops.OfType<RegionUpserted>().Single().Region;
        // Placed on the first tick at or after the recorded sample: within a tick (25 frames) of exact.
        Assert.Equal(2 * Bar, region.StartTick);
        Assert.InRange(region.Length, 96_000 - 25, 96_000);

        // Flashback Capture: what the armed track heard while playing, without recording.
        await client.EditAsync(new SetRecordingCommand(Autopunch: false));
        await client.TransportAsync(TransportCommand.Play, 4 * Bar);
        await events.WaitAsync<TransportEvent>(
            static e => e.Transport is { State: TransportState.Playing, PositionTick: > 4 * Bar + 960 },
            30
        );
        Assert.Equal(
            HttpStatusCode.Accepted,
            (await client.PostAsync("/api/v1/transport/capture", null, TestContext.Current.CancellationToken))
            .StatusCode
        );
        var captured = await events.WaitAsync<ProjectChangedEvent>(
            static e => e.Delta.Ops.OfType<RegionUpserted>().Any(r => r.Region.StartTick >= 4 * Bar),
            30
        );
        await client.TransportAsync(TransportCommand.Stop);
        var flashback = captured.Delta.Ops.OfType<RegionUpserted>().Single().Region;
        Assert.Equal(track.Id, flashback.TrackId);
        Assert.InRange(flashback.StartTick, 4 * Bar, 4 * Bar + 1);
        Assert.True(flashback.Length > 24_000, $"Captured {flashback.Length} frames.");
    }

    void StartHost(params string[] extra) {
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
            StartInfo = new("dotnet", ["exec", dll, "--serve", "--null-audio", "--socket", socketPath, ..extra]) {
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
