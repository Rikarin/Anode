using Anode.Contracts;
using Anode.Model.Midi;
using Anode.Server.Library;
using Microsoft.Extensions.DependencyInjection;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Anode.Server.Tests;

/// <summary>
///     Loops and packs (docs/07-phase-6.md): ACID and sidecar reading, installing, listing and removing packs, the
///     loop index and its queries and favourites, adding loops that follow the project's tempo and key, and "Add to
///     Loop Library".
/// </summary>
public sealed class LoopsApiTests : IDisposable {
    readonly string folder = Directory.CreateTempSubdirectory("anode-loops-").FullName;

    static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(folder, true);

    /// <summary>A WAV with an <c>acid</c> chunk after its data.</summary>
    string AcidWav(string name, bool oneShot, int? root, uint beats, float tempo, long frames = 96_000) {
        var path = TestWav.Write(folder, name, frames: frames);
        var chunk = LoopFiles.AcidChunk(oneShot, root, beats, tempo);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        stream.Seek(0, SeekOrigin.End);
        stream.Write("acid"u8);
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)chunk.Length);
        stream.Write(size);
        stream.Write(chunk);
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)(stream.Length - 8));
        stream.Seek(4, SeekOrigin.Begin);
        stream.Write(size);
        return path;
    }

    [Fact]
    public void An_ACID_chunk_gives_the_root_note_beats_tempo_and_one_shot_flag() {
        var loop = LoopFiles.Read(AcidWav("Loop.wav", false, 62, 8, 100))!;
        Assert.Equal(
            (LoopKind.Audio, 2, 8.0, 100.0, false),
            (loop.Kind, loop.Key!.Value, loop.Beats!.Value, loop.Tempo!.Value, loop.OneShot)
        );
        var shot = LoopFiles.Read(AcidWav("Hit.wav", true, null, 0, 0))!;
        Assert.Equal((true, (double?)null, (int?)null), (shot.OneShot, shot.Tempo, shot.Key));
        // A sidecar wins over the chunk.
        var path = AcidWav("Tagged.wav", false, 60, 4, 90);
        File.WriteAllText(
            path + LoopFiles.SidecarSuffix,
            JsonSerializer.Serialize(
                new LoopSidecar(1, LoopKind.Audio, "Tagged", 95, 4, 9, LoopScale.Minor, ["Drums"], false),
                ContractsJsonContext.Default.LoopSidecar
            )
        );
        Assert.Equal((95.0, 9), (LoopFiles.Read(path)!.Tempo!.Value, LoopFiles.Read(path)!.Key!.Value));
        Assert.Equal(
            (0, 6, 10, 9),
            (LoopFiles.Tonic("C")!.Value, LoopFiles.Tonic("F#m")!.Value, LoopFiles.Tonic("Bb minor")!.Value,
                LoopFiles.Tonic("a")!.Value)
        );
    }

    /// <summary>A test pack: a 100 bpm drum loop in C (sidecar), an ACID loop, a MIDI bass loop in C and a pattern loop.</summary>
    string Pack(string id = "test-pack", string name = "Test Pack") {
        var staging = Directory.CreateDirectory(Path.Combine(folder, $"stage-{Guid.NewGuid():n}")).FullName;
        File.WriteAllText(
            Path.Combine(staging, "pack.json"),
            $$"""{"id":"{{id}}","name":"{{name}}","version":"1.2.0","author":"Anode"}"""
        );
        Directory.CreateDirectory(Path.Combine(staging, "Drums"));
        var drums = TestWav.Write(Path.Combine(staging, "Drums"), "Drums 100.wav", frames: 48_000 * 24 / 10 * 2);
        Sidecar(
            drums + LoopFiles.SidecarSuffix,
            new(1, LoopKind.Audio, "Drums 100", 100, 8, 0, LoopScale.Neither, ["Drums"], false)
        );
        File.Move(AcidWav("Acid Keys.wav", false, 64, 4, 120), Path.Combine(staging, "Drums", "Acid Keys.wav"));
        Directory.CreateDirectory(Path.Combine(staging, "Bass"));
        var bass = Path.Combine(staging, "Bass", "Bass C.mid");
        File.WriteAllBytes(
            bass,
            StandardMidiFileWriter.Write(
                [new(1, 36, 100, 1, 0, 480), new(2, 43, 90, 1, 960, 480)],
                null,
                100,
                "Bass C",
                3840
            )
        );
        Sidecar(
            bass + LoopFiles.SidecarSuffix,
            new(1, LoopKind.Midi, "Bass C", 100, 4, 0, LoopScale.Minor, ["Bass"], false)
        );
        Sidecar(
            Path.Combine(staging, "Beat.anodeloop.json"),
            new(
                1,
                LoopKind.Pattern,
                "Beat",
                100,
                4,
                null,
                LoopScale.Neither,
                ["Drums"],
                false,
                null,
                new(
                    [
                        new(
                            "k",
                            PatternRowKind.Pad,
                            36,
                            "Kick",
                            4,
                            null,
                            PatternPlayMode.Forward,
                            false,
                            [..Enumerable.Repeat(new PatternStep(true, 100, 1, 1, 1, 0), 4)]
                        )
                    ],
                    0,
                    1
                )
            )
        );
        // A kit with a Quick Sampler pad on a sample of the pack (patches/ is no loop).
        Directory.CreateDirectory(Path.Combine(staging, "patches", "samples"));
        TestWav.Write(Path.Combine(staging, "patches", "samples"), "Clap.wav", frames: 4_800);
        File.WriteAllText(
            Path.Combine(staging, "patches", "kit_pack.json"),
            """
            {"schemaVersion":1,"id":"kit_pack","name":"Pack Kit","category":"Drum Kits","kind":"stack","inserts":[],"sends":[],
             "pads":[{"note":39,"name":"Clap","instrument":{"pluginId":"anode.quick-sampler","name":"Quick Sampler",
               "parameters":{"mode":0,"keyFollow":0},"file":"patches/samples/Clap.wav"},"inserts":[]}]}
            """
        );
        var pack = Path.Combine(folder, $"{id}-{Guid.NewGuid():n}.anodepack");
        ZipFile.CreateFromDirectory(staging, pack);
        return pack;
    }

    static void Sidecar(string path, LoopSidecar sidecar) =>
        File.WriteAllText(path, JsonSerializer.Serialize(sidecar, ContractsJsonContext.Default.LoopSidecar));

    static async Task<Pack> InstallAsync(HttpClient client, string path) {
        var response = await client.PostAsJsonAsync(
            "/api/v1/library/packs",
            new(path),
            ContractsJsonContext.Default.InstallPackRequest,
            Token
        );
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Token));
        return (await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.Pack, Token))!;
    }

    static async Task<LoopPage> QueryAsync(HttpClient client, string query = "") =>
        (await client.GetFromJsonAsync($"/api/v1/library/loops{query}", ContractsJsonContext.Default.LoopPage, Token))!;

    [Fact]
    public async Task Packs_install_list_and_remove_and_the_index_follows() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        var pack = await InstallAsync(client, Pack());
        Assert.Equal(
            ("test-pack", "Test Pack", "1.2.0", 4, true),
            (pack.Id, pack.Name, pack.Version, pack.LoopCount, pack.Installed)
        );
        Assert.True(pack.Size > 0);
        Assert.Equal(
            ["Acid Keys", "Bass C", "Beat", "Drums 100"],
            (await QueryAsync(client)).Items.Select(static l => l.Name)
        );
        Assert.Equal(
            ["Drums 100"],
            (await QueryAsync(client, "?tags=Drums&kind=audio")).Items.Select(static l => l.Name)
        );
        Assert.Equal(["Acid Keys"], (await QueryAsync(client, "?key=4")).Items.Select(static l => l.Name));
        Assert.Equal(["Bass C"], (await QueryAsync(client, "?scale=minor")).Items.Select(static l => l.Name));
        Assert.Equal(["Bass C"], (await QueryAsync(client, "?text=bass")).Items.Select(static l => l.Name));
        var page = await QueryAsync(client, "?offset=1&limit=2");
        Assert.Equal((4, 2), (page.Total, page.Items.Count));
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            (await client.GetAsync("/api/v1/library/loops?kind=nonsense", Token)).StatusCode
        );

        // Favourites survive a reinstall (the index is rebuilt).
        var bass = (await QueryAsync(client, "?text=bass")).Items[0];
        var marked = await client.PutAsJsonAsync(
            $"/api/v1/library/loops/{bass.Id}/favourite",
            new(true),
            ContractsJsonContext.Default.LoopFavouriteRequest,
            Token
        );
        Assert.True((await marked.Content.ReadFromJsonAsync(ContractsJsonContext.Default.LoopInfo, Token))!.Favourite);
        await InstallAsync(client, Pack());
        Assert.Equal([bass.Id], (await QueryAsync(client, "?favouritesOnly=true")).Items.Select(static l => l.Id));

        // The pack's kit makes a Drum Machine Designer whose sample pad loads the installed sample.
        await client.CreateProjectAsync();
        await client.EditAsync(new CreateDrumMachineCommand(null, "kit_pack"));
        var pad = (await client.ProjectAsync()).Tracks.Single(static t => t.Name == "Clap").Instrument!;
        Assert.True(Path.IsPathRooted(pad.File) && File.Exists(pad.File), pad.File);
        Assert.EndsWith(
            Path.Combine("test-pack", "patches", "samples", "Clap.wav"),
            pad.File,
            StringComparison.Ordinal
        );

        var packs = await client.GetFromJsonAsync(
            "/api/v1/library/packs",
            ContractsJsonContext.Default.IReadOnlyListPack,
            Token
        );
        Assert.Single(packs!);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await client.DeleteAsync("/api/v1/library/packs/test-pack", Token)).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.DeleteAsync("/api/v1/library/packs/test-pack", Token)).StatusCode
        );
        Assert.Equal(0, (await QueryAsync(client)).Total);
    }

    [Fact]
    public async Task A_pack_with_a_file_outside_its_folder_is_refused() {
        var path = Path.Combine(folder, "evil.anodepack");
        await using (var zip = await ZipFile.OpenAsync(path, ZipArchiveMode.Create, Token)) {
            await using (var writer = new StreamWriter(await zip.CreateEntry("pack.json").OpenAsync(Token))) {
                await writer.WriteAsync("""{"id":"evil","name":"Evil","version":"1","author":"x"}""");
            }

            await using (var writer = new StreamWriter(await zip.CreateEntry("../../escape.txt").OpenAsync(Token))) {
                await writer.WriteAsync("no");
            }
        }

        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/v1/library/packs",
            new(path),
            ContractsJsonContext.Default.InstallPackRequest,
            Token
        );
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(
            (await client.GetFromJsonAsync(
                "/api/v1/library/packs",
                ContractsJsonContext.Default.IReadOnlyListPack,
                Token
            ))!
        );
    }

    static async Task<CommandResult> AddLoopAsync(HttpClient client, AddLoopRequest request) {
        var response = await client.PostAsJsonAsync(
            "/api/v1/project/loops",
            request,
            ContractsJsonContext.Default.AddLoopRequest,
            Token
        );
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Token));
        return (await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.CommandResult, Token))!;
    }

    [Fact]
    public async Task Added_loops_follow_the_project_tempo_and_key() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await InstallAsync(client, Pack());
        await client.CreateProjectAsync();
        await client.EditAsync(
            new SetTempoEventsCommand([new(0, 110, TempoCurve.Step)]),
            new SetKeysCommand([new(0, 2, KeyMode.Minor)])
        );
        var loops = (await QueryAsync(client)).Items;

        // An audio loop at 100 bpm in C: a new audio track, the region following the tempo at the sidecar's tempo,
        // transposed up to D; the file copied into the project.
        await AddLoopAsync(client, new(loops.Single(static l => l.Name == "Drums 100").Id, null, 3840));
        var project = await client.ProjectAsync();
        var region = Assert.Single(project.Regions);
        var file = project.AudioFiles.Single(f => f.Id == region.FileId);
        Assert.Equal((FlexFollow.On, 2, 3840L), (region.Follow, region.Transpose, region.StartTick));
        Assert.Equal(100, file.Tempo!.Bpm);
        Assert.StartsWith("Audio Files/", file.Path, StringComparison.Ordinal);
        Assert.Equal(TrackKind.Audio, project.Tracks.Single(t => t.Id == region.TrackId).Kind);

        // A MIDI loop in C: its notes up a tone, on a new instrument track; one undo step each.
        await AddLoopAsync(client, new(loops.Single(static l => l.Name == "Bass C").Id));
        project = await client.ProjectAsync();
        var midi = Assert.Single(project.MidiRegions);
        Assert.Equal([38, 45], midi.Notes.Select(static n => n.Pitch));
        Assert.Equal(3840, midi.LengthTicks);
        var history = await client.GetFromJsonAsync(
            "/api/v1/project/history",
            ContractsJsonContext.Default.UndoHistory,
            Token
        );
        Assert.Equal("Add Loop", history!.Undo[0].Label);

        // Preview in sync: at 110/100 of the loop's speed, up a tone, on the next bar; MIDI loops do not preview.
        var drums = loops.Single(static l => l.Name == "Drums 100");
        var preview = await client.PostAsync($"/api/v1/library/loops/{drums.Id}/preview", null, Token);
        Assert.Equal(HttpStatusCode.NoContent, preview.StatusCode);
        var host = server.Services.GetRequiredService<Engine.EngineConnection>().InProcessHost!;
        await ServerFixture.Eventually(() => Task.FromResult(Math.Abs(host.PreviewSync.TempoRatio - 1.1) < 1e-9));
        Assert.Equal((2, true), (host.PreviewSync.TransposeSemitones, host.PreviewSync.StartOnBar));
        var bassId = loops.Single(static l => l.Name == "Bass C").Id;
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            (await client.PostAsync($"/api/v1/library/loops/{bassId}/preview", null, Token)).StatusCode
        );

        // A pattern loop into a Live Loops cell of that track.
        await client.EditAsync(new AddSceneCommand());
        project = await client.ProjectAsync();
        await AddLoopAsync(
            client,
            new(loops.Single(static l => l.Name == "Beat").Id, midi.TrackId, 0, project.LiveLoops.Scenes[0].Id)
        );
        var cell = Assert.Single((await client.ProjectAsync()).LiveLoops.Cells);
        Assert.Equal((CellKind.Pattern, 3840L), (cell.Kind, cell.LengthTicks));
    }

    [Fact]
    public async Task A_region_goes_to_the_loop_library_with_its_tempo_and_tags() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await client.CreateProjectAsync();
        await client.EditAsync(
            new AddTrackCommand("Keys", Kind: TrackKind.Instrument),
            new SetTempoEventsCommand([new(0, 96, TempoCurve.Step)])
        );
        var track = (await client.ProjectAsync()).Tracks[0].Id;
        await client.EditAsync(new AddMidiRegionCommand(track, 0, 3840 * 2, "Riff"));
        var region = (await client.ProjectAsync()).MidiRegions[0].Id;
        await client.EditAsync(new AddNotesCommand(region, [new(60, 100, 1, 0, 480), new(67, 90, 1, 1920, 480)]));
        var response = await client.PostAsJsonAsync(
            "/api/v1/library/loops",
            new(region, "My Riff", ["Keys"], 0, LoopScale.Major),
            ContractsJsonContext.Default.AddToLoopLibraryRequest,
            Token
        );
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Token));
        var loop = (await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.LoopInfo, Token))!;
        Assert.Equal(
            (LoopKind.Midi, 96.0, 8.0, (string?)null, 0),
            (loop.Kind, loop.Tempo!.Value, loop.Beats!.Value, loop.PackId, loop.Key!.Value)
        );
        Assert.Equal(
            [60, 67],
            StandardMidiFile.Read(loop.Path).Tracks.SelectMany(static t => t.Notes).Select(static n => n.Pitch)
        );
        Assert.Equal([loop.Id], (await QueryAsync(client, "?tags=Keys")).Items.Select(static l => l.Id));
    }
}
