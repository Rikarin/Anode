using Anode.Contracts;
using Anode.Server.Engine;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;

namespace Anode.Server.Tests;

/// <summary>
///     Alternatives, backups, screensets, MIDI file import, key commands, bookmarks, the file browser and preview
///     (docs/04-phase-3.md).
/// </summary>
public sealed class LibraryApiTests : IDisposable {
    readonly string media = Directory.CreateTempSubdirectory("anode-library-").FullName;

    static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(media, true);

    [Fact]
    public async Task Without_a_project_the_project_endpoints_are_404() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        foreach (var path in new[] { "alternatives", "backups", "screensets" }) {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/project/{path}", Token)).StatusCode);
        }

        await ApiCalls.AssertProblemAsync(
            await client.PostAsync("/api/v1/project/alternatives/alt_x/open", null, Token),
            HttpStatusCode.NotFound
        );
        await ApiCalls.AssertProblemAsync(
            await client.PostAsync("/api/v1/transport/capture", null, Token),
            HttpStatusCode.NotFound
        );
    }

    [Fact]
    public async Task An_alternative_keeps_the_project_as_it_was_and_switching_back_restores_it() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await client.CreateProjectAsync();
        await client.EditAsync(new AddTrackCommand("First"));
        await PostAsync(client, "/api/v1/project/save");
        var first = Content(await client.ProjectAsync());

        var created = await client.PostAsJsonAsync(
            "/api/v1/project/alternatives",
            new("Second"),
            ContractsJsonContext.Default.CreateAlternativeRequest,
            Token
        );
        var second = (await created.Content.ReadFromJsonAsync(ContractsJsonContext.Default.ProjectAlternative, Token))!;
        Assert.Equal(("Second", true), (second.Name, second.Current));
        Assert.False((await client.ProjectAsync()).Dirty);
        await client.EditAsync(new AddTrackCommand("Only in the second"));

        var alternatives = await AlternativesAsync(client);
        Assert.Equal(2, alternatives.Count);
        var original = alternatives.Single(static a => !a.Current);
        Assert.Equal(second.Id, alternatives.Single(static a => a.Current).Id);

        // Switching keeps the unsaved edit in a backup and opens the first alternative as it was saved.
        var opened = await PostProjectAsync(client, $"/api/v1/project/alternatives/{original.Id}/open");
        Assert.Equal(first, Content(opened));
        Assert.NotEmpty(await BackupsAsync(client));
        Assert.Equal(original.Id, (await AlternativesAsync(client)).Single(static a => a.Current).Id);

        // The second, as saved when it was made (without the unsaved track).
        var back = await PostProjectAsync(client, $"/api/v1/project/alternatives/{second.Id}/open");
        Assert.Equal(["First"], back.Tracks.Select(static t => t.Name));

        await ApiCalls.AssertProblemAsync(
            await client.DeleteAsync($"/api/v1/project/alternatives/{second.Id}", Token),
            HttpStatusCode.UnprocessableEntity
        );
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/v1/project/alternatives/{original.Id}", Token)).StatusCode
        );
        Assert.Single(await AlternativesAsync(client));
        await ApiCalls.AssertProblemAsync(
            await client.DeleteAsync($"/api/v1/project/alternatives/{original.Id}", Token),
            HttpStatusCode.NotFound
        );
        await ApiCalls.AssertProblemAsync(
            await client.PostAsJsonAsync(
                "/api/v1/project/alternatives",
                new(" "),
                ContractsJsonContext.Default.CreateAlternativeRequest,
                Token
            ),
            HttpStatusCode.UnprocessableEntity
        );
    }

    [Fact]
    public async Task Every_save_keeps_a_backup_of_the_one_before_and_ten_are_kept() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await client.CreateProjectAsync();
        Assert.Empty(await BackupsAsync(client));
        for (var i = 0; i < 12; i++) {
            await client.EditAsync(new AddTrackCommand($"Track {i}"));
            await PostAsync(client, "/api/v1/project/save");
        }

        var backups = await BackupsAsync(client);
        Assert.Equal(10, backups.Count);
        Assert.True(backups[0].Saved >= backups[^1].Saved);

        // The newest backup is the save before the last: eleven tracks.
        var reverted = await PostProjectAsync(client, $"/api/v1/project/backups/{backups[0].Id}/revert");
        Assert.Equal(11, reverted.Tracks.Count);
        Assert.Equal(11, (await client.ProjectAsync()).Tracks.Count);
        await ApiCalls.AssertProblemAsync(
            await client.PostAsync("/api/v1/project/backups/nope/revert", null, Token),
            HttpStatusCode.NotFound
        );
    }

    [Fact]
    public async Task Screensets_are_saved_in_the_project_without_an_undo_step() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        var project = await client.CreateProjectAsync();
        var saved = await PutAsync(
            client,
            "/api/v1/project/screensets/2",
            new(0, " Mixing ", true, """{"panes":["mixer"]}"""),
            ContractsJsonContext.Default.Screenset
        );
        var screenset = (await saved.Content.ReadFromJsonAsync(ContractsJsonContext.Default.Screenset, Token))!;
        Assert.Equal(new(2, "Mixing", true, """{"panes":["mixer"]}"""), screenset);
        var list = await client.GetFromJsonAsync(
            "/api/v1/project/screensets",
            ContractsJsonContext.Default.IReadOnlyListScreenset,
            Token
        );
        Assert.Equal([screenset], list!);
        var now = await client.ProjectAsync();
        Assert.True(now.Dirty);
        Assert.Equal(project.Revision, now.Revision);
        Assert.Empty(
            (await client.GetFromJsonAsync("/api/v1/project/history", ContractsJsonContext.Default.UndoHistory, Token))!
            .Undo
        );

        await PostAsync(client, "/api/v1/project/save");
        await PostAsync(client, "/api/v1/project/close");
        await client.PostAsJsonAsync(
            "/api/v1/projects/open",
            new(project.Path),
            ContractsJsonContext.Default.OpenProjectRequest,
            Token
        );
        Assert.Equal(
            [screenset],
            (await client.GetFromJsonAsync(
                "/api/v1/project/screensets",
                ContractsJsonContext.Default.IReadOnlyListScreenset,
                Token
            ))!
        );

        await ApiCalls.AssertProblemAsync(
            await PutAsync(client, "/api/v1/project/screensets/10", screenset, ContractsJsonContext.Default.Screenset),
            HttpStatusCode.NotFound
        );
        await ApiCalls.AssertProblemAsync(
            await PutAsync(
                client,
                "/api/v1/project/screensets/3",
                screenset with { Name = string.Empty },
                ContractsJsonContext.Default.Screenset
            ),
            HttpStatusCode.UnprocessableEntity
        );
    }

    [Fact]
    public async Task A_MIDI_file_imports_as_instrument_tracks_in_one_undoable_step() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await client.CreateProjectAsync();
        var path = Path.Combine(media, "Groove.mid");
        await File.WriteAllBytesAsync(path, TestMidiFile.Bytes, Token);
        var response = await client.PostAsJsonAsync(
            "/api/v1/project/files/import-midi",
            new(path),
            ContractsJsonContext.Default.ImportFileRequest,
            Token
        );
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var project = await client.ProjectAsync();
        var track = Assert.Single(project.Tracks);
        Assert.Equal(("Groove", TrackKind.Instrument), (track.Name, track.Kind));
        Assert.Equal(2, Assert.Single(project.MidiRegions).Notes.Count);
        Assert.Equal(90, project.Settings.Tempo, 6);
        var history = await client.GetFromJsonAsync(
            "/api/v1/project/history",
            ContractsJsonContext.Default.UndoHistory,
            Token
        );
        Assert.Equal("Import MIDI File", history!.Undo[0].Label);
        await client.PostResultAsync("/api/v1/project/undo");
        var undone = await client.ProjectAsync();
        Assert.Empty(undone.Tracks);
        Assert.Equal(120, undone.Settings.Tempo);

        await ApiCalls.AssertProblemAsync(
            await client.PostAsJsonAsync(
                "/api/v1/project/files/import-midi",
                new(Path.Combine(media, "none.mid")),
                ContractsJsonContext.Default.ImportFileRequest,
                Token
            ),
            HttpStatusCode.NotFound
        );
        var bad = Path.Combine(media, "bad.mid");
        await File.WriteAllTextAsync(bad, "not midi", Token);
        await ApiCalls.AssertProblemAsync(
            await client.PostAsJsonAsync(
                "/api/v1/project/files/import-midi",
                new(bad),
                ContractsJsonContext.Default.ImportFileRequest,
                Token
            ),
            HttpStatusCode.UnprocessableEntity
        );
    }

    [Fact]
    public async Task Key_commands_and_bookmarks_are_the_users_and_persist() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        var empty = await client.GetFromJsonAsync(
            "/api/v1/settings/key-commands",
            ContractsJsonContext.Default.KeyCommandSet,
            Token
        );
        Assert.Empty(empty!.Bindings);
        var set = new KeyCommandSet(
            new Dictionary<string, IReadOnlyList<string>> {
                ["transport.play"] = ["Space", "Enter"], ["edit.undo"] = ["Meta+Z"]
            }
        );
        Assert.Equal(
            HttpStatusCode.OK,
            (await PutAsync(client, "/api/v1/settings/key-commands", set, ContractsJsonContext.Default.KeyCommandSet))
            .StatusCode
        );
        var read = await client.GetFromJsonAsync(
            "/api/v1/settings/key-commands",
            ContractsJsonContext.Default.KeyCommandSet,
            Token
        );
        Assert.Equal(["Space", "Enter"], read!.Bindings["transport.play"]);
        Assert.Equal(2, read.Bindings.Count);
        await ApiCalls.AssertProblemAsync(
            await PutAsync(
                client,
                "/api/v1/settings/key-commands",
                new(new Dictionary<string, IReadOnlyList<string>> { [string.Empty] = ["X"] }),
                ContractsJsonContext.Default.KeyCommandSet
            ),
            HttpStatusCode.UnprocessableEntity
        );

        Assert.Empty(
            (await client.GetFromJsonAsync(
                "/api/v1/settings/bookmarks",
                ContractsJsonContext.Default.Bookmarks,
                Token
            ))!.Paths
        );
        await PutAsync(
            client,
            "/api/v1/settings/bookmarks",
            new Bookmarks([media, media + "/"]),
            ContractsJsonContext.Default.Bookmarks
        );
        Assert.Equal(
            [Path.TrimEndingDirectorySeparator(media)],
            (await client.GetFromJsonAsync(
                "/api/v1/settings/bookmarks",
                ContractsJsonContext.Default.Bookmarks,
                Token
            ))!.Paths
        );
        await ApiCalls.AssertProblemAsync(
            await PutAsync(
                client,
                "/api/v1/settings/bookmarks",
                new Bookmarks(["relative/path"]),
                ContractsJsonContext.Default.Bookmarks
            ),
            HttpStatusCode.UnprocessableEntity
        );
    }

    [Fact]
    public async Task The_file_browser_lists_folders_first_with_kinds_and_audio_formats() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        Directory.CreateDirectory(Path.Combine(media, "Loops"));
        Directory.CreateDirectory(Path.Combine(media, "Song.anode"));
        Directory.CreateDirectory(Path.Combine(media, ".hidden"));
        TestWav.Write(media, "Kick.wav", channels: 2, frames: 4800);
        await File.WriteAllBytesAsync(Path.Combine(media, "Groove.mid"), TestMidiFile.Bytes, Token);
        await File.WriteAllTextAsync(Path.Combine(media, "notes.txt"), "hello", Token);
        await File.WriteAllTextAsync(Path.Combine(media, "Broken.wav"), "not a wav", Token);

        var listing = await ListAsync(client, media);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(media)), listing.Path);
        Assert.Equal(Directory.GetParent(media)!.FullName, listing.Parent);
        Assert.Equal(
            [
                ("Loops", FileKind.Folder), ("Broken.wav", FileKind.Audio), ("Groove.mid", FileKind.Midi),
                ("Kick.wav", FileKind.Audio), ("notes.txt", FileKind.Other), ("Song.anode", FileKind.Project)
            ],
            listing.Entries.Select(static e => (e.Name, e.Kind))
        );
        var kick = listing.Entries.Single(static e => e.Name == "Kick.wav");
        Assert.Equal((48_000, 2, 4800L), (kick.SampleRate, kick.Channels, kick.Frames));
        Assert.Null(listing.Entries.Single(static e => e.Name == "Broken.wav").SampleRate);

        var home = await ListAsync(client, null);
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            home.Path
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/v1/files?path={Uri.EscapeDataString(Path.Combine(media, "none"))}", Token))
            .StatusCode
        );
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            (await client.GetAsync("/api/v1/files?path=relative", Token)).StatusCode
        );
    }

    [Fact]
    public async Task Preview_plays_an_audio_file_through_the_engine_and_stops() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await ServerFixture.Eventually(async () =>
            (await server.SystemAsync(client)).Engine.State == EngineState.InProcess
        );
        var host = server.Services.GetRequiredService<EngineConnection>().InProcessHost!;
        var wav = TestWav.Write(media, "Loop.wav");
        Assert.Equal(HttpStatusCode.NoContent, (await PreviewAsync(client, wav)).StatusCode);
        await ServerFixture.Eventually(() => Task.FromResult(host.Preview == wav));
        Assert.Equal(HttpStatusCode.NoContent, (await PreviewAsync(client, null)).StatusCode);
        await ServerFixture.Eventually(() => Task.FromResult(host.Preview is null));

        await ApiCalls.AssertProblemAsync(
            await PreviewAsync(client, Path.Combine(media, "none.wav")),
            HttpStatusCode.NotFound
        );
        var text = Path.Combine(media, "notes.txt");
        await File.WriteAllTextAsync(text, "x", Token);
        await ApiCalls.AssertProblemAsync(await PreviewAsync(client, text), HttpStatusCode.UnprocessableEntity);
    }

    static Task<HttpResponseMessage> PreviewAsync(HttpClient client, string? path) =>
        client.PostAsJsonAsync(
            "/api/v1/files/preview",
            new(path),
            ContractsJsonContext.Default.PreviewRequest,
            Token
        );

    static async Task<DirectoryListing> ListAsync(HttpClient client, string? path) =>
        (await client.GetFromJsonAsync(
            path is null ? "/api/v1/files" : $"/api/v1/files?path={Uri.EscapeDataString(path)}",
            ContractsJsonContext.Default.DirectoryListing,
            Token
        ))!;

    static async Task<IReadOnlyList<ProjectAlternative>> AlternativesAsync(HttpClient client) =>
        (await client.GetFromJsonAsync(
            "/api/v1/project/alternatives",
            ContractsJsonContext.Default.IReadOnlyListProjectAlternative,
            Token
        ))!;

    static async Task<IReadOnlyList<ProjectBackup>> BackupsAsync(HttpClient client) =>
        (await client.GetFromJsonAsync(
            "/api/v1/project/backups",
            ContractsJsonContext.Default.IReadOnlyListProjectBackup,
            Token
        ))!;

    static async Task PostAsync(HttpClient client, string path) {
        var response = await client.PostAsync(path, null, Token);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Token));
    }

    static async Task<Project> PostProjectAsync(HttpClient client, string path) {
        var response = await client.PostAsync(path, null, Token);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Token));
        return (await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.Project, Token))!;
    }

    static Task<HttpResponseMessage> PutAsync<T>(
        HttpClient client,
        string path,
        T body,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type
    ) =>
        client.PutAsJsonAsync(path, body, type, Token);

    static string Content(Project p) =>
        System.Text.Json.JsonSerializer.Serialize(
            p with { Revision = 0, Dirty = false },
            ContractsJsonContext.Default.Project
        );
}

/// <summary>A small Standard MIDI File: format 0, 480 PPQ, 90 BPM, two quarter notes.</summary>
static class TestMidiFile {
    public static byte[] Bytes { get; } = [
        (byte)'M', (byte)'T', (byte)'h', (byte)'d', 0, 0, 0, 6, 0, 0, 0, 1, 0x01, 0xE0,
        (byte)'M', (byte)'T', (byte)'r', (byte)'k', 0, 0, 0, 29,
        0x00, 0xFF, 0x51, 0x03, 0x0A, 0x2C, 0x2B, // 666 667 µs: 90 BPM
        0x00, 0x90, 0x3C, 0x64,
        0x83, 0x60, 0x80, 0x3C, 0x00,
        0x00, 0x90, 0x3E, 0x64,
        0x83, 0x60, 0x80, 0x3E, 0x00,
        0x00, 0xFF, 0x2F, 0x00
    ];
}
