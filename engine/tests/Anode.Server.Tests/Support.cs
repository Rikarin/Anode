using Anode.Contracts;
using Anode.Model.Audio;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Anode.Server.Tests;

/// <summary>Typed calls of the REST API, as the editor's client makes them.</summary>
static class ApiCalls {
    static CancellationToken Token => TestContext.Current.CancellationToken;

    public static async Task<Project> CreateProjectAsync(
        this HttpClient client,
        string name = "Song",
        int? rate = null
    ) {
        var response = await client.PostAsJsonAsync(
            "/api/v1/projects",
            new(name, null, rate),
            ContractsJsonContext.Default.CreateProjectRequest,
            Token
        );
        await EnsureAsync(response);
        return (await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.Project, Token))!;
    }

    public static async Task<Project> ProjectAsync(this HttpClient client) =>
        (await client.GetFromJsonAsync("/api/v1/project", ContractsJsonContext.Default.Project, Token))!;

    public static Task<HttpResponseMessage> SendCommandsAsync(
        this HttpClient client,
        long? ifMatch,
        params EditCommand[] commands
    ) {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/project/commands") {
            Content = JsonContent.Create(new(commands), ContractsJsonContext.Default.CommandBatch)
        };
        if (ifMatch is { } revision) {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{revision}\"");
        }

        return client.SendAsync(request, Token);
    }

    public static async Task<CommandResult> EditAsync(this HttpClient client, params EditCommand[] commands) {
        var response = await client.SendCommandsAsync(null, commands);
        await EnsureAsync(response);
        return (await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.CommandResult, Token))!;
    }

    public static async Task<CommandResult> PostResultAsync(this HttpClient client, string path) {
        var response = await client.PostAsync(path, null, Token);
        await EnsureAsync(response);
        return (await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.CommandResult, Token))!;
    }

    public static async Task<AudioFile> ImportAsync(this HttpClient client, string path) {
        var response = await client.PostAsJsonAsync(
            "/api/v1/project/files/import",
            new(path),
            ContractsJsonContext.Default.ImportFileRequest,
            Token
        );
        await EnsureAsync(response);
        return (await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.AudioFile, Token))!;
    }

    public static async Task<TransportStatus> TransportAsync(
        this HttpClient client,
        TransportCommand command,
        long? tick = null
    ) {
        var response = await client.PostAsJsonAsync(
            "/api/v1/transport",
            new(command, tick),
            ContractsJsonContext.Default.TransportRequest,
            Token
        );
        await EnsureAsync(response);
        return (await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.TransportStatus, Token))!;
    }

    /// <summary>Asserts a problem response with <paramref name="status" />; returns its detail.</summary>
    public static async Task<string> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status) {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        Assert.Equal((int)status, problem.RootElement.GetProperty("status").GetInt32());
        return problem.RootElement.TryGetProperty("detail", out var detail)
            ? detail.GetString() ?? string.Empty
            : string.Empty;
    }

    static async Task EnsureAsync(HttpResponseMessage response) {
        if (!response.IsSuccessStatusCode) {
            Assert.Fail($"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Token)}");
        }
    }
}

/// <summary>A <c>/ws</c> client that collects every event it receives.</summary>
sealed class EventStream : IAsyncDisposable {
    readonly WebSocket socket;
    readonly ConcurrentQueue<RealtimeEvent> events = new();
    readonly CancellationTokenSource stop;
    readonly Task reading;

    EventStream(WebSocket socket) {
        this.socket = socket;
        stop = new();
        reading = ReadAsync();
    }

    public IReadOnlyList<RealtimeEvent> Events => [..events];

    public static async Task<EventStream> ConnectAsync(ServerFixture server) {
        var socket = await server.Server.CreateWebSocketClient()
            .ConnectAsync(new(server.Server.BaseAddress, "/ws"), TestContext.Current.CancellationToken);
        var stream = new EventStream(socket);
        await stream.WaitAsync<ConnectedEvent>();
        return stream;
    }

    public IEnumerable<T> OfType<T>() where T : RealtimeEvent => Events.OfType<T>();

    /// <summary>Waits for an event of <typeparamref name="T" /> matching <paramref name="match" />.</summary>
    public async Task<T> WaitAsync<T>(Func<T, bool>? match = null, int seconds = 10) where T : RealtimeEvent {
        T? found = null;
        await ServerFixture.Eventually(
            () => {
                found = events.OfType<T>().FirstOrDefault(e => match?.Invoke(e) ?? true);
                return Task.FromResult(found is not null);
            },
            seconds
        );
        return found!;
    }

    public async ValueTask DisposeAsync() {
        await stop.CancelAsync();
        try {
            await reading;
        } catch (OperationCanceledException) {
            // stopped
        }

        socket.Dispose();
        stop.Dispose();
    }

    async Task ReadAsync() {
        var buffer = new byte[1 << 20];
        while (socket.State == WebSocketState.Open && !stop.IsCancellationRequested) {
            var length = 0;
            ValueWebSocketReceiveResult result;
            do {
                try {
                    result = await socket.ReceiveAsync(buffer.AsMemory(length), stop.Token);
                } catch (Exception e) when (e is OperationCanceledException or WebSocketException) {
                    return;
                }

                length += result.Count;
            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Text) {
                events.Enqueue(
                    JsonSerializer.Deserialize(
                        Encoding.UTF8.GetString(buffer, 0, length),
                        ContractsJsonContext.Default.RealtimeEvent
                    )!
                );
            }
        }
    }
}

/// <summary>WAV files for tests.</summary>
static class TestWav {
    /// <summary>A WAV of a quiet sine.</summary>
    public static string Write(
        string folder,
        string name,
        int rate = 48_000,
        int channels = 1,
        long frames = 48_000,
        WavEncoding encoding = WavEncoding.Pcm24
    ) {
        var path = Path.Combine(folder, name);
        using var writer = new WavWriter(path, channels, rate, encoding);
        var samples = new float[frames * channels];
        for (var i = 0; i < frames; i++) {
            for (var c = 0; c < channels; c++) {
                samples[i * channels + c] = 0.25f * MathF.Sin(i * 0.05f);
            }
        }

        writer.Write(samples);
        return path;
    }
}
