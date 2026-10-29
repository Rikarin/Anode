using Anode.Contracts;
using Anode.Ipc;
using Anode.Server.Engine;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;

namespace Anode.Server.Tests;

/// <summary>
///     Loading a file into an insert (Phase 6, <c>loadInsertFile</c>): an impulse response into the convolution reverb is
///     copied into the project, kept relative, and reaches the host as an absolute path in the strip.
/// </summary>
public sealed class InsertFileApiTests : IDisposable {
    readonly string folder = Directory.CreateTempSubdirectory("anode-ir-").FullName;

    public void Dispose() => Directory.Delete(folder, true);

    [Fact]
    public async Task An_impulse_response_is_copied_into_the_project_and_reaches_the_host() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        var project = await client.CreateProjectAsync();
        var catalogue = server.Services.GetRequiredService<EngineCatalog>();
        await ServerFixture.Eventually(() =>
            Task.FromResult(catalogue.ForEdits.Find("anode.space") is { FileTypes.Count: > 0 })
        );
        await client.EditAsync(new AddInsertCommand("anode.space"));
        var insert = (await client.ProjectAsync()).Master.Inserts.Single().Id;
        var ir = TestWav.Write(folder, "Cathedral.wav", frames: 12_000);

        await client.EditAsync(new LoadInsertFileCommand(insert, ir));
        var file = (await client.ProjectAsync()).Master.Inserts.Single().File;
        Assert.Equal("Impulse Responses/Cathedral.wav", file);
        Assert.True(File.Exists(Path.Combine(project.Path, file!)));
        var host = server.Services.GetRequiredService<EngineConnection>().InProcessHost!;
        await ServerFixture.Eventually(() => Task.FromResult(
                host.Received.OfType<SetChannelMessage>()
                    .Any(m => m.TrackId == -1 && m.Strip.Inserts!.Single().File == Path.Combine(project.Path, file!))
            )
        );

        var history = await client.GetFromJsonAsync(
            "/api/v1/project/history",
            ContractsJsonContext.Default.UndoHistory,
            TestContext.Current.CancellationToken
        );
        Assert.Equal("Load Impulse Response", history!.Undo[0].Label);
        var text = Path.Combine(folder, "notes.txt");
        await File.WriteAllTextAsync(text, "no", TestContext.Current.CancellationToken);
        await ApiCalls.AssertProblemAsync(
            await client.SendCommandsAsync(null, new LoadInsertFileCommand(insert, text)),
            HttpStatusCode.UnprocessableEntity
        );
    }
}
