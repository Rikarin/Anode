using Anode.Contracts;
using Anode.Model.Audio;
using Anode.Model.Storage;
using Anode.Server.Projects;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Anode.Server.Tests;

/// <summary>Import from Project (docs/04-phase-3.md): chosen tracks of a package on the disk join the open project.</summary>
public sealed class ImportTracksApiTests : IDisposable {
    const long Bar = 3_840;
    readonly string media = Directory.CreateTempSubdirectory("anode-import-tracks-").FullName;

    static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(media, true);

    /// <summary>
    ///     A saved source: "Gtr" with a region, a compressor, a send to "Verb" on bus 1 and a volume lane; a stack "Keys" with
    ///     an instrument member that has notes; and "Other", which nothing here chooses.
    /// </summary>
    async Task<Project> SourceAsync(HttpClient client, int? rate = null) {
        await client.CreateProjectAsync("Source", rate, media);
        var file = await client.ImportAsync(TestWav.Write(media, "Riff.wav", rate ?? 48_000));
        await client.EditAsync(
            new AddTrackCommand("Gtr"),
            new AddAuxCommand(1, "Verb"),
            new AddTrackCommand("Keys Part", Kind: TrackKind.Instrument),
            new AddTrackCommand("Other")
        );
        var tracks = (await client.ProjectAsync()).Tracks;
        var (gtr, verb, keys) = (tracks[0].Id, tracks[1].Id, tracks[2].Id);
        await client.EditAsync(
            new AddRegionCommand(gtr, file.Id, 960),
            new AddInsertCommand("anode.compressor", gtr),
            new AddInsertCommand("anode.reverb", verb),
            new AddSendCommand(gtr, 1, -6),
            new SetAutomationCommand("volume", [new(0, -12, 0), new(Bar, 0, 0)], gtr),
            new AddMidiRegionCommand(keys, 0, Bar),
            new CreateStackCommand([keys], "Keys")
        );
        var region = (await client.ProjectAsync()).MidiRegions[0].Id;
        await client.EditAsync(new AddNotesCommand(region, [new(60, 100, 1, 0, 480), new(64, 100, 1, 0, 480)]));
        Assert.True((await client.PostAsync("/api/v1/project/save", null, Token)).IsSuccessStatusCode);
        return await client.ProjectAsync();
    }

    /// <summary>The project to import into: its own aux on bus 1, so a careless import would mix with it.</summary>
    static async Task<Project> TargetAsync(HttpClient client, int? rate = null) {
        await client.CreateProjectAsync("Target", rate);
        await client.EditAsync(new AddTrackCommand("Vox"), new AddAuxCommand(1, "Hall"));
        var tracks = (await client.ProjectAsync()).Tracks;
        await client.EditAsync(new AddSendCommand(tracks[0].Id, 1, -6));
        return await client.ProjectAsync();
    }

    static Task<Job> ImportAsync(
        HttpClient client,
        Project source,
        IReadOnlyList<string> trackIds,
        bool? regions = null,
        bool? inserts = null,
        bool? sends = null,
        bool? automation = null
    ) =>
        client.RunJobAsync(
            "/api/v1/project/import-tracks",
            new(source.Path, trackIds, regions, inserts, sends, automation),
            ContractsJsonContext.Default.ImportProjectTracksRequest
        );

    static string Id(Project project, string name) => project.Tracks.Single(t => t.Name == name).Id;

    static Dictionary<string, byte[]> Bytes(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(folder, f)
                    .StartsWith(ProjectPackage.PeaksFolder, StringComparison.Ordinal)
            )
            .ToDictionary(f => Path.GetRelativePath(folder, f), File.ReadAllBytes);

    [Fact]
    public async Task A_track_comes_with_its_regions_inserts_sends_and_automation_as_one_undo_step() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        var source = await SourceAsync(client);
        var sourceBytes = Bytes(source.Path);
        var target = await TargetAsync(client);

        var job = await ImportAsync(client, source, [Id(source, "Gtr")]);

        Assert.True(job.State == JobState.Done, job.Message);
        var now = await client.ProjectAsync();
        Assert.Equal(["Vox", "Hall", "Gtr", "Verb"], now.Tracks.Select(static t => t.Name));
        var (gtr, verb) = (now.Tracks[2], now.Tracks[3]);
        Assert.DoesNotContain(gtr.Id, source.Tracks.Select(static t => t.Id));

        // The audio is a copy, under a new id.
        var region = Assert.Single(now.Regions);
        Assert.Equal(gtr.Id, region.TrackId);
        var file = Assert.Single(now.AudioFiles);
        Assert.Equal(region.FileId, file.Id);
        Assert.NotEqual(source.AudioFiles[0].Id, file.Id);
        Assert.Equal(960, region.StartTick);
        Assert.Equal(
            await File.ReadAllBytesAsync(Path.Combine(source.Path, source.AudioFiles[0].Path), Token),
            await File.ReadAllBytesAsync(Path.Combine(target.Path, file.Path), Token)
        );
        Assert.Empty(now.Offline.Files);

        // Inserts and automation came along.
        Assert.Equal("anode.compressor", Assert.Single(gtr.Strip.Inserts).PluginId);
        Assert.Equal("anode.reverb", Assert.Single(verb.Strip.Inserts).PluginId);
        var lane = Assert.Single(now.Automation);
        Assert.Equal((gtr.Id, "volume", 2), (lane.TrackId, lane.Target, lane.Points.Count));

        // The send still reaches its aux, on a bus of its own: the project's aux on bus 1 hears nothing of it.
        var send = Assert.Single(gtr.Strip.Sends);
        Assert.NotEqual(1, send.Bus);
        Assert.Equal(verb.InputBus, send.Bus);
        Assert.Equal(1, now.Tracks[1].InputBus);

        // The source was only read.
        var after = Bytes(source.Path);
        Assert.Equal(sourceBytes.Keys.Order(), after.Keys.Order());
        Assert.All(sourceBytes, p => Assert.True(p.Value.AsSpan().SequenceEqual(after[p.Key]), p.Key));

        // One step, both ways.
        await client.PostResultAsync("/api/v1/project/undo");
        var undone = await client.ProjectAsync();
        Assert.Equal(["Vox", "Hall"], undone.Tracks.Select(static t => t.Name));
        Assert.Empty(undone.Regions);
        Assert.Empty(undone.Automation);
        await client.PostResultAsync("/api/v1/project/redo");
        Assert.Equal(4, (await client.ProjectAsync()).Tracks.Count);
    }

    [Fact]
    public async Task A_stack_brings_its_members_and_their_notes_with_new_ids() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        var source = await SourceAsync(client);
        await TargetAsync(client);

        Assert.Equal(JobState.Done, (await ImportAsync(client, source, [Id(source, "Keys")])).State);

        var now = await client.ProjectAsync();
        var stack = now.Tracks.Single(static t => t.Name == "Keys");
        var member = now.Tracks.Single(static t => t.Name == "Keys Part");
        Assert.Equal(stack.Id, member.ParentId);
        var region = Assert.Single(now.MidiRegions);
        Assert.Equal(member.Id, region.TrackId);
        Assert.Equal([60, 64], region.Notes.Select(static n => n.Pitch));
        Assert.Equal(2, region.Notes.Select(static n => n.Id).Distinct().Count());
    }

    [Fact]
    public async Task Importing_twice_makes_two_sets_that_share_nothing() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        var source = await SourceAsync(client);
        await TargetAsync(client);
        var gtr = Id(source, "Gtr");

        await ImportAsync(client, source, [gtr]);
        var job = await ImportAsync(client, source, [gtr]);

        Assert.Equal(JobState.Done, job.State);
        var now = await client.ProjectAsync();
        Assert.Equal(6, now.Tracks.Count);
        Assert.Equal(2, now.Regions.Select(static r => r.FileId).Distinct().Count());
        Assert.Equal(2, now.AudioFiles.Select(static f => f.Path).Distinct().Count());
        var auxes = now.Tracks.Where(static t => t.Kind == TrackKind.Aux).Select(static t => t.InputBus).ToArray();
        Assert.Equal(3, auxes.Distinct().Count());
    }

    [Fact]
    public async Task Left_out_parts_stay_behind() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        var source = await SourceAsync(client);
        await TargetAsync(client);

        var job = await ImportAsync(client, source, [Id(source, "Gtr")], false, false, false, false);

        Assert.Equal(JobState.Done, job.State);
        var now = await client.ProjectAsync();
        Assert.Equal(["Vox", "Hall", "Gtr"], now.Tracks.Select(static t => t.Name));
        var gtr = now.Tracks[2];
        Assert.Empty(gtr.Strip.Inserts);
        Assert.Empty(gtr.Strip.Sends);
        Assert.Empty(now.Regions);
        Assert.Empty(now.AudioFiles);
        Assert.Empty(now.Automation);
    }

    [Fact]
    public async Task Plugin_states_follow_their_plugins() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        var source = await SourceAsync(client);
        var gtr = source.Tracks.Single(static t => t.Name == "Gtr");
        var states = Path.Combine(source.Path, ProjectPackage.PluginStatesFolder);
        Directory.CreateDirectory(states);
        await File.WriteAllBytesAsync(
            Path.Combine(
                states,
                ProjectPackage.StateFileName(ProjectPackage.StateKey(gtr.Id, gtr.Strip.Inserts[0].Id))
            ),
            [1, 2, 3],
            Token
        );
        var target = await TargetAsync(client);

        await ImportAsync(client, source, [gtr.Id]);
        Assert.True((await client.PostAsync("/api/v1/project/save", null, Token)).IsSuccessStatusCode);

        var now = await client.ProjectAsync();
        var copied = now.Tracks.Single(static t => t.Name == "Gtr");
        Assert.Equal(
            [1, 2, 3],
            await File.ReadAllBytesAsync(
                Path.Combine(
                    target.Path,
                    ProjectPackage.PluginStatesFolder,
                    ProjectPackage.StateFileName(ProjectPackage.StateKey(copied.Id, copied.Strip.Inserts[0].Id))
                ),
                Token
            )
        );
    }

    [Fact]
    public async Task A_source_at_another_rate_is_converted_with_its_positions() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        var source = await SourceAsync(client, 44_100);
        var original = source.Regions[0];
        var target = await TargetAsync(client, 48_000);

        var job = await ImportAsync(client, source, [Id(source, "Gtr")]);

        Assert.True(job.State == JobState.Done, job.Message);
        var now = await client.ProjectAsync();
        var file = Assert.Single(now.AudioFiles);
        Assert.Equal(48_000, file.SampleRate);
        Assert.Equal(48_000, WavReader.Probe(Path.Combine(target.Path, file.Path)).SampleRate);
        Assert.Equal(52_245, file.Frames); // 48000 frames at 44.1 kHz
        var region = Assert.Single(now.Regions);
        Assert.Equal(52_245, region.Length);
        Assert.Equal(original.StartTick, region.StartTick);
        Assert.Empty(now.Offline.Files);
    }

    [Fact]
    public async Task An_offline_file_comes_along_offline_and_finds_no_stranger_of_its_name() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        var source = await SourceAsync(client);
        File.Delete(Path.Combine(source.Path, source.AudioFiles[0].Path));
        await TargetAsync(client);
        var stranger = await client.ImportAsync(TestWav.Write(media, "Riff.wav", frames: 100));

        var job = await ImportAsync(client, source, [Id(source, "Gtr")]);

        Assert.True(job.State == JobState.Done, job.Message);
        var now = await client.ProjectAsync();
        var offline = Assert.Single(now.Offline.Files);
        Assert.Equal(OfflineReason.Missing, offline.Reason);
        Assert.NotEqual(stranger.Path, offline.Path);
        Assert.Single(now.Regions);
        Assert.Equal(2, now.AudioFiles.Count);
        Assert.Equal(stranger.Path, now.AudioFiles.Single(f => f.Id == stranger.Id).Path);
    }

    [Fact]
    public async Task An_offline_file_that_would_need_converting_refuses_the_import() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        var source = await SourceAsync(client, 44_100);
        File.Delete(Path.Combine(source.Path, source.AudioFiles[0].Path));
        await TargetAsync(client, 48_000);

        var response = await client.PostAsJsonAsync(
            "/api/v1/project/import-tracks",
            new(source.Path, [Id(source, "Gtr")]),
            ContractsJsonContext.Default.ImportProjectTracksRequest,
            Token
        );

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task An_import_that_fails_removes_its_copies_and_leaves_the_project_as_it_was() {
        await using var server = new ServerFixture(editor: new RefusingEditor());
        using var client = server.CreateClient();
        var source = await SourceAsync(client, 44_100);
        var target = await TargetAsync(client, 48_000);

        var job = await ImportAsync(client, source, [Id(source, "Gtr")]);

        Assert.Equal(JobState.Failed, job.State);
        var now = await client.ProjectAsync();
        Assert.Equal((target.Revision, 2), (now.Revision, now.Tracks.Count));
        Assert.Empty(now.AudioFiles);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(target.Path, ProjectPackage.AudioFolder)));
    }

    [Fact]
    public async Task Too_few_free_buses_fail_with_nothing_changed() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        var source = await SourceAsync(client);
        await TargetAsync(client);
        await client.EditAsync([..Enumerable.Range(2, 63).Select(static b => new AddAuxCommand(b))]);
        var before = await client.ProjectAsync();

        var job = await ImportAsync(client, source, [Id(source, "Gtr")]);

        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("buses", job.Message, StringComparison.Ordinal);
        var now = await client.ProjectAsync();
        Assert.Equal(before.Revision, now.Revision);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(now.Path, ProjectPackage.AudioFolder)));
    }

    [Fact]
    public async Task The_browser_reads_a_project_without_opening_it() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        var source = await SourceAsync(client);
        var target = await TargetAsync(client);

        var response = await client.PostAsJsonAsync(
            "/api/v1/projects/contents",
            new(source.Path),
            ContractsJsonContext.Default.OpenProjectRequest,
            Token
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var contents = (await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.ProjectContents, Token))!;
        Assert.Equal(("Source", 48_000), (contents.Name, contents.SampleRate));
        var gtr = contents.Tracks.Single(static t => t.Name == "Gtr");
        Assert.Equal((1, 1, 1, 1), (gtr.Regions, gtr.Inserts, gtr.Sends, gtr.Lanes));
        Assert.Equal(target.Path, (await client.ProjectAsync()).Path);
    }

    [Fact]
    public async Task What_cannot_be_read_is_refused() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        var source = await SourceAsync(client);
        var newer = Path.Combine(source.Path, "project.json");
        var root = JsonNode.Parse(await File.ReadAllTextAsync(newer, Token))!;
        await TargetAsync(client);

        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(new(media, ["trk"]))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(new(source.Path, ["trk_gone"]))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAsync(new(source.Path, []))).StatusCode);

        root["schemaVersion"] = 999;
        await File.WriteAllTextAsync(newer, root.ToJsonString(), Token);
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            (await PostAsync(new(source.Path, [Id(source, "Gtr")]))).StatusCode
        );

        Task<HttpResponseMessage> PostAsync(ImportProjectTracksRequest request) =>
            client.PostAsJsonAsync(
                "/api/v1/project/import-tracks",
                request,
                ContractsJsonContext.Default.ImportProjectTracksRequest,
                Token
            );
    }

    [Fact]
    public async Task An_instruments_file_inside_the_source_comes_along() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        var source = await SourceAsync(client);
        var keys = Id(source, "Keys Part");
        var kits = Directory.CreateDirectory(Path.Combine(source.Path, "Samples")).FullName;
        await File.WriteAllTextAsync(Path.Combine(kits, "Kit.sfz"), "<region> sample=a.wav", Token);
        await client.PostAsJsonAsync(
            "/api/v1/projects/open",
            new(source.Path),
            ContractsJsonContext.Default.OpenProjectRequest,
            Token
        );
        await client.EditAsync(new AddTrackCommand("Sampled", Kind: TrackKind.Instrument, PluginId: "anode.sampler"));
        var sampled = (await client.ProjectAsync()).Tracks[^1].Id;
        await client.EditAsync(new LoadInstrumentFileCommand(sampled, "Samples/Kit.sfz"));
        Assert.True((await client.PostAsync("/api/v1/project/save", null, Token)).IsSuccessStatusCode);
        var target = await TargetAsync(client);

        var job = await ImportAsync(client, source, [sampled]);

        Assert.True(job.State == JobState.Done, job.Message);
        var track = (await client.ProjectAsync()).Tracks[^1];
        Assert.Equal("Samples/Kit.sfz", track.Instrument!.File);
        Assert.True(File.Exists(Path.Combine(target.Path, "Samples", "Kit.sfz")));
        Assert.NotEqual(keys, track.Id);
    }

    /// <summary>A converter that always fails.</summary>
    sealed class RefusingEditor : IAudioEditor {
        public (float[][] Channels, int SampleRate) Apply(
            float[][] channels,
            int sampleRate,
            AudioEditOperation operation
        ) =>
            throw new ArgumentException("The converter gave up.");

        public IReadOnlyList<Dsp.Editing.LoopCandidate> Loops(
            float[] mono,
            int sampleRate,
            long start,
            long end,
            int count,
            long minFrames
        ) =>
            [];

        public IReadOnlyList<Dsp.Editing.GrooveOnset> Onsets(float[] mono, int sampleRate) => [];
    }
}
