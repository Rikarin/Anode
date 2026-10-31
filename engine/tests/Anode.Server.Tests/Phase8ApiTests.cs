using Anode.Contracts;
using Anode.Ipc;
using Anode.Model.Audio;
using Anode.Server.Engine;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;

namespace Anode.Server.Tests;

/// <summary>
///     Phase 8's sync plumbing and Stem Splitter job (docs/09-phase-8.md, workstream B), against the in-process
///     <see cref="FakeHost" /> and a stand-in separator (<see cref="TestSeparator" />).
/// </summary>
public sealed class Phase8ApiTests : IAsyncDisposable {
    static CancellationToken Token => TestContext.Current.CancellationToken;

    readonly string media = Directory.CreateTempSubdirectory("anode-stems-").FullName;
    ServerFixture? server;
    HttpClient? client;

    public async ValueTask DisposeAsync() {
        client?.Dispose();
        if (server is not null) {
            await server.DisposeAsync();
        }

        Directory.Delete(media, true);
    }

    HttpClient Client => client!;

    FakeHost Host => server!.Services.GetRequiredService<EngineConnection>().InProcessHost!;

    async Task StartAsync(TestStemModels? models = null) {
        server = new(configure: s => s.AddSingleton<Projects.IStemModels>(models ?? new TestStemModels()));
        client = server.CreateClient();
        await ServerFixture.Eventually(async () =>
            (await server.SystemAsync(Client)).Engine.State == EngineState.InProcess
        );
    }

    Task<SyncState?> SyncAsync() =>
        Client.GetFromJsonAsync("/api/v1/project/sync", ContractsJsonContext.Default.SyncState, Token);

    [Fact]
    public async Task The_projects_sync_settings_reach_the_host_and_its_state_comes_back() {
        await StartAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync("/api/v1/project/sync", Token)).StatusCode);
        await Client.CreateProjectAsync();
        await ServerFixture.Eventually(() => Task.FromResult(Host.Sync is { Source: EngineSyncSource.Internal }));
        Assert.Equal(new(SyncSource.Internal, true, null, 0), await SyncAsync());

        await using var events = await EventStream.ConnectAsync(server!);
        await Client.EditAsync(
            new SetSyncSettingsCommand(
                SyncSource.MidiClock,
                [new("fake-out", true, true, 4.5)],
                MtcFrameRate.Fps30,
                3600,
                ClockInPort: "fake-in"
            )
        );
        await ServerFixture.Eventually(() => Task.FromResult(Host.Sync is { Source: EngineSyncSource.MidiClock }));
        var sent = Host.Sync!;
        Assert.Equal(
            (EngineMtcRate.Fps30, 3600d, "fake-in", (string?)null),
            (sent.FrameRate, sent.StartOffsetSeconds, sent.ClockInPort, sent.MtcInPort)
        );
        Assert.Equal([new EngineSyncDestination("fake-out", true, true, 4.5)], sent.Destinations);
        // The fake host answers: not locked until clock arrives.
        await events.WaitAsync<SyncEvent>(static e => e.State is { Source: SyncSource.MidiClock, Locked: false });

        await Host.ReportSyncStateAsync(new(EngineSyncSource.MidiClock, true, 100.2, 0));
        await events.WaitAsync<SyncEvent>(static e => e.State is { Locked: true, ExternalTempo: 100.2 });
        Assert.Equal(new(SyncSource.MidiClock, true, 100.2, 0), await SyncAsync());

        // Undo is a settings change like any other.
        await Client.PostResultAsync("/api/v1/project/undo");
        await ServerFixture.Eventually(() => Task.FromResult(Host.Sync is { Source: EngineSyncSource.Internal }));
        var json = await Client.GetStringAsync("/api/v1/project", Token);
        Assert.Contains("\"sync\":{\"source\":\"internal\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sync_events_go_out_at_most_ten_a_second_and_the_last_one_always() {
        await StartAsync();
        await Client.CreateProjectAsync();
        await Client.EditAsync(new SetSyncSettingsCommand(SyncSource.Link));
        await ServerFixture.Eventually(() => Task.FromResult(Host.Sync is { Source: EngineSyncSource.Link }));
        await using var events = await EventStream.ConnectAsync(server!);
        for (var peers = 1; peers <= 30; peers++) {
            await Host.ReportSyncStateAsync(new(EngineSyncSource.Link, true, 120, peers));
        }

        await events.WaitAsync<SyncEvent>(static e => e.State.LinkPeers == 30);
        Assert.InRange(events.OfType<SyncEvent>().Count(), 1, 3);
    }

    [Fact]
    public async Task Following_Link_the_sessions_tempo_becomes_the_projects_in_one_undo_step_and_clock_tempo_does_not() {
        await StartAsync();
        await Client.CreateProjectAsync();
        async Task<int> StepsAsync() =>
            (await Client.GetFromJsonAsync("/api/v1/project/history", ContractsJsonContext.Default.UndoHistory, Token))!
            .Undo.Count;

        // MIDI Clock's tempo moves the host's transport only.
        await Client.EditAsync(new SetSyncSettingsCommand(SyncSource.MidiClock));
        await Host.ReportSyncStateAsync(new(EngineSyncSource.MidiClock, true, 100.2, 0));
        await ServerFixture.Eventually(async () => (await SyncAsync())?.ExternalTempo == 100.2);
        Assert.Equal(120, (await Client.ProjectAsync()).Settings.Tempo);

        await Client.EditAsync(new SetSyncSettingsCommand(SyncSource.Link));
        var steps = await StepsAsync();
        foreach (var bpm in new[] { 128, 128.5, 129.25 }) {
            await Host.ReportSyncStateAsync(new(EngineSyncSource.Link, true, bpm, 1));
            await ServerFixture.Eventually(async () => (await Client.ProjectAsync()).Settings.Tempo == bpm);
        }

        // One step for the burst; undone, the tempo is the project's own again.
        Assert.Equal(steps + 1, await StepsAsync());
        // The same tempo again writes nothing.
        await Host.ReportSyncStateAsync(new(EngineSyncSource.Link, true, 129.25, 1));
        await Task.Delay(100, Token);
        Assert.Equal(steps + 1, await StepsAsync());
        await Client.PostResultAsync("/api/v1/project/undo");
        Assert.Equal(120, (await Client.ProjectAsync()).Settings.Tempo);
    }

    /// <summary>A project with an audio region of a stereo file at tick 960, from frame 4800 for 24000 frames.</summary>
    async Task<(string Track, AudioRegion Region)> RegionAsync() {
        await Client.CreateProjectAsync();
        await Client.EditAsync(new AddTrackCommand("Mix", Stereo: true), new AddTrackCommand("After"));
        var path = TestWav.Write(media, "Mix.wav", channels: 2, frames: 48_000);
        var file = await Client.ImportAsync(path);
        var project = await Client.ProjectAsync();
        await Client.EditAsync(
            new SetOutputCommand(project.Tracks[0].Id, "output:2"),
            new AddRegionCommand(project.Tracks[0].Id, file.Id, 960, 4800, 24_000)
        );
        project = await Client.ProjectAsync();
        return (project.Tracks[0].Id, project.Regions.Single());
    }

    async Task<Job> SplitAsync(string regionId, StemSplitRequest request) {
        var response = await Client.PostAsJsonAsync(
            $"/api/v1/project/regions/{regionId}/split-stems",
            request,
            ContractsJsonContext.Default.StemSplitRequest,
            Token
        );
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var job = (await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.Job, Token))!;
        Assert.Equal(JobKind.StemSplit, job.Kind);
        return job;
    }

    async Task<Job> WaitAsync(string jobId, params JobState[] states) {
        Job? job = null;
        await ServerFixture.Eventually(async () => {
                job = await Client.GetFromJsonAsync($"/api/v1/jobs/{jobId}", ContractsJsonContext.Default.Job, Token);
                return states.Contains(job!.State);
            }
        );
        return job!;
    }

    [Fact]
    public async Task Splitting_a_region_adds_a_track_per_stem_below_it_as_one_undo_step() {
        var models = new TestStemModels();
        await StartAsync(models);
        var (track, region) = await RegionAsync();
        var history = (await Client.GetFromJsonAsync(
                "/api/v1/project/history",
                ContractsJsonContext.Default.UndoHistory,
                Token
            ))!
            .Undo.Count;
        var job = await WaitAsync((await SplitAsync(region.Id, new())).Id, JobState.Done, JobState.Failed);
        Assert.True(job.State == JobState.Done, job.Message);
        Assert.Equal(4, job.Files.Count);
        Assert.Equal([Stems.StemModel.FourStems], models.Opened);

        var project = await Client.ProjectAsync();
        Assert.Equal(
            ["Mix", "Mix – Vocals", "Mix – Drums", "Mix – Bass", "Mix – Other", "After"],
            project.Tracks.Select(static t => t.Name)
        );
        Assert.All(project.Tracks.Skip(1).Take(4), static t => Assert.Equal("output:2", t.Strip.Output));
        Assert.True(project.Regions.Single(r => r.Id == region.Id).Muted);
        var stems = project.Regions.Where(r => r.Id != region.Id).ToArray();
        Assert.Equal(4, stems.Length);
        Assert.All(stems, static r => Assert.Equal((960L, 0L, 24_000L), (r.StartTick, r.FileOffset, r.Length)));
        Assert.Equal(
            history + 1,
            (await Client.GetFromJsonAsync("/api/v1/project/history", ContractsJsonContext.Default.UndoHistory, Token))!
            .Undo.Count
        );

        // Each stem file is the region's span of the source, as the separator made it (drums: × 0.2).
        var drumsTrack = project.Tracks[2].Id;
        var drums = project.AudioFiles.Single(f => f.Id == stems.Single(r => r.TrackId == drumsTrack).FileId);
        var folder = (await Client.ProjectAsync()).Path;
        var (audio, rate) = BounceProcessor.Read(Path.Combine(folder, drums.Path));
        var (source, _) = BounceProcessor.Read(Path.Combine(media, "Mix.wav"));
        Assert.Equal((48_000, 2, 24_000), (rate, audio.Length, audio[0].Length));
        Assert.Equal(source[1][4800 + 100] * 0.2f, audio[1][100], 3);

        await Client.PostResultAsync("/api/v1/project/undo");
        project = await Client.ProjectAsync();
        Assert.Equal(["Mix", "After"], project.Tracks.Select(static t => t.Name));
        Assert.False(project.Regions.Single().Muted);
        Assert.Equal(track, project.Tracks[0].Id);
    }

    [Fact]
    public async Task Guitar_or_piano_runs_the_six_stem_model_and_keeping_the_source_leaves_it_playing() {
        var models = new TestStemModels();
        await StartAsync(models);
        var (_, region) = await RegionAsync();
        var job = await WaitAsync(
            (await SplitAsync(region.Id, new([StemKind.Piano, StemKind.Bass], true))).Id,
            JobState.Done,
            JobState.Failed
        );
        Assert.True(job.State == JobState.Done, job.Message);
        Assert.Equal([Stems.StemModel.SixStems], models.Opened);
        var project = await Client.ProjectAsync();
        Assert.Equal(["Mix", "Mix – Bass", "Mix – Piano", "After"], project.Tracks.Select(static t => t.Name));
        Assert.False(project.Regions.Single(r => r.Id == region.Id).Muted);
    }

    [Fact]
    public async Task A_cancelled_split_leaves_neither_files_nor_tracks() {
        await StartAsync(new(true));
        var (_, region) = await RegionAsync();
        var job = await SplitAsync(region.Id, new());
        await WaitAsync(job.Id, JobState.Running);
        var cancelled = await Client.PostAsync($"/api/v1/jobs/{job.Id}/cancel", null, Token);
        Assert.True(cancelled.IsSuccessStatusCode);
        await WaitAsync(job.Id, JobState.Cancelled);
        var project = await Client.ProjectAsync();
        Assert.Equal(2, project.Tracks.Count);
        Assert.Single(project.AudioFiles);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(project.Path, "Audio Files"), "*.wav"));
    }

    [Fact]
    public async Task Splitting_is_checked() {
        await StartAsync();
        var (_, region) = await RegionAsync();
        var unknown = await Client.PostAsJsonAsync(
            "/api/v1/project/regions/rgn_gone/split-stems",
            new(),
            ContractsJsonContext.Default.StemSplitRequest,
            Token
        );
        await ApiCalls.AssertProblemAsync(unknown, HttpStatusCode.NotFound);
        var twice = await Client.PostAsJsonAsync(
            $"/api/v1/project/regions/{region.Id}/split-stems",
            new StemSplitRequest([StemKind.Bass, StemKind.Bass]),
            ContractsJsonContext.Default.StemSplitRequest,
            Token
        );
        await ApiCalls.AssertProblemAsync(twice, HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Without_its_model_the_Stem_Splitter_fails_the_job_with_where_to_put_it() {
        server = new();
        client = server.CreateClient();
        var (_, region) = await RegionAsync();
        var job = await WaitAsync((await SplitAsync(region.Id, new())).Id, JobState.Failed);
        Assert.Contains("not", job.Message, StringComparison.Ordinal);
        Assert.Equal(2, (await Client.ProjectAsync()).Tracks.Count);
    }
}
