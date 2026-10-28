using Anode.Contracts;
using System.Net;
using System.Net.Http.Json;

namespace Anode.Server.Tests;

/// <summary>
///     Phase 3 recording against the in-process host (docs/04-phase-3.md): cycle recording into take folders or
///     regions, replace mode, autopunch and Flashback Capture.
/// </summary>
public sealed class RecordingApiTests {
    const long Beat = 960;
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Cycle_recording_makes_a_take_folder_of_its_passes_that_comps_and_flattens() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await using var events = await EventStream.ConnectAsync(server);
        var track = await ArmedTrackAsync(server, client);
        await client.EditAsync(new SetCycleCommand(true, 0, Beat)); // half a second

        await RecordAsync(client, events, 1400);
        var folder = (await events.WaitAsync<ProjectChangedEvent>(static e => e.Delta.Ops.Any(static o => o is TakeFolderUpserted)))
            .Delta.Ops.OfType<TakeFolderUpserted>()
            .Single()
            .Folder;
        Assert.Equal((track, 0L, Beat), (folder.TrackId, folder.StartTick, folder.LengthTicks));
        Assert.True(folder.Takes.Count >= 2, $"{folder.Takes.Count} takes");
        Assert.Single(folder.Takes.Select(static t => t.FileId).Distinct()); // one file for the whole recording
        Assert.Equal(folder.Takes[^1].Id, Assert.Single(folder.Comp).TakeId);
        Assert.Empty((await client.ProjectAsync()).Regions);

        // Quick Swipe: the first half from take 1, the rest from take 2; then flatten.
        await client.EditAsync(
            new SetCompCommand(folder.Id, [new(0, Beat / 2, folder.Takes[0].Id), new(Beat / 2, Beat, folder.Takes[1].Id)])
        );
        await client.EditAsync(new FlattenTakeFolderCommand(folder.Id));
        var project = await client.ProjectAsync();
        Assert.Empty(project.TakeFolders);
        Assert.Equal(2, project.Regions.Count);
        Assert.Equal(folder.Crossfade, project.Regions.OrderBy(static r => r.StartTick).First().FadeOut);
    }

    [Fact]
    public async Task In_regions_mode_cycle_passes_become_regions() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await using var events = await EventStream.ConnectAsync(server);
        var track = await ArmedTrackAsync(server, client);
        await client.EditAsync(new SetCycleCommand(true, 0, Beat), new SetRecordingCommand(CycleRecordMode.Regions));
        await RecordAsync(client, events, 1400);
        var regions = (await events.WaitAsync<ProjectChangedEvent>(static e => e.Delta.Ops.Any(static o => o is RegionUpserted)))
            .Delta.Ops.OfType<RegionUpserted>()
            .Select(static r => r.Region)
            .ToArray();
        Assert.True(regions.Length >= 2);
        Assert.All(regions, r => Assert.Equal((track, 0L), (r.TrackId, r.StartTick)));
        Assert.Empty((await client.ProjectAsync()).TakeFolders);
    }

    [Fact]
    public async Task Autopunch_records_only_between_the_locators_and_replace_clears_what_it_covers() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await using var events = await EventStream.ConnectAsync(server);
        var track = await ArmedTrackAsync(server, client);
        var file = await client.ImportAsync(TestWav.Write(Directory.CreateTempSubdirectory("anode-punch-").FullName, "Bed.wav", frames: 96_000));
        await client.EditAsync(new AddRegionCommand(track, file.Id, 0));
        await client.EditAsync(new SetRecordingCommand(Replace: true, Autopunch: true, PunchInTick: Beat / 2, PunchOutTick: Beat + Beat / 2));

        await RecordAsync(client, events, 1100);
        await events.WaitAsync<ProjectChangedEvent>(static e => e.Delta.Ops.OfType<RegionUpserted>().Count() >= 2);
        var regions = (await client.ProjectAsync()).Regions.OrderBy(static r => r.StartTick).ToArray();
        // The bed is cut around the punch range; the take fills it exactly (one beat: 24 000 frames).
        Assert.Equal(
            [(file.Id, 0L, 0L, 12_000L), ("take", Beat / 2, 0L, 24_000L), (file.Id, Beat + Beat / 2, 36_000L, 60_000L)],
            regions.Select(r => (r.FileId == file.Id ? file.Id : "take", r.StartTick, r.FileOffset, r.Length))
        );
    }

    [Fact]
    public async Task Flashback_Capture_keeps_what_was_played_as_an_undoable_step() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await using var events = await EventStream.ConnectAsync(server);
        var track = await ArmedTrackAsync(server, client);
        await client.TransportAsync(TransportCommand.Play, Beat);
        await events.WaitAsync<TransportEvent>(static e => e.Transport is { State: TransportState.Playing, PositionSamples: > 24_000 + 12_000 });
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync("/api/v1/transport/capture", null, Token)).StatusCode);

        var region = (await events.WaitAsync<ProjectChangedEvent>(static e => e.Delta.Ops.Any(static o => o is RegionUpserted)))
            .Delta.Ops.OfType<RegionUpserted>()
            .Single()
            .Region;
        Assert.Equal((track, Beat), (region.TrackId, region.StartTick));
        Assert.True(region.Length >= 12_000);
        var history = await client.GetFromJsonAsync("/api/v1/project/history", ContractsJsonContext.Default.UndoHistory, Token);
        Assert.Equal("Flashback Capture", history!.Undo[0].Label);
        await client.TransportAsync(TransportCommand.Stop);
        await client.PostResultAsync("/api/v1/project/undo");
        Assert.Empty((await client.ProjectAsync()).Regions);
    }

    [Fact]
    public async Task Flashback_Capture_needs_the_engine_and_a_project() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await ApiCalls.AssertProblemAsync(await client.PostAsync("/api/v1/transport/capture", null, Token), HttpStatusCode.NotFound);
    }

    static async Task<string> ArmedTrackAsync(ServerFixture server, HttpClient client) {
        await client.CreateProjectAsync();
        await client.EditAsync(new AddTrackCommand("Vox"));
        var track = (await client.ProjectAsync()).Tracks[0].Id;
        await client.EditAsync(new SetTrackCommand(track, RecordArmed: true));
        await ServerFixture.Eventually(async () => (await server.SystemAsync(client)).Engine.State == EngineState.InProcess);
        return track;
    }

    static async Task RecordAsync(HttpClient client, EventStream events, int milliseconds) {
        await client.TransportAsync(TransportCommand.Record, 0);
        await events.WaitAsync<TransportEvent>(static e => e.Transport.State == TransportState.Recording);
        await Task.Delay(milliseconds, Token);
        await client.TransportAsync(TransportCommand.Stop);
    }
}
