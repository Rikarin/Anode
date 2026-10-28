using Anode.Contracts;
using Anode.Dsp.Analysis;
using Anode.Ipc.Segments;
using Anode.Model.Audio;
using Anode.Server.Engine;
using Anode.Server.Projects;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;

namespace Anode.Server.Tests;

/// <summary>
///     Phase 5 through the API (docs/06-phase-5.md, "Server"): file analysis and its cache and event, import analysis
///     and the tempo modes, commands that analyse, the analyze, bounce-in-place and process jobs, and what the host is
///     sent for warped regions and Varispeed. Analysis comes from <see cref="FakeAnalyzer" />.
/// </summary>
public sealed class FlexApiTests {
    const int Rate = 48_000;
    const long Beat = 28_800;
    static CancellationToken Token => TestContext.Current.CancellationToken;

    readonly string media = Directory.CreateTempSubdirectory("anode-flex-").FullName;

    [Fact]
    public async Task A_file_is_analysed_on_import_cached_in_the_package_and_announced() {
        var analyzer = new FakeAnalyzer();
        await using var server = new ServerFixture(analyzer: analyzer);
        using var client = server.CreateClient();
        await using var events = await EventStream.ConnectAsync(server);
        var project = await client.CreateProjectAsync();
        var file = await client.ImportAsync(TestWav.Write(media, "Loop.wav", frames: 8 * Beat));
        Assert.Equal(100, file.Tempo!.Bpm);
        await events.WaitAsync<AnalysisEvent>(e => e.FileId == file.Id);
        Assert.True(File.Exists(Path.Combine(project.Path, "Analysis", file.Id + ".json")));

        var analysis = (await client.GetFromJsonAsync(
            $"/api/v1/project/files/{file.Id}/analysis",
            ContractsJsonContext.Default.FileAnalysis,
            Token
        ))!;
        Assert.Equal(analyzer.Transients, analysis.Transients);
        Assert.Empty(analysis.Pitch);
        Assert.Equal(1, analyzer.RhythmRuns); // cached, not analysed again
        var unknown = await client.GetAsync("/api/v1/project/files/aud_none/analysis", Token);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Without_import_analysis_the_analysis_is_built_when_asked_for() {
        var analyzer = new FakeAnalyzer();
        await using var server = new ServerFixture(analyzer: analyzer);
        using var client = server.CreateClient();
        await using var events = await EventStream.ConnectAsync(server);
        await client.CreateProjectAsync();
        await client.EditAsync(new SetSmartTempoCommand(AnalyzeImports: false));
        var file = await client.ImportAsync(TestWav.Write(media, "Loop.wav", frames: 8 * Beat));
        Assert.Null(file.Tempo);
        Assert.Equal(0, analyzer.RhythmRuns);
        var analysis = (await client.GetFromJsonAsync(
            $"/api/v1/project/files/{file.Id}/analysis",
            ContractsJsonContext.Default.FileAnalysis,
            Token
        ))!;
        Assert.Equal(100, analysis.Tempo!.Bpm);
        await events.WaitAsync<AnalysisEvent>(e => e.FileId == file.Id);
    }

    [Fact]
    public async Task In_adapt_mode_the_first_audio_sets_the_project_tempo() {
        await using var server = new ServerFixture(analyzer: new FakeAnalyzer());
        using var client = server.CreateClient();
        await client.CreateProjectAsync();
        await client.EditAsync(new SetSmartTempoCommand(ProjectTempoMode.Adapt), new AddTrackCommand());
        var file = await client.ImportAsync(TestWav.Write(media, "Loop.wav", frames: 8 * Beat));
        var track = (await client.ProjectAsync()).Tracks[0].Id;
        await client.EditAsync(new AddRegionCommand(track, file.Id, 0));
        var project = await client.ProjectAsync();
        Assert.Equal(100, project.Settings.Tempo, 6);
        Assert.Equal(FlexFollow.Off, project.Regions[0].Follow);
    }

    [Fact]
    public async Task Commands_that_need_analysis_run_it_and_refuse_without_it() {
        var analyzer = new FakeAnalyzer();
        await using var server = new ServerFixture(analyzer: analyzer);
        using var client = server.CreateClient();
        await client.CreateProjectAsync();
        await client.EditAsync(new SetSmartTempoCommand(AnalyzeImports: false), new AddTrackCommand());
        var file = await client.ImportAsync(TestWav.Write(media, "Loop.wav", frames: 8 * Beat));
        var track = (await client.ProjectAsync()).Tracks[0].Id;
        await client.EditAsync(new AddRegionCommand(track, file.Id, 0));
        var region = (await client.ProjectAsync()).Regions[0].Id;
        await client.EditAsync(new SetRegionFlexCommand(region, FlexFollow.On), new QuantizeAudioCommand([region], 240));
        var project = await client.ProjectAsync();
        Assert.True(project.Tracks[0].FlexOn);
        Assert.NotEmpty(project.Regions[0].FlexMarkers);
        Assert.Equal(100, project.AudioFiles[0].Tempo!.Bpm);

        await client.EditAsync(new StripSilenceCommand([region]));
        Assert.Equal(8 * Beat / 30_000 + 1, (await client.ProjectAsync()).Regions.Count);

        analyzer.Fail = true;
        var other = await client.ImportAsync(TestWav.Write(media, "Other.wav", frames: 8 * Beat));
        await client.EditAsync(new AddRegionCommand(track, other.Id, 960 * 64));
        var unanalysed = (await client.ProjectAsync()).Regions.Single(r => r.FileId == other.Id).Id;
        var refused = await client.SendCommandsAsync(null, new SetRegionFlexCommand(unanalysed, FlexFollow.On));
        Assert.Contains("analyse", await ApiCalls.AssertProblemAsync(refused, HttpStatusCode.UnprocessableEntity));
    }

    [Fact]
    public async Task The_analyze_job_adds_the_pitch_curve_and_notes() {
        await using var server = new ServerFixture(analyzer: new FakeAnalyzer());
        using var client = server.CreateClient();
        await using var events = await EventStream.ConnectAsync(server);
        await client.CreateProjectAsync();
        var file = await client.ImportAsync(TestWav.Write(media, "Melody.wav", frames: 4 * Beat));
        var job = await StartAsync<ProcessRequest>(client, $"/api/v1/project/files/{file.Id}/analyze", null);
        Assert.Equal(JobKind.Analyze, job.Kind);
        await events.WaitAsync<JobEvent>(e => e.Job.Id == job.Id && e.Job.State == JobState.Done, 30);
        var analysis = (await client.GetFromJsonAsync(
            $"/api/v1/project/files/{file.Id}/analysis",
            ContractsJsonContext.Default.FileAnalysis,
            Token
        ))!;
        Assert.Equal(6_900, analysis.Pitch[0]!.Value, 3);
        Assert.Equal(2, analysis.Notes.Count);
        Assert.False(analysis.Polyphonic);
        var missing = await client.PostAsync("/api/v1/project/files/aud_none/analyze", null, Token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Bounce_in_place_renders_to_a_new_track_and_mutes_the_source_in_one_step() {
        await using var server = new ServerFixture(analyzer: new FakeAnalyzer());
        using var client = server.CreateClient();
        await using var events = await EventStream.ConnectAsync(server);
        await client.CreateProjectAsync();
        await client.EditAsync(new AddTrackCommand("Gtr"));
        var file = await client.ImportAsync(TestWav.Write(media, "Gtr.wav", frames: Rate));
        var track = (await client.ProjectAsync()).Tracks[0].Id;
        await client.EditAsync(new AddRegionCommand(track, file.Id, 960));
        await WaitEngineAsync(server, client);

        var bad = await PostAsync(client, "/api/v1/project/bounce-in-place", new BounceInPlaceRequest());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        var job = await StartAsync(client, "/api/v1/project/bounce-in-place", new BounceInPlaceRequest([track]));
        Assert.Equal(JobKind.BounceInPlace, job.Kind);
        var done = await events.WaitAsync<JobEvent>(e => e.Job.Id == job.Id && e.Job.State is JobState.Done or JobState.Failed, 30);
        Assert.Equal(JobState.Done, done.Job.State);

        var project = await client.ProjectAsync();
        Assert.Equal(["Gtr", "Gtr Bounce"], project.Tracks.Select(static t => t.Name));
        Assert.True(project.Tracks[0].Strip.Mute);
        var bounced = project.Regions.Single(r => r.TrackId == project.Tracks[1].Id);
        Assert.Equal(960, bounced.StartTick);
        // The region's second plus the two-second tail.
        var bouncedFile = project.AudioFiles.Single(f => f.Id == bounced.FileId);
        Assert.Equal(3 * Rate, bouncedFile.Frames);
        var (audio, _) = BounceProcessor.Read(Path.Combine(project.Path, bouncedFile.Path));
        // The fake host plays the region's file: the test sine (0.25 · sin(0.05 n)) from the start.
        Assert.Equal(0.25 * Math.Sin(0.05 * 1000), audio[0][1000], 4);
        Assert.Equal(0, audio[0][Rate + 100], 6);
        Assert.Equal("Bounce in Place", (await History(client)).Undo[0].Label);

        await client.PostResultAsync("/api/v1/project/undo");
        project = await client.ProjectAsync();
        Assert.Single(project.Tracks);
        Assert.False(project.Tracks[0].Strip.Mute);
    }

    [Fact]
    public async Task Selection_based_processing_replaces_part_of_a_region_with_a_processed_file() {
        await using var server = new ServerFixture(analyzer: new FakeAnalyzer());
        using var client = server.CreateClient();
        await using var events = await EventStream.ConnectAsync(server);
        await client.CreateProjectAsync();
        await client.EditAsync(new AddTrackCommand());
        var file = await client.ImportAsync(TestWav.Write(media, "Take.wav", frames: Rate));
        var track = (await client.ProjectAsync()).Tracks[0].Id;
        await client.EditAsync(new AddRegionCommand(track, file.Id, 0));
        var region = (await client.ProjectAsync()).Regions[0].Id;

        var unknown = await PostAsync(
            client,
            "/api/v1/project/process",
            new ProcessRequest([region], [new("com.example.none")])
        );
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
        // The second half of the region (a second is 1920 ticks at 120 bpm) through Gain +6 dB.
        var job = await StartAsync(
            client,
            "/api/v1/project/process",
            new ProcessRequest(
                [region],
                [new("anode.gain", new Dictionary<string, double> { ["gain"] = 6 }), new("anode.channel-eq")],
                960
            )
        );
        var done = await events.WaitAsync<JobEvent>(e => e.Job.Id == job.Id && e.Job.State is JobState.Done or JobState.Failed, 30);
        Assert.Equal(JobState.Done, done.Job.State);
        var project = await client.ProjectAsync();
        var parts = project.Regions.OrderBy(static r => r.StartTick).ToArray();
        Assert.Equal(2, parts.Length);
        Assert.Equal((region, 0L, Rate / 2L), (parts[0].Id, parts[0].FileOffset, parts[0].Length));
        Assert.Equal((960L, 0L, Rate / 2L), (parts[1].StartTick, parts[1].FileOffset, parts[1].Length));
        var processed = project.AudioFiles.Single(f => f.Id == parts[1].FileId);
        var (audio, _) = BounceProcessor.Read(Path.Combine(project.Path, processed.Path));
        var source = 0.25 * Math.Sin(0.05 * (Rate / 2 + 20_000));
        Assert.Equal(source * Math.Pow(10, 6 / 20.0), audio[0][20_000], 2);
        Assert.Equal("Process Regions", (await History(client)).Undo[0].Label);
        await client.PostResultAsync("/api/v1/project/undo");
        Assert.Equal(Rate, Assert.Single((await client.ProjectAsync()).Regions).Length);
    }

    [Fact]
    public async Task Warped_regions_and_Varispeed_reach_the_host_as_warp_maps() {
        await using var server = new ServerFixture(analyzer: new FakeAnalyzer());
        using var client = server.CreateClient();
        await client.CreateProjectAsync();
        await client.EditAsync(new AddTrackCommand());
        var file = await client.ImportAsync(TestWav.Write(media, "Loop.wav", frames: 8 * Beat));
        var track = (await client.ProjectAsync()).Tracks[0].Id;
        await client.EditAsync(new AddRegionCommand(track, file.Id, 0));
        await WaitEngineAsync(server, client);
        var host = server.Services.GetRequiredService<EngineConnection>().InProcessHost!;
        var region = (await client.ProjectAsync()).Regions[0].Id;
        await client.EditAsync(new SetTrackFlexCommand(track, true, FlexMode.Rhythmic), new SetRegionFlexCommand(region, FlexFollow.On));
        await ServerFixture.Eventually(() => Task.FromResult(host.PlaylistOf(0) is [{ Algorithm: FlexAlgorithm.Rhythmic }]));
        // 8 beats of 100 bpm at 120: 8 quarter notes, 4 s.
        Assert.Equal(4 * Rate, host.PlaylistOf(0)[0].Length);
        var warp = Assert.Single(host.WarpOf(0));
        Assert.Equal((4L * Rate, 8.0 * Beat), (warp[^1].Offset, warp[^1].FileFrame));

        await client.EditAsync(new SetVarispeedCommand(true, VarispeedMode.SpeedAndPitch, 0.8));
        await ServerFixture.Eventually(() => Task.FromResult(host.PlaylistOf(0) is [{ Algorithm: FlexAlgorithm.Speed }]));
        Assert.Equal(5 * Rate, host.PlaylistOf(0)[0].Length);
        // The transport reports positions in compiled samples: tick 960 is 0.625 s in.
        var status = await client.TransportAsync(TransportCommand.Locate, 960);
        Assert.Equal(30_000, status.PositionSamples);
    }

    static Task WaitEngineAsync(ServerFixture server, HttpClient client) =>
        ServerFixture.Eventually(async () => (await server.SystemAsync(client)).Engine.State == EngineState.InProcess);

    static Task<HttpResponseMessage> PostAsync<T>(HttpClient client, string path, T body) =>
        client.PostAsJsonAsync(path, body, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)ContractsJsonContext.Default.GetTypeInfo(typeof(T))!, Token);

    static async Task<Job> StartAsync<T>(HttpClient client, string path, T? body) where T : class {
        var response = body is null ? await client.PostAsync(path, null, Token) : await PostAsync(client, path, body);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.Job, Token))!;
    }

    static async Task<UndoHistory> History(HttpClient client) =>
        (await client.GetFromJsonAsync("/api/v1/project/history", ContractsJsonContext.Default.UndoHistory, Token))!;

    /// <summary>
    ///     A deterministic analyzer: transients every 1/16 of 100 bpm, every second one 300 frames late; steady 100 bpm
    ///     beats; a pitch of 6900 cents throughout, two notes; sound in 20 000 frames of every 30 000.
    /// </summary>
    public sealed class FakeAnalyzer : IAudioAnalyzer {
        int rhythmRuns;

        public long[] Transients { get; } = [..Enumerable.Range(0, 32).Select(static i => i * 7_200L + (i % 2) * 300)];

        public int RhythmRuns => Volatile.Read(ref rhythmRuns);

        public bool Fail { get; set; }

        long[] IAudioAnalyzer.Transients(float[] mono, int sampleRate) {
            Interlocked.Increment(ref rhythmRuns);
            return Fail ? throw new NotImplementedException("Phase 5 workstream D") : [..Transients.Where(t => t < mono.Length)];
        }

        public BeatTrack Beats(float[] mono, int sampleRate) {
            long[] beats = [..Enumerable.Range(0, (int)(mono.Length / Beat)).Select(static i => i * Beat)];
            return new(100, false, beats, [..beats.Where(static (_, i) => i % 4 == 0)], 1);
        }

        public PitchTrack Pitch(float[] mono, int sampleRate, int hop) {
            var n = mono.Length / hop;
            return new(hop, [..Enumerable.Repeat(6_900f, n)], [..Enumerable.Repeat(1f, n)]);
        }

        public NoteSegment[] Notes(PitchTrack track, int sampleRate) {
            var end = (long)track.Cents.Length * track.Hop;
            return [new(0, end / 2, 6_900, 1), new(end / 2, end, 6_900, 1)];
        }

        public (long Start, long End)[] Silence(
            float[] mono,
            int sampleRate,
            double thresholdDb,
            double minTimeMs,
            double preAttackMs,
            double postReleaseMs
        ) =>
            [
                ..Enumerable.Range(0, mono.Length / 30_000 + 1)
                    .Select(i => (i * 30_000L, Math.Min(mono.Length, i * 30_000L + 20_000)))
                    .Where(static x => x.Item1 < x.Item2)
            ];
    }
}
