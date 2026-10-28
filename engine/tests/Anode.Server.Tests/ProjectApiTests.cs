using Anode.Contracts;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Anode.Server.Tests;

/// <summary>Projects and the open project: create, open, recent, read, edit, undo, save, close.</summary>
public sealed class ProjectApiTests {
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Creating_a_project_opens_it_with_its_revision_as_the_ETag() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await using var events = await EventStream.ConnectAsync(server);
        var created = await client.CreateProjectAsync("First Song", 44_100);
        Assert.Equal(
            ("First Song", 44_100, 1L, false),
            (created.Name, created.SampleRate, created.Revision, created.Dirty)
        );
        Assert.EndsWith("First Song.anode", created.Path, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(created.Path, "project.json")));

        var response = await client.GetAsync("/api/v1/project", Token);
        Assert.Equal("\"1\"", response.Headers.ETag?.Tag);
        Assert.Equal(
            created.Id,
            (await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.Project, Token))!.Id
        );
        await events.WaitAsync<ProjectOpenedEvent>(e => e.Project.Id == created.Id && e.Revision == 1);

        var recent = await client.GetFromJsonAsync(
            "/api/v1/projects/recent",
            ContractsJsonContext.Default.IReadOnlyListProjectSummary,
            Token
        );
        Assert.Equal(created.Path, Assert.Single(recent!).Path);
    }

    [Theory]
    [InlineData("", null, HttpStatusCode.UnprocessableEntity)]
    [InlineData("a/b", null, HttpStatusCode.UnprocessableEntity)]
    [InlineData("Song", 12_345, HttpStatusCode.UnprocessableEntity)]
    public async Task Bad_project_requests_are_problems(string name, int? rate, HttpStatusCode status) {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/v1/projects",
            new(name, null, rate),
            ContractsJsonContext.Default.CreateProjectRequest,
            Token
        );
        await ApiCalls.AssertProblemAsync(response, status);
    }

    [Fact]
    public async Task A_project_folder_that_exists_is_a_conflict_and_a_missing_one_not_found() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await client.CreateProjectAsync("Twice");
        var again = await client.PostAsJsonAsync(
            "/api/v1/projects",
            new("Twice", null, null),
            ContractsJsonContext.Default.CreateProjectRequest,
            Token
        );
        await ApiCalls.AssertProblemAsync(again, HttpStatusCode.Conflict);
        var missing = await client.PostAsJsonAsync(
            "/api/v1/projects/open",
            new(Path.Combine(Path.GetTempPath(), "no-such.anode")),
            ContractsJsonContext.Default.OpenProjectRequest,
            Token
        );
        await ApiCalls.AssertProblemAsync(missing, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Without_an_open_project_the_project_endpoints_answer_404() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await ApiCalls.AssertProblemAsync(await client.GetAsync("/api/v1/project", Token), HttpStatusCode.NotFound);
        await ApiCalls.AssertProblemAsync(
            await client.GetAsync("/api/v1/project/history", Token),
            HttpStatusCode.NotFound
        );
        await ApiCalls.AssertProblemAsync(
            await client.GetAsync("/api/v1/project/files/aud_x/peaks", Token),
            HttpStatusCode.NotFound
        );
        foreach (var path in new[] { "save", "close", "undo", "redo" }) {
            await ApiCalls.AssertProblemAsync(
                await client.PostAsync($"/api/v1/project/{path}", null, Token),
                HttpStatusCode.NotFound
            );
        }

        await ApiCalls.AssertProblemAsync(
            await client.SendCommandsAsync(null, new AddTrackCommand()),
            HttpStatusCode.NotFound
        );
        var import = await client.PostAsJsonAsync(
            "/api/v1/project/files/import",
            new("/x.wav"),
            ContractsJsonContext.Default.ImportFileRequest,
            Token
        );
        await ApiCalls.AssertProblemAsync(import, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Edits_need_the_current_revision_when_they_name_one() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await client.CreateProjectAsync();
        var ok = await client.SendCommandsAsync(1, new AddTrackCommand());
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("\"2\"", ok.Headers.ETag?.Tag);
        var result = (await ok.Content.ReadFromJsonAsync(ContractsJsonContext.Default.CommandResult, Token))!;
        Assert.Equal(2, result.Revision);
        Assert.IsType<TrackUpserted>(Assert.Single(result.Delta.Ops));

        var stale = await client.SendCommandsAsync(1, new AddTrackCommand());
        Assert.Contains(
            "revision 2",
            await ApiCalls.AssertProblemAsync(stale, HttpStatusCode.Conflict),
            StringComparison.Ordinal
        );
        Assert.Equal(HttpStatusCode.OK, (await client.SendCommandsAsync(null, new AddTrackCommand())).StatusCode);
        Assert.Equal(3, (await client.ProjectAsync()).Revision);
    }

    [Fact]
    public async Task An_edit_that_does_not_apply_is_422_and_changes_nothing() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await client.CreateProjectAsync();
        var response = await client.SendCommandsAsync(null, new AddTrackCommand(), new SplitRegionCommand("rgn_x", 10));
        Assert.Contains(
            "rgn_x",
            await ApiCalls.AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity),
            StringComparison.Ordinal
        );
        var project = await client.ProjectAsync();
        Assert.Equal((1L, 0), (project.Revision, project.Tracks.Count));

        var empty = await client.PostAsync(
            "/api/v1/project/commands",
            new StringContent("""{"commands":[]}""", System.Text.Encoding.UTF8, "application/json"),
            Token
        );
        await ApiCalls.AssertProblemAsync(empty, HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Undo_and_redo_are_edits_of_their_own_and_every_change_reaches_the_socket() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await using var events = await EventStream.ConnectAsync(server);
        await client.CreateProjectAsync();
        await client.EditAsync(new AddTrackCommand("Drums"));
        await client.EditAsync(new SetTempoCommand(90));
        var history = await client.GetFromJsonAsync(
            "/api/v1/project/history",
            ContractsJsonContext.Default.UndoHistory,
            Token
        );
        Assert.Equal(["Change Tempo", "Add Track"], history!.Undo.Select(static h => h.Label));

        var undone = await client.PostResultAsync("/api/v1/project/undo");
        Assert.Equal(4, undone.Revision);
        Assert.Equal(120, Assert.IsType<SettingsChanged>(Assert.Single(undone.Delta.Ops)).Settings.Tempo);
        var redone = await client.PostResultAsync("/api/v1/project/redo");
        Assert.Equal(5, redone.Revision);
        Assert.Equal(90, (await client.ProjectAsync()).Settings.Tempo);
        await ApiCalls.AssertProblemAsync(
            await client.PostAsync("/api/v1/project/redo", null, Token),
            HttpStatusCode.UnprocessableEntity
        );

        await client.PostResultAsync("/api/v1/project/undo");
        await client.PostResultAsync("/api/v1/project/undo");
        await ApiCalls.AssertProblemAsync(
            await client.PostAsync("/api/v1/project/undo", null, Token),
            HttpStatusCode.UnprocessableEntity
        );

        // One event per revision, in order, and replaying them gives the project.
        var changes = await WaitForRevisionsAsync(events, 2, 7);
        var replay = new Replay(new([], [], [], ProjectDefaults()));
        foreach (var change in changes) {
            replay.Apply(change.Delta.Ops);
        }

        var project = await client.ProjectAsync();
        Assert.Equal(7, project.Revision);
        Assert.Empty(project.Tracks);
        Assert.Equal(replay.Tracks.Count, project.Tracks.Count);
        Assert.Equal(120, replay.Settings?.Tempo);
    }

    [Fact]
    public async Task Saving_closing_and_reopening_keeps_the_project() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await using var events = await EventStream.ConnectAsync(server);
        var created = await client.CreateProjectAsync("Keep");
        await client.EditAsync(
            new AddTrackCommand("Bass"),
            new SetStripCommand(VolumeDb: -3),
            new RenameProjectCommand("Kept")
        );
        Assert.True((await client.ProjectAsync()).Dirty);
        var saved = (await (await client.PostAsync("/api/v1/project/save", null, Token)).Content
            .ReadFromJsonAsync(ContractsJsonContext.Default.ProjectSummary, Token))!;
        Assert.Equal(("Kept", created.Path), (saved.Name, saved.Path));
        await events.WaitAsync<ProjectSavedEvent>(static e => e.Revision == 2);
        var before = await client.ProjectAsync();
        Assert.False(before.Dirty);

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await client.PostAsync("/api/v1/project/close", null, Token)).StatusCode
        );
        await events.WaitAsync<ProjectClosedEvent>();
        var opened = await client.PostAsJsonAsync(
            "/api/v1/projects/open",
            new(created.Path),
            ContractsJsonContext.Default.OpenProjectRequest,
            Token
        );
        var after = (await opened.Content.ReadFromJsonAsync(ContractsJsonContext.Default.Project, Token))!;
        Assert.Equal(Content(before), Content(after));
        Assert.Equal(-3, after.Master.VolumeDb);
        Assert.Equal("Bass", Assert.Single(after.Tracks).Name);
    }

    [Fact]
    public async Task Replacing_a_project_with_unsaved_edits_keeps_a_backup() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        var first = await client.CreateProjectAsync("One");
        await client.EditAsync(new AddTrackCommand());
        await client.CreateProjectAsync("Two");
        // Kept as a backup (Backups/<timestamp>/) that holds the unsaved track.
        var backup = Assert.Single(Directory.GetDirectories(Path.Combine(first.Path, "Backups")));
        Assert.Single(Anode.Model.Storage.ProjectPackage.Load(backup).Tracks);
    }

    static ProjectSettings ProjectDefaults() => Anode.Model.ProjectDefaults.NewProject("p", "p", 48_000).Settings;

    static string Content(Project p) =>
        JsonSerializer.Serialize(p with { Revision = 0, Dirty = false }, ContractsJsonContext.Default.Project);

    static async Task<List<ProjectChangedEvent>> WaitForRevisionsAsync(EventStream events, long first, long last) {
        await events.WaitAsync<ProjectChangedEvent>(e => e.Delta.Revision == last);
        var changes = events.OfType<ProjectChangedEvent>().ToList();
        Assert.Equal(
            Enumerable.Range((int)first, (int)(last - first + 1)).Select(static r => (long)r),
            changes.Select(static c => c.Delta.Revision)
        );
        return changes;
    }

    /// <summary>The bits of a client store these tests look at.</summary>
    sealed class Replay(Replay.Start start) {
        public sealed record Start(
            List<Track> Tracks,
            List<AudioRegion> Regions,
            List<AudioFile> Files,
            ProjectSettings Settings);

        public List<Track> Tracks { get; } = start.Tracks;
        public ProjectSettings? Settings { get; private set; } = start.Settings;

        public void Apply(IEnumerable<DeltaOp> ops) {
            foreach (var op in ops) {
                switch (op) {
                    case TrackUpserted(var track, var index):
                        Tracks.RemoveAll(t => t.Id == track.Id);
                        Tracks.Insert(index, track);
                        break;
                    case TrackRemoved(var id):
                        Tracks.RemoveAll(t => t.Id == id);
                        break;
                    case SettingsChanged(var settings):
                        Settings = settings;
                        break;
                }
            }
        }
    }
}
