using Anode.Contracts;
using System.Net;
using System.Net.WebSockets;
using System.Text;

namespace Anode.Server.Tests;

public sealed class SystemApiTests {
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reports_version_and_in_process_engine() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await ServerFixture.Eventually(async () =>
            (await server.SystemAsync(client)).Engine.State == EngineState.InProcess
        );
        var system = await server.SystemAsync(client);
        Assert.False(string.IsNullOrEmpty(system.Version));
    }

    [Fact]
    public async Task A_project_at_another_rate_than_the_device_is_reported() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await ServerFixture.Eventually(async () =>
            (await server.SystemAsync(client)).Engine.State == EngineState.InProcess
        );

        // No rate asked: the device's (the in-process engine runs at 48 kHz).
        Assert.Equal(48_000, (await client.CreateProjectAsync("Default")).SampleRate);
        Assert.Null((await server.SystemAsync(client)).Engine.Message);

        await client.CreateProjectAsync("Other", 44_100);
        await ServerFixture.Eventually(async () =>
            (await server.SystemAsync(client)).Engine.Message?.Contains("44100 Hz", StringComparison.Ordinal) == true
        );
    }

    [Fact]
    public async Task With_a_token_every_request_needs_it() {
        await using var server = new ServerFixture(token: "secret");
        using var client = server.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/system", Token)).StatusCode);

        client.DefaultRequestHeaders.Authorization = new("Bearer", "secret");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/system", Token)).StatusCode);
    }

    [Fact]
    public async Task WebSocket_greets_with_connected() {
        await using var server = new ServerFixture();
        using var socket = await server.Server.CreateWebSocketClient()
            .ConnectAsync(new(server.Server.BaseAddress, "/ws"), Token);
        var buffer = new byte[4096];
        var result = await socket.ReceiveAsync(buffer, Token);
        var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
        Assert.StartsWith("""{"type":"connected","version":""", text, StringComparison.Ordinal);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, Token);
    }

    [Fact]
    public async Task Serves_the_editor_build_marked_same_origin() {
        var editor = Directory.CreateTempSubdirectory("anode-editor-").FullName;
        try {
            await File.WriteAllTextAsync(Path.Combine(editor, "index.html"), "<html><head></head></html>", Token);
            await using var server = new ServerFixture(token: "secret", editorDirectory: editor);
            using var client = server.CreateClient();
            var html = await client.GetStringAsync("/", Token);
            Assert.Contains("""<meta name="anode-server" content="same-origin">""", html, StringComparison.Ordinal);
        } finally {
            Directory.Delete(editor, true);
        }
    }

    [Fact]
    public void Listening_beyond_loopback_needs_a_token() =>
        Assert.Throws<InvalidOperationException>(static () =>
            Api.Access.RequireTokenBeyondLoopback("http://0.0.0.0:5190", null)
        );
}
