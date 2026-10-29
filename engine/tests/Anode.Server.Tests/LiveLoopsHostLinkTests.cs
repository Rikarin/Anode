using Anode.Contracts;
using Anode.Ipc;
using Anode.Server.Engine;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;

namespace Anode.Server.Tests;

/// <summary>
///     Phase 6 between the server and a host (docs/07-phase-6.md), against the in-process <see cref="FakeHost" />:
///     tempo points, cells and note routes in the model; launches at quantise boundaries and the <c>cell</c> events;
///     performance recording; Smart Controls' coalescing and Compare.
/// </summary>
public sealed class LiveLoopsHostLinkTests {
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static FakeHost Host(ServerFixture server) => server.Services.GetRequiredService<EngineConnection>().InProcessHost!;

    static async Task PostAsync<T>(
        HttpClient client,
        string path,
        T body,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type
    ) {
        var response = await client.PostAsJsonAsync(path, body, type, Token);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Token));
    }

    /// <summary>A project with an instrument track, a drum machine of two pads, two scenes and a MIDI cell.</summary>
    static async Task<(string Keys, string Dmd, Project Project)> SetUpAsync(HttpClient client) {
        await client.CreateProjectAsync();
        await client.EditAsync(
            new AddTrackCommand("Keys", Kind: TrackKind.Instrument),
            new CreateDrumMachineCommand(null, null, "Drums"),
            new AddSceneCommand(),
            new AddSceneCommand()
        );
        var project = await client.ProjectAsync();
        var (keys, dmd) = (project.Tracks[0].Id, project.Tracks[1].Id);
        await client.EditAsync(
            new AddDrumPadCommand(dmd, 36, "Kick", "anode.synth"),
            new AddDrumPadCommand(dmd, 38, "Snare", "anode.synth")
        );
        var midi = new MidiRegion(
            "x",
            keys,
            "Riff",
            0,
            3840,
            new(0, 0, 0, 1, 0, null),
            [new(0, 60, 100, 1, 0, 480, 0)],
            [],
            false
        );
        await client.EditAsync(
            new SetCellCommand(keys, project.LiveLoops.Scenes[0].Id, new(Midi: midi), Quantize: LaunchQuantize.Quarter)
        );
        return (keys, dmd, await client.ProjectAsync());
    }

    [Fact]
    public async Task Tempo_points_cells_and_note_routes_reach_the_host_and_a_launch_waits_for_its_boundary() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await using var events = await EventStream.ConnectAsync(server);
        var (keys, dmd, project) = await SetUpAsync(client);
        var host = Host(server);
        await ServerFixture.Eventually(() => Task.FromResult(host.Cells.Count == 1 && host.TempoPoints.Count == 1));
        Assert.Equal(120, host.TempoPoints[0].Bpm);
        var dmdTrack = host.Tracks.Values.Single(static t => t.NoteRoutes is { Count: 2 });
        Assert.Equal([36, 38], dmdTrack.NoteRoutes!.Select(static r => r.Note));
        Assert.NotNull(dmdTrack.Midi);

        // Stopped: a launch waits for play (activating its track first), then starts on its boundary.
        var cell = project.LiveLoops.Cells.Single();
        await PostAsync(
            client,
            "/api/v1/project/live-loops/launch",
            new(SceneId: project.LiveLoops.Scenes[0].Id),
            ContractsJsonContext.Default.LaunchRequest
        );
        await events.WaitAsync<CellEvent>(e => e.CellId == cell.Id && e.State == CellState.Queued);
        var launch = host.Received.OfType<LaunchCellsMessage>().First();
        Assert.Equal(EngineQuantize.Quarter, launch.Cells.Single().Quantize);
        Assert.True((await client.ProjectAsync()).Tracks.Single(t => t.Id == keys).LiveLoopsActive);
        Assert.True(
            host.Tracks.Values.Single(static t => t.Instrument is not null && t.LiveLoopsActive).LiveLoopsActive
        );
        // The launch started the transport (as Logic does).
        await ServerFixture.Eventually(async () =>
            (await client.GetFromJsonAsync("/api/v1/transport", ContractsJsonContext.Default.TransportStatus, Token))!
            .State
            == TransportState.Playing
        );
        var started = await events.WaitAsync<CellEvent>(e => e.CellId == cell.Id && e.State == CellState.Playing);
        Assert.Equal(0, started.Sample % 24_000);

        // Playing: a scene without the cell stops it at the grid's boundary (a bar), and a relaunch on a quarter.
        await PostAsync(
            client,
            "/api/v1/project/live-loops/launch",
            new(SceneId: project.LiveLoops.Scenes[1].Id),
            ContractsJsonContext.Default.LaunchRequest
        );
        var stopping = await events.WaitAsync<CellEvent>(e => e.CellId == cell.Id && e.State == CellState.Stopping);
        Assert.Equal(0, stopping.Sample % 96_000);
        Assert.Equal(EngineQuantize.Bar, host.Received.OfType<LaunchCellsMessage>().Last().Stops!.Single().Quantize);
        await events.WaitAsync<CellEvent>(e => e.CellId == cell.Id && e.State == CellState.Stopped, 15);
        await PostAsync(
            client,
            "/api/v1/project/live-loops/launch",
            new([cell.Id]),
            ContractsJsonContext.Default.LaunchRequest
        );
        var queued = await events.WaitAsync<CellEvent>(e => e.CellId == cell.Id && e.State == CellState.Queued);
        Assert.Equal(0, queued.Sample % 24_000);
        var playing = await events.WaitAsync<CellEvent>(e =>
            e.CellId == cell.Id && e.State == CellState.Playing && e.Sample == queued.Sample
        );
        Assert.Equal(queued.Sample, playing.Sample);
        await PostAsync(client, "/api/v1/project/live-loops/stop", new(), ContractsJsonContext.Default.StopRequest);
        await ServerFixture.Eventually(() => Task.FromResult(
                host.Received.OfType<StopCellsMessage>().Any(static m => m.Stops.Count == 1)
            )
        );
        await client.TransportAsync(TransportCommand.Stop);
        Assert.Contains(project.Tracks, t => t.Id == keys);
        Assert.Contains(project.Tracks, t => t.Id == dmd);
    }

    [Fact]
    public async Task A_performance_is_recorded_into_the_tracks_area_as_one_undo_step() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await using var events = await EventStream.ConnectAsync(server);
        var (keys, _, project) = await SetUpAsync(client);
        await client.EditAsync(new SetLiveLoopsCommand(LaunchQuantize.Off), new AddInsertCommand("anode.compressor"));
        var cell = project.LiveLoops.Cells.Single();
        await client.EditAsync(new SetCellCommand(keys, cell.SceneId, GridQuantize: true));
        var insert = (await client.ProjectAsync()).Master.Inserts[0].Id;
        await ServerFixture.Eventually(() => Task.FromResult(Host(server).Cells.Count == 1));
        await client.TransportAsync(TransportCommand.Play);
        await PostAsync(
            client,
            "/api/v1/project/live-loops/record",
            new(true),
            ContractsJsonContext.Default.LiveRecordRequest
        );
        await PostAsync(
            client,
            "/api/v1/project/live-loops/launch",
            new([cell.Id]),
            ContractsJsonContext.Default.LaunchRequest
        );
        await events.WaitAsync<CellEvent>(e => e.CellId == cell.Id && e.State == CellState.Playing);
        await Task.Delay(300, Token);
        await client.EditAsync(new SetInsertParameterCommand(insert, "threshold", -30));
        await Task.Delay(300, Token);
        await PostAsync(
            client,
            "/api/v1/project/live-loops/record",
            new(false),
            ContractsJsonContext.Default.LiveRecordRequest
        );
        await client.TransportAsync(TransportCommand.Stop);
        var after = await client.ProjectAsync();
        var region = Assert.Single(after.MidiRegions);
        Assert.Equal((keys, "Riff"), (region.TrackId, region.Name));
        var lane = Assert.Single(after.Automation);
        Assert.Equal(
            (null, $"insert:{insert}:threshold", -30.0),
            (lane.TrackId, lane.Target, lane.Points.Single().Value)
        );
        var history = await client.GetFromJsonAsync(
            "/api/v1/project/history",
            ContractsJsonContext.Default.UndoHistory,
            Token
        );
        Assert.Equal("Record Performance", history!.Undo[0].Label);
        await client.PostResultAsync("/api/v1/project/undo");
        var undone = await client.ProjectAsync();
        Assert.Empty(undone.MidiRegions);
        Assert.Empty(undone.Automation);
    }

    [Fact]
    public async Task Smart_control_moves_coalesce_and_Compare_takes_no_undo_step() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await client.CreateProjectAsync();
        await client.EditAsync(new AddTrackCommand("Lead"));
        var lead = (await client.ProjectAsync()).Tracks[0].Id;
        await client.EditAsync(new SetSmartMappingsCommand(lead, "knob1", [new("volume", -30, 0, false, [])]));
        await client.EditAsync(new SetSmartControlCommand(lead, "knob1", 0.5));
        await client.EditAsync(new SetSmartControlCommand(lead, "knob1", 0.8));
        var history = await client.GetFromJsonAsync(
            "/api/v1/project/history",
            ContractsJsonContext.Default.UndoHistory,
            Token
        );
        Assert.Equal(
            ["Change Smart Control", "Change Smart Control Mapping"],
            history!.Undo.Take(2).Select(static e => e.Label)
        );
        Assert.Equal(-6, (await client.ProjectAsync()).Tracks[0].Strip.VolumeDb, 6);
        // Saving makes the current values the saved ones: Compare then has nothing to swap.
        var before = (await client.ProjectAsync()).Revision;
        (await client.PostAsync("/api/v1/project/save", null, Token)).EnsureSuccessStatusCode();
        var saved = await client.ProjectAsync();
        Assert.True(saved.Revision > before);
        Assert.All(saved.Tracks[0].SmartControls!.Controls, static c => Assert.Null(c.SavedValue));
        await client.EditAsync(new SetSmartControlCommand(lead, "knob1", 0.5));
        history = await client.GetFromJsonAsync(
            "/api/v1/project/history",
            ContractsJsonContext.Default.UndoHistory,
            Token
        );
        await client.EditAsync(new CompareSmartControlsCommand(lead, true));
        var compared = await client.ProjectAsync();
        Assert.Equal((-6.0, true), (compared.Tracks[0].Strip.VolumeDb, compared.Tracks[0].SmartControls!.Comparing));
        var unchanged = await client.GetFromJsonAsync(
            "/api/v1/project/history",
            ContractsJsonContext.Default.UndoHistory,
            Token
        );
        Assert.Equal(history!.Undo.Count, unchanged!.Undo.Count);
    }
}
