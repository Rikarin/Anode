using Anode.Contracts;
using Anode.Server.Control;
using Anode.Server.Engine;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;

namespace Anode.Server.Tests;

/// <summary>
///     Phase 8's control service end to end (docs/09-phase-8.md, workstream B), through the REST API and the in-process
///     <see cref="FakeHost" />, with stand-ins for workstream D's protocols (<see cref="TestProtocols" />): assignments
///     and their routing, the value modes the service feeds (absolute, relative, pickup, toggle), the selected track,
///     gestures as undo steps, automation writing, Learn, and the selection's API.
/// </summary>
public sealed class ControlApiTests : IAsyncDisposable {
    static CancellationToken Token => TestContext.Current.CancellationToken;

    readonly TestProtocols protocols = new();
    readonly ServerFixture server;
    readonly HttpClient client;

    public ControlApiTests() {
        server = new(configure: s => s.AddSingleton<IControlProtocols>(protocols));
        client = server.CreateClient();
    }

    public async ValueTask DisposeAsync() {
        client.Dispose();
        await server.DisposeAsync();
    }

    FakeHost Host => server.Services.GetRequiredService<EngineConnection>().InProcessHost!;

    /// <summary>A project with two audio tracks, the host connected; their ids.</summary>
    async Task<(string One, string Two)> ProjectAsync() {
        await ServerFixture.Eventually(async () =>
            (await server.SystemAsync(client)).Engine.State == EngineState.InProcess
            && server.Services.GetRequiredService<EngineConnection>().InProcessHost is not null
        );
        await client.CreateProjectAsync();
        await client.EditAsync(new AddTrackCommand("One"), new AddTrackCommand("Two"));
        var tracks = (await client.ProjectAsync()).Tracks;
        return (tracks[0].Id, tracks[1].Id);
    }

    static ControllerAssignments Zone(params ControlAssignment[] assignments) =>
        new([new("zone", "Zone", [new("mode", "Mode", assignments)], "mode")]);

    static ControlAssignment Cc(
        int number,
        ControlTarget target,
        ControlValueMode mode = ControlValueMode.Absolute,
        bool pickup = false
    ) =>
        new($"cc{number}", null, new(null, ControlInputKind.ControlChange, null, number), target, mode, 0, 1, pickup);

    static ControlTarget Volume(string? trackId) => new(ControlTargetKind.Parameter, trackId, "volume");

    async Task<T> PutAsync<T>(string path, T body, JsonTypeInfo<T> type) {
        var response = await client.PutAsJsonAsync(path, body, type, Token);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Token));
        return (await response.Content.ReadFromJsonAsync(type, Token))!;
    }

    async Task<double> VolumeAsync(string trackId) =>
        (await client.ProjectAsync()).Tracks.Single(t => t.Id == trackId).Strip.VolumeDb;

    async Task<int> UndoStepsAsync() =>
        (await client.GetFromJsonAsync("/api/v1/project/history", ContractsJsonContext.Default.UndoHistory, Token))!
        .Undo.Count;

    static byte[] Cc(int number, int value) => [0xB0, (byte)number, (byte)value];

    [Fact]
    public async Task Assignments_are_checked_saved_and_routed_and_an_absolute_control_sets_its_target() {
        var (one, two) = await ProjectAsync();
        var bad = await client.PutAsJsonAsync(
            "/api/v1/control/assignments",
            Zone(Cc(21, Volume(two)), Cc(21, Volume(one)) with { Id = "cc21" }),
            ContractsJsonContext.Default.ControllerAssignments,
            Token
        );
        await ApiCalls.AssertProblemAsync(bad, HttpStatusCode.UnprocessableEntity);

        var assignments = Zone(Cc(21, Volume(two)));
        await PutAsync("/api/v1/control/assignments", assignments, ContractsJsonContext.Default.ControllerAssignments);
        await ServerFixture.Eventually(() => Task.FromResult(Host.ControlRouting?.Matchers.Count == 1));
        Assert.Equal(new(null, 0xB0, -1, 21), Host.ControlRouting!.Matchers[0]);
        Assert.False(Host.ControlRouting.Learn);
        Assert.Empty(Host.ControlRouting.ControlPorts);
        Assert.True(File.Exists(Path.Combine(server.DataDirectory, "settings", "controller-assignments.json")));

        await Host.PlayControlAsync("fake-in", Cc(21, 100));
        await ServerFixture.Eventually(async () =>
            Math.Abs(await VolumeAsync(two) - Taper.Db(100 / 127.0)) < 1e-9
        );
        Assert.Equal(0, await VolumeAsync(one));
        var stored = await client.GetFromJsonAsync(
            "/api/v1/control/assignments",
            ContractsJsonContext.Default.ControllerAssignments,
            Token
        );
        Assert.Equal("cc21", stored!.Zones[0].Modes[0].Assignments[0].Id);
    }

    [Fact]
    public async Task A_gesture_is_one_undo_step_and_ends_half_a_second_after_its_last_move() {
        var (_, two) = await ProjectAsync();
        await PutAsync(
            "/api/v1/control/assignments",
            Zone(Cc(21, Volume(two))),
            ContractsJsonContext.Default.ControllerAssignments
        );
        var before = await UndoStepsAsync();
        foreach (var value in new[] { 60, 70, 80, 90 }) {
            await Host.PlayControlAsync("fake-in", Cc(21, value));
            await Task.Delay(50, Token);
        }

        await ServerFixture.Eventually(async () => Math.Abs(await VolumeAsync(two) - Taper.Db(90 / 127.0)) < 1e-9);
        Assert.Equal(before + 1, await UndoStepsAsync());

        await Task.Delay(ControlService.GestureTimeout + TimeSpan.FromMilliseconds(200), Token);
        await Host.PlayControlAsync("fake-in", Cc(21, 20));
        await ServerFixture.Eventually(async () => Math.Abs(await VolumeAsync(two) - Taper.Db(20 / 127.0)) < 1e-9);
        Assert.Equal(before + 2, await UndoStepsAsync());

        await client.PostResultAsync("/api/v1/project/undo");
        Assert.Equal(Taper.Db(90 / 127.0), await VolumeAsync(two), 9);
        await client.PostResultAsync("/api/v1/project/undo");
        Assert.Equal(0, await VolumeAsync(two));
    }

    [Fact]
    public async Task Relative_controls_step_from_the_current_value_and_pickup_waits_for_the_control_to_cross_it() {
        var (one, two) = await ProjectAsync();
        await PutAsync(
            "/api/v1/control/assignments",
            Zone(
                Cc(21, Volume(one), ControlValueMode.RelativeTwosComplement),
                Cc(22, Volume(two), pickup: true)
            ),
            ContractsJsonContext.Default.ControllerAssignments
        );
        var start = Taper.Position(0);
        await Host.PlayControlAsync("fake-in", Cc(21, 2), Cc(21, 2), Cc(21, 127));
        await ServerFixture.Eventually(async () =>
            Math.Abs(Taper.Position(await VolumeAsync(one)) - (start + 3 / 128.0)) < 1e-9
        );

        // Track 2 sits at 0 dB (0.78 of the travel): a control at 10 and 60 does not move it…
        await Host.PlayControlAsync("fake-in", Cc(22, 10), Cc(22, 60));
        await Task.Delay(200, Token);
        Assert.Equal(0, await VolumeAsync(two));
        // …until it passes the target's value.
        await Host.PlayControlAsync("fake-in", Cc(22, 110), Cc(22, 112));
        await ServerFixture.Eventually(async () => Math.Abs(await VolumeAsync(two) - Taper.Db(112 / 127.0)) < 1e-9);
    }

    [Fact]
    public async Task Selected_track_targets_follow_the_editors_selection() {
        var (one, two) = await ProjectAsync();
        await PutAsync(
            "/api/v1/control/assignments",
            Zone(
                Cc(21, Volume(null)),
                new(
                    "mute",
                    null,
                    new(null, ControlInputKind.Note, null, 36),
                    new(ControlTargetKind.TrackSwitch, Switch: TrackSwitchAction.Mute),
                    ControlValueMode.Toggle,
                    0,
                    1,
                    false
                )
            ),
            ContractsJsonContext.Default.ControllerAssignments
        );
        // Nothing selected: nothing moves.
        await Host.PlayControlAsync("fake-in", Cc(21, 10));
        await Task.Delay(150, Token);
        Assert.Equal((0d, 0d), (await VolumeAsync(one), await VolumeAsync(two)));

        await SelectAsync(two);
        await Host.PlayControlAsync("fake-in", Cc(21, 50), [0x90, 36, 100], [0x80, 36, 0]);
        await ServerFixture.Eventually(async () =>
            (await client.ProjectAsync()).Tracks.Single(t => t.Id == two).Strip is { Mute: true } strip
            && Math.Abs(strip.VolumeDb - Taper.Db(50 / 127.0)) < 1e-9
        );

        await SelectAsync(one);
        await Host.PlayControlAsync("fake-in", [0x90, 36, 100], [0x80, 36, 0]);
        await ServerFixture.Eventually(async () =>
            (await client.ProjectAsync()).Tracks.Single(t => t.Id == one).Strip.Mute
        );
        Assert.True((await client.ProjectAsync()).Tracks.Single(t => t.Id == two).Strip.Mute);

        var unknown = await client.PutAsJsonAsync(
            "/api/v1/project/selection",
            new("trk_gone"),
            ContractsJsonContext.Default.SelectionRequest,
            Token
        );
        await ApiCalls.AssertProblemAsync(unknown, HttpStatusCode.NotFound);
    }

    async Task SelectAsync(string? trackId) {
        var response = await client.PutAsJsonAsync(
            "/api/v1/project/selection",
            new(trackId),
            ContractsJsonContext.Default.SelectionRequest,
            Token
        );
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Moves_while_the_tracks_automation_writes_are_recorded_as_automation() {
        var (_, two) = await ProjectAsync();
        await PutAsync(
            "/api/v1/control/assignments",
            Zone(Cc(21, Volume(two))),
            ContractsJsonContext.Default.ControllerAssignments
        );
        await client.EditAsync(new SetAutomationModeCommand(AutomationMode.Latch, two));
        await client.TransportAsync(TransportCommand.Play, 0);
        var engine = server.Services.GetRequiredService<EngineConnection>();
        await ServerFixture.Eventually(() => Task.FromResult(engine.Transport().PositionTick > 100));
        foreach (var value in new[] { 30, 60, 90 }) {
            await Host.PlayControlAsync("fake-in", Cc(21, value));
            await Task.Delay(80, Token);
        }

        await ServerFixture.Eventually(async () =>
            (await client.ProjectAsync()).Automation.Any(l => l.TrackId == two
                && l.Target == "volume"
                && l.Points.Count > 0
            )
        );
        await client.TransportAsync(TransportCommand.Stop);
        var lane = (await client.ProjectAsync()).Automation.Single(l => l.TrackId == two && l.Target == "volume");
        Assert.Contains(lane.Points, static p => Math.Abs(p.Value - Taper.Db(90 / 127.0)) < 1e-6);
    }

    [Fact]
    public async Task Learn_offers_each_message_and_assigns_the_control_moved_to_its_target() {
        var (_, two) = await ProjectAsync();
        await using var events = await EventStream.ConnectAsync(server);
        var started = await client.PostAsJsonAsync(
            "/api/v1/control/learn/start",
            new(),
            ContractsJsonContext.Default.LearnRequest,
            Token
        );
        Assert.True(started.IsSuccessStatusCode);
        await ServerFixture.Eventually(() => Task.FromResult(Host.ControlRouting?.Learn == true));

        // No target yet: the message is offered, nothing assigned.
        await Host.PlayControlAsync("fake-in", Cc(20, 5));
        var offered = await events.WaitAsync<ControlLearnEvent>();
        Assert.Null(offered.AssignmentId);
        Assert.Equal(
            (0xB0, 20, 5, new ControlInput("fake-in", ControlInputKind.ControlChange, 1, 20)),
            (offered.Message.Status, offered.Message.Data1, offered.Message.Data2, offered.Message.Input)
        );

        await client.PostAsJsonAsync(
            "/api/v1/control/learn/start",
            new(Volume(two)),
            ContractsJsonContext.Default.LearnRequest,
            Token
        );
        await Host.PlayControlAsync("fake-in", Cc(21, 64), Cc(21, 65));
        var learned = await events.WaitAsync<ControlLearnEvent>(static e => e.AssignmentId is not null);
        var state = await client.GetFromJsonAsync(
            "/api/v1/control/learn",
            ContractsJsonContext.Default.LearnState,
            Token
        );
        Assert.Equal((true, 21), (state!.On, state.Last!.Data1));
        var assignment = Assert.Single(
            (await client.GetFromJsonAsync(
                "/api/v1/control/assignments",
                ContractsJsonContext.Default.ControllerAssignments,
                Token
            ))!.Zones.Single()
            .Modes.Single()
            .Assignments
        );
        Assert.Equal(
            (learned.AssignmentId, 21, ControlValueMode.Absolute),
            (assignment.Id, assignment.Input.Number, assignment.Mode)
        );
        // Learning, the control does not move the target (the host plays it).
        Assert.Equal(0, await VolumeAsync(two));

        // Moving another control re-learns the same target.
        await Host.PlayControlAsync("fake-in", Cc(23, 1));
        await events.WaitAsync<ControlLearnEvent>(static e => e.Message.Data1 == 23);
        var relearned = Assert.Single(
            (await client.GetFromJsonAsync(
                "/api/v1/control/assignments",
                ContractsJsonContext.Default.ControllerAssignments,
                Token
            ))!.Zones.Single()
            .Modes.Single()
            .Assignments
        );
        Assert.Equal((learned.AssignmentId, 23), (relearned.Id, relearned.Input.Number));

        var stopped = await client.PostAsync("/api/v1/control/learn/stop", null, Token);
        Assert.False((await stopped.Content.ReadFromJsonAsync(ContractsJsonContext.Default.LearnState, Token))!.On);
        await ServerFixture.Eventually(() => Task.FromResult(Host.ControlRouting is { Learn: false, Matchers.Count: 1 })
        );
        await Host.PlayControlAsync("fake-in", Cc(23, 100));
        await ServerFixture.Eventually(async () => Math.Abs(await VolumeAsync(two) - Taper.Db(100 / 127.0)) < 1e-9);
    }

    [Fact]
    public async Task Learn_refuses_a_zone_that_is_not_there_and_a_target_without_its_fields() {
        var missingZone = await client.PostAsJsonAsync(
            "/api/v1/control/learn/start",
            new(Volume(null), "nope"),
            ContractsJsonContext.Default.LearnRequest,
            Token
        );
        await ApiCalls.AssertProblemAsync(missingZone, HttpStatusCode.UnprocessableEntity);
        var noAddress = await client.PostAsJsonAsync(
            "/api/v1/control/learn/start",
            new(new(ControlTargetKind.Parameter)),
            ContractsJsonContext.Default.LearnRequest,
            Token
        );
        await ApiCalls.AssertProblemAsync(noAddress, HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Selection_needs_a_project() {
        var response = await client.PutAsJsonAsync(
            "/api/v1/project/selection",
            new(null),
            ContractsJsonContext.Default.SelectionRequest,
            Token
        );
        await ApiCalls.AssertProblemAsync(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Assignments_survive_a_restart_from_the_user_settings() {
        await PutAsync(
            "/api/v1/control/assignments",
            Zone(Cc(7, Volume(null))),
            ContractsJsonContext.Default.ControllerAssignments
        );
        await using var again = new ServerFixture(
            configure: static s => s.AddSingleton<IControlProtocols>(new TestProtocols()),
            data: server.DataDirectory
        );
        using var second = again.CreateClient();
        var stored = await second.GetFromJsonAsync(
            "/api/v1/control/assignments",
            ContractsJsonContext.Default.ControllerAssignments,
            Token
        );
        Assert.Equal("cc7", stored!.Zones.Single().Modes.Single().Assignments.Single().Id);
    }
}
