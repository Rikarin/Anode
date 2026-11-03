using Anode.Contracts;
using Anode.Ipc;
using Anode.Model.Audio;
using Anode.Server.Engine;
using Anode.Server.Projects;
using Anode.Server.Remote;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Anode.Server.Tests;

/// <summary>
///     Phase 10's server (docs/11-phase-10.md, workstream B) over REST and the sockets: monitoring, pads and discovery;
///     the movie and its jobs with a stand-in for FFmpeg (workstream E), the range stream and the tick ↔ movie mapping;
///     ADM BWF export and import with a stand-in writer and reader (workstream D); listen-in with a fake ring and framer.
/// </summary>
public sealed class Phase10ApiTests : IAsyncDisposable {
    static CancellationToken Token => TestContext.Current.CancellationToken;

    readonly string media = Directory.CreateTempSubdirectory("anode-phase10-").FullName;
    readonly FakeMovieMedia movies = new();
    readonly FakeSpatialFiles spatial = new();
    readonly FakeEncoders encoders = new();
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

    ServerFixture Server => server!;

    FakeHost Host => Server.Services.GetRequiredService<EngineConnection>().InProcessHost!;

    FakeTap Tap => (FakeTap)Server.Services.GetRequiredService<IMasterTapSource>();

    async Task StartAsync(string? token = null, bool fakeTap = true) {
        server = new(
            token: token,
            configure: s => {
                s.AddSingleton<IMovieMedia>(movies);
                s.AddSingleton<ISpatialFiles>(spatial);
                s.AddSingleton<IListenInEncoders>(encoders);
                if (fakeTap) {
                    s.AddSingleton<IMasterTapSource, FakeTap>();
                }
            }
        );
        client = server.CreateClient();
        if (token is not null) {
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        }

        await ServerFixture.Eventually(async () =>
            (await server.SystemAsync(Client)).Engine.State == EngineState.InProcess
        );
    }

    async Task<Job> PostJobAsync<T>(
        string path,
        T request,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type
    ) {
        var response = await Client.PostAsJsonAsync(path, request, type, Token);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Token));
        return (await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.Job, Token))!;
    }

    async Task<Job> DoneAsync<T>(string path, T request, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) {
        var job = await PostJobAsync(path, request, type);
        Job? now = null;
        await ServerFixture.Eventually(async () => {
                now = await Client.GetFromJsonAsync($"/api/v1/jobs/{job.Id}", ContractsJsonContext.Default.Job, Token);
                return now!.State is JobState.Done or JobState.Failed or JobState.Cancelled;
            }
        );
        Assert.True(now!.State == JobState.Done, now.Message);
        return now;
    }

    /// <summary>
    ///     A project at 120 BPM (1920 ticks a second) with a movie of 10 s at 25 fps imported at
    ///     <paramref name="startTick" />.
    /// </summary>
    async Task<Project> WithMovieAsync(long startTick = 3840, bool copy = true) {
        await Client.CreateProjectAsync();
        var path = Path.Combine(media, "Picture.mov");
        await File.WriteAllBytesAsync(path, [..Enumerable.Range(0, 1000).Select(static i => (byte)i)], Token);
        await DoneAsync(
            "/api/v1/project/movie",
            new(path, copy, startTick),
            ContractsJsonContext.Default.MovieImportRequest
        );
        return await Client.ProjectAsync();
    }

    [Fact]
    public async Task Monitoring_is_a_user_setting_sent_to_the_host_with_a_0_based_map() {
        await StartAsync();
        var initial = await Client.GetFromJsonAsync(
            "/api/v1/settings/monitoring",
            ContractsJsonContext.Default.MonitoringSettings,
            Token
        );
        Assert.Equal((false, null, 0), (initial!.Binaural, initial.DownmixTo, initial.SpeakerMap.Count));
        await ServerFixture.Eventually(() => Task.FromResult(Host.Monitoring is not null));

        var response = await Client.PutAsJsonAsync(
            "/api/v1/settings/monitoring",
            new MonitoringSettings(true, null, [3, 4, 0]),
            ContractsJsonContext.Default.MonitoringSettings,
            Token
        );
        response.EnsureSuccessStatusCode();
        await ServerFixture.Eventually(() => Task.FromResult(Host.Monitoring is { Binaural: true }));
        Assert.Equal([2, 3, -1], Host.Monitoring!.SpeakerMap);

        // A down-mix narrower than the master only; outputs 0…256.
        await Client.CreateProjectAsync();
        await ApiCalls.AssertProblemAsync(
            await Client.PutAsJsonAsync(
                "/api/v1/settings/monitoring",
                new MonitoringSettings(false, ChannelFormat.Stereo, []),
                ContractsJsonContext.Default.MonitoringSettings,
                Token
            ),
            HttpStatusCode.UnprocessableEntity
        );
        await ApiCalls.AssertProblemAsync(
            await Client.PutAsJsonAsync(
                "/api/v1/settings/monitoring",
                new MonitoringSettings(false, null, [300]),
                ContractsJsonContext.Default.MonitoringSettings,
                Token
            ),
            HttpStatusCode.UnprocessableEntity
        );
        await Client.EditAsync(new SetSurroundFormatCommand(ChannelFormat.Surround51));
        (await Client.PutAsJsonAsync(
            "/api/v1/settings/monitoring",
            new MonitoringSettings(false, ChannelFormat.Stereo, []),
            ContractsJsonContext.Default.MonitoringSettings,
            Token
        )).EnsureSuccessStatusCode();
        await ServerFixture.Eventually(() =>
            Task.FromResult(Host.Monitoring is { DownmixTo: EngineChannelFormat.Stereo })
        );

        // Back to a stereo master: the down-mix is not sent.
        await Client.EditAsync(new SetSurroundFormatCommand(ChannelFormat.Stereo));
        await ServerFixture.Eventually(() => Task.FromResult(Host.Monitoring is { DownmixTo: null, Binaural: false }));
        Assert.Equal(
            ChannelFormat.Stereo,
            (await Client.GetFromJsonAsync(
                "/api/v1/settings/monitoring",
                ContractsJsonContext.Default.MonitoringSettings,
                Token
            ))!
            .DownmixTo
        );
    }

    [Fact]
    public void Monitoring_messages_convert_the_map_and_drop_a_down_mix_no_narrower_than_the_master() {
        var message = MonitoringService.Message(
            new(false, ChannelFormat.Surround51, [1, 0, 8]),
            ChannelFormat.Surround714,
            "x.anhrtf"
        );
        Assert.Equal((EngineChannelFormat.Surround51, null), (message.DownmixTo, message.HrtfPath));
        Assert.Equal([0, -1, 7], message.SpeakerMap);
        message = MonitoringService.Message(
            new(true, ChannelFormat.Surround51, []),
            ChannelFormat.Surround51,
            "x.anhrtf"
        );
        Assert.Equal((null, "x.anhrtf", true), (message.DownmixTo, message.HrtfPath, message.Binaural));
    }

    [Fact]
    public async Task Pad_sets_are_user_settings_with_unique_ids() {
        await StartAsync();
        var empty = await Client.GetFromJsonAsync(
            "/api/v1/remote/pads",
            ContractsJsonContext.Default.RemotePads,
            Token
        );
        Assert.Empty(empty!.Sets);
        var pads = new RemotePads(
            [
                new(
                    "set-1",
                    " Edit ",
                    [new("pad-1", "Undo", "edit.undo", "#ff0000"), new("pad-2", "Cycle", "transport.cycle")]
                )
            ]
        );
        var saved = await (await Client.PutAsJsonAsync(
                "/api/v1/remote/pads",
                pads,
                ContractsJsonContext.Default.RemotePads,
                Token
            ))
            .Content.ReadFromJsonAsync(ContractsJsonContext.Default.RemotePads, Token);
        Assert.Equal("Edit", saved!.Sets[0].Name);
        var read = await Client.GetFromJsonAsync("/api/v1/remote/pads", ContractsJsonContext.Default.RemotePads, Token);
        Assert.Equal(["pad-1", "pad-2"], read!.Sets[0].Pads.Select(static p => p.Id));
        await ApiCalls.AssertProblemAsync(
            await Client.PutAsJsonAsync(
                "/api/v1/remote/pads",
                new RemotePads([new("set-1", "A", [new("set-1", "Undo", "edit.undo")])]),
                ContractsJsonContext.Default.RemotePads,
                Token
            ),
            HttpStatusCode.UnprocessableEntity
        );
        await ApiCalls.AssertProblemAsync(
            await Client.PutAsJsonAsync(
                "/api/v1/remote/pads",
                new RemotePads([new("set-2", "A", [new("pad", "Undo", string.Empty)])]),
                ContractsJsonContext.Default.RemotePads,
                Token
            ),
            HttpStatusCode.UnprocessableEntity
        );
    }

    [Fact]
    public async Task Discovery_names_the_server_its_project_and_whether_pairing_is_needed() {
        await StartAsync("secret");
        var info = await Client.GetFromJsonAsync(
            "/api/v1/system/discovery",
            ContractsJsonContext.Default.DiscoveryInfo,
            Token
        );
        Assert.Equal((Environment.MachineName, null, true), (info!.Name, info.ProjectName, info.PairingRequired));
        Assert.Equal(EngineConnection.Version, info.Version);
        await Client.CreateProjectAsync("Score Cue");
        info = await Client.GetFromJsonAsync(
            "/api/v1/system/discovery",
            ContractsJsonContext.Default.DiscoveryInfo,
            Token
        );
        Assert.Equal("Score Cue", info!.ProjectName);
        Assert.Equal(
            ["version=" + info.Version, "project=Score Cue", "pairing=1", "path=/api/v1"],
            DiscoveryService.Txt(info).Select(static p => $"{p.Key}={p.Value}")
        );
    }

    [Fact]
    public async Task A_movie_is_imported_copied_or_referenced_streamed_with_ranges_and_removed_with_undo() {
        await StartAsync();
        var project = await WithMovieAsync();
        var movie = project.Movie!;
        Assert.Equal(
            ("Movies/Picture.mov", "Picture", true, 3840L, 0.0, 25, 250L, "h264"),
            (movie.Path, movie.Name, movie.InProject, movie.StartTick, movie.OffsetSeconds, movie.FrameRate.Numerator,
                movie.Frames, movie.Codec)
        );
        Assert.True(File.Exists(Path.Combine(project.Path, "Movies", "Picture.mov")));
        Assert.Equal(
            movie,
            await Client.GetFromJsonAsync("/api/v1/project/movie", ContractsJsonContext.Default.Movie, Token)
        );

        // The stream answers ranges.
        using var range = new HttpRequestMessage(HttpMethod.Get, "/api/v1/project/movie/stream");
        range.Headers.Range = new(10, 19);
        var partial = await Client.SendAsync(range, Token);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal("video/quicktime", partial.Content.Headers.ContentType?.MediaType);
        Assert.Equal("bytes 10-19/1000", partial.Content.Headers.ContentRange?.ToString());
        Assert.Equal(
            Enumerable.Range(10, 10).Select(static i => (byte)i),
            await partial.Content.ReadAsByteArrayAsync(Token)
        );
        var whole = await Client.GetAsync("/api/v1/project/movie/stream", Token);
        Assert.Equal(HttpStatusCode.OK, whole.StatusCode);
        Assert.Equal(1000, (await whole.Content.ReadAsByteArrayAsync(Token)).Length);
        Assert.Contains("bytes", whole.Headers.AcceptRanges);

        // Remove Movie, then undo; the copied file stays.
        var removed = await Client.DeleteAsync("/api/v1/project/movie", Token);
        removed.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync("/api/v1/project/movie", Token)).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await Client.GetAsync("/api/v1/project/movie/stream", Token)).StatusCode
        );
        await ApiCalls.AssertProblemAsync(
            await Client.DeleteAsync("/api/v1/project/movie", Token),
            HttpStatusCode.UnprocessableEntity
        );
        await Client.PostResultAsync("/api/v1/project/undo");
        Assert.Equal(movie, (await Client.ProjectAsync()).Movie);
        Assert.True(File.Exists(Path.Combine(project.Path, "Movies", "Picture.mov")));

        // Referenced in place; a second copy gets its own name; not a movie; without FFmpeg.
        await DoneAsync(
            "/api/v1/project/movie",
            new(Path.Combine(media, "Picture.mov"), false),
            ContractsJsonContext.Default.MovieImportRequest
        );
        var referenced = (await Client.ProjectAsync()).Movie!;
        Assert.Equal(
            (Path.Combine(media, "Picture.mov"), false, 0L),
            (referenced.Path, referenced.InProject, referenced.StartTick)
        );
        await DoneAsync(
            "/api/v1/project/movie",
            new(Path.Combine(media, "Picture.mov")),
            ContractsJsonContext.Default.MovieImportRequest
        );
        Assert.Equal("Movies/Picture 2.mov", (await Client.ProjectAsync()).Movie!.Path);
        var text = Path.Combine(media, "notes.txt");
        await File.WriteAllTextAsync(text, "x", Token);
        await ApiCalls.AssertProblemAsync(
            await Client.PostAsJsonAsync(
                "/api/v1/project/movie",
                new(text),
                ContractsJsonContext.Default.MovieImportRequest,
                Token
            ),
            HttpStatusCode.UnprocessableEntity
        );
        movies.Available = false;
        await ApiCalls.AssertProblemAsync(
            await Client.PostAsJsonAsync(
                "/api/v1/project/movie",
                new(Path.Combine(media, "Picture.mov")),
                ContractsJsonContext.Default.MovieImportRequest,
                Token
            ),
            HttpStatusCode.ServiceUnavailable
        );
    }

    [Fact]
    public async Task Thumbnails_are_rendered_once_per_movie_and_height_and_served_as_JPEG() {
        await StartAsync();
        await WithMovieAsync();
        await DoneAsync(
            "/api/v1/project/movie/thumbnails",
            new ThumbnailsRequest([10, 0, 10]),
            ContractsJsonContext.Default.ThumbnailsRequest
        );
        var cached = await Client.GetFromJsonAsync(
            "/api/v1/project/movie/thumbnails",
            ContractsJsonContext.Default.MovieThumbnails,
            Token
        );
        Assert.Equal((114, 64), (cached!.Width, cached.Height));
        Assert.Equal([0L, 10], cached.Frames);
        var frame = await Client.GetAsync("/api/v1/project/movie/frames/10?height=64", Token);
        Assert.Equal(HttpStatusCode.OK, frame.StatusCode);
        Assert.Equal("image/jpeg", frame.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await Client.GetAsync("/api/v1/project/movie/frames/11", Token)).StatusCode
        );
        Assert.Empty(
            (await Client.GetFromJsonAsync(
                "/api/v1/project/movie/thumbnails?height=32",
                ContractsJsonContext.Default.MovieThumbnails,
                Token
            ))!.Frames
        );

        await DoneAsync(
            "/api/v1/project/movie/thumbnails",
            new ThumbnailsRequest([10, 20]),
            ContractsJsonContext.Default.ThumbnailsRequest
        );
        Assert.Equal(
            ["thumbnails 0,10 64", "thumbnails 20 64"],
            movies.Calls.Where(static c => c.StartsWith("thumbnails", StringComparison.Ordinal))
        );
        await ApiCalls.AssertProblemAsync(
            await Client.PostAsJsonAsync(
                "/api/v1/project/movie/thumbnails",
                new ThumbnailsRequest([250]),
                ContractsJsonContext.Default.ThumbnailsRequest,
                Token
            ),
            HttpStatusCode.UnprocessableEntity
        );
        await ApiCalls.AssertProblemAsync(
            await Client.PostAsJsonAsync(
                "/api/v1/project/movie/thumbnails",
                new ThumbnailsRequest([1], 4),
                ContractsJsonContext.Default.ThumbnailsRequest,
                Token
            ),
            HttpStatusCode.UnprocessableEntity
        );
    }

    [Fact]
    public async Task Scene_cuts_become_scene_markers_at_their_frames_ticks_through_the_movie_position() {
        await StartAsync();
        await WithMovieAsync();
        await Client.EditAsync(new AddMarkerCommand("Verse", 0));
        await DoneAsync(
            "/api/v1/project/movie/scenes",
            new(0.3),
            ContractsJsonContext.Default.SceneDetectRequest
        );
        // Frames 50 and 125 at 25 fps are 2 s and 5 s into the movie, which starts at 2 s (tick 3840 at 120 BPM).
        var markers = (await Client.ProjectAsync()).Markers.OrderBy(static m => m.Tick).ToArray();
        Assert.Equal(
            [
                ("Verse", 0L, MarkerKind.Marker), ("Scene 1", 7680, MarkerKind.Scene),
                ("Scene 2", 13440, MarkerKind.Scene)
            ],
            markers.Select(static m => (m.Name, m.Tick, m.Kind))
        );
        Assert.Contains("scenes 0.3", movies.Calls);

        // With the movie 3 s in at the project start: frame 100 (4 s) plays at 1 s; frame 25 (1 s) is before the start.
        await Client.EditAsync(new SetMoviePositionCommand(0, 3));
        movies.Cuts = [25, 100];
        await DoneAsync(
            "/api/v1/project/movie/scenes",
            new(),
            ContractsJsonContext.Default.SceneDetectRequest
        );
        Assert.Equal(
            [1920L],
            (await Client.ProjectAsync()).Markers.Where(static m => m.Kind == MarkerKind.Scene)
            .Select(static m => m.Tick)
        );
        await ApiCalls.AssertProblemAsync(
            await Client.PostAsJsonAsync(
                "/api/v1/project/movie/scenes",
                new(0),
                ContractsJsonContext.Default.SceneDetectRequest,
                Token
            ),
            HttpStatusCode.UnprocessableEntity
        );
    }

    [Fact]
    public async Task Audio_from_the_movie_starts_at_its_first_frame_and_one_cut_off_by_the_project_start_skips_in() {
        await StartAsync();
        await WithMovieAsync();
        await DoneAsync(
            "/api/v1/project/movie/audio/import",
            new(),
            ContractsJsonContext.Default.MovieAudioImportRequest
        );
        var project = await Client.ProjectAsync();
        var track = Assert.Single(project.Tracks);
        Assert.Equal(("Picture", ChannelFormat.Stereo), (track.Name, track.Strip.InputFormat));
        var region = Assert.Single(project.Regions);
        Assert.Equal((3840L, 0L, 480_000L), (region.StartTick, region.FileOffset, region.Length));
        Assert.Contains("extract 48000", movies.Calls);

        // The movie's first frame two seconds before the project start: the region starts at 0, 2 s into the file.
        await Client.EditAsync(new SetMoviePositionCommand(0, 2));
        movies.AudioChannels = 6;
        await DoneAsync(
            "/api/v1/project/movie/audio/import",
            new("Dialogue"),
            ContractsJsonContext.Default.MovieAudioImportRequest
        );
        project = await Client.ProjectAsync();
        var dialogue = project.Tracks.Single(static t => t.Name == "Dialogue");
        Assert.Equal(ChannelFormat.Surround51, dialogue.Strip.InputFormat);
        var second = project.Regions.Single(r => r.TrackId == dialogue.Id);
        Assert.Equal((0L, 96_000L, 384_000L), (second.StartTick, second.FileOffset, second.Length));
        Assert.Equal(6, project.AudioFiles.Single(f => f.Id == second.FileId).Channels);

        // One undo step each.
        await Client.PostResultAsync("/api/v1/project/undo");
        Assert.Single((await Client.ProjectAsync()).Tracks);
    }

    [Fact]
    public async Task Audio_exported_to_the_movie_is_the_bounce_of_its_span_aligned_to_its_first_frame() {
        await StartAsync();
        await WithMovieAsync();
        await Client.EditAsync(new AddTrackCommand("Score"));
        var job = await DoneAsync(
            "/api/v1/project/movie/audio/export",
            new("With Score", Codec: MovieAudioCodec.Pcm),
            ContractsJsonContext.Default.MovieAudioExportRequest
        );
        var project = await Client.ProjectAsync();
        Assert.Equal([Path.Combine(project.Path, "Exports", "With Score.mov")], job.Files);
        var (audio, rate, replace, codec, _) = movies.Remuxed!.Value;
        Assert.Equal((48_000, true, MovieAudioCodec.Pcm, 2), (rate, replace, codec, audio.Length));
        // The movie spans 2 s … 12 s of the project: 10 s of the bounce, its first sample the project's sample 96000.
        Assert.Equal(480_000, audio[0].Length);
        var render = Host.Received.OfType<RenderMessage>().Last();
        Assert.Equal((96_000L, 576_000L, null), (render.StartSample, render.EndSample, render.Tracks));
        Assert.True(audio[0].Max() > 0.1f);

        // Its first frame 1.5 s before the project start: 1.5 s of silence, then the project from its start.
        await Client.EditAsync(new SetMoviePositionCommand(0, 1.5));
        await DoneAsync(
            "/api/v1/project/movie/audio/export",
            new("Offset", Path.Combine(media, "out"), false),
            ContractsJsonContext.Default.MovieAudioExportRequest
        );
        (audio, _, replace, codec, var output) = movies.Remuxed!.Value;
        Assert.Equal((false, MovieAudioCodec.Aac, Path.Combine(media, "out", "Offset.mov")), (replace, codec, output));
        Assert.Equal(480_000, audio[0].Length);
        Assert.All(audio[0][..72_000], static s => Assert.Equal(0f, s));
        Assert.True(audio[0][72_000..].Max() > 0.1f);
        render = Host.Received.OfType<RenderMessage>().Last();
        Assert.Equal((0L, 408_000L), (render.StartSample, render.EndSample));

        await Client.EditAsync(new SetMoviePositionCommand(0, 0));
        await ApiCalls.AssertProblemAsync(
            await Client.PostAsJsonAsync(
                "/api/v1/project/movie/audio/export",
                new("Picture", Path.Combine(project.Path, "Movies")),
                ContractsJsonContext.Default.MovieAudioExportRequest,
                Token
            ),
            HttpStatusCode.UnprocessableEntity
        );
    }

    [Fact]
    public async Task Movie_endpoints_need_a_movie() {
        await StartAsync();
        await Client.CreateProjectAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync("/api/v1/project/movie", Token)).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await Client.GetAsync("/api/v1/project/movie/thumbnails", Token)).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await Client.GetAsync("/api/v1/project/movie/frames/0", Token)).StatusCode
        );
        await ApiCalls.AssertProblemAsync(
            await Client.PostAsJsonAsync(
                "/api/v1/project/movie/scenes",
                new(),
                ContractsJsonContext.Default.SceneDetectRequest,
                Token
            ),
            HttpStatusCode.NotFound
        );
        await ApiCalls.AssertProblemAsync(
            await Client.PostAsJsonAsync(
                "/api/v1/project/movie/audio/import",
                new(),
                ContractsJsonContext.Default.MovieAudioImportRequest,
                Token
            ),
            HttpStatusCode.NotFound
        );
    }

    [Fact]
    public async Task An_ADM_export_bounces_the_bed_and_each_object_before_the_renderer() {
        await StartAsync();
        await Client.CreateProjectAsync();
        await Client.EditAsync(
            new SetSurroundFormatCommand(ChannelFormat.Surround51),
            new AddTrackCommand("Strings"),
            new AddTrackCommand("Bee"),
            new AddTrackCommand("To pair 2")
        );
        var project = await Client.ProjectAsync();
        var (strings, bee, other) = (project.Tracks[0].Id, project.Tracks[1].Id, project.Tracks[2].Id);
        await Client.EditAsync(
            new SetStripFormatCommand(bee, ChannelFormat.Mono),
            new SetPannerCommand(bee, PannerMode.Object, Azimuth: 30),
            new SetAutomationCommand(PannerTargets.ObjectAzimuth, [new(0, 30, 0), new(1920, -110, 0)], bee),
            new SetOutputCommand(other, "output:2")
        );
        var job = await DoneAsync(
            "/api/v1/project/export/adm",
            new("Mix", StartTick: 0, EndTick: 7680, BitDepth: 32),
            ContractsJsonContext.Default.AdmExportRequest
        );
        Assert.Equal([Path.Combine(project.Path, "Exports", "Mix.wav")], job.Files);
        var scene = spatial.Written!;
        Assert.Equal(
            ("Song", 48_000, 192_000L, 32),
            (scene.Name, scene.SampleRate, scene.Frames, spatial.WrittenBitDepth)
        );
        var bed = Assert.Single(scene.Beds);
        Assert.Equal(ChannelFormat.Surround51, bed.Format);
        var item = Assert.Single(scene.Objects);
        Assert.Equal(("Bee", 1), (item.Name, item.Channels));
        // The stems rendered: the tracks to the master, not the one to a device output.
        var render = Host.Received.OfType<RenderMessage>().Last();
        Assert.Equal(2, render.Tracks!.Count);

        // The bed: the strings' stem in 5.1 (the fake host's stems carry L and R); the object: Bee's mono signal.
        var bedAudio = spatial.WrittenAudio["bed.wav"];
        Assert.Equal(6, bedAudio.Length);
        Assert.True(bedAudio[0].Max() > 0.1f);
        Assert.Equal(0f, bedAudio[2].Max());
        var objectAudio = Assert.Single(
                spatial.WrittenAudio,
                static p => p.Key.StartsWith("object", StringComparison.Ordinal)
            )
            .Value;
        Assert.Single(objectAudio);
        Assert.Equal(192_000, objectAudio[0].Length);

        // The automation as blocks: from 30° moving to −110° over the first second (20 ms blocks), then still.
        Assert.Equal(0, item.Blocks[0].StartFrame);
        Assert.Equal(192_000, item.Blocks.Sum(static b => b.DurationFrames));
        Assert.All(
            item.Blocks.Zip(item.Blocks.Skip(1)),
            static p => Assert.Equal(p.First.StartFrame + p.First.DurationFrames, p.Second.StartFrame)
        );
        var moving = item.Blocks.Where(static b => b.Interpolate).ToArray();
        Assert.True(moving.Length >= 50);
        Assert.All(moving, static b => Assert.True(b.DurationFrames <= 960));
        Assert.Equal(-110, item.Blocks[^1].Azimuth, 6);
        Assert.Equal(30, item.Blocks[0].Azimuth, 6);
        Assert.False(item.Blocks[^1].Interpolate);
        Assert.Equal(48_000, item.Blocks[^1].StartFrame);

        await ApiCalls.AssertProblemAsync(
            await Client.PostAsJsonAsync(
                "/api/v1/project/export/adm",
                new AdmExportRequest("Mix", TrackIds: [strings]),
                ContractsJsonContext.Default.AdmExportRequest,
                Token
            ),
            HttpStatusCode.UnprocessableEntity
        );
        await ApiCalls.AssertProblemAsync(
            await Client.PostAsJsonAsync(
                "/api/v1/project/export/adm",
                new("Mix", BitDepth: 16),
                ContractsJsonContext.Default.AdmExportRequest,
                Token
            ),
            HttpStatusCode.UnprocessableEntity
        );
    }

    [Fact]
    public async Task An_ADM_import_adds_beds_and_objects_in_one_undo_step_taking_the_bed_format() {
        await StartAsync();
        await Client.CreateProjectAsync();
        var bed = TestWav.Write(media, "bed.wav", channels: 6, encoding: WavEncoding.Float32);
        var voice = TestWav.Write(media, "voice.wav", encoding: WavEncoding.Float32);
        spatial.ToRead = new(
            "Film",
            48_000,
            48_000,
            [new("Main bed", ChannelFormat.Surround51, bed)],
            [
                new(
                    "Voice",
                    voice,
                    1,
                    [new(0, 24_000, 30, 0, 1, 0, false), new(24_000, 24_000, -110, 0, 1, 0, true)]
                )
            ]
        );
        var film = Path.Combine(media, "film.wav");
        await File.WriteAllBytesAsync(film, [.."RIFF"u8], Token);
        await DoneAsync(
            "/api/v1/project/import/adm",
            new(film),
            ContractsJsonContext.Default.AdmImportRequest
        );
        var project = await Client.ProjectAsync();
        Assert.Equal(ChannelFormat.Surround51, project.Settings.SurroundFormat);
        var (main, item) = (project.Tracks.Single(static t => t.Name == "Main bed"),
            project.Tracks.Single(static t => t.Name == "Voice"));
        Assert.Equal(
            (ChannelFormat.Surround51, ChannelFormat.Surround51, PannerMode.Surround),
            (main.Strip.InputFormat, main.Strip.OutputFormat, main.Strip.Panner.Mode)
        );
        Assert.Equal(
            (ChannelFormat.Mono, PannerMode.Object, 30.0),
            (item.Strip.InputFormat, item.Strip.Panner.Mode, item.Strip.Panner.Object.Azimuth)
        );
        var lane = project.Automation.Single(l => l.TrackId == item.Id);
        Assert.Equal(PannerTargets.ObjectAzimuth, lane.Target);
        Assert.Equal([(0L, 30.0), (960, 30), (1920, -110)], lane.Points.Select(static p => (p.Tick, p.Value)));
        Assert.Equal(
            6,
            project.AudioFiles.Single(f => f.Id == project.Regions.Single(r2 => r2.TrackId == main.Id).FileId).Channels
        );

        await Client.PostResultAsync("/api/v1/project/undo");
        project = await Client.ProjectAsync();
        Assert.Equal(ChannelFormat.Stereo, project.Settings.SurroundFormat);
        Assert.Empty(project.Tracks);

        spatial.ToRead = null;
        var job = await PostJobAsync(
            "/api/v1/project/import/adm",
            new(bed),
            ContractsJsonContext.Default.AdmImportRequest
        );
        await ServerFixture.Eventually(async () =>
            (await Client.GetFromJsonAsync($"/api/v1/jobs/{job.Id}", ContractsJsonContext.Default.Job, Token))!.State
            == JobState.Failed
        );
    }

    [Fact]
    public async Task Listen_in_sends_its_format_then_frames_with_the_tap_on_while_anyone_listens() {
        await StartAsync();
        await using var events = await EventStream.ConnectAsync(Server);
        using var socket = await ConnectListenAsync();
        var format = JsonSerializer.Deserialize(
            Encoding.UTF8.GetString(await ReceiveAsync(socket, WebSocketMessageType.Text)),
            ContractsJsonContext.Default.ListenInFormat
        );
        Assert.Equal(new(48_000, 2, 960, 128_000, 48_000), format);
        await ServerFixture.Eventually(() => Task.FromResult(Host.Tapping));
        await events.WaitAsync<ListenInEvent>(static e => e.State is { On: true, Listeners: 1 });

        // The ring is written as the host would: blocks of 480 frames at the transport's position, playing.
        using var writing = new CancellationTokenSource();
        var writer = Task.Run(
            async () => {
                long transport = 48_000;
                var block = new float[480 * 2];
                Array.Fill(block, 0.5f);
                while (!writing.IsCancellationRequested) {
                    Tap.Writer.Write(block, transport, true, false);
                    transport += 480;
                    await Task.Delay(5, CancellationToken.None);
                }
            },
            CancellationToken.None
        );
        var frames = new List<(uint Sequence, long Transport, byte Flags, float Value)>();
        while (frames.Count < 3) {
            var frame = await ReceiveAsync(socket, WebSocketMessageType.Binary);
            Assert.True(
                ListenIn.TryReadHeader(frame, out var sequence, out var transport, out var flags, out var packet)
            );
            frames.Add((sequence, transport, flags, BitConverter.ToSingle(packet)));
        }

        await writing.CancelAsync();
        await writer;
        Assert.Equal([0u, 1, 2], frames.Select(static f => f.Sequence));
        Assert.All(frames, static f => Assert.Equal(0.5f, f.Value));
        Assert.All(frames, static f => Assert.True((f.Flags & ListenIn.FlagPlaying) != 0));
        Assert.True((frames[0].Flags & ListenIn.FlagDiscontinuity) != 0);
        Assert.Equal(frames[0].Transport + 960, frames[1].Transport);
        Assert.True(frames[0].Transport >= 48_000);
        var state = await Client.GetFromJsonAsync(
            "/api/v1/remote/listen/state",
            ContractsJsonContext.Default.ListenInState,
            Token
        );
        Assert.True(state is { On: true, Listeners: 1, LatencyMs: > 0 });

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, Token);
        await ServerFixture.Eventually(() => Task.FromResult(!Host.Tapping && encoders.Disposed == 1));
        await events.WaitAsync<ListenInEvent>(static e => e.State is { On: false, Listeners: 0, LatencyMs: null });
    }

    [Fact]
    public async Task At_most_four_remotes_listen_in() {
        await StartAsync();
        var sockets = new List<WebSocket>();
        for (var i = 0; i < ListenIn.MaxListeners; i++) {
            sockets.Add(await ConnectListenAsync());
        }

        var refused = await Assert.ThrowsAnyAsync<Exception>(() => ConnectListenAsync());
        Assert.Contains("503", refused.Message, StringComparison.Ordinal);
        Assert.Equal(
            4,
            (await Client.GetFromJsonAsync(
                "/api/v1/remote/listen/state",
                ContractsJsonContext.Default.ListenInState,
                Token
            ))!.Listeners
        );
        Assert.Equal(1, Host.Received.OfType<SetMasterTapMessage>().Count(static m => m.Enabled));

        await sockets[0].CloseAsync(WebSocketCloseStatus.NormalClosure, null, Token);
        await ServerFixture.Eventually(async () =>
            (await Client.GetFromJsonAsync(
                "/api/v1/remote/listen/state",
                ContractsJsonContext.Default.ListenInState,
                Token
            ))!.Listeners
            == 3
        );
        sockets[0] = await ConnectListenAsync();
        Assert.True(Host.Tapping);
        foreach (var socket in sockets) {
            socket.Dispose();
        }
    }

    [Fact]
    public async Task The_fake_host_taps_its_master_and_listen_in_reads_it() {
        await StartAsync(fakeTap: false);
        await Client.CreateProjectAsync();
        await Client.TransportAsync(TransportCommand.Play);
        using var socket = await ConnectListenAsync();
        await ReceiveAsync(socket, WebSocketMessageType.Text);
        var frame = await ReceiveAsync(socket, WebSocketMessageType.Binary, 10);
        Assert.True(ListenIn.TryReadHeader(frame, out _, out var transport, out var flags, out _));
        Assert.True((flags & ListenIn.FlagPlaying) != 0 && transport >= 0);
        await Client.TransportAsync(TransportCommand.Stop);
    }

    [Fact]
    public async Task Listen_in_checks_the_origin_without_a_token_and_takes_the_token_in_the_query() {
        await StartAsync();
        var foreign = Server.Server.CreateWebSocketClient();
        foreign.ConfigureRequest = static r => r.Headers.Origin = "http://evil.example";
        var e = await Assert.ThrowsAnyAsync<Exception>(() =>
            foreign.ConnectAsync(new(Server.Server.BaseAddress, "/api/v1/remote/listen"), Token)
        );
        Assert.Contains("403", e.Message, StringComparison.Ordinal);
        var local = Server.Server.CreateWebSocketClient();
        local.ConfigureRequest = static r => r.Headers.Origin = "http://localhost:4200";
        using var socket = await local.ConnectAsync(new(Server.Server.BaseAddress, "/api/v1/remote/listen"), Token);
        Assert.Equal(WebSocketState.Open, socket.State);
        await Server.DisposeAsync();
        server = null;

        await StartAsync("secret");
        var anonymous = Server.Server.CreateWebSocketClient();
        e = await Assert.ThrowsAnyAsync<Exception>(() =>
            anonymous.ConnectAsync(new(Server.Server.BaseAddress, "/api/v1/remote/listen"), Token)
        );
        Assert.Contains("401", e.Message, StringComparison.Ordinal);
        using var paired = await Server.Server.CreateWebSocketClient()
            .ConnectAsync(new(Server.Server.BaseAddress, "/api/v1/remote/listen?access_token=secret"), Token);
        Assert.Equal(WebSocketState.Open, paired.State);
    }

    Task<WebSocket> ConnectListenAsync() =>
        Server.Server.CreateWebSocketClient()
            .ConnectAsync(new(Server.Server.BaseAddress, "/api/v1/remote/listen"), Token);

    static async Task<byte[]> ReceiveAsync(WebSocket socket, WebSocketMessageType type, int seconds = 5) {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        var buffer = new byte[4096];
        var length = 0;
        WebSocketReceiveResult result;
        do {
            result = await socket.ReceiveAsync(
                new(buffer, length, buffer.Length - length),
                timeout.Token
            );
            length += result.Count;
        } while (!result.EndOfMessage);

        Assert.Equal(type, result.MessageType);
        return buffer[..length];
    }
}
