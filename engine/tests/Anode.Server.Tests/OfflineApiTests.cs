using Anode.Contracts;
using Anode.Model.Storage;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Anode.Server.Tests;

/// <summary>A project whose audio files are gone: it opens, plays what it has, saves, and gets them back.</summary>
public sealed class OfflineApiTests : IDisposable {
    readonly string media = Directory.CreateTempSubdirectory("anode-offline-api-").FullName;

    static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(media, true);

    /// <summary>A saved project with a region of a one-second mono <c>Kick.wav</c>, its file taken away.</summary>
    async Task<(Project Project, AudioFile File)> ProjectWithoutItsFileAsync(HttpClient client) {
        var (project, file) = await SavedProjectAsync(client);
        File.Delete(Path.Combine(project.Path, file.Path));
        return (await ReopenAsync(client, project.Path), file);
    }

    async Task<(Project Project, AudioFile File)> SavedProjectAsync(HttpClient client) {
        var project = await client.CreateProjectAsync();
        var file = await client.ImportAsync(TestWav.Write(media, "Kick.wav"));
        await client.EditAsync(new AddTrackCommand("Drums"));
        await client.EditAsync(new AddRegionCommand((await client.ProjectAsync()).Tracks[0].Id, file.Id, 960));
        await SaveAsync(client);
        return (project, file);
    }

    static async Task SaveAsync(HttpClient client) =>
        Assert.True((await client.PostAsync("/api/v1/project/save", null, Token)).IsSuccessStatusCode);

    static async Task<Project> ReopenAsync(HttpClient client, string path) {
        var response = await client.PostAsJsonAsync(
            "/api/v1/projects/open",
            new(path),
            ContractsJsonContext.Default.OpenProjectRequest,
            Token
        );
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.Project, Token))!;
    }

    static Task<HttpResponseMessage> RelinkAsync(HttpClient client, string fileId, string path) =>
        client.PostAsJsonAsync(
            "/api/v1/project/offline/relink",
            new([new(fileId, path)]),
            ContractsJsonContext.Default.RelinkRequest,
            Token
        );

    static async Task<OfflineReport> OfflineAsync(HttpClient client) =>
        (await client.GetFromJsonAsync("/api/v1/project/offline", ContractsJsonContext.Default.OfflineReport, Token))!;

    static async Task<OfflineSearch> SearchAsync(HttpClient client, string? directory) {
        var response = await client.PostAsJsonAsync(
            "/api/v1/project/offline/search",
            new(directory),
            ContractsJsonContext.Default.OfflineSearchRequest,
            Token
        );
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.OfflineSearch, Token))!;
    }

    [Fact]
    public async Task A_project_without_its_files_opens_reports_them_and_saves_them() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();

        var (project, file) = await ProjectWithoutItsFileAsync(client);

        var offline = Assert.Single(project.Offline.Files);
        Assert.Equal((file.Id, OfflineReason.Missing, file.Path), (offline.FileId, offline.Reason, offline.Path));
        Assert.Equal(file.Id, Assert.Single(project.Regions).FileId);

        await client.EditAsync(new RenameProjectCommand("Still here"));
        await SaveAsync(client);
        var again = await ReopenAsync(client, project.Path);

        Assert.Equal("Still here", again.Name);
        Assert.Equal(file, Assert.Single(again.AudioFiles));
        Assert.Equal(file.Id, Assert.Single(again.Regions).FileId);
        Assert.Single(again.Offline.Files);
    }

    [Fact]
    public async Task A_file_the_project_lost_keeps_its_region_through_a_save() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        var (project, file) = await SavedProjectAsync(client);
        var root = Path.Combine(project.Path, ProjectPackage.ProjectFileName);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(root, Token))!.AsObject();
        json["audioFiles"] = new JsonArray();
        await File.WriteAllTextAsync(root, json.ToJsonString(), Token);

        var opened = await ReopenAsync(client, project.Path);

        Assert.Equal(OfflineReason.Unlisted, Assert.Single(opened.Offline.Files).Reason);
        await client.EditAsync(new RenameProjectCommand("Saved"));
        await SaveAsync(client);
        var again = await ReopenAsync(client, project.Path);
        var region = Assert.Single(again.Regions);
        Assert.Equal(file.Id, region.FileId);

        var relinked = TestWav.Write(media, "Other name.wav", channels: 2, frames: 96_000);
        Assert.Equal(HttpStatusCode.OK, (await RelinkAsync(client, file.Id, relinked)).StatusCode);

        var found = Assert.Single((await client.ProjectAsync()).AudioFiles);
        Assert.Equal((2, 96_000L, 24), (found.Channels, found.Frames, found.BitDepth));
        Assert.Empty((await OfflineAsync(client)).Files);
    }

    [Theory]
    [InlineData(44_100, 1, 48_000, "44100 Hz")]
    [InlineData(48_000, 2, 48_000, "2 channels")]
    [InlineData(48_000, 1, 24_000, "24000 frames")]
    public async Task A_relink_refuses_a_file_that_does_not_match_whatever_its_name(
        int rate,
        int channels,
        long frames,
        string complaint
    ) {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        var (project, file) = await ProjectWithoutItsFileAsync(client);
        var wrong = TestWav.Write(media, "Kick.wav", rate, channels, frames);

        var response = await RelinkAsync(client, file.Id, wrong);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(complaint, await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        Assert.Equal(file, Assert.Single((await client.ProjectAsync()).AudioFiles));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(project.Path, ProjectPackage.AudioFolder)));
    }

    [Fact]
    public async Task A_relink_is_an_undo_step_and_the_project_follows_it() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await using var events = await EventStream.ConnectAsync(server);
        var (project, file) = await ProjectWithoutItsFileAsync(client);
        var found = Directory.CreateDirectory(Path.Combine(media, "backup")).FullName;
        var right = TestWav.Write(found, "Renamed.wav");

        var response = await RelinkAsync(client, file.Id, right);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await events.WaitAsync<OfflineChangedEvent>(static e => e.Offline.Files.Count == 0);
        var relinked = Assert.Single((await client.ProjectAsync()).AudioFiles);
        Assert.Equal("Audio Files/Renamed.wav", relinked.Path);
        Assert.True(relinked.Version > file.Version);
        Assert.True(File.Exists(Path.Combine(project.Path, relinked.Path)));
        Assert.True(File.Exists(right));
        var peaks = await client.GetAsync($"/api/v1/project/files/{file.Id}/peaks?framesPerPeak=256", Token);
        Assert.Equal(HttpStatusCode.OK, peaks.StatusCode);
        Assert.Equal(
            "Relink Files",
            (await client.GetFromJsonAsync(
                "/api/v1/project/history",
                ContractsJsonContext.Default.UndoHistory,
                Token
            ))!.Undo[^1].Label
        );

        await client.PostResultAsync("/api/v1/project/undo");
        await events.WaitAsync<OfflineChangedEvent>(static e => e.Offline.Files.Count == 1);
        Assert.Equal(file.Path, Assert.Single((await client.ProjectAsync()).AudioFiles).Path);

        await client.PostResultAsync("/api/v1/project/redo");
        await events.WaitAsync<OfflineChangedEvent>(static e => e.Offline.Files.Count == 0);
    }

    [Fact]
    public async Task A_file_put_back_comes_online_when_the_disk_is_read_again() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await using var events = await EventStream.ConnectAsync(server);
        var (project, file) = await ProjectWithoutItsFileAsync(client);

        Assert.Single((await OfflineAsync(client)).Files);
        TestWav.Write(Path.Combine(project.Path, ProjectPackage.AudioFolder), "Kick.wav");

        Assert.Empty((await OfflineAsync(client)).Files);
        await events.WaitAsync<OfflineChangedEvent>(static e => e.Offline.Files.Count == 0);
        Assert.Empty((await client.ProjectAsync()).Offline.Files);
        Assert.Equal(file, Assert.Single((await client.ProjectAsync()).AudioFiles));
    }

    [Fact]
    public async Task A_file_deleted_while_open_is_offline_once_the_disk_is_read() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        var (project, file) = await SavedProjectAsync(client);
        Assert.Empty((await client.ProjectAsync()).Offline.Files);

        File.Delete(Path.Combine(project.Path, file.Path));

        Assert.Equal(file.Id, Assert.Single((await OfflineAsync(client)).Files).FileId);
    }

    [Fact]
    public async Task A_search_says_which_files_match() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        var (_, file) = await ProjectWithoutItsFileAsync(client);
        var right = Directory.CreateDirectory(Path.Combine(media, "found", "right")).FullName;
        var wrong = Directory.CreateDirectory(Path.Combine(media, "found", "wrong")).FullName;
        var good = TestWav.Write(right, "Kick.wav");
        TestWav.Write(wrong, "kick.wav", frames: 10);

        var search = await SearchAsync(client, Path.Combine(media, "found"));

        Assert.Equal(new OfflineCandidate(file.Id, good, null), search.Candidates[0]);
        Assert.NotNull(search.Candidates[1].Problem);
        Assert.Equal(2, search.Candidates.Count);
    }

    [Fact]
    public async Task A_search_of_the_project_finds_a_file_moved_inside_it() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        var (project, file) = await SavedProjectAsync(client);
        var moved = Directory.CreateDirectory(Path.Combine(project.Path, "Moved")).FullName;
        File.Move(Path.Combine(project.Path, file.Path), Path.Combine(moved, "Kick.wav"));

        var search = await SearchAsync(client, null);

        Assert.Equal(moved, Path.GetDirectoryName(Assert.Single(search.Candidates).Path));
    }

    [Fact]
    public async Task A_search_of_a_folder_that_is_not_there_is_a_404() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await client.CreateProjectAsync();

        var response = await client.PostAsJsonAsync(
            "/api/v1/project/offline/search",
            new(Path.Combine(media, "nowhere")),
            ContractsJsonContext.Default.OfflineSearchRequest,
            Token
        );

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_relink_of_a_file_the_project_does_not_have_is_a_404() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await client.CreateProjectAsync();

        var response = await RelinkAsync(client, "fil_none", TestWav.Write(media, "Kick.wav"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Saving_keeps_a_track_it_could_not_read() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        var (project, _) = await SavedProjectAsync(client);
        var id = project.Id;
        var track = (await client.ProjectAsync()).Tracks[0].Id;
        var path = Path.Combine(project.Path, ProjectPackage.TracksFolder, ProjectPackage.TrackFileName(track));
        await File.WriteAllTextAsync(path, "{ damaged", Token);

        var opened = await ReopenAsync(client, project.Path);

        Assert.Equal(id, opened.Id);
        Assert.Empty(opened.Tracks);
        Assert.Equal(track, Assert.Single(opened.Offline.UnreadableTracks));
        await client.EditAsync(new RenameProjectCommand("Saved"));
        await SaveAsync(client);
        Assert.Equal("{ damaged", await File.ReadAllTextAsync(path, Token));
        Assert.Equal(track, Assert.Single((await ReopenAsync(client, project.Path)).Offline.UnreadableTracks));
    }
}
