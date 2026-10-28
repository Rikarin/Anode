using Anode.Contracts;
using Anode.Model.Audio;
using Anode.Server.Engine;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;

namespace Anode.Server.Tests;

/// <summary>
///     Phase 4 against the in-process host (docs/05-phase-4.md): mixer edits, automation recording during playback,
///     freeze, bounce with post-processing, patches, I/O labels, plugin editors and the host's plugin reports.
/// </summary>
public sealed class MixApiTests {
    const long Bar = 3840;
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_mixer_is_edited_through_commands_and_loops_are_refused() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await client.CreateProjectAsync();
        await client.EditAsync(new AddTrackCommand("Gtr"), new AddAuxCommand(1, "Verb"), new AddVcaCommand("Band"));
        var project = await client.ProjectAsync();
        var (gtr, verb, vca) = (project.Tracks[0].Id, project.Tracks[1].Id, project.Tracks[2].Id);
        await client.EditAsync(
            new AddInsertCommand("anode.compressor", gtr),
            new AddInsertCommand("anode.reverb", verb),
            new AddSendCommand(gtr, 1, -6),
            new SetGroupCommand(1, "Guitars"),
            new SetTrackGroupsCommand(gtr, [1]),
            new AssignVcaCommand(gtr, vca),
            new SetAutomationCommand("volume", [new(0, -12, 0), new(Bar, 0, 0)], gtr),
            new SetMixerSettingsCommand(PdcMode.Tracks)
        );
        project = await client.ProjectAsync();
        var strip = project.Tracks[0].Strip;
        Assert.Equal(
            ("anode.compressor", 1, -6.0, vca),
            (strip.Inserts[0].PluginId, strip.Sends[0].Bus, strip.Sends[0].LevelDb, strip.VcaId)
        );
        Assert.Equal([1], strip.Groups);
        Assert.Equal("Guitars", Assert.Single(project.Groups).Name);
        Assert.Equal("volume", Assert.Single(project.Automation).Target);
        Assert.Equal(PdcMode.Tracks, project.Settings.Mixer.Pdc);

        var loop = await client.SendCommandsAsync(null, new SetOutputCommand(verb, "bus:1"));
        Assert.Contains(
            "loop",
            await ApiCalls.AssertProblemAsync(loop, HttpStatusCode.UnprocessableEntity),
            StringComparison.Ordinal
        );
        var range = await client.SendCommandsAsync(null, new AddSendCommand(gtr, 65));
        await ApiCalls.AssertProblemAsync(range, HttpStatusCode.UnprocessableEntity);
        var group = await client.SendCommandsAsync(null, new SetGroupCommand(33));
        await ApiCalls.AssertProblemAsync(group, HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Moving_a_fader_in_touch_during_playback_records_one_undo_step_per_pass() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await using var events = await EventStream.ConnectAsync(server);
        await client.CreateProjectAsync();
        await client.EditAsync(new AddTrackCommand("Gtr"));
        var gtr = (await client.ProjectAsync()).Tracks[0].Id;
        await client.EditAsync(new SetAutomationModeCommand(AutomationMode.Latch, gtr));
        var undoBefore = (await History(client)).Undo.Count;

        await client.TransportAsync(TransportCommand.Play, 0);
        await events.WaitAsync<TransportEvent>(static e => e.Transport is {
                State: TransportState.Playing, PositionTick: > 100
            }
        );
        foreach (var db in (double[])[-10, -8, -6]) {
            await client.EditAsync(new SetStripCommand(gtr, db));
            await Task.Delay(150, Token);
        }

        await Task.Delay(200, Token);
        await client.TransportAsync(TransportCommand.Stop);
        await ServerFixture.Eventually(async () =>
            (await client.ProjectAsync()).Automation.SingleOrDefault()?.Points.Count
            >= 4 // three moves and the latch end
        );
        var lane = (await client.ProjectAsync()).Automation.Single();
        Assert.Equal(("volume", gtr), (lane.Target, lane.TrackId));
        Assert.Equal([-10.0, -8, -6, -6], lane.Points.Select(static p => p.Value));
        Assert.True(lane.Points.Zip(lane.Points.Skip(1)).All(static p => p.First.Tick < p.Second.Tick));
        Assert.Equal(undoBefore + 1, (await History(client)).Undo.Count);

        await client.PostResultAsync("/api/v1/project/undo");
        var undone = await client.ProjectAsync();
        Assert.Empty(undone.Automation);
        Assert.Equal(0, undone.Tracks[0].Strip.VolumeDb);

        // Read mode records nothing.
        await client.EditAsync(new SetAutomationModeCommand(AutomationMode.Read, gtr));
        await client.TransportAsync(TransportCommand.Play, 0);
        await events.WaitAsync<TransportEvent>(static e => e.Transport is {
                State: TransportState.Playing, PositionTick: > 100
            }
        );
        await client.EditAsync(new SetStripCommand(gtr, -3));
        await client.TransportAsync(TransportCommand.Stop);
        Assert.Empty((await client.ProjectAsync()).Automation);
    }

    [Fact]
    public async Task Freezing_renders_the_track_and_is_one_undoable_step() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await using var events = await EventStream.ConnectAsync(server);
        await client.CreateProjectAsync();
        await client.EditAsync(new AddTrackCommand("Keys", Kind: TrackKind.Instrument), new AddTrackCommand("Empty"));
        var (keys, empty) = ((await client.ProjectAsync()).Tracks[0].Id, (await client.ProjectAsync()).Tracks[1].Id);
        await client.EditAsync(new AddMidiRegionCommand(keys, 0, Bar));

        var nothing = await client.PostAsync($"/api/v1/project/tracks/{empty}/freeze", null, Token);
        await ApiCalls.AssertProblemAsync(nothing, HttpStatusCode.UnprocessableEntity);
        var unknown = await client.PostAsync("/api/v1/project/tracks/trk_none/freeze", null, Token);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var job = await StartAsync(client, $"/api/v1/project/tracks/{keys}/freeze", null);
        Assert.Equal(JobKind.Freeze, job.Kind);
        var done = await events.WaitAsync<JobEvent>(e => e.Job.Id == job.Id && e.Job.State == JobState.Done, 30);
        var file = Assert.Single(done.Job.Files);
        Assert.Equal("Freeze Files", Path.GetFileName(Path.GetDirectoryName(file)));
        // Two seconds of 120 BPM bar, plus two seconds of tail, at 48 kHz.
        Assert.Equal(4 * 48_000L, WavReader.Probe(file).Frames);
        var project = await client.ProjectAsync();
        var frozen = project.Tracks[0].FrozenFileId;
        Assert.NotNull(frozen);
        Assert.Equal(Path.GetFileNameWithoutExtension(file), project.AudioFiles.Single(f => f.Id == frozen).Name);
        Assert.Equal("Freeze Track", (await History(client)).Undo[0].Label);
        var conflict = await client.PostAsync($"/api/v1/project/tracks/{keys}/freeze", null, Token);
        await ApiCalls.AssertProblemAsync(conflict, HttpStatusCode.UnprocessableEntity);

        await client.PostResultAsync("/api/v1/project/undo");
        Assert.Null((await client.ProjectAsync()).Tracks[0].FrozenFileId);
        await client.PostResultAsync("/api/v1/project/redo");
        await client.EditAsync(new UnfreezeTrackCommand(keys));
        var thawed = await client.ProjectAsync();
        Assert.Null(thawed.Tracks[0].FrozenFileId);
        Assert.Contains(thawed.AudioFiles, f => f.Id == frozen); // the freeze file stays in the pool
    }

    [Fact]
    public async Task A_mix_bounce_is_normalised_to_its_loudness_and_ceiling_and_can_join_the_project() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await using var events = await EventStream.ConnectAsync(server);
        var created = await client.CreateProjectAsync();
        await client.EditAsync(new AddTrackCommand("A"), new AddTrackCommand("B"));
        var job = await StartAsync(
            client,
            "/api/v1/project/bounce",
            new(
                "Mix",
                StartTick: 0,
                EndTick: 4 * Bar,
                BitDepth: 24,
                SampleRate: 44_100,
                Normalize: NormalizeMode.Loudness,
                LoudnessLufs: -16,
                PeakCeilingDb: -1,
                AddToProject: true
            )
        );
        var done = await events.WaitAsync<JobEvent>(
            e => e.Job.Id == job.Id && e.Job.State is JobState.Done or JobState.Failed,
            60
        );
        Assert.Equal(JobState.Done, done.Job.State);
        Assert.Contains(events.OfType<JobEvent>(), e => e.Job.Id == job.Id && e.Job.State == JobState.Running);
        var file = Assert.Single(done.Job.Files);
        Assert.Equal(
            Path.Combine(Path.GetDirectoryName(created.Path)!, Path.GetFileName(created.Path), "Bounces", "Mix.wav"),
            file
        );
        var info = WavReader.Probe(file);
        Assert.Equal((44_100, 24, 2), (info.SampleRate, info.BitDepth, info.Channels));
        Assert.InRange(info.Frames, 8 * 44_100 - 1, 8 * 44_100 + 1);
        var (audio, rate) = BounceProcessor.Read(file);
        Assert.InRange(Loudness.Integrated(audio, rate), -16.5, -15.5);
        Assert.True(TruePeak.MeasureDb(audio) <= -0.95, $"true peak {TruePeak.MeasureDb(audio)}");
        // Added at the project's rate.
        var added = (await client.ProjectAsync()).AudioFiles.Single();
        Assert.Equal((48_000, "Mix"), (added.SampleRate, added.Name));
        Assert.Equal("Add Bounce", (await History(client)).Undo[0].Label);

        var status = await client.GetFromJsonAsync($"/api/v1/jobs/{job.Id}", ContractsJsonContext.Default.Job, Token);
        Assert.Equal(JobState.Done, status!.State);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/jobs/job_none", Token)).StatusCode);
    }

    [Fact]
    public async Task Stems_bounce_to_AIFF_peak_normalised_and_jobs_cancel() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await using var events = await EventStream.ConnectAsync(server);
        await client.CreateProjectAsync();
        await client.EditAsync(new AddTrackCommand("Kick"), new AddTrackCommand("Bass"), new AddTrackCommand("Muted"));
        var tracks = (await client.ProjectAsync()).Tracks;
        var directory = Directory.CreateTempSubdirectory("anode-stems-").FullName;
        try {
            var job = await StartAsync(
                client,
                "/api/v1/project/bounce",
                new BounceRequest(
                    "Song",
                    directory,
                    0,
                    2 * Bar,
                    BounceSource.Stems,
                    [tracks[0].Id, tracks[1].Id],
                    AudioFormat.Aiff,
                    16,
                    Dither: DitherMode.Shaped,
                    Normalize: NormalizeMode.Peak,
                    PeakCeilingDb: -3
                )
            );
            var done = await events.WaitAsync<JobEvent>(
                e => e.Job.Id == job.Id && e.Job.State is JobState.Done or JobState.Failed,
                60
            );
            Assert.Equal(JobState.Done, done.Job.State);
            Assert.Equal(["Song - Bass.aif", "Song - Kick.aif"], done.Job.Files.Select(Path.GetFileName).Order());
            foreach (var path in done.Job.Files) {
                var (audio, rate, bits) = AiffReader.Read(path);
                Assert.Equal((48_000, 16), (rate, bits));
                Assert.InRange(TruePeak.MeasureDb(audio), -3.05, -2.95);
            }

            var bad = await client.PostAsJsonAsync(
                "/api/v1/project/bounce",
                new("Song", BitDepth: 20),
                ContractsJsonContext.Default.BounceRequest,
                Token
            );
            await ApiCalls.AssertProblemAsync(bad, HttpStatusCode.UnprocessableEntity);

            // A long render cancelled while it runs, and one cancelled while it waits behind it.
            var long1 = await StartAsync(client, "/api/v1/project/bounce", new("Long", directory, 0, 2000 * Bar));
            var long2 = await StartAsync(client, "/api/v1/project/bounce", new("Later", directory, 0, Bar));
            var cancelled = await client.PostAsync($"/api/v1/jobs/{long2.Id}/cancel", null, Token);
            Assert.Equal(
                JobState.Cancelled,
                (await cancelled.Content.ReadFromJsonAsync(ContractsJsonContext.Default.Job, Token))!.State
            );
            await events.WaitAsync<JobEvent>(e => e.Job.Id == long1.Id && e.Job.State == JobState.Running, 30);
            await client.PostAsync($"/api/v1/jobs/{long1.Id}/cancel", null, Token);
            await events.WaitAsync<JobEvent>(e => e.Job.Id == long1.Id && e.Job.State == JobState.Cancelled, 30);
            Assert.False(File.Exists(Path.Combine(directory, "Long.wav")));
            Assert.Equal(
                HttpStatusCode.NotFound,
                (await client.PostAsync("/api/v1/jobs/job_none/cancel", null, Token)).StatusCode
            );
        } finally {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Patches_save_list_apply_with_their_states_and_delete() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await WaitEngineAsync(server, client);
        await client.CreateProjectAsync();
        await client.EditAsync(new AddTrackCommand("Vox"), new AddTrackCommand("Backing"));
        var (vox, backing) = ((await client.ProjectAsync()).Tracks[0].Id, (await client.ProjectAsync()).Tracks[1].Id);
        await client.EditAsync(
            new AddInsertCommand("anode.channel-eq", vox),
            new AddInsertCommand(FakeHost.FakeVst3Id, vox),
            new AddSendCommand(vox, 2, -9)
        );
        Assert.Empty(
            (await client.GetFromJsonAsync(
                "/api/v1/library/patches",
                ContractsJsonContext.Default.IReadOnlyListPatchInfo,
                Token
            ))!
        );
        var response = await client.PostAsJsonAsync(
            "/api/v1/library/patches",
            new(vox, "Lead Vocal", "Vocals"),
            ContractsJsonContext.Default.SavePatchRequest,
            Token
        );
        var patch = (await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.PatchInfo, Token))!;
        Assert.Equal(("Lead Vocal", "Vocals", TrackKind.Audio), (patch.Name, patch.Category, patch.Kind));
        Assert.Equal(
            patch,
            Assert.Single(
                (await client.GetFromJsonAsync(
                    "/api/v1/library/patches",
                    ContractsJsonContext.Default.IReadOnlyListPatchInfo,
                    Token
                ))!
            )
        );

        await client.EditAsync(new ApplyPatchCommand(backing, patch.Id));
        var strip = (await client.ProjectAsync()).Tracks[1].Strip;
        Assert.Equal(["anode.channel-eq", FakeHost.FakeVst3Id], strip.Inserts.Select(static i => i.PluginId));
        Assert.Equal((2, -9.0), (strip.Sends[0].Bus, strip.Sends[0].LevelDb));
        // The patch's plugin states reach the host with the new inserts.
        var host = server.Services.GetRequiredService<EngineConnection>().InProcessHost!;
        await ServerFixture.Eventually(() => Task.FromResult(
                host.Received.OfType<Ipc.SetChannelMessage>()
                    .Any(m => m.Strip.Inserts!.Any(i => i.Id == strip.Inserts[1].Id && i.State is not null))
            )
        );

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/v1/library/patches/{patch.Id}", Token)).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.DeleteAsync($"/api/v1/library/patches/{patch.Id}", Token)).StatusCode
        );
        var missing = await client.SendCommandsAsync(null, new ApplyPatchCommand(backing, patch.Id));
        await ApiCalls.AssertProblemAsync(missing, HttpStatusCode.UnprocessableEntity);
        var noTrack = await client.PostAsJsonAsync(
            "/api/v1/library/patches",
            new("trk_none", "X", "Y"),
            ContractsJsonContext.Default.SavePatchRequest,
            Token
        );
        Assert.Equal(HttpStatusCode.NotFound, noTrack.StatusCode);
    }

    [Fact]
    public async Task IO_labels_are_kept_per_user() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        var empty = await client.GetFromJsonAsync(
            "/api/v1/settings/io-labels",
            ContractsJsonContext.Default.IoLabels,
            Token
        );
        Assert.Equal((0, 0, 0), (empty!.Inputs.Count, empty.Outputs.Count, empty.Buses.Count));
        var labels = new IoLabels(
            [new(1, "Vocal Mic"), new(2, " ")],
            [new(1, "Monitors")],
            [new(1, "Reverb"), new(64, "Print")]
        );
        var put = await client.PutAsJsonAsync(
            "/api/v1/settings/io-labels",
            labels,
            ContractsJsonContext.Default.IoLabels,
            Token
        );
        put.EnsureSuccessStatusCode();
        var saved = await client.GetFromJsonAsync(
            "/api/v1/settings/io-labels",
            ContractsJsonContext.Default.IoLabels,
            Token
        );
        Assert.Equal(["Vocal Mic"], saved!.Inputs.Select(static l => l.Name));
        Assert.Equal([(1, "Reverb"), (64, "Print")], saved.Buses.Select(static l => (l.Number, l.Name)));
        var bad = await client.PutAsJsonAsync(
            "/api/v1/settings/io-labels",
            new IoLabels([], [], [new(65, "Nope")]),
            ContractsJsonContext.Default.IoLabels,
            Token
        );
        await ApiCalls.AssertProblemAsync(bad, HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Plugin_editors_open_on_the_host_and_its_parameter_changes_and_problems_come_back() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await using var events = await EventStream.ConnectAsync(server);
        await WaitEngineAsync(server, client);
        await client.CreateProjectAsync();
        await client.EditAsync(new AddTrackCommand("Gtr"));
        var gtr = (await client.ProjectAsync()).Tracks[0].Id;
        await client.EditAsync(new AddInsertCommand(FakeHost.FakeVst3Id, gtr), new AddInsertCommand("anode.gain", gtr));
        var inserts = (await client.ProjectAsync()).Tracks[0].Strip.Inserts;
        var host = server.Services.GetRequiredService<EngineConnection>().InProcessHost!;

        Assert.Equal(HttpStatusCode.NoContent, (await Editor(client, new(gtr, inserts[0].Id, true))).StatusCode);
        await ServerFixture.Eventually(() => Task.FromResult(host.Editors.Contains((0, inserts[0].Id))));
        await ApiCalls.AssertProblemAsync(await Editor(client, new(gtr, inserts[1].Id, true)), HttpStatusCode.Conflict);
        Assert.Equal(HttpStatusCode.NotFound, (await Editor(client, new(gtr, "ins_none", true))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Editor(client, new("trk_none", null, true))).StatusCode);
        await Editor(client, new(gtr, inserts[0].Id, false));
        await ServerFixture.Eventually(() => Task.FromResult(host.Editors.Count == 0));

        // The plugin's own editor moves its gain twice: one coalesced undo step.
        var undo = (await History(client)).Undo.Count;
        await host.ChangeParameterAsync(0, inserts[0].Id, "0", 3);
        await host.ChangeParameterAsync(0, inserts[0].Id, "0", 6);
        await ServerFixture.Eventually(async () =>
            (await client.ProjectAsync()).Tracks[0].Strip.Inserts[0].Parameters["0"] == 6
        );
        Assert.Equal(undo + 1, (await History(client)).Undo.Count);

        await host.ReportReadingsAsync([new(0, inserts[1].Id, -4.5f), new(7, "ins_gone", 1)]);
        var meters = await events.WaitAsync<MetersEvent>(static e => e.Readings is { Count: > 0 });
        Assert.Equal(new(gtr, inserts[1].Id, -4.5), Assert.Single(meters.Readings!));

        await host.ReportProblemAsync(0, inserts[0].Id, FakeHost.FakeVst3Id, "The plugin crashed.");
        var problem = await events.WaitAsync<PluginProblemEvent>();
        Assert.Equal(
            (gtr, inserts[0].Id, FakeHost.FakeVst3Id, "The plugin crashed."),
            (problem.TrackId, problem.InsertId, problem.PluginId, problem.Message)
        );
    }

    [Fact]
    public async Task Insert_states_are_saved_per_insert_and_given_back_on_reopen() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await WaitEngineAsync(server, client);
        var created = await client.CreateProjectAsync();
        await client.EditAsync(new AddTrackCommand("Gtr"), new AddInsertCommand("anode.limiter"));
        var gtr = (await client.ProjectAsync()).Tracks[0].Id;
        await client.EditAsync(new AddInsertCommand(FakeHost.FakeVst3Id, gtr));
        var project = await client.ProjectAsync();
        var (insert, master) = (project.Tracks[0].Strip.Inserts[0].Id, project.Master.Inserts[0].Id);
        (await client.PostAsync("/api/v1/project/save", null, Token)).EnsureSuccessStatusCode();
        var states = Path.Combine(created.Path, "Plugin States");
        Assert.True(File.Exists(Path.Combine(states, $"{gtr}.{insert}.bin")));
        Assert.True(File.Exists(Path.Combine(states, $"master.{master}.bin")));

        await client.PostAsync("/api/v1/project/close", null, Token);
        var open = await client.PostAsJsonAsync(
            "/api/v1/projects/open",
            new(created.Path),
            ContractsJsonContext.Default.OpenProjectRequest,
            Token
        );
        open.EnsureSuccessStatusCode();
        var host = server.Services.GetRequiredService<EngineConnection>().InProcessHost!;
        await ServerFixture.Eventually(() => Task.FromResult(
                host.Received.OfType<Ipc.LoadModelMessage>()
                    .Any(m => m.Replace
                        && m.Tracks.Any(t => t.Strip.Inserts!.Any(i => i.Id == insert && i.State is not null))
                        && m.Master.Inserts!.Any(i => i.Id == master && i.State is not null)
                    )
            )
        );
    }

    static Task<HttpResponseMessage> Editor(HttpClient client, PluginEditorRequest request) =>
        client.PostAsJsonAsync(
            "/api/v1/plugins/editor",
            request,
            ContractsJsonContext.Default.PluginEditorRequest,
            Token
        );

    static async Task<UndoHistory> History(HttpClient client) =>
        (await client.GetFromJsonAsync("/api/v1/project/history", ContractsJsonContext.Default.UndoHistory, Token))!;

    static Task WaitEngineAsync(ServerFixture server, HttpClient client) =>
        ServerFixture.Eventually(async () => (await server.SystemAsync(client)).Engine.State == EngineState.InProcess);

    static async Task<Job> StartAsync(HttpClient client, string path, BounceRequest? request) {
        var response = request is null
            ? await client.PostAsync(path, null, Token)
            : await client.PostAsJsonAsync(path, request, ContractsJsonContext.Default.BounceRequest, Token);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var job = (await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.Job, Token))!;
        Assert.Equal($"/api/v1/jobs/{job.Id}", response.Headers.Location?.ToString());
        return job;
    }
}
