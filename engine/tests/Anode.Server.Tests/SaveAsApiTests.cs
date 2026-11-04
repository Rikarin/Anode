using Anode.Contracts;
using Anode.Model.Storage;
using Anode.Server.Projects;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Anode.Server.Tests;

/// <summary>
///     Save As and Save a Copy (docs/04-phase-3.md): the whole package somewhere else, with the failures that must leave
///     everything where it was, and autosave and offline files around them.
/// </summary>
public sealed class SaveAsApiTests : IDisposable {
    readonly string data = Directory.CreateTempSubdirectory("anode-saveas-data-").FullName;
    readonly string places = Directory.CreateTempSubdirectory("anode-saveas-").FullName;

    static CancellationToken Token => TestContext.Current.CancellationToken;

    string Place(string name) => Path.Combine(places, name + ProjectPackage.Extension);

    public void Dispose() {
        Directory.Delete(places, true);
        Directory.Delete(data, true);
    }

    ServerFixture Start() =>
        new(data: data, configure: s => s.Configure<ServerOptions>(static o => o.AutosaveSeconds = 0));

    static ProjectSession Session(ServerFixture server) => server.Services.GetRequiredService<ProjectSession>();

    /// <summary>A saved project with a region of a file, and an unsaved track named "Unsaved".</summary>
    async Task<(Project Project, AudioFile File)> ProjectAsync(HttpClient client) {
        var project = await client.CreateProjectAsync("Original", directory: places);
        var file = await client.ImportAsync(TestWav.Write(places, "Kick.wav"));
        await client.EditAsync(new AddTrackCommand("Drums"));
        await client.EditAsync(new AddRegionCommand((await client.ProjectAsync()).Tracks[0].Id, file.Id, 960));
        await SaveAsync(client);
        await client.EditAsync(new AddTrackCommand("Unsaved"));
        return (await client.ProjectAsync(), file);
    }

    static async Task SaveAsync(HttpClient client) =>
        Assert.True((await client.PostAsync("/api/v1/project/save", null, Token)).IsSuccessStatusCode);

    static Task<Job> SaveToAsync(HttpClient client, string kind, string path) =>
        client.RunJobAsync($"/api/v1/project/{kind}", new(path), ContractsJsonContext.Default.SaveProjectAsRequest);

    static Dictionary<string, byte[]> Bytes(string folder, params string[] leaving) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => !leaving.Any(l => Path.GetRelativePath(folder, f).StartsWith(l, StringComparison.Ordinal)))
            .ToDictionary(f => Path.GetRelativePath(folder, f), File.ReadAllBytes);

    static void AssertSameFiles(Dictionary<string, byte[]> before, Dictionary<string, byte[]> after) {
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (path, bytes) in before) {
            Assert.True(bytes.AsSpan().SequenceEqual(after[path]), $"{path} changed.");
        }
    }

    static IEnumerable<string> Builds(string folder) =>
        Directory.EnumerateDirectories(folder, ".saving-*");

    [Fact]
    public async Task Save_As_writes_the_whole_package_and_goes_on_there_with_its_undo() {
        await using var server = Start();
        using var client = server.CreateClient();
        var (original, file) = await ProjectAsync(client);
        var saved = Bytes(original.Path, ProjectPackage.BackupsFolder, ProjectPackage.PeaksFolder);
        var target = Place("Second");

        var job = await SaveToAsync(client, "save-as", target);

        Assert.Equal((JobState.Done, target), (job.State, Assert.Single(job.Files)));
        var now = await client.ProjectAsync();
        Assert.Equal((target, "Second", false), (now.Path, now.Name, now.Dirty));
        Assert.NotEqual(original.Id, now.Id);
        Assert.Equal(["Drums", "Unsaved"], now.Tracks.Select(static t => t.Name));
        Assert.True(now.Revision > original.Revision);
        Assert.Equal(
            await File.ReadAllBytesAsync(Path.Combine(original.Path, file.Path), Token),
            await File.ReadAllBytesAsync(Path.Combine(target, file.Path), Token)
        );
        Assert.Empty(now.Offline.Files);

        // The original keeps its last save: only a backup of the unsaved edits joined it.
        AssertSameFiles(saved, Bytes(original.Path, ProjectPackage.BackupsFolder, ProjectPackage.PeaksFolder));
        Assert.Contains(
            ProjectHistory.Backups(original.Path),
            b => ProjectPackage.Load(ProjectHistory.BackupFolder(original.Path, b.Id)!)
                .Tracks.Any(static t => t.Name == "Unsaved")
        );

        // Undo goes on, and a save lands in the new package.
        await client.PostResultAsync("/api/v1/project/undo");
        await SaveAsync(client);
        Assert.Single(ProjectPackage.Load(target).Tracks);
        Assert.Equal(["Drums"], ProjectPackage.Load(original.Path).Tracks.Select(static t => t.Name));

        var recent = await client.GetFromJsonAsync(
            "/api/v1/projects/recent",
            ContractsJsonContext.Default.IReadOnlyListProjectSummary,
            Token
        );
        Assert.Contains(recent!, p => p.Path == original.Path);
        Assert.Contains(recent!, p => p.Path == target);
    }

    [Fact]
    public async Task Save_a_Copy_leaves_the_open_project_alone_and_keeps_its_unsaved_edits_there() {
        await using var server = Start();
        using var client = server.CreateClient();
        var (original, _) = await ProjectAsync(client);
        var target = Place("Copy");

        var job = await SaveToAsync(client, "save-copy", target);

        Assert.Equal(JobState.Done, job.State);
        var now = await client.ProjectAsync();
        Assert.Equal((original.Path, original.Id, true), (now.Path, now.Id, now.Dirty));
        Assert.Equal(original.Revision, now.Revision);
        var copy = ProjectPackage.Load(target);
        Assert.Equal("Copy", copy.Name);
        Assert.NotEqual(original.Id, copy.Id);
        Assert.Equal(["Drums", "Unsaved"], copy.Tracks.Select(static t => t.Name));
        Assert.Equal(["Drums"], ProjectPackage.Load(original.Path).Tracks.Select(static t => t.Name));
    }

    [Fact]
    public async Task Alternatives_and_backups_go_with_the_package_as_the_new_project() {
        await using var server = Start();
        using var client = server.CreateClient();
        var (original, _) = await ProjectAsync(client);
        var alternative = await client.PostAsJsonAsync(
            "/api/v1/project/alternatives",
            new("Mix B"),
            ContractsJsonContext.Default.CreateAlternativeRequest,
            Token
        );
        Assert.True(alternative.IsSuccessStatusCode);
        var target = Place("With History");

        await SaveToAsync(client, "save-as", target);

        var now = await client.ProjectAsync();
        var manifest = ProjectHistory.Alternatives(target, now.Name);
        Assert.Equal(2, manifest.Alternatives.Count);
        var other = manifest.Alternatives.Single(a => a.Id != manifest.Current);
        var document = ProjectPackage.Load(ProjectHistory.AlternativeFolder(target, other.Id));
        Assert.Equal((now.Id, now.Name), (document.Id, document.Name));
        Assert.NotEmpty(ProjectHistory.Backups(target));
        Assert.All(
            ProjectHistory.Backups(target),
            b => Assert.Equal(now.Id, ProjectPackage.Load(ProjectHistory.BackupFolder(target, b.Id)!).Id)
        );

        // In the source, nothing took the new identity.
        Assert.Equal(original.Id, ProjectPackage.Load(original.Path).Id);
    }

    [Fact]
    public async Task After_Save_As_autosaves_go_to_the_new_package_and_the_original_offers_nothing() {
        await using var server = Start();
        using var client = server.CreateClient();
        var session = Session(server);
        var (original, _) = await ProjectAsync(client);
        Assert.True(session.Autosave());
        Assert.NotNull(ProjectAutosave.Recoverable(original.Path));
        var target = Place("Moved On");

        await SaveToAsync(client, "save-as", target);

        // The snapshot neither travelled nor stayed: its edits are saved in the new package.
        Assert.False(Directory.Exists(Path.Combine(target, ProjectPackage.AutosaveFolder)));
        Assert.Null(ProjectAutosave.Read(original.Path));
        Assert.Empty(session.Recoverable());

        await client.EditAsync(new AddTrackCommand("After"));
        Assert.True(session.Autosave());
        var manifest = ProjectAutosave.Recoverable(target);
        Assert.NotNull(manifest);
        Assert.Equal((await client.ProjectAsync()).Id, manifest.ProjectId);
        Assert.Contains(
            ProjectPackage.Load(ProjectAutosave.Document(target, manifest)).Tracks,
            static t => t.Name == "After"
        );
        Assert.False(Directory.Exists(Path.Combine(original.Path, ProjectPackage.AutosaveFolder)));
    }

    [Fact]
    public async Task A_copy_carries_no_snapshot_and_the_original_keeps_its_own() {
        await using var server = Start();
        using var client = server.CreateClient();
        var session = Session(server);
        var (original, _) = await ProjectAsync(client);
        Assert.True(session.Autosave());
        var target = Place("Snapshotless");

        await SaveToAsync(client, "save-copy", target);

        Assert.False(Directory.Exists(Path.Combine(target, ProjectPackage.AutosaveFolder)));
        Assert.NotNull(ProjectAutosave.Recoverable(original.Path));
    }

    [Fact]
    public async Task Offline_files_stay_offline_in_a_copy_and_a_damaged_track_is_kept_as_it_is() {
        await using var server = Start();
        using var client = server.CreateClient();
        var (original, file) = await ProjectAsync(client);
        await SaveAsync(client);
        var damaged = (await client.ProjectAsync()).Tracks[1].Id;
        var trackFile = Path.Combine(original.Path, ProjectPackage.TracksFolder, ProjectPackage.TrackFileName(damaged));
        File.Delete(Path.Combine(original.Path, file.Path));
        await File.WriteAllTextAsync(trackFile, "{ not json", Token);
        var opened = await client.PostAsJsonAsync(
            "/api/v1/projects/open",
            new(original.Path),
            ContractsJsonContext.Default.OpenProjectRequest,
            Token
        );
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        var target = Place("Offline Copy");

        Assert.Equal(JobState.Done, (await SaveToAsync(client, "save-as", target)).State);

        var now = await client.ProjectAsync();
        Assert.Equal(target, now.Path);
        Assert.Equal(OfflineReason.Missing, Assert.Single(now.Offline.Files).Reason);
        Assert.Equal([damaged], now.Offline.UnreadableTracks);
        Assert.Equal(
            "{ not json",
            await File.ReadAllTextAsync(
                Path.Combine(target, ProjectPackage.TracksFolder, ProjectPackage.TrackFileName(damaged)),
                Token
            )
        );
        Assert.Single(now.Regions); // the offline file's region stayed
    }

    [Theory]
    [InlineData("save-as")]
    [InlineData("save-copy")]
    public async Task A_copy_that_fails_leaves_no_package_and_everything_as_it_was(string kind) {
        await using var server = Start();
        using var client = server.CreateClient();
        var (original, file) = await ProjectAsync(client);
        var before = Bytes(original.Path, ProjectPackage.PeaksFolder);
        // A link to nothing: the package lists it, and its bytes cannot be read.
        var broken = File.CreateSymbolicLink(
            Path.Combine(original.Path, file.Path + ".broken"),
            Path.Combine(places, "gone")
        );
        var target = Place("Failed");

        var job = await SaveToAsync(client, kind, target);

        broken.Delete();
        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains(target, job.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(target));
        Assert.Empty(Builds(places));
        AssertSameFiles(before, Bytes(original.Path, ProjectPackage.PeaksFolder));
        var now = await client.ProjectAsync();
        Assert.Equal((original.Path, original.Id, true), (now.Path, now.Id, now.Dirty));
        Assert.Equal(original.Revision, now.Revision);
        Assert.Equal(["Drums", "Unsaved"], now.Tracks.Select(static t => t.Name));

        // It can still be saved, undone and copied.
        await SaveAsync(client);
        Assert.Equal(2, ProjectPackage.Load(original.Path).Tracks.Count);
        Assert.Equal(JobState.Done, (await SaveToAsync(client, kind, target)).State);
    }

    [Fact]
    public async Task A_place_that_cannot_hold_a_folder_fails_the_job_and_changes_nothing() {
        await using var server = Start();
        using var client = server.CreateClient();
        var (original, _) = await ProjectAsync(client);
        var aFile = Path.Combine(places, "A File");
        await File.WriteAllTextAsync(aFile, "not a folder", Token);

        var job = await SaveToAsync(client, "save-as", Path.Combine(aFile, "Nope"));

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal(original.Path, (await client.ProjectAsync()).Path);
        Assert.Empty(Builds(places));
    }

    [Fact]
    public async Task Save_As_refuses_what_is_there_itself_and_no_name() {
        await using var server = Start();
        using var client = server.CreateClient();
        var (original, _) = await ProjectAsync(client);
        var taken = Directory.CreateDirectory(Place("Taken")).FullName;

        await Refused(taken, HttpStatusCode.Conflict);
        await Refused(original.Path, HttpStatusCode.UnprocessableEntity);
        await Refused(Path.Combine(original.Path, "Inside"), HttpStatusCode.UnprocessableEntity);
        await Refused("  ", HttpStatusCode.UnprocessableEntity);
        await Refused(Path.Combine(places, ".hidden"), HttpStatusCode.UnprocessableEntity);

        async Task Refused(string path, HttpStatusCode status) {
            var response = await client.PostAsJsonAsync(
                "/api/v1/project/save-as",
                new(path),
                ContractsJsonContext.Default.SaveProjectAsRequest,
                Token
            );
            Assert.Equal(status, response.StatusCode);
        }
    }

    [Fact]
    public async Task A_cancelled_copy_builds_nothing() {
        await using var server = Start();
        using var client = server.CreateClient();
        await ProjectAsync(client);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var target = Place("Cancelled");

        Assert.Throws<OperationCanceledException>(() => Session(server).SaveCopy(target, null, null, cancelled.Token));

        Assert.False(Directory.Exists(target));
        Assert.Empty(Builds(places));
    }

    [Fact]
    public async Task A_missing_extension_is_added() {
        await using var server = Start();
        using var client = server.CreateClient();
        await ProjectAsync(client);

        var job = await SaveToAsync(client, "save-copy", Path.Combine(places, "Plain"));

        Assert.Equal(Place("Plain"), Assert.Single(job.Files));
        Assert.Equal("Plain", ProjectPackage.Load(Place("Plain")).Name);
    }

    [Fact]
    public async Task A_copied_document_keeps_the_saved_schema() {
        await using var server = Start();
        using var client = server.CreateClient();
        await ProjectAsync(client);
        var target = Place("Schema");

        await SaveToAsync(client, "save-copy", target);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(target, "project.json"), Token))!;
        Assert.Equal(PackageMigrations.CurrentSchemaVersion, root["schemaVersion"]!.GetValue<int>());
    }
}
