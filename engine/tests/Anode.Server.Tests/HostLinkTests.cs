using Anode.Contracts;
using Anode.Ipc;
using Anode.Ipc.Segments;
using Anode.Model;
using Anode.Server.Engine;

namespace Anode.Server.Tests;

/// <summary>
///     What the server sends a host over the socket (docs/02-phase-1.md, "Model swaps"), against
///     <see cref="FakeHost" /> listening as anode-host does: the project whole on connect, then only what changed, and
///     everything again after the host restarts.
/// </summary>
public sealed class HostLinkTests : IAsyncDisposable {
    readonly string socketPath = Path.Combine(Path.GetTempPath(), $"an-{Guid.NewGuid().ToString("n")[..8]}.sock");
    readonly string media = Directory.CreateTempSubdirectory("anode-link-").FullName;
    FakeHost? host;

    public async ValueTask DisposeAsync() {
        if (host is not null) {
            await host.DisposeAsync();
        }

        Directory.Delete(media, true);
    }

    [Fact]
    public async Task The_host_gets_the_project_whole_then_only_what_changes() {
        StartHost();
        await using var server = new ServerFixture(socketPath);
        using var client = server.CreateClient();
        await WaitConnectedAsync(server, client);

        await client.CreateProjectAsync();
        var opened = await WaitForAsync<LoadModelMessage>(static m => m.Replace);
        Assert.Empty(opened.Tracks);
        await WaitForAsync<SetCycleMessage>();
        await WaitForAsync<SetMetronomeMessage>();
        await WaitForAsync<SetStripMessage>(static m => m.TrackId == -1);

        var file = await client.ImportAsync(TestWav.Write(media, "Kick.wav", frames: 48_000));
        var withFile = await WaitForAsync<LoadModelMessage>(static m => m.Files.Count > 0);
        Assert.False(withFile.Replace);
        Assert.EndsWith("Kick.wav", Assert.Single(withFile.Files).Path, StringComparison.Ordinal);

        await client.EditAsync(new AddTrackCommand("A"), new AddTrackCommand("B", Stereo: true));
        var tracks = (await client.ProjectAsync()).Tracks;
        await client.EditAsync(new AddRegionCommand(tracks[0].Id, file.Id, 960));
        LoadModelMessage[] Patches() => [..Received<LoadModelMessage>().SkipWhile(m => m != withFile).Skip(1)];
        await ServerFixture.Eventually(() => Task.FromResult(Patches().Length == 3));
        var patches = Patches();
        Assert.Equal(3, host!.Model.Tracks.Count);
        Assert.All(patches, static m => Assert.False(m.Replace));
        Assert.Equal([0, 1, 0], patches.Select(static m => Assert.Single(m.Tracks).Id));
        Assert.True(patches[1].Tracks[0].Stereo);

        await client.EditAsync(new SetStripCommand(tracks[1].Id, -6, -1));
        var strip = await WaitForAsync<SetStripMessage>(static m => m.TrackId == 1);
        Assert.Equal((Units.DbToGain(-6), -1f), (strip.Strip.Gain, strip.Strip.Pan));

        await client.EditAsync(new SetTrackCommand(tracks[0].Id, RecordArmed: true, Input: new(3, true)));
        var io = await WaitForAsync<SetTrackIoMessage>();
        Assert.Equal((0, 2, true, true), (io.TrackId, io.InputChannel, io.Stereo, io.RecordArmed));

        await client.EditAsync(new SetCycleCommand(true, 960 * 4, 960 * 8));
        var cycle = await WaitForAsync<SetCycleMessage>(static m => m.Enabled);
        Assert.Equal((96_000L, 192_000L), (cycle.StartSample, cycle.EndSample));
        await client.EditAsync(new SetMetronomeCommand(true, LevelDb: 0));
        Assert.Equal(1f, (await WaitForAsync<SetMetronomeMessage>(static m => m.Enabled)).Gain);
        await client.EditAsync(new SetMetronomeCommand(CountIn: true));
        Assert.True((await WaitForAsync<SetMetronomeMessage>(static m => m.CountIn)).Enabled);

        var before = Received<LoadModelMessage>().Count(static m => m.Replace);
        await client.EditAsync(new SetTempoCommand(60));
        await ServerFixture.Eventually(() =>
            Task.FromResult(Received<LoadModelMessage>().Count(static m => m.Replace) == before + 1)
        );

        await client.EditAsync(new RemoveTrackCommand(tracks[0].Id));
        var removed = await WaitForAsync<LoadModelMessage>(static m => m is { Replace: true, Tracks.Count: 1 });
        Assert.Equal(1, removed.Tracks[0].Id);
        await ServerFixture.Eventually(() =>
            Task.FromResult(host!.Model.Tracks.SequenceEqual([TrackEntry.MetronomeTrack, 1]))
        );

        await client.PostAsync("/api/v1/project/close", null, TestContext.Current.CancellationToken);
        await WaitForAsync<TransportMessage>(static m => m.Action == TransportAction.Stop);
        await ServerFixture.Eventually(() =>
            Task.FromResult(host!.Model.Tracks.SequenceEqual([TrackEntry.MetronomeTrack]))
        );
    }

    [Fact]
    public async Task A_restarted_host_gets_everything_again() {
        StartHost();
        await using var server = new ServerFixture(socketPath);
        using var client = server.CreateClient();
        await WaitConnectedAsync(server, client);
        await client.CreateProjectAsync();
        await client.EditAsync(new AddTrackCommand(), new SetStripCommand(VolumeDb: -12));
        var track = (await client.ProjectAsync()).Tracks[0];
        await client.EditAsync(
            new SetStripCommand(track.Id, Mute: true),
            new SetTrackCommand(track.Id, RecordArmed: true)
        );
        await WaitForAsync<SetStripMessage>(static m => m.TrackId == 0);

        await host!.DisposeAsync();
        host = null;
        await ServerFixture.Eventually(async () =>
            (await server.SystemAsync(client)).Engine.State == EngineState.Disconnected
        );
        StartHost();
        await WaitConnectedAsync(server, client);

        var load = await WaitForAsync<LoadModelMessage>(static m => m.Replace);
        var engineTrack = Assert.Single(load.Tracks);
        Assert.Equal((0, true, true), (engineTrack.Id, engineTrack.Strip.Mute, engineTrack.RecordArmed));
        Assert.Equal(Units.DbToGain(-12), load.Master.Gain);
        Assert.EndsWith("Audio Files", load.RecordDirectory, StringComparison.Ordinal);
        Assert.Equal(
            Units.DbToGain(-12),
            (await WaitForAsync<SetStripMessage>(static m => m.TrackId == -1)).Strip.Gain
        );
        await WaitForAsync<SetCycleMessage>();
        await WaitForAsync<SetMetronomeMessage>();
        Assert.Equal([TrackEntry.MetronomeTrack, 0], host!.Model.Tracks);

        // Transport through the new link, in order after the sync.
        await client.TransportAsync(TransportCommand.Play, 0);
        await WaitForAsync<TransportMessage>(static m => m.Action == TransportAction.Play);
    }

    void StartHost() {
        host = new(EngineConnection.Version);
        host.Listen(socketPath);
    }

    static Task WaitConnectedAsync(ServerFixture server, HttpClient client) =>
        ServerFixture.Eventually(async () => (await server.SystemAsync(client)).Engine.State
            == EngineState.Connected
        );

    IReadOnlyList<T> Received<T>() where T : IpcMessage => [..host!.Received.OfType<T>()];

    async Task<T> WaitForAsync<T>(Func<T, bool>? match = null) where T : IpcMessage {
        T? found = null;
        await ServerFixture.Eventually(
            () => {
                found = host!.Received.OfType<T>().LastOrDefault(m => match?.Invoke(m) ?? true);
                return Task.FromResult(found is not null);
            },
            10
        );
        return found!;
    }
}
