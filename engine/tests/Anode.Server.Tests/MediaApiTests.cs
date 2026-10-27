using Anode.Contracts;
using Anode.Model.Audio;
using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;

namespace Anode.Server.Tests;

/// <summary>Importing WAV files (by path and by upload) and their peaks.</summary>
public sealed class MediaApiTests : IDisposable {
    readonly string folder = Directory.CreateTempSubdirectory("anode-media-").FullName;

    static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(folder, true);

    public static TheoryData<WavEncoding, int> Formats() {
        var data = new TheoryData<WavEncoding, int>();
        foreach (var encoding in Enum.GetValues<WavEncoding>()) {
            data.Add(encoding, 1);
            data.Add(encoding, 2);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Importing_copies_the_file_into_the_project(WavEncoding encoding, int channels) {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await using var events = await EventStream.ConnectAsync(server);
        var project = await client.CreateProjectAsync();
        var source = TestWav.Write(folder, "Take.wav", channels: channels, frames: 12_345, encoding: encoding);
        var file = await client.ImportAsync(source);
        Assert.Equal(
            ("Take", "Audio Files/Take.wav", 48_000, channels, 12_345L),
            (file.Name, file.Path, file.SampleRate, file.Channels, file.Frames)
        );
        Assert.Equal(
            encoding switch {
                WavEncoding.Pcm16 => 16,
                WavEncoding.Pcm24 => 24,
                _ => 32
            },
            file.BitDepth
        );
        Assert.True(File.Exists(Path.Combine(project.Path, "Audio Files", "Take.wav")));

        // An import is a change of its own (not an undo step) that every client sees.
        var change = await events.WaitAsync<ProjectChangedEvent>();
        Assert.Equal(file, Assert.IsType<AudioFileUpserted>(Assert.Single(change.Delta.Ops)).File);
        Assert.Equal(2, (await client.ProjectAsync()).Revision);
        Assert.Empty(
            (await client.GetFromJsonAsync(
                "/api/v1/project/history",
                ContractsJsonContext.Default.UndoHistory,
                Token
            ))!.Undo
        );

        var second = await client.ImportAsync(source);
        Assert.Equal("Audio Files/Take 2.wav", second.Path);
        Assert.NotEqual(file.Id, second.Id);
    }

    [Fact]
    public async Task Uploading_works_like_importing() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        var project = await client.CreateProjectAsync();
        var source = TestWav.Write(folder, "Up.wav", channels: 2, frames: 1000);
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(await File.ReadAllBytesAsync(source, Token)), "file", "Vocal take.wav");
        var response = await client.PostAsync("/api/v1/project/files", form, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var file = (await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.AudioFile, Token))!;
        Assert.Equal(
            ("Vocal take", "Audio Files/Vocal take.wav", 2, 1000L),
            (file.Name, file.Path, file.Channels, file.Frames)
        );
        Assert.True(File.Exists(Path.Combine(project.Path, file.Path)));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(project.Path, "Audio Files"), "*.tmp"));
    }

    [Fact]
    public async Task Files_Anode_cannot_play_are_refused() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        var project = await client.CreateProjectAsync();
        var cases = new[] {
            (TestWav.Write(folder, "441.wav", 44_100), "44100 Hz"),
            (TestWav.Write(folder, "surround.wav", channels: 3), "3 channels"),
            (WriteText("text.wav", "hello"), "RIFF"), (Path.Combine(folder, "missing.wav"), "does not exist")
        };
        foreach (var (path, reason) in cases) {
            var response = await client.PostAsJsonAsync(
                "/api/v1/project/files/import",
                new(path),
                ContractsJsonContext.Default.ImportFileRequest,
                Token
            );
            Assert.Contains(
                reason,
                await ApiCalls.AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity),
                StringComparison.Ordinal
            );
        }

        Assert.Empty((await client.ProjectAsync()).AudioFiles);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(project.Path, "Audio Files")));
    }

    [Fact]
    public async Task Peaks_come_at_the_nearest_resolution_and_in_slices() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await client.CreateProjectAsync();
        var file = await client.ImportAsync(TestWav.Write(folder, "p.wav", channels: 2, frames: 100_000));
        var response = await client.GetAsync($"/api/v1/project/files/{file.Id}/peaks?framesPerPeak=900", Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsByteArrayAsync(Token);
        Assert.Equal(PeakFormat.Magic, BinaryPrimitives.ReadUInt32LittleEndian(body));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(6)));
        Assert.Equal(1024u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
        var count = (100_000 + 1023) / 1024;
        Assert.Equal((ulong)count, BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(16)));
        Assert.Equal(PeakFormat.HeaderBytes + count * 8, body.Length);
        Assert.InRange(BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(PeakFormat.HeaderBytes + 2)), 8000, 8200);

        var slice = await client.GetByteArrayAsync(
            $"/api/v1/project/files/{file.Id}/peaks?framesPerPeak=64&start=10&count=5",
            Token
        );
        Assert.Equal(10u, BinaryPrimitives.ReadUInt32LittleEndian(slice.AsSpan(12)));
        Assert.Equal(5ul, BinaryPrimitives.ReadUInt64LittleEndian(slice.AsSpan(16)));

        await ApiCalls.AssertProblemAsync(
            await client.GetAsync("/api/v1/project/files/aud_nope/peaks", Token),
            HttpStatusCode.NotFound
        );
    }

    string WriteText(string name, string text) {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, text);
        return path;
    }
}
