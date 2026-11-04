using Anode.Contracts;
using Anode.Model.Storage;
using System.Net;
using System.Net.Http.Json;

namespace Anode.Server.Tests;

/// <summary>Project and track templates (docs/04-phase-3.md, "Templates").</summary>
public sealed class TemplatesApiTests : IDisposable {
    readonly string media = Directory.CreateTempSubdirectory("anode-templates-").FullName;

    static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(media, true);

    static async Task<IReadOnlyList<ProjectTemplate>> ProjectTemplatesAsync(HttpClient client) =>
        (await client.GetFromJsonAsync(
            "/api/v1/library/project-templates",
            ContractsJsonContext.Default.IReadOnlyListProjectTemplate,
            Token
        ))!;

    static async Task<IReadOnlyList<TrackTemplate>> TrackTemplatesAsync(HttpClient client) =>
        (await client.GetFromJsonAsync(
            "/api/v1/library/track-templates",
            ContractsJsonContext.Default.IReadOnlyListTrackTemplate,
            Token
        ))!;

    static Task<HttpResponseMessage> CreateAsync(HttpClient client, string name, string? template, int? rate = null) =>
        client.PostAsJsonAsync(
            "/api/v1/projects",
            new(name, null, rate, template),
            ContractsJsonContext.Default.CreateProjectRequest,
            Token
        );

    static Task<Job> SaveProjectTemplateAsync(HttpClient client, string name) =>
        client.RunJobAsync(
            "/api/v1/library/project-templates",
            new(name, "Mine"),
            ContractsJsonContext.Default.SaveTemplateRequest
        );

    static async Task<TrackTemplate> SaveTrackTemplateAsync(HttpClient client, string name, params string[] tracks) {
        var response = await client.PostAsJsonAsync(
            "/api/v1/library/track-templates",
            new(name, null, tracks),
            ContractsJsonContext.Default.SaveTrackTemplateRequest,
            Token
        );
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.TrackTemplate, Token))!;
    }

    static async Task<Job> ApplyAsync(HttpClient client, string id) {
        var response = await client.PostAsync($"/api/v1/library/track-templates/{id}/apply", null, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var job = (await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.Job, Token))!;
        await ServerFixture.Eventually(async () => {
                job = (await client.GetFromJsonAsync(
                    $"/api/v1/jobs/{job.Id}",
                    ContractsJsonContext.Default.Job,
                    Token
                ))!;
                return job.State is JobState.Done or JobState.Failed;
            }
        );
        return job;
    }

    /// <summary>A saved project with audio on a track with a compressor, and an unsaved aux.</summary>
    async Task<(Project Project, AudioFile File)> ProjectAsync(HttpClient client) {
        await client.CreateProjectAsync("Session");
        var file = await client.ImportAsync(TestWav.Write(media, "Loop.wav"));
        await client.EditAsync(new AddTrackCommand("Drums"));
        var track = (await client.ProjectAsync()).Tracks[0].Id;
        await client.EditAsync(
            new AddRegionCommand(track, file.Id, 0),
            new AddInsertCommand("anode.compressor", track)
        );
        Assert.True((await client.PostAsync("/api/v1/project/save", null, Token)).IsSuccessStatusCode);
        await client.EditAsync(new AddAuxCommand(1, "Unsaved"));
        return (await client.ProjectAsync(), file);
    }

    [Fact]
    public async Task The_chooser_offers_the_factorys_templates_and_each_makes_a_project_that_works() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();

        var templates = await ProjectTemplatesAsync(client);

        Assert.True(templates.Count >= 5);
        Assert.All(templates, static t => Assert.True(t.Factory));
        foreach (var template in templates) {
            var response = await CreateAsync(client, template.Name + " Test", template.Id, 96_000);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var project = await client.ProjectAsync();
            Assert.Equal(96_000, project.SampleRate);
            Assert.False(project.Dirty);
            var read = ProjectPackage.Load(project.Path);
            Assert.Equal(project.Tracks.Select(static t => t.Name), read.Tracks.Select(static t => t.Name));
            await client.EditAsync(new AddTrackCommand("One More"));
            await client.PostResultAsync("/api/v1/project/undo");
        }
    }

    [Fact]
    public async Task A_factory_template_has_tracks_and_routing() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await CreateAsync(client, "Song", "factory-songwriter")).StatusCode);

        var project = await client.ProjectAsync();
        var reverb = project.Tracks.Single(static t => t.Name == "Reverb");
        Assert.Equal(TrackKind.Aux, reverb.Kind);
        Assert.Equal("anode.reverb", Assert.Single(reverb.Strip.Inserts).PluginId);
        var vocal = project.Tracks.Single(static t => t.Name == "Vocal");
        Assert.Equal(reverb.InputBus, Assert.Single(vocal.Strip.Sends).Bus);
    }

    [Fact]
    public async Task A_template_there_is_not_is_a_404_and_no_template_is_an_empty_project() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await CreateAsync(client, "Song", "tpl_gone")).StatusCode);
        Assert.False(Directory.Exists(Path.Combine(server.DataDirectory, "Projects", "Song.anode")));
        Assert.Equal(HttpStatusCode.OK, (await CreateAsync(client, "Plain", null)).StatusCode);
        Assert.Empty((await client.ProjectAsync()).Tracks);
    }

    [Fact]
    public async Task A_project_saved_as_a_template_makes_new_projects_with_its_media_and_nothing_of_its_history() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        var (original, file) = await ProjectAsync(client);

        var job = await SaveProjectTemplateAsync(client, "My Session");

        Assert.True(job.State == JobState.Done, job.Message);
        Assert.True((await client.ProjectAsync()).Dirty); // saving a template saves nothing of the project
        var template = (await ProjectTemplatesAsync(client)).Single(static t => !t.Factory);
        Assert.Equal(
            ("My Session", "Mine", 48_000, true),
            (template.Name, template.Category, template.SampleRate, template.HasAudio)
        );

        Assert.Equal(HttpStatusCode.OK, (await CreateAsync(client, "From Template", template.Id)).StatusCode);

        var project = await client.ProjectAsync();
        Assert.Equal("From Template", project.Name);
        Assert.NotEqual(original.Id, project.Id);
        Assert.Equal(["Drums", "Unsaved"], project.Tracks.Select(static t => t.Name));
        Assert.Equal(file.Id, Assert.Single(project.AudioFiles).Id);
        Assert.Single(project.Regions);
        Assert.Empty(project.Offline.Files);
        Assert.True(File.Exists(Path.Combine(project.Path, file.Path)));
        Assert.False(Directory.Exists(Path.Combine(project.Path, ProjectPackage.BackupsFolder)));
        Assert.False(Directory.Exists(Path.Combine(project.Path, ProjectHistory.AlternativesFolder)));

        // A second project is its own: the first's edits do not reach the template.
        await client.EditAsync(new AddTrackCommand("Only Here"));
        Assert.True((await client.PostAsync("/api/v1/project/save", null, Token)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CreateAsync(client, "Second", template.Id)).StatusCode);
        Assert.Equal(2, (await client.ProjectAsync()).Tracks.Count);
    }

    [Fact]
    public async Task A_template_with_audio_takes_only_its_rate_and_one_without_takes_any() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await ProjectAsync(client);
        await SaveProjectTemplateAsync(client, "With Audio");
        await client.CreateProjectAsync("Bare");
        await client.EditAsync(new AddTrackCommand("Track"));
        await SaveProjectTemplateAsync(client, "No Audio");
        var templates = await ProjectTemplatesAsync(client);

        var refused = await CreateAsync(
            client,
            "Other",
            templates.Single(static t => t.Name == "With Audio").Id,
            44_100
        );
        var taken = await CreateAsync(client, "Other", templates.Single(static t => t.Name == "No Audio").Id, 44_100);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, taken.StatusCode);
        Assert.Equal(44_100, (await client.ProjectAsync()).SampleRate);
    }

    [Fact]
    public async Task A_template_of_a_project_with_offline_files_keeps_them_offline() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        var (original, file) = await ProjectAsync(client);
        File.Delete(Path.Combine(original.Path, file.Path));

        Assert.Equal(JobState.Done, (await SaveProjectTemplateAsync(client, "Offline")).State);

        var template = (await ProjectTemplatesAsync(client)).Single(static t => !t.Factory);
        Assert.Equal(HttpStatusCode.OK, (await CreateAsync(client, "Copy", template.Id)).StatusCode);
        Assert.Equal(OfflineReason.Missing, Assert.Single((await client.ProjectAsync()).Offline.Files).Reason);
    }

    [Fact]
    public async Task A_template_that_cannot_be_written_is_not_listed_and_leaves_the_project_alone() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        var (original, file) = await ProjectAsync(client);
        var broken = File.CreateSymbolicLink(
            Path.Combine(original.Path, file.Path + ".broken"),
            Path.Combine(media, "gone")
        );

        var job = await SaveProjectTemplateAsync(client, "Fails");

        broken.Delete();
        Assert.Equal(JobState.Failed, job.State);
        Assert.DoesNotContain(await ProjectTemplatesAsync(client), static t => !t.Factory);
        var folder = Path.Combine(server.DataDirectory, "Library", "Project Templates");
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
        Assert.Equal(original.Revision, (await client.ProjectAsync()).Revision);
    }

    [Fact]
    public async Task A_deleted_template_is_gone_and_the_factorys_cannot_be_deleted() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await ProjectAsync(client);
        await SaveProjectTemplateAsync(client, "Mine");
        var template = (await ProjectTemplatesAsync(client)).Single(static t => !t.Factory);

        var deleted = await client.DeleteAsync($"/api/v1/library/project-templates/{template.Id}", Token);
        var factory = await client.DeleteAsync("/api/v1/library/project-templates/factory-empty", Token);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, factory.StatusCode);
        Assert.DoesNotContain(await ProjectTemplatesAsync(client), static t => !t.Factory);
        Assert.Equal(HttpStatusCode.NotFound, (await CreateAsync(client, "Song", template.Id)).StatusCode);
    }

    /// <summary>A track with a compressor and a send to a reverb aux, on a region that must stay behind.</summary>
    async Task<string> RoutedTrackAsync(HttpClient client) {
        await client.CreateProjectAsync("Source");
        var file = await client.ImportAsync(TestWav.Write(media, "Take.wav"));
        await client.EditAsync(new AddTrackCommand("Lead"), new AddAuxCommand(5, "Plate"));
        var tracks = (await client.ProjectAsync()).Tracks;
        var (lead, plate) = (tracks[0].Id, tracks[1].Id);
        await client.EditAsync(
            new AddRegionCommand(lead, file.Id, 0),
            new AddInsertCommand("anode.compressor", lead),
            new AddInsertCommand("anode.reverb", plate),
            new AddSendCommand(lead, 5, -9),
            new SetAutomationCommand("volume", [new(0, -3, 0)], lead)
        );
        return lead;
    }

    [Fact]
    public async Task A_track_saves_as_a_template_with_its_routing_and_comes_back_on_buses_of_its_own() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        var lead = await RoutedTrackAsync(client);

        var template = await SaveTrackTemplateAsync(client, "Lead Chain", lead);

        Assert.Equal(("Lead Chain", 2), (template.Name, template.Tracks)); // the aux came with it
        Assert.Equal(template.Id, Assert.Single(await TrackTemplatesAsync(client)).Id);
        await client.CreateProjectAsync("Target", 44_100);
        await client.EditAsync(new AddTrackCommand("Own"), new AddAuxCommand(1, "Hall"));
        var before = await client.ProjectAsync();

        var job = await ApplyAsync(client, template.Id);

        Assert.True(job.State == JobState.Done, job.Message);
        var now = await client.ProjectAsync();
        Assert.Equal(["Own", "Hall", "Lead", "Plate"], now.Tracks.Select(static t => t.Name));
        var (added, plate) = (now.Tracks[2], now.Tracks[3]);
        Assert.Equal("anode.compressor", Assert.Single(added.Strip.Inserts).PluginId);
        Assert.Equal("anode.reverb", Assert.Single(plate.Strip.Inserts).PluginId);
        var send = Assert.Single(added.Strip.Sends);
        Assert.Equal((plate.InputBus, -9.0), (send.Bus, send.LevelDb));
        Assert.NotEqual(1, send.Bus);

        // Routing, not content.
        Assert.Empty(now.Regions);
        Assert.Empty(now.AudioFiles);
        Assert.Empty(now.Automation);

        await client.PostResultAsync("/api/v1/project/undo");
        Assert.Equal(before.Tracks.Count, (await client.ProjectAsync()).Tracks.Count);
    }

    [Fact]
    public async Task Plugin_states_travel_in_a_track_template() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        var project = await client.CreateProjectAsync("Source");
        await client.EditAsync(new AddTrackCommand("Lead"));
        var lead = (await client.ProjectAsync()).Tracks[0].Id;
        await client.EditAsync(new AddInsertCommand("anode.compressor", lead));
        Assert.True((await client.PostAsync("/api/v1/project/save", null, Token)).IsSuccessStatusCode);
        var insert = (await client.ProjectAsync()).Tracks[0].Strip.Inserts[0].Id;
        var states = Directory.CreateDirectory(Path.Combine(project.Path, ProjectPackage.PluginStatesFolder));
        await File.WriteAllBytesAsync(
            Path.Combine(states.FullName, ProjectPackage.StateFileName(ProjectPackage.StateKey(lead, insert))),
            [7, 8],
            Token
        );
        await client.PostAsJsonAsync(
            "/api/v1/projects/open",
            new(project.Path),
            ContractsJsonContext.Default.OpenProjectRequest,
            Token
        );
        var template = await SaveTrackTemplateAsync(client, "With State", lead);
        var target = await client.CreateProjectAsync("Target");

        await ApplyAsync(client, template.Id);
        Assert.True((await client.PostAsync("/api/v1/project/save", null, Token)).IsSuccessStatusCode);

        var added = (await client.ProjectAsync()).Tracks[0];
        Assert.Equal(
            [7, 8],
            await File.ReadAllBytesAsync(
                Path.Combine(
                    target.Path,
                    ProjectPackage.PluginStatesFolder,
                    ProjectPackage.StateFileName(ProjectPackage.StateKey(added.Id, added.Strip.Inserts[0].Id))
                ),
                Token
            )
        );
    }

    [Fact]
    public async Task Saving_a_track_template_refuses_what_is_not_there_and_deleting_it_removes_it() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        var lead = await RoutedTrackAsync(client);

        Assert.Equal(HttpStatusCode.NotFound, (await Post(new("Name", null, ["trk_gone"]))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post(new("Name", null, []))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post(new("  ", null, [lead]))).StatusCode);

        var template = await SaveTrackTemplateAsync(client, "Chain", lead);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/v1/library/track-templates/{template.Id}", Token)).StatusCode
        );
        Assert.Empty(await TrackTemplatesAsync(client));
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.PostAsync($"/api/v1/library/track-templates/{template.Id}/apply", null, Token)).StatusCode
        );

        Task<HttpResponseMessage> Post(SaveTrackTemplateRequest request) =>
            client.PostAsJsonAsync(
                "/api/v1/library/track-templates",
                request,
                ContractsJsonContext.Default.SaveTrackTemplateRequest,
                Token
            );
    }
}
