using Anode.Contracts;
using Anode.Model.Storage;
using Anode.Server.Projects;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;

namespace Anode.Server.Tests;

/// <summary>
///     Autosave and the offer to recover (docs/04-phase-3.md, "Autosave and crash recovery"). Restarting a fixture here
///     stops the Server cleanly; <see cref="AutosaveKillTests" /> kills a real one.
/// </summary>
public sealed class AutosaveApiTests : IDisposable {
    readonly string data = Directory.CreateTempSubdirectory("anode-autosave-api-").FullName;

    static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(data, true);

    /// <summary>A Server on this test's data folder, autosaving only when told to (or every <paramref name="seconds" />).</summary>
    ServerFixture Start(double seconds = 0) =>
        new(data: data, configure: s => s.Configure<ServerOptions>(o => o.AutosaveSeconds = seconds));

    static ProjectSession Session(ServerFixture server) => server.Services.GetRequiredService<ProjectSession>();

    [Fact]
    public async Task Edits_are_autosaved_in_the_background_and_the_saved_project_stays_as_it_was() {
        await using var server = Start(0.01);
        using var client = server.CreateClient();
        var project = await client.CreateProjectAsync();
        await using var events = await EventStream.ConnectAsync(server);
        var saved = Read(project);

        var edit = await client.EditAsync(new AddTrackCommand("Unsaved"));

        var autosaved = await events.WaitAsync<ProjectAutosavedEvent>(e => e.Revision == edit.Revision);
        var manifest = ProjectAutosave.Read(project.Path)!;
        Assert.Equal(autosaved.Autosaved, manifest.Autosaved);
        Assert.Equal("Unsaved", Assert.Single(Load(project, manifest).Tracks).Name);
        Assert.Equal(saved, Read(project));
        Assert.True((await client.ProjectAsync()).Dirty); // autosaving is not saving
    }

    [Fact]
    public async Task An_autosave_writes_only_when_there_is_something_new_and_leaves_the_engine_alone() {
        await using var server = Start();
        using var client = server.CreateClient();
        var session = Session(server);
        await client.CreateProjectAsync();
        Assert.False(session.Autosave()); // nothing unsaved

        await client.EditAsync(new AddTrackCommand());
        var syncs = 0;
        session.Changed += () => Interlocked.Increment(ref syncs);
        Assert.True(session.Autosave());
        Assert.False(session.Autosave());
        Assert.Equal(0, syncs);

        await client.EditAsync(new AddTrackCommand());
        Assert.True(session.Autosave());
    }

    [Fact]
    public async Task A_screenset_counts_as_an_edit() {
        await using var server = Start();
        using var client = server.CreateClient();
        var session = Session(server);
        await client.CreateProjectAsync();

        session.SaveScreenset(1, new(1, "Mix", false, "{}"));

        Assert.True(session.Autosave());
    }

    [Fact]
    public async Task The_next_launch_offers_the_edits_and_recovers_them_without_touching_the_save() {
        Project project;
        await using (var first = Start()) {
            using var client = first.CreateClient();
            project = await client.CreateProjectAsync();
            await client.EditAsync(new AddTrackCommand("Unsaved"));
            Session(first).Autosave();
        }

        var saved = Read(project);
        await using var second = Start();
        using var next = second.CreateClient();
        var offered = Assert.Single(await RecoverableAsync(next));
        Assert.Equal((project.Id, project.Name, project.Path), (offered.Id, offered.Name, offered.Path));

        var recovered = await PostAsync(next, "recover", project.Path);
        await EnsureAsync(recovered);
        var open = await next.ProjectAsync();

        Assert.True(open.Dirty);
        Assert.Equal("Unsaved", Assert.Single(open.Tracks).Name);
        Assert.Equal(saved, Read(project));
        Assert.Empty(await RecoverableAsync(next)); // it is open now

        await next.PostAsync("/api/v1/project/save", null, Token);
        Assert.Null(ProjectAutosave.Read(project.Path));
        Assert.Equal("Unsaved", Assert.Single(ProjectPackage.Load(project.Path).Tracks).Name);
    }

    [Fact]
    public async Task A_clean_stop_keeps_the_last_edits_too() {
        Project project;
        await using (var first = Start(3600)) {
            using var client = first.CreateClient();
            project = await client.CreateProjectAsync();
            await client.EditAsync(new AddTrackCommand("Last"));
        }

        Assert.NotNull(ProjectAutosave.Recoverable(project.Path));
    }

    [Fact]
    public async Task A_second_crash_after_recovering_loses_nothing() {
        Project project;
        await using (var first = Start()) {
            using var client = first.CreateClient();
            project = await client.CreateProjectAsync();
            await client.EditAsync(new AddTrackCommand("Unsaved"));
            Session(first).Autosave();
        }

        await using (var second = Start()) {
            using var client = second.CreateClient();
            await EnsureAsync(await PostAsync(client, "recover", project.Path));
        }

        await using var third = Start();
        using var last = third.CreateClient();
        Assert.Single(await RecoverableAsync(last));
    }

    [Fact]
    public async Task Saving_removes_the_snapshot_so_nothing_is_offered() {
        await using var server = Start();
        using var client = server.CreateClient();
        var session = Session(server);
        var project = await client.CreateProjectAsync();
        await client.EditAsync(new AddTrackCommand());
        session.Autosave();
        Assert.NotNull(ProjectAutosave.Read(project.Path));

        await client.PostAsync("/api/v1/project/save", null, Token);

        Assert.False(Directory.Exists(Path.Combine(project.Path, ProjectPackage.AutosaveFolder)));
        Assert.False(session.Autosave());
    }

    [Fact]
    public async Task Closing_keeps_unsaved_edits_in_a_backup_and_stops_the_offer() {
        await using var server = Start();
        using var client = server.CreateClient();
        var project = await client.CreateProjectAsync();
        await client.EditAsync(new AddTrackCommand("Unsaved"));
        Session(server).Autosave();

        await client.PostAsync("/api/v1/project/close", null, Token);

        Assert.Empty(await RecoverableAsync(client));
        Assert.Null(ProjectAutosave.Read(project.Path));
        var backup = Assert.Single(ProjectHistory.Backups(project.Path));
        Assert.Equal(
            "Unsaved",
            Assert.Single(ProjectPackage.Load(ProjectHistory.BackupFolder(project.Path, backup.Id)!).Tracks).Name
        );
    }

    [Fact]
    public async Task Opening_without_recovering_keeps_the_edits_as_a_backup_before_new_ones_arrive() {
        Project project;
        await using (var first = Start()) {
            using var client = first.CreateClient();
            project = await client.CreateProjectAsync();
            await client.EditAsync(new AddTrackCommand("Unsaved"));
            Session(first).Autosave();
        }

        await using var second = Start();
        using var next = second.CreateClient();
        var opened = await next.PostAsJsonAsync(
            "/api/v1/projects/open",
            new(project.Path),
            ContractsJsonContext.Default.OpenProjectRequest,
            Token
        );
        await EnsureAsync(opened);

        Assert.Empty((await next.ProjectAsync()).Tracks); // the saved project
        Assert.Null(ProjectAutosave.Read(project.Path));
        var backup = Assert.Single(ProjectHistory.Backups(project.Path));
        Assert.Single(ProjectPackage.Load(ProjectHistory.BackupFolder(project.Path, backup.Id)!).Tracks);
    }

    [Fact]
    public async Task Discarding_the_offer_keeps_the_edits_as_the_newest_backup() {
        Project project;
        await using (var first = Start()) {
            using var client = first.CreateClient();
            project = await client.CreateProjectAsync();
            await client.EditAsync(new AddTrackCommand("Unsaved"));
            Session(first).Autosave();
        }

        await using var second = Start();
        using var next = second.CreateClient();
        var discarded = await PostAsync(next, "recoverable/discard", project.Path);

        Assert.Equal(HttpStatusCode.NoContent, discarded.StatusCode);
        Assert.Empty(await RecoverableAsync(next));
        Assert.Single(ProjectHistory.Backups(project.Path));
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(next, "recover", project.Path)).StatusCode);
    }

    [Fact]
    public async Task Switching_alternatives_or_reverting_does_not_bring_old_edits_back() {
        await using var server = Start();
        using var client = server.CreateClient();
        var session = Session(server);
        var project = await client.CreateProjectAsync();
        await client.PostAsync("/api/v1/project/save", null, Token);
        await client.EditAsync(new AddTrackCommand("Unsaved"));
        session.Autosave();
        await client.PostAsync("/api/v1/project/save", null, Token);
        await client.EditAsync(new AddTrackCommand("Also unsaved"));
        session.Autosave();

        var backup = ProjectHistory.Backups(project.Path)[0];
        session.RevertToBackup(backup.Id);

        Assert.Null(ProjectAutosave.Read(project.Path));
        Assert.False((await client.ProjectAsync()).Dirty);
    }

    [Fact]
    public async Task A_failing_autosave_is_reported_once_and_edits_go_on() {
        await using var server = Start();
        using var client = server.CreateClient();
        var session = Session(server);
        var project = await client.CreateProjectAsync();
        await using var events = await EventStream.ConnectAsync(server);
        var blocker = Path.Combine(project.Path, ProjectPackage.AutosaveFolder);
        await File.WriteAllTextAsync(blocker, "in the way", Token);

        await client.EditAsync(new AddTrackCommand());
        Assert.False(session.Autosave());
        await client.EditAsync(new AddTrackCommand());
        Assert.False(session.Autosave());

        await events.WaitAsync<AutosaveFailedEvent>();
        Assert.Single(events.OfType<AutosaveFailedEvent>());

        File.Delete(blocker);
        Assert.True(session.Autosave());
    }

    [Fact]
    public async Task Recovering_needs_a_snapshot_and_a_project_that_is_not_open() {
        await using var server = Start();
        using var client = server.CreateClient();
        var project = await client.CreateProjectAsync();
        await client.EditAsync(new AddTrackCommand());
        Session(server).Autosave();

        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(client, "recover", project.Path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(client, "recover", data)).StatusCode);
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            (await PostAsync(client, "recoverable/discard", " ")).StatusCode
        );
    }

    static async Task<IReadOnlyList<RecoverableProject>> RecoverableAsync(HttpClient client) =>
        (await client.GetFromJsonAsync(
            "/api/v1/projects/recoverable",
            ContractsJsonContext.Default.IReadOnlyListRecoverableProject,
            Token
        ))!;

    static Task<HttpResponseMessage> PostAsync(HttpClient client, string action, string path) =>
        client.PostAsJsonAsync(
            $"/api/v1/projects/{action}",
            new(path),
            ContractsJsonContext.Default.OpenProjectRequest,
            Token
        );

    static async Task EnsureAsync(HttpResponseMessage response) {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Token));
    }

    static Anode.Model.ProjectState Load(Project project, AutosaveManifest manifest) =>
        ProjectPackage.Load(ProjectAutosave.Document(project.Path, manifest));

    static string Read(Project project) => File.ReadAllText(Path.Combine(project.Path, ProjectPackage.ProjectFileName));
}
