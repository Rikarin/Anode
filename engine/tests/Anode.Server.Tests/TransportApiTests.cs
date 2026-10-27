using Anode.Contracts;
using System.Net;
using System.Net.Http.Json;

namespace Anode.Server.Tests;

/// <summary>The transport, its telemetry on <c>/ws</c>, and recording (against the in-process host).</summary>
public sealed class TransportApiTests {
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Playing_streams_the_playhead_and_stopping_sends_one_last_frame() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await using var events = await EventStream.ConnectAsync(server);
        var initial = await client.GetFromJsonAsync(
            "/api/v1/transport",
            ContractsJsonContext.Default.TransportStatus,
            Token
        );
        Assert.Equal(TransportState.Stopped, initial!.State);
        await client.CreateProjectAsync();
        await ServerFixture.Eventually(async () =>
            (await server.SystemAsync(client)).Engine.State == EngineState.InProcess
        );

        var playing = await client.TransportAsync(TransportCommand.Play, 960 * 4);
        Assert.Equal(
            (TransportState.Playing, 96_000L, 3840L),
            (playing.State, playing.PositionSamples, playing.PositionTick)
        );
        var moving = await events.WaitAsync<TransportEvent>(static e => e.Transport.PositionSamples > 96_000 + 4_800);
        Assert.Equal(TransportState.Playing, moving.Transport.State);
        Assert.InRange(moving.Transport.PositionTick, 3840, 3840 + 960 * 4);

        await client.TransportAsync(TransportCommand.Stop);
        var stopped = await events.WaitAsync<TransportEvent>(static e => e.Transport.State == TransportState.Stopped);
        await Task.Delay(300, Token);
        Assert.Single(events.OfType<TransportEvent>().SkipWhile(e => e != stopped));
        var status = await client.GetFromJsonAsync(
            "/api/v1/transport",
            ContractsJsonContext.Default.TransportStatus,
            Token
        );
        Assert.Equal(stopped.Transport.PositionSamples, status!.PositionSamples);

        await client.TransportAsync(TransportCommand.Locate, 0);
        await events.WaitAsync<TransportEvent>(static e => e.Transport is {
                State: TransportState.Stopped, PositionSamples: 0
            }
        );
    }

    [Fact]
    public async Task Transport_requests_that_cannot_be_carried_out_are_422() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await Assert422(client, new(TransportCommand.Play));
        await client.CreateProjectAsync();
        await Assert422(client, new(TransportCommand.Record));
        await Assert422(client, new(TransportCommand.Locate));
        await Assert422(client, new(TransportCommand.Play, -5));
    }

    [Fact]
    public async Task A_recording_becomes_regions_in_one_undoable_Record_step() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await using var events = await EventStream.ConnectAsync(server);
        var project = await client.CreateProjectAsync();
        await client.EditAsync(
            new AddTrackCommand("Vocal"),
            new AddTrackCommand("Guitar", Stereo: true),
            new AddTrackCommand("Idle")
        );
        var tracks = (await client.ProjectAsync()).Tracks;
        await client.EditAsync(
            new SetTrackCommand(tracks[0].Id, RecordArmed: true),
            new SetTrackCommand(tracks[1].Id, RecordArmed: true)
        );
        await ServerFixture.Eventually(async () =>
            (await server.SystemAsync(client)).Engine.State == EngineState.InProcess
        );

        await client.TransportAsync(TransportCommand.Record, 960 * 8);
        await events.WaitAsync<TransportEvent>(static e => e.Transport is {
                State: TransportState.Recording, PositionSamples: > 200_000
            }
        );
        var meters = await events.WaitAsync<MetersEvent>(static e => e.Tracks.Any(static t => t.PeakLeft > 0));
        Assert.Contains(meters.Tracks, t => t.TrackId == tracks[0].Id && t.PeakLeft > 0);
        await client.TransportAsync(TransportCommand.Stop);

        var recorded = await events.WaitAsync<ProjectChangedEvent>(static e => e.Delta.Ops.Any(static o =>
                o is RegionUpserted
            )
        );
        var regions = recorded.Delta.Ops.OfType<RegionUpserted>().Select(static r => r.Region).ToArray();
        var files = recorded.Delta.Ops.OfType<AudioFileUpserted>().Select(static f => f.File).ToArray();
        Assert.Equal(2, regions.Length);
        Assert.Equal(2, files.Length);
        Assert.Equal(new[] { tracks[0].Id, tracks[1].Id }.Order(), regions.Select(static r => r.TrackId).Order());
        Assert.All(regions, static r => Assert.Equal(960 * 8, r.StartTick));
        Assert.All(
            regions,
            r => Assert.Equal(r.TrackId == tracks[1].Id ? 2 : 1, files.Single(f => f.Id == r.FileId).Channels)
        );
        Assert.All(files, f => Assert.True(File.Exists(Path.Combine(project.Path, f.Path))));
        Assert.All(files, static f => Assert.StartsWith("Audio Files/", f.Path, StringComparison.Ordinal));

        var history = await client.GetFromJsonAsync(
            "/api/v1/project/history",
            ContractsJsonContext.Default.UndoHistory,
            Token
        );
        Assert.Equal("Record", history!.Undo[0].Label);
        var undone = await client.PostResultAsync("/api/v1/project/undo");
        Assert.All(undone.Delta.Ops, static o => Assert.IsType<RegionRemoved>(o));
        var after = await client.ProjectAsync();
        Assert.Empty(after.Regions);
        Assert.Equal(2, after.AudioFiles.Count); // the takes stay in the project's audio
        await client.PostResultAsync("/api/v1/project/redo");
        Assert.Equal(2, (await client.ProjectAsync()).Regions.Count);
    }

    static async Task Assert422(HttpClient client, TransportRequest request) {
        var response = await client.PostAsJsonAsync(
            "/api/v1/transport",
            request,
            ContractsJsonContext.Default.TransportRequest,
            Token
        );
        await ApiCalls.AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity);
    }
}
