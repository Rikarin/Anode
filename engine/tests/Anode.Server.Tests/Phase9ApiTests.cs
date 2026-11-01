using Anode.Contracts;
using Anode.Model.Editing;
using Anode.Model.Midi;
using Anode.Model.Tests;
using Anode.Server.Projects;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace Anode.Server.Tests;

/// <summary>
///     Phase 9's server (docs/10-phase-9.md, workstream B): the score API through a stand-in notation engine, the score
///     commands and score edits over REST, and the interchange jobs with stand-ins for MusicXML and AAF (the Standard
///     MIDI File mapping is real).
/// </summary>
public sealed class Phase9ApiTests : IAsyncDisposable {
    static CancellationToken Token => TestContext.Current.CancellationToken;

    readonly string media = Directory.CreateTempSubdirectory("anode-interchange-").FullName;
    readonly FakeNotation notation = new();
    readonly FakeFormats formats = new();
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

    async Task StartAsync() {
        server = new(
            configure: s => {
                s.AddSingleton<INotation>(notation);
                s.AddSingleton<IScoreNotation>(ScoreNotationDouble.Instance);
                s.AddSingleton<IInterchangeFormats>(formats);
            }
        );
        client = server.CreateClient();
        await ServerFixture.Eventually(async () =>
            (await server.SystemAsync(Client)).Engine.State == EngineState.InProcess
        );
    }

    /// <summary>A project with a piano track holding a region of C4 D4 E4 F4, and a second instrument track.</summary>
    async Task<(Project Project, string Track, string Other, MidiRegion Region)> PianoAsync() {
        await Client.CreateProjectAsync();
        await Client.EditAsync(
            new AddTrackCommand("Piano", Kind: TrackKind.Instrument),
            new AddTrackCommand("Clarinet", Kind: TrackKind.Instrument)
        );
        var project = await Client.ProjectAsync();
        var track = project.Tracks[0].Id;
        await Client.EditAsync(new AddMidiRegionCommand(track, 0, 3840));
        var regionId = (await Client.ProjectAsync()).MidiRegions[0].Id;
        await Client.EditAsync(
            new AddNotesCommand(
                regionId,
                [..new[] { 60, 62, 64, 65 }.Select(static (p, i) => new NewNote(p, 90, 1, i * 960L, 960))]
            )
        );
        project = await Client.ProjectAsync();
        return (project, track, project.Tracks[1].Id, project.MidiRegions.Single(r => r.Id == regionId));
    }

    Task<HttpResponseMessage> ScoreAsync(string query = "") => Client.GetAsync($"/api/v1/project/score{query}", Token);

    async Task<int> StepsAsync() =>
        (await Client.GetFromJsonAsync("/api/v1/project/history", ContractsJsonContext.Default.UndoHistory, Token))!
        .Undo.Count;

    async Task<Job> PostJobAsync<T>(
        string path,
        T request,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type
    ) {
        var response = await Client.PostAsJsonAsync(path, request, type, Token);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Token));
        return (await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.Job, Token))!;
    }

    async Task<Job> WaitAsync(Job job) {
        Job? now = null;
        await ServerFixture.Eventually(async () => {
                now = await Client.GetFromJsonAsync($"/api/v1/jobs/{job.Id}", ContractsJsonContext.Default.Job, Token);
                return now!.State is JobState.Done or JobState.Failed or JobState.Cancelled;
            }
        );
        return now!;
    }

    async Task<Job> DoneAsync<T>(string path, T request, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) {
        var job = await WaitAsync(await PostJobAsync(path, request, type));
        Assert.True(job.State == JobState.Done, job.Message);
        return job;
    }

    [Fact]
    public async Task The_score_is_rendered_cached_per_revision_and_checked() {
        await StartAsync();
        await ApiCalls.AssertProblemAsync(await ScoreAsync(), HttpStatusCode.NotFound);
        var (_, track, _, region) = await PianoAsync();
        var first = await ScoreAsync();
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var document = (await first.Content.ReadFromJsonAsync(ContractsJsonContext.Default.ScoreDocument, Token))!;
        Assert.Equal(["Piano", "Clarinet"], document.View.Parts.Select(static p => p.Name));
        Assert.All(region.Notes, n => Assert.Contains(ScoreIds.Note(n.Id), document.Mei, StringComparison.Ordinal));
        Assert.Equal(ScoreViewMode.Linear, document.View.Mode);

        // The same request at the same revision is answered from the cache, byte for byte.
        var renders = notation.Renders;
        var again = await ScoreAsync();
        Assert.Equal(await first.Content.ReadAsStringAsync(Token), await again.Content.ReadAsStringAsync(Token));
        Assert.Equal(renders, notation.Renders);
        var page = (await Client.GetFromJsonAsync(
            "/api/v1/project/score?view=page",
            ContractsJsonContext.Default.ScoreDocument,
            Token
        ))!;
        Assert.Equal(ScoreViewMode.Page, page.View.Mode);
        Assert.Equal(renders + 1, notation.Renders);

        // An edit is a new revision: rendered again, with the symbol drawn at its note.
        await Client.EditAsync(new AddScoreSymbolsCommand([new(ScoreSymbolKind.Staccato, track, region.Notes[0].Id)]));
        document = (await Client.GetFromJsonAsync(
            "/api/v1/project/score",
            ContractsJsonContext.Default.ScoreDocument,
            Token
        ))!;
        Assert.Equal(renders + 2, notation.Renders);
        var symbol = Assert.Single(document.View.Symbols);
        Assert.Contains(symbol.Id, document.View.Parts[0].Measures[0].Staves[0].Voices[0].Events[0].SymbolIds);

        // A set and a part.
        await Client.EditAsync(new SetScoreSetCommand(new("scs_piano", "Piano only", [track])));
        var set = (await Client.GetFromJsonAsync(
            "/api/v1/project/score?set=scs_piano",
            ContractsJsonContext.Default.ScoreDocument,
            Token
        ))!;
        Assert.Equal([track], set.View.Parts.Select(static p => p.TrackId));
        var part = (await Client.GetFromJsonAsync(
            $"/api/v1/project/score?part={track}",
            ContractsJsonContext.Default.ScoreDocument,
            Token
        ))!;
        Assert.True(part.View.Transposing);

        await ApiCalls.AssertProblemAsync(await ScoreAsync("?view=scroll"), HttpStatusCode.UnprocessableEntity);
        await ApiCalls.AssertProblemAsync(await ScoreAsync("?set=scs_none"), HttpStatusCode.UnprocessableEntity);
        await ApiCalls.AssertProblemAsync(await ScoreAsync("?part=trk_none"), HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task The_library_lists_the_factory_staff_styles() {
        await StartAsync();
        var styles = await Client.GetFromJsonAsync(
            "/api/v1/library/staff-styles",
            ContractsJsonContext.Default.IReadOnlyListStaffStyle,
            Token
        );
        Assert.Equal(ScoreNotationDouble.Styles.Select(static s => s.Id), styles!.Select(static s => s.Id));
    }

    [Fact]
    public async Task Score_content_and_edits_go_through_commands_one_undo_step_each() {
        await StartAsync();
        var (_, track, other, region) = await PianoAsync();
        await using var events = await EventStream.ConnectAsync(server!);
        await Client.EditAsync(
            new AddScoreSymbolsCommand([new(ScoreSymbolKind.Accent, track, region.Notes[2].Id)]),
            new SetLyricsCommand(1, [new(region.Notes[2].Id, "la")])
        );
        var project = await Client.ProjectAsync();
        Assert.Single(project.Score.Symbols);
        Assert.Single(project.Score.Lyrics);
        await events.WaitAsync<ProjectChangedEvent>(static e => e.Delta.Ops.Any(static o => o is ScoreSymbolUpserted));

        // Drag E4 up a step: F4. One step, and the Piano Roll's notes are the same notes.
        var steps = await StepsAsync();
        await Client.EditAsync(new MoveScoreNotesCommand(region.Id, [region.Notes[2].Id], 1));
        project = await Client.ProjectAsync();
        Assert.Equal(65, project.MidiRegions.Single().Notes.Single(n => n.Id == region.Notes[2].Id).Pitch);
        Assert.Equal(steps + 1, await StepsAsync());

        // Insert a quarter at beat 3 of bar 2 on the other track: a region of bar 2 there.
        await Client.EditAsync(new AddScoreNoteCommand(other, 3840 + 1920, 960, 4));
        project = await Client.ProjectAsync();
        var added = project.MidiRegions.Single(r => r.TrackId == other);
        Assert.Equal((3840L, 71), (added.StartTick, Assert.Single(added.Notes).Pitch));
        Assert.Equal(steps + 2, await StepsAsync());

        // Delete the note with its accent and syllable; undo brings all back.
        await Client.EditAsync(new DeleteNotesCommand(region.Id, [region.Notes[2].Id]));
        project = await Client.ProjectAsync();
        Assert.Empty(project.Score.Symbols);
        Assert.Empty(project.Score.Lyrics);
        await Client.PostResultAsync("/api/v1/project/undo");
        project = await Client.ProjectAsync();
        Assert.Single(project.Score.Symbols);
        Assert.Single(project.Score.Lyrics);

        // The score survives save and reopen.
        Assert.Equal(HttpStatusCode.OK, (await Client.PostAsync("/api/v1/project/save", null, Token)).StatusCode);
        var path = project.Path;
        await Client.PostAsync("/api/v1/project/close", null, Token);
        var opened = await Client.PostAsJsonAsync(
            "/api/v1/projects/open",
            new(path),
            ContractsJsonContext.Default.OpenProjectRequest,
            Token
        );
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        project = await Client.ProjectAsync();
        Assert.Single(project.Score.Symbols);
        Assert.Equal("la", Assert.Single(project.Score.Lyrics).Text);
    }

    [Fact]
    public async Task Exports_write_their_files_into_the_projects_Exports_folder() {
        await StartAsync();
        var (project, track, _, region) = await PianoAsync();
        await Client.EditAsync(new SetScoreLayoutCommand(ScoreLayout.Default with { Title = "Etude" }));

        var job = await DoneAsync(
            "/api/v1/project/export/musicxml",
            new("Etude"),
            ContractsJsonContext.Default.MusicXmlExportRequest
        );
        var file = Assert.Single(job.Files);
        Assert.Equal(Path.Combine(project.Path, Jobs.ExportsFolder, "Etude.musicxml"), file);
        Assert.Contains("title=\"Etude\"", await File.ReadAllTextAsync(file, Token), StringComparison.Ordinal);
        job = await DoneAsync(
            "/api/v1/project/export/musicxml",
            new("Part.mxl", media, PartTrackId: track, Compressed: true),
            ContractsJsonContext.Default.MusicXmlExportRequest
        );
        Assert.Equal(Path.Combine(media, "Part.mxl"), Assert.Single(job.Files));
        Assert.Equal([track], formats.Written!.Parts.Select(static p => p.TrackId));

        job = await DoneAsync(
            "/api/v1/project/export/midi",
            new("Song"),
            ContractsJsonContext.Default.MidiExportRequest
        );
        var midi = StandardMidiFile.Read(Assert.Single(job.Files));
        Assert.Equal(1, midi.Format);
        Assert.Equal(
            region.Notes.Select(static n => n.Pitch),
            midi.Tracks.Single(static t => t.Notes.Count > 0).Notes.Select(static n => n.Pitch).Order()
        );
        job = await DoneAsync(
            "/api/v1/project/export/midi",
            new("Region", Format: 0, RegionId: region.Id),
            ContractsJsonContext.Default.MidiExportRequest
        );
        Assert.Equal(0, StandardMidiFile.Read(Assert.Single(job.Files)).Format);

        // Audio for AAF: embedded (one file) and linked (the media beside it).
        await Client.EditAsync(new AddTrackCommand("Dialog"));
        var audio = (await Client.ProjectAsync()).Tracks.Single(static t => t.Kind == TrackKind.Audio).Id;
        var wav = await Client.ImportAsync(TestWav.Write(media, "Line.wav"));
        await Client.EditAsync(new AddRegionCommand(audio, wav.Id, 0));
        job = await DoneAsync(
            "/api/v1/project/export/aaf",
            new("Edit"),
            ContractsJsonContext.Default.AafExportRequest
        );
        Assert.Equal($"AAF {audio}", await File.ReadAllTextAsync(Assert.Single(job.Files), Token));
        job = await DoneAsync(
            "/api/v1/project/export/aaf",
            new("Linked", EmbedMedia: false),
            ContractsJsonContext.Default.AafExportRequest
        );
        Assert.Equal(2, job.Files.Count);
        Assert.EndsWith(Path.Combine("Linked Media", "Line.wav"), job.Files[1], StringComparison.Ordinal);

        // Requests that cannot be carried out.
        var bad = await Client.PostAsJsonAsync(
            "/api/v1/project/export/musicxml",
            new("X", SetId: "scs_none"),
            ContractsJsonContext.Default.MusicXmlExportRequest,
            Token
        );
        await ApiCalls.AssertProblemAsync(bad, HttpStatusCode.UnprocessableEntity);
        bad = await Client.PostAsJsonAsync(
            "/api/v1/project/export/midi",
            new("X", RegionId: "mid_none"),
            ContractsJsonContext.Default.MidiExportRequest,
            Token
        );
        await ApiCalls.AssertProblemAsync(bad, HttpStatusCode.NotFound);
        bad = await Client.PostAsJsonAsync(
            "/api/v1/project/export/midi",
            new("X", Format: 2),
            ContractsJsonContext.Default.MidiExportRequest,
            Token
        );
        await ApiCalls.AssertProblemAsync(bad, HttpStatusCode.UnprocessableEntity);
        bad = await Client.PostAsJsonAsync(
            "/api/v1/project/export/aaf",
            new AafExportRequest("X", TrackIds: [track]),
            ContractsJsonContext.Default.AafExportRequest,
            Token
        );
        await ApiCalls.AssertProblemAsync(bad, HttpStatusCode.UnprocessableEntity);
        bad = await Client.PostAsJsonAsync(
            "/api/v1/project/export/midi",
            new("a/b"),
            ContractsJsonContext.Default.MidiExportRequest,
            Token
        );
        await ApiCalls.AssertProblemAsync(bad, HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_MusicXML_import_adds_its_tracks_and_score_in_one_undo_step() {
        await StartAsync();
        await Client.CreateProjectAsync();
        var path = Path.Combine(media, "Flute.musicxml");
        await File.WriteAllTextAsync(path, "<score-partwise/>", Token);
        var steps = await StepsAsync();
        await DoneAsync(
            "/api/v1/project/import/musicxml",
            new(path),
            ContractsJsonContext.Default.MusicXmlImportRequest
        );
        var project = await Client.ProjectAsync();
        var flute = Assert.Single(project.Tracks);
        Assert.Equal(("Flute", StaffStyleIds.Treble), (flute.Name, flute.StaffStyleId));
        var region = Assert.Single(project.MidiRegions);
        Assert.Equal(region.Notes[0].Id, Assert.Single(project.Score.Symbols).NoteId);
        Assert.Equal(region.Notes[1].Id, Assert.Single(project.Score.Lyrics).NoteId);
        Assert.Equal("Imported", project.Score.Layout.Title);
        // Into an empty project the score's tempo and meter are the project's.
        Assert.Equal(80, project.Settings.Tempo);
        Assert.Equal(steps + 1, await StepsAsync());

        // Into a project with tracks they are not.
        await DoneAsync(
            "/api/v1/project/import/musicxml",
            new(path),
            ContractsJsonContext.Default.MusicXmlImportRequest
        );
        project = await Client.ProjectAsync();
        Assert.Equal(2, project.Tracks.Count);
        await Client.PostResultAsync("/api/v1/project/undo");
        await Client.PostResultAsync("/api/v1/project/undo");
        project = await Client.ProjectAsync();
        Assert.Empty(project.Tracks);
        Assert.Empty(project.Score.Symbols);
        Assert.Equal(120, project.Settings.Tempo);

        // An upload, as the browser sends it.
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("<score-partwise/>")), "file", "Up.musicxml");
        var response = await Client.PostAsync("/api/v1/project/import/musicxml/upload", form, Token);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var job = await WaitAsync((await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.Job, Token))!);
        Assert.True(job.State == JobState.Done, job.Message);
        Assert.Single((await Client.ProjectAsync()).Tracks);
        Assert.Empty(Directory.EnumerateDirectories(project.Path, ".upload-*"));
    }

    [Fact]
    public async Task Failed_imports_change_nothing_and_the_queue_goes_on() {
        await StartAsync();
        var project = await Client.CreateProjectAsync();
        var missing = await Client.PostAsJsonAsync(
            "/api/v1/project/import/aaf",
            new(Path.Combine(media, "none.aaf")),
            ContractsJsonContext.Default.AafImportRequest,
            Token
        );
        await ApiCalls.AssertProblemAsync(missing, HttpStatusCode.UnprocessableEntity);

        var bad = Path.Combine(media, "bad.musicxml");
        await File.WriteAllTextAsync(bad, "not xml", Token);
        var job = await WaitAsync(
            await PostJobAsync(
                "/api/v1/project/import/musicxml",
                new(bad),
                ContractsJsonContext.Default.MusicXmlImportRequest
            )
        );
        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("Not a MusicXML file", job.Message, StringComparison.Ordinal);

        // A reader that is not there yet (a workstream's stub) fails its job, not the queue.
        formats.Throw = new NotImplementedException("Phase 9 workstream D");
        job = await WaitAsync(
            await PostJobAsync(
                "/api/v1/project/export/musicxml",
                new("X"),
                ContractsJsonContext.Default.MusicXmlExportRequest
            )
        );
        Assert.Equal(JobState.Failed, job.State);
        formats.Throw = null;
        var midi = Path.Combine(media, "In.mid");
        await File.WriteAllBytesAsync(midi, StandardMidiFileWriter.Write([new(0, 60, 90, 1, 0, 480, 0)]), Token);
        await DoneAsync(
            "/api/v1/project/import/midi",
            new(midi),
            ContractsJsonContext.Default.MidiImportRequest
        );
        Assert.Single((await Client.ProjectAsync()).Tracks);
        _ = project;
    }

    [Fact]
    public async Task A_MIDI_file_imports_with_its_options_from_disk_or_an_upload() {
        await StartAsync();
        var (project, _, _, _) = await PianoAsync();
        await Client.EditAsync(
            new SetTempoEventsCommand([new(0, 100, TempoCurve.Step)]),
            new AddMarkerCommand("Intro", 0),
            new AddMarkerCommand("Verse", 3840)
        );
        var file = Path.Combine(media, "Song.mid");
        await DoneAsync(
            "/api/v1/project/export/midi",
            new("Song", media),
            ContractsJsonContext.Default.MidiExportRequest
        );
        await Client.CreateProjectAsync("Other");
        var steps = await StepsAsync();
        await DoneAsync(
            "/api/v1/project/import/midi",
            new(file),
            ContractsJsonContext.Default.MidiImportRequest
        );
        var imported = await Client.ProjectAsync();
        Assert.Equal(["Piano"], imported.Tracks.Select(static t => t.Name));
        Assert.Equal(100, imported.Settings.Tempo);
        Assert.Equal(["Intro", "Verse"], imported.Markers.OrderBy(static m => m.Tick).Select(static m => m.Name));
        Assert.Equal(steps + 1, await StepsAsync());
        await Client.PostResultAsync("/api/v1/project/undo");

        // Uploaded, without the tempo and markers.
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(await File.ReadAllBytesAsync(file, Token)), "file", "Song.mid");
        var response = await Client.PostAsync(
            "/api/v1/project/import/midi/upload?importTempo=false&importMarkers=false",
            form,
            Token
        );
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var job = await WaitAsync((await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.Job, Token))!);
        Assert.True(job.State == JobState.Done, job.Message);
        imported = await Client.ProjectAsync();
        Assert.Single(imported.Tracks);
        Assert.Equal(120, imported.Settings.Tempo);
        Assert.Empty(imported.Markers);
        _ = project;
    }

    [Fact]
    public async Task An_AAF_import_copies_its_media_and_adds_its_tracks_in_one_undo_step() {
        await StartAsync();
        var project = await Client.CreateProjectAsync();
        var path = Path.Combine(media, "Edit.aaf");
        await File.WriteAllTextAsync(path, "AAF x", Token);
        var steps = await StepsAsync();
        var job = await DoneAsync(
            "/api/v1/project/import/aaf",
            new(path),
            ContractsJsonContext.Default.AafImportRequest
        );
        var copied = Assert.Single(job.Files);
        Assert.Equal(Path.Combine(project.Path, "Audio Files", "Dialog Clip.wav"), copied);
        Assert.True(File.Exists(copied));
        project = await Client.ProjectAsync();
        var track = Assert.Single(project.Tracks);
        Assert.Equal(("Dialog", TrackKind.Audio), (track.Name, track.Kind));
        var region = Assert.Single(project.Regions);
        Assert.Equal((1920L, 24_000L, -3.0), (region.StartTick, region.Length, region.GainDb));
        Assert.Equal(project.AudioFiles.Single().Id, region.FileId);
        Assert.Equal("Hit", Assert.Single(project.Markers).Name);
        Assert.Equal(steps + 1, await StepsAsync());
        Assert.Empty(Directory.EnumerateDirectories(project.Path, ".aaf-*"));
        await Client.PostResultAsync("/api/v1/project/undo");
        project = await Client.ProjectAsync();
        Assert.Empty(project.Tracks);
        Assert.Empty(project.Markers);
    }
}
