using Anode.Contracts;
using Anode.Ipc;
using Anode.Ipc.Segments;
using Anode.Model;
using Anode.Server.Engine;

namespace Anode.Server.Tests;

/// <summary>
///     What the server sends a host over the socket for the mixer (docs/05-phase-4.md), against <see cref="FakeHost" />
///     listening as anode-host does: strip structure, levels, parameters, sends, mixer settings, aux buses, VCA
///     folding, automation targets and events, freeze files and renders.
/// </summary>
public sealed class MixHostLinkTests : IAsyncDisposable {
    readonly string socketPath = Path.Combine(Path.GetTempPath(), $"an-{Guid.NewGuid().ToString("n")[..8]}.sock");
    FakeHost? host;

    public async ValueTask DisposeAsync() {
        if (host is not null) {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task Strip_structure_levels_parameters_and_mixer_settings_reach_the_host_as_their_own_messages() {
        host = new(EngineConnection.Version);
        host.Listen(socketPath);
        await using var server = new ServerFixture(socketPath);
        using var client = server.CreateClient();
        await ServerFixture.Eventually(async () => (await server.SystemAsync(client)).Engine.State == EngineState.Connected);
        await client.CreateProjectAsync();
        await client.EditAsync(new AddTrackCommand("Gtr"), new AddAuxCommand(4, "Verb"), new AddVcaCommand("V"));
        var tracks = (await client.ProjectAsync()).Tracks;
        var (gtr, verb, vca) = (tracks[0].Id, tracks[1].Id, tracks[2].Id);
        // The aux arrives with its input bus; the VCA has no engine track.
        var aux = await WaitForAsync<LoadModelMessage>(static m => m.Tracks.Any(static t => t.InputBus == 4));
        Assert.Equal(1, aux.Tracks.Single(static t => t.InputBus == 4).Id);
        Assert.DoesNotContain(host.Model.Tracks, static t => t == 2);

        await client.EditAsync(new AddInsertCommand("anode.compressor", gtr), new AddSendCommand(gtr, 4, -6));
        var channel = await WaitForAsync<SetChannelMessage>(static m => m.TrackId == 0 && m.Strip.Sends!.Count == 1);
        var insert = (await client.ProjectAsync()).Tracks[0].Strip.Inserts[0];
        Assert.Equal(insert.Id, channel.Strip.Inserts![0].Id);
        Assert.Equal((4, Units.DbToGain(-6)), (channel.Strip.Sends![0].Bus, channel.Strip.Sends[0].Gain));

        var sendId = (await client.ProjectAsync()).Tracks[0].Strip.Sends[0].Id;
        await client.EditAsync(new SetSendCommand(gtr, sendId, -12, 0.5));
        var level = await WaitForAsync<SetSendLevelMessage>();
        Assert.Equal((0, sendId, Units.DbToGain(-12), 0.5f), (level.TrackId, level.SendId, level.Gain, level.Pan));

        await client.EditAsync(new SetInsertParameterCommand(insert.Id, "threshold", -30, gtr));
        var parameter = await WaitForAsync<SetInsertParameterMessage>();
        Assert.Equal((0, insert.Id, "threshold", -30.0), (parameter.TrackId, parameter.InsertId, parameter.ParameterId, parameter.Value));

        await client.EditAsync(new SetOutputCommand(verb, "output:2"));
        Assert.Equal(-2, (await WaitForAsync<SetChannelMessage>(static m => m.TrackId == 1)).Strip.Output);
        await client.EditAsync(new AddInsertCommand("anode.limiter"));
        Assert.Single((await WaitForAsync<SetChannelMessage>(static m => m.TrackId == -1)).Strip.Inserts!);

        // A VCA's fader folds into its member's gain.
        await client.EditAsync(new AssignVcaCommand(gtr, vca), new SetStripCommand(vca, -10));
        await WaitForAsync<SetStripMessage>(static m => m.TrackId == 0 && Math.Abs(m.Strip.Gain - Units.DbToGain(-10)) < 1e-6);

        await client.EditAsync(new SetMixerSettingsCommand(PdcMode.Off, true, 3));
        var mixer = await WaitForAsync<SetMixerMessage>(static m => m.Pdc == "off");
        Assert.Equal((true, 3.0), (mixer.LowLatency, mixer.LowLatencyLimitMs));

        // A lane: its target travels with the model, its events in the track's segment.
        await client.EditAsync(new SetAutomationCommand("volume", [new(0, -20, 0), new(3840, 0, 0)], gtr));
        var withLane = await WaitForAsync<LoadModelMessage>(static m => m.AutomationTargets is { Count: > 0 });
        var target = Assert.Single(withLane.AutomationTargets!);
        Assert.Equal((0, "volume"), (target.TrackId, target.Kind));
        await ServerFixture.Eventually(() => Task.FromResult(
                host.EventsOf(0).Any(e => e.Kind == PlaybackEventKind.Automation && e.Target == target.Index)
            )
        );
        // The VCA's −10 dB rides on the lane's −20 dB.
        var first = host.EventsOf(0).First(static e => e.Kind == PlaybackEventKind.Automation);
        Assert.InRange(first.Value, Units.DbToGain(-30) * 0.999f, Units.DbToGain(-30) * 1.001f);
    }

    [Fact]
    public async Task A_freeze_renders_through_the_host_and_the_track_is_sent_its_freeze_file() {
        host = new(EngineConnection.Version);
        host.Listen(socketPath);
        await using var server = new ServerFixture(socketPath);
        using var client = server.CreateClient();
        await using var events = await EventStream.ConnectAsync(server);
        await ServerFixture.Eventually(async () => (await server.SystemAsync(client)).Engine.State == EngineState.Connected);
        await client.CreateProjectAsync();
        await client.EditAsync(new AddTrackCommand("Keys", Kind: TrackKind.Instrument));
        var keys = (await client.ProjectAsync()).Tracks[0].Id;
        await client.EditAsync(new AddMidiRegionCommand(keys, 0, 960));
        var response = await client.PostAsync($"/api/v1/project/tracks/{keys}/freeze", null, TestContext.Current.CancellationToken);
        var job = (await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync(response.Content, ContractsJsonContext.Default.Job, TestContext.Current.CancellationToken))!;
        var render = await WaitForAsync<RenderMessage>();
        Assert.Equal((true, 0L, 24_000L + 96_000), (render.Freeze, render.StartSample, render.EndSample));
        Assert.Equal([0], render.Tracks!);
        await events.WaitAsync<JobEvent>(e => e.Job.Id == job.Id && e.Job.State == JobState.Done, 30);
        var load = await WaitForAsync<LoadModelMessage>(static m => m.Tracks.Any(static t => t.FreezeFile is not null));
        Assert.Equal(render.Path, load.Tracks.Single().FreezeFile);
    }

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
