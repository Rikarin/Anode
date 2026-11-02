using Anode.Contracts;
using Anode.Model.Audio;
using Anode.Notation;
using Anode.Server.Projects;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Anode.Server.Tests;

/// <summary>
///     Phase 9's server with its own registrations (docs/10-phase-9.md, integration): the notation engine
///     (<see cref="EngineNotation" />, <see cref="EngineScoreNotation" />) behind the score API and the score edits, and
///     the real MusicXML and AAF readers and writers (<see cref="InterchangeFormats" />) behind the jobs, end to end.
/// </summary>
public sealed class Phase9EngineTests : IAsyncDisposable {
    const int Bar = 3840;

    static CancellationToken Token => TestContext.Current.CancellationToken;

    readonly string media = Directory.CreateTempSubdirectory("anode-phase9-engine-").FullName;
    ServerFixture? server;
    HttpClient? client;

    HttpClient Client => client!;

    public async ValueTask DisposeAsync() {
        client?.Dispose();
        if (server is not null) {
            await server.DisposeAsync();
        }

        Directory.Delete(media, true);
    }

    async Task StartAsync() {
        server = new();
        client = server.CreateClient();
        await ServerFixture.Eventually(async () =>
            (await server.SystemAsync(Client)).Engine.State == EngineState.InProcess
        );
    }

    /// <summary>
    ///     The exit criterion's piano passage (chords, a tie over the bar line, a triplet, two voices, notes off the grid)
    ///     on a piano track at 1/16 with the 1/16-triplet grid, and a B♭ clarinet track holding one B♭4.
    /// </summary>
    async Task<(Project Project, string Piano, string Clarinet)> NotationAsync() {
        await Client.CreateProjectAsync();
        await Client.EditAsync(
            new AddTrackCommand("Piano", Kind: TrackKind.Instrument),
            new AddTrackCommand("Clarinet", Kind: TrackKind.Instrument),
            new SetKeysCommand([new(0, 0, KeyMode.Major)])
        );
        var project = await Client.ProjectAsync();
        var (piano, clarinet) = (project.Tracks[0].Id, project.Tracks[1].Id);
        await Client.EditAsync(
            new AssignStaffStyleCommand([piano], StaffStyleIds.Piano),
            new AssignStaffStyleCommand([clarinet], StaffStyleIds.ClarinetBb),
            new AddMidiRegionCommand(piano, 0, 3 * Bar),
            new AddMidiRegionCommand(clarinet, 0, Bar)
        );
        project = await Client.ProjectAsync();
        var pianoRegion = project.MidiRegions.Single(r => r.TrackId == piano).Id;
        var clarinetRegion = project.MidiRegions.Single(r => r.TrackId == clarinet).Id;
        // A's exit criterion case (Anode.Notation.Tests' Cases.ExitCriterion, the "exit-criterion" vector).
        (int Pitch, long Start, long Length)[] passage = [
            (60, 12, 940), (64, 12, 940), (67, 12, 940), (62, 965, 300), (64, 1290, 300), (65, 1595, 300),
            (67, 1920, 2880), (48, 0, Bar), (76, Bar + 960, 2880), (72, Bar + 960, 960), (74, Bar + 1920, 960),
            (71, Bar + 2880, 960), (41, Bar + 10, 1900), (43, Bar + 1930, 1900), (64, 2 * Bar, 240),
            (65, 2 * Bar + 240, 240), (67, 2 * Bar + 480, 480), (60, 2 * Bar + 960, 2880), (64, 2 * Bar + 960, 2880),
            (67, 2 * Bar + 960, 2880), (48, 2 * Bar, Bar)
        ];
        await Client.EditAsync(
            new AddNotesCommand(
                pianoRegion,
                [..passage.Select(static n => new NewNote(n.Pitch, 90, 1, n.Start, n.Length))]
            ),
            new AddNotesCommand(clarinetRegion, [new(70, 90, 1, 0, Bar)]),
            new SetRegionNotationCommand([pianoRegion], 240, 160)
        );
        return (await Client.ProjectAsync(), piano, clarinet);
    }

    async Task<ScoreDocument> ScoreAsync(string query) =>
        (await Client.GetFromJsonAsync(
            $"/api/v1/project/score?{query}",
            ContractsJsonContext.Default.ScoreDocument,
            Token
        ))!;

    async Task<Job> DoneAsync<T>(string path, T request, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) {
        var response = await Client.PostAsJsonAsync(path, request, type, Token);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Token));
        var job = (await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.Job, Token))!;
        await ServerFixture.Eventually(async () => {
                job = await Client.GetFromJsonAsync($"/api/v1/jobs/{job.Id}", ContractsJsonContext.Default.Job, Token);
                return job!.State is JobState.Done or JobState.Failed or JobState.Cancelled;
            }
        );
        Assert.True(job.State == JobState.Done, job.Message);
        return job;
    }

    static IEnumerable<ScoreEvent> Events(ScoreDocument document, string trackId, int staff, int voice) =>
        document.View.Parts.Single(p => p.TrackId == trackId)
            .Measures.SelectMany(m => m.Staves[staff].Voices.Where(v => v.Number == voice))
            .SelectMany(static v => v.Events);

    [Fact]
    public async Task The_score_API_and_library_come_from_the_notation_engine() {
        await StartAsync();
        var styles = await Client.GetFromJsonAsync(
            "/api/v1/library/staff-styles",
            ContractsJsonContext.Default.IReadOnlyListStaffStyle,
            Token
        );
        Assert.Equal(
            JsonSerializer.Serialize(FactoryStaffStyles.All, ContractsJsonContext.Default.IReadOnlyListStaffStyle),
            JsonSerializer.Serialize(styles!, ContractsJsonContext.Default.IReadOnlyListStaffStyle)
        );

        var (project, piano, clarinet) = await NotationAsync();
        var notes = project.MidiRegions.Single(r => r.TrackId == piano).Notes;
        long Id(int pitch, long start) => notes.Single(n => n.Pitch == pitch && Math.Abs(n.StartTick - start) < 20).Id;
        var document = await ScoreAsync($"part={piano}&view=linear");
        Assert.Equal(MeiWriter.Write(document.View), document.Mei);
        var treble = Events(document, piano, 0, 1).ToList();
        Assert.Equal(
            (ScoreEventKind.Chord, 0L, NoteValue.Quarter),
            (treble[0].Kind, treble[0].Tick, treble[0].Duration.Value)
        );
        var triplet = treble.Where(static e => e.Duration.Tuplet is not null).ToList();
        Assert.Equal([960L, 1280, 1600], triplet.Select(static e => e.Tick));
        Assert.Equal([Id(62, 965), Id(64, 1290), Id(65, 1595)], triplet.Select(static e => e.Notes[0].NoteId));
        Assert.Single(triplet.Select(static e => e.Duration.Tuplet!.Group).Distinct());
        Assert.Equal(
            [(1920L, true, false), (Bar, false, true)],
            treble.Where(e => e.Notes.Any(n => n.NoteId == Id(67, 1920)))
                .Select(static e => (e.Tick, e.Notes[0].TieStart, e.Notes[0].TieStop))
        );
        Assert.Equal(
            [72, 74, 71],
            Events(document, piano, 0, 2)
                .Where(static e => e.Kind != ScoreEventKind.Rest)
                .Select(static e => e.Notes[0].Pitch)
        );
        Assert.Equal(
            [48, 41, 43, 48],
            Events(document, piano, 1, 1)
                .Where(static e => e.Kind != ScoreEventKind.Rest)
                .Select(static e => e.Notes[0].Pitch)
        );
        Assert.All(
            notes,
            n => Assert.Contains($"xml:id=\"{ScoreIds.Note(n.Id)}\"", document.Mei, StringComparison.Ordinal)
        );

        // The clarinet: written a major second up in its part, at concert pitch in the full score.
        var part = await ScoreAsync($"part={clarinet}");
        Assert.Equal(new(PitchStep.C, 0, 5), Events(part, clarinet, 0, 1).First().Notes[0].Written);
        var full = await ScoreAsync("view=page");
        Assert.False(full.View.Transposing);
        Assert.Equal(new(PitchStep.B, -1, 4), Events(full, clarinet, 0, 1).First().Notes[0].Written);

        // Display quantise 1/8 with the 1/8-triplet grid (Logic's "8,12") changes the score, not the notes: bar 3's
        // sixteenths become E4/8 F4+G4/8.
        var region = project.MidiRegions.Single(r => r.TrackId == piano);
        await Client.EditAsync(new SetRegionNotationCommand([region.Id], 480, 320));
        var eighths = await ScoreAsync($"part={piano}&view=linear");
        Assert.NotEqual(document.Mei, eighths.Mei);
        Assert.Equal(
            [(7680L, NoteValue.Eighth, 1), (8160L, NoteValue.Eighth, 2)],
            Events(eighths, piano, 0, 1)
                .Where(static e => e.Tick is >= 2 * Bar and < 2 * Bar + 960)
                .Select(static e => (e.Tick, e.Duration.Value, e.Notes.Count))
        );
        Assert.Equal(notes, (await Client.ProjectAsync()).MidiRegions.Single(r => r.TrackId == piano).Notes);
    }

    [Fact]
    public async Task Score_edits_convert_with_the_engines_staff_positions_in_the_written_key() {
        await StartAsync();
        var (project, piano, clarinet) = await NotationAsync();
        var region = project.MidiRegions.Single(r => r.TrackId == piano);
        var e5 = region.Notes.Single(static n => n.Pitch == 76).Id;

        // E5 up a step in C major: F5. A quarter at beat 3 of bar 2 on the treble's middle line: B4.
        await Client.EditAsync(new MoveScoreNotesCommand(region.Id, [e5], 1));
        await Client.EditAsync(new AddScoreNoteCommand(piano, Bar + 1920, 960, 4));
        project = await Client.ProjectAsync();
        region = project.MidiRegions.Single(r => r.TrackId == piano);
        Assert.Equal(77, region.Notes.Single(n => n.Id == e5).Pitch);
        Assert.Contains(region.Notes, static n => n is { StartTick: Bar + 1920, LengthTicks: 960, Pitch: 71 });

        // On the B♭ clarinet positions and steps are written: the middle line is written B4, concert A4; a step up from
        // there with a written natural is written C5, concert B♭4.
        await Client.EditAsync(new AddScoreNoteCommand(clarinet, 1920, 960, 4));
        var clarinetRegion = (await Client.ProjectAsync()).MidiRegions.Single(r => r.TrackId == clarinet);
        var a4 = clarinetRegion.Notes.Single(static n => n.StartTick == 1920);
        Assert.Equal(69, a4.Pitch);
        await Client.EditAsync(
            new MoveScoreNotesCommand(clarinetRegion.Id, [a4.Id], 1, Accidental: Accidental.Natural)
        );
        clarinetRegion = (await Client.ProjectAsync()).MidiRegions.Single(r => r.TrackId == clarinet);
        Assert.Equal(70, clarinetRegion.Notes.Single(n => n.Id == a4.Id).Pitch);
    }

    [Fact]
    public async Task A_score_set_exported_as_MusicXML_imports_into_a_new_project_with_its_score() {
        await StartAsync();
        var (project, piano, clarinet) = await NotationAsync();
        var notes = project.MidiRegions.Single(r => r.TrackId == piano).Notes;
        long Id(int pitch, long start) => notes.Single(n => n.Pitch == pitch && Math.Abs(n.StartTick - start) < 20).Id;
        var (c, d, e) = (Id(62, 965), Id(64, 1290), Id(65, 1595));
        await Client.EditAsync(
            new AddScoreSymbolsCommand(
                [
                    new(ScoreSymbolKind.Staccato, piano, c), new(ScoreSymbolKind.Accent, piano, d),
                    new(ScoreSymbolKind.Dynamic, piano, c, Text: "mf"),
                    new(ScoreSymbolKind.Slur, piano, c, EndNoteId: e),
                    new(ScoreSymbolKind.Crescendo, piano, Tick: 960, EndTick: 1920)
                ]
            ),
            new SetLyricsCommand(1, [new(c, "Hap", Syllabic.Begin), new(d, "py", Syllabic.End), new(e, "day")]),
            new AddChordSymbolsCommand(
                [
                    new(piano, 0, new(PitchStep.C, 0), string.Empty), new(piano, 1920, new(PitchStep.A, 0), "m7"),
                    new(piano, Bar, new(PitchStep.F, 0), "maj7"), new(piano, 2 * Bar, new(PitchStep.G, 0), "7")
                ]
            ),
            new SetScoreSetCommand(new("scs_all", "All", [piano, clarinet])),
            new SetScoreLayoutCommand(ScoreLayout.Default with { Title = "Golden Etude" })
        );
        var original = await Client.ProjectAsync();
        var drawn = Drawn(await ScoreAsync("set=scs_all&view=page"));
        var job = await DoneAsync(
            "/api/v1/project/export/musicxml",
            new("Golden", media, "scs_all"),
            ContractsJsonContext.Default.MusicXmlExportRequest
        );
        var file = Assert.Single(job.Files);

        await Client.PostAsync("/api/v1/project/close", null, Token);
        await Client.CreateProjectAsync("Reimport");
        await DoneAsync(
            "/api/v1/project/import/musicxml",
            new(file),
            ContractsJsonContext.Default.MusicXmlImportRequest
        );
        var imported = await Client.ProjectAsync();
        Assert.Equal(["Piano", "Clarinet"], imported.Tracks.Select(static t => t.Name));
        Assert.Equal(
            [StaffStyleIds.Piano, StaffStyleIds.ClarinetBb],
            imported.Tracks.Select(static t => t.StaffStyleId)
        );
        Assert.Equal("Golden Etude", imported.Score.Layout.Title);
        // The notes where the score draws them (display quantise is what a MusicXML file holds).
        Assert.Equal(drawn, Notes(imported));
        Assert.NotEqual(Notes(original), drawn);
        Assert.Equal(
            original.Score.Symbols.Select(static s => s.Kind).Order(),
            imported.Score.Symbols.Select(static s => s.Kind).Order()
        );
        var starts = imported.MidiRegions.SelectMany(static r => r.Notes)
            .ToDictionary(static n => n.Id, static n => n.StartTick);
        Assert.Equal(
            ["Hap", "py", "day"],
            imported.Score.Lyrics.OrderBy(l => starts[l.NoteId]).Select(static l => l.Text)
        );
        Assert.Equal(
            ["0 C", "1920 Am7", "3840 Fmaj7", "7680 G7"],
            imported.Score.Chords.OrderBy(static ch => ch.Tick)
                .Select(static ch => $"{ch.Tick} {ch.Root.Step.ToString().ToUpperInvariant()}{ch.Quality}")
        );
        var again = await ScoreAsync($"part={imported.Tracks[0].Id}");
        var treble = Events(again, imported.Tracks[0].Id, 0, 1).ToList();
        Assert.Contains(treble, static ev => ev.Notes.Any(static n => n.TieStart));
        Assert.Contains(treble, static ev => ev.Duration.Tuplet is not null);
    }

    [Fact]
    public async Task An_AAF_export_imports_into_a_new_project_to_the_sample_embedded_and_linked() {
        await StartAsync();
        var project = await Client.CreateProjectAsync("Audio");
        var wav = await Client.ImportAsync(TestWav.Write(media, "Tone.wav", frames: 4 * 48_000));
        await Client.EditAsync(new AddTrackCommand("Audio A"), new AddTrackCommand("Audio B"));
        project = await Client.ProjectAsync();
        var (a, b) = (project.Tracks[0].Id, project.Tracks[1].Id);
        // Two overlapping regions on A (a crossfade of 2400 samples), one with an offset, gain and fades on B.
        await Client.EditAsync(
            new AddRegionCommand(a, wav.Id, 0, 0, 2 * 48_000 + 2400),
            new AddRegionCommand(a, wav.Id, Bar, 48_000, 2 * 48_000),
            new AddRegionCommand(b, wav.Id, Bar / 2, 12_345, 48_000)
        );
        project = await Client.ProjectAsync();
        var regions = project.Regions;
        await Client.EditAsync(
            new SetRegionCommand(regions.Single(r => r.TrackId == b).Id, GainDb: -6, FadeIn: 480, FadeOut: 4800),
            new SetRegionCommand(
                regions.Single(r => r is { TrackId: var t, StartTick: 0 } && t == a).Id,
                FadeOut: 2400
            ),
            new SetRegionCommand(regions.Single(static r => r.StartTick == Bar).Id, FadeIn: 2400)
        );
        var original = (await Client.ProjectAsync()).Regions;
        var exported = new List<string>();
        foreach (var embed in new[] { true, false }) {
            var job = await DoneAsync(
                "/api/v1/project/export/aaf",
                new(embed ? "Embedded" : "Linked", media, EmbedMedia: embed),
                ContractsJsonContext.Default.AafExportRequest
            );
            Assert.Equal(embed ? 1 : 2, job.Files.Count);
            exported.Add(job.Files[0]);
        }

        foreach (var file in exported) {
            await Client.PostAsync("/api/v1/project/close", null, Token);
            await Client.CreateProjectAsync(Path.GetFileNameWithoutExtension(file));
            var job = await DoneAsync(
                "/api/v1/project/import/aaf",
                new(file),
                ContractsJsonContext.Default.AafImportRequest
            );
            var imported = await Client.ProjectAsync();
            Assert.Equal(["Audio A", "Audio B"], imported.Tracks.Select(static t => t.Name));
            Assert.Equal(Placed(project, original), Placed(imported, imported.Regions));
            var copied = Assert.Single(job.Files);
            Assert.Equal((48_000, 4L * 48_000), (WavReader.Probe(copied).SampleRate, WavReader.Probe(copied).Frames));
        }
    }

    [Fact]
    public async Task An_AAF_at_another_sample_rate_imports_with_its_media_resampled() {
        await StartAsync();
        // A 44.1 kHz project's region: half a second in, a tenth of a second into its file, half a second long.
        await Client.CreateProjectAsync("Rate", 44_100);
        var wav = await Client.ImportAsync(TestWav.Write(media, "Slow.wav", 44_100, frames: 2 * 44_100));
        await Client.EditAsync(new AddTrackCommand("Dialog"));
        var track = (await Client.ProjectAsync()).Tracks[0].Id;
        await Client.EditAsync(new AddRegionCommand(track, wav.Id, 960, 4410, 22_050));
        var regionId = (await Client.ProjectAsync()).Regions[0].Id;
        await Client.EditAsync(new SetRegionCommand(regionId, FadeIn: 441, FadeOut: 882));
        var job = await DoneAsync(
            "/api/v1/project/export/aaf",
            new("Rate", media),
            ContractsJsonContext.Default.AafExportRequest
        );

        await Client.PostAsync("/api/v1/project/close", null, Token);
        await Client.CreateProjectAsync("At 48k", 48_000);
        job = await DoneAsync(
            "/api/v1/project/import/aaf",
            new(job.Files[0]),
            ContractsJsonContext.Default.AafImportRequest
        );
        var project = await Client.ProjectAsync();
        var file = Assert.Single(project.AudioFiles);
        Assert.Equal((48_000, 2L * 48_000), (file.SampleRate, file.Frames));
        var info = WavReader.Probe(Assert.Single(job.Files));
        Assert.Equal((48_000, 2L * 48_000), (info.SampleRate, info.Frames));
        var region = Assert.Single(project.Regions);
        Assert.Equal(
            (960L, 4800L, 24_000L, 480L, 960L),
            (region.StartTick, region.FileOffset, region.Length, region.FadeIn, region.FadeOut)
        );
    }

    static List<(long Start, long Length, int Pitch)> Notes(Project project) => [
        ..project.MidiRegions
            .SelectMany(static r => r.Notes.Select(n => (r.StartTick + n.StartTick, n.LengthTicks, n.Pitch)))
            .Order()
    ];

    /// <summary>Every note as the score draws it: the start of its first segment to the end of its last, concert pitch.</summary>
    static List<(long Start, long Length, int Pitch)> Drawn(ScoreDocument document) {
        var spans = new Dictionary<long, (long Start, long End, int Pitch)>();
        foreach (var e in document.View.Parts.SelectMany(static p => p.Measures)
                     .SelectMany(static m => m.Staves)
                     .SelectMany(static s => s.Voices)
                     .SelectMany(static v => v.Events)) {
            foreach (var n in e.Notes) {
                var end = e.Tick + e.DurationTicks;
                spans[n.NoteId] = spans.TryGetValue(n.NoteId, out var s)
                    ? (Math.Min(s.Start, e.Tick), Math.Max(s.End, end), n.Pitch)
                    : (e.Tick, end, n.Pitch);
            }
        }

        return [..spans.Values.Select(static s => (s.Start, s.End - s.Start, s.Pitch)).Order()];
    }

    /// <summary>Each track's regions by track name: position in samples, file offset, length, gain and fades.</summary>
    static List<(string Track, long Position, long Offset, long Length, double Gain, long FadeIn, long FadeOut)> Placed(
        Project project,
        IEnumerable<AudioRegion> regions
    ) =>
        [
            ..regions.Select(r => (project.Tracks.Single(t => t.Id == r.TrackId).Name, r.StartTick * 25, r.FileOffset,
                        r.Length, Math.Round(r.GainDb, 6), r.FadeIn, r.FadeOut)
                )
                .Order()
        ];
}
