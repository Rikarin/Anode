using Anode.Ipc;
using System.Net.Sockets;
using System.Threading.Channels;

namespace Anode.EngineHost;

/// <summary>
///     The host's IPC endpoint: listens on a Unix domain socket and serves one anode-server connection at a time (a
///     new connection replaces the old one, as after a server restart; the engine keeps playing in between). Messages
///     are handled in order by <paramref name="handler" />, and their replies are written in that order. Once the
///     connection's hello is answered, the host may also <see cref="Push" /> messages of its own (the plugin catalogue,
///     MIDI ports and activity); they go out through the same queue, so nothing overtakes the hello.
/// </summary>
public sealed class EngineHostServer(
    string socketPath,
    Func<IpcMessage, CancellationToken, ValueTask<IpcMessage?>> handler,
    Action<string>? log = null
) : IAsyncDisposable {
    // SK3502 misses the IAsyncDisposable on this primary-constructor type; DisposeAsync disposes the source.
#pragma warning disable SK3502
    readonly CancellationTokenSource stopping = new();
#pragma warning restore SK3502
    Socket? listener;
    Task? acceptLoop;
    Outbox? current;

    /// <summary>A server that answers only hello and ping (the host before a device is open, and tests).</summary>
    public EngineHostServer(string socketPath, string version, string backend)
        : this(socketPath, Basic(new(version, backend))) { }

    public string SocketPath { get; } = socketPath;

    /// <summary>The current connection's hello was answered (called on the connection's thread, after the reply is queued).</summary>
    public Action? HelloAnswered { get; set; }

    /// <summary>
    ///     Sends a message of the host's own to the connected server, after everything queued before it; dropped when no
    ///     server is connected or its hello is not answered yet. Any thread.
    /// </summary>
    public void Push(IpcMessage message) {
        if (Volatile.Read(ref current) is { HelloAnswered: true } outbox) {
            outbox.Queue.Writer.TryWrite(message);
        }
    }

    public void Start() {
        if (File.Exists(SocketPath)) {
            File.Delete(SocketPath); // left over from a host that did not exit cleanly
        }

        listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
        listener.Listen(1);
        acceptLoop = AcceptLoopAsync(listener, stopping.Token);
    }

    public async ValueTask DisposeAsync() {
        await stopping.CancelAsync().ConfigureAwait(false);
        listener?.Dispose();
        if (acceptLoop is not null) {
            try {
                await acceptLoop.ConfigureAwait(false);
            } catch (OperationCanceledException) {
                // stopping
            }
        }

        stopping.Dispose();
        if (File.Exists(SocketPath)) {
            File.Delete(SocketPath);
        }
    }

    static Func<IpcMessage, CancellationToken, ValueTask<IpcMessage?>> Basic(HelloMessage hello) =>
        (message, _) => ValueTask.FromResult<IpcMessage?>(
            message switch {
                HelloMessage => hello,
                PingMessage ping => new PongMessage(ping.Id),
                _ => new ErrorMessage($"Unsupported message {message.GetType().Name}.")
            }
        );

    async Task AcceptLoopAsync(Socket socket, CancellationToken cancellationToken) {
        while (!cancellationToken.IsCancellationRequested) {
            Socket client;
            try {
                client = await socket.AcceptAsync(cancellationToken).ConfigureAwait(false);
            } catch (Exception e) when (e is ObjectDisposedException or SocketException) {
                return;
            }

            log?.Invoke("server connected");
            await using var stream = new NetworkStream(client, true);
            var outbox = new Outbox();
            Volatile.Write(ref current, outbox);
            var writing = WriteAsync(stream, outbox, cancellationToken);
            try {
                await ServeAsync(stream, outbox, cancellationToken).ConfigureAwait(false);
            } catch (Exception e) when (e is IOException or SocketException or InvalidDataException) {
                log?.Invoke($"connection ended: {e.Message}");
            } catch (System.Text.Json.JsonException e) {
                log?.Invoke($"connection ended: unreadable message: {e.Message}");
            } finally {
                Interlocked.CompareExchange(ref current, null, outbox);
                outbox.Queue.Writer.TryComplete();
                try {
                    await writing.ConfigureAwait(false);
                } catch (Exception e) when (e is IOException
                                                or SocketException
                                                or OperationCanceledException
                                                or ObjectDisposedException) {
                    // the connection is gone
                }
            }

            log?.Invoke("server disconnected");
        }
    }

    async Task ServeAsync(Stream stream, Outbox outbox, CancellationToken cancellationToken) {
        while (await IpcFraming.ReadAsync(stream, cancellationToken).ConfigureAwait(false) is { } message) {
            if (await handler(message, cancellationToken).ConfigureAwait(false) is not { } reply) {
                continue;
            }

            outbox.Queue.Writer.TryWrite(reply);
            if (reply is HelloMessage) {
                outbox.HelloAnswered = true;
                HelloAnswered?.Invoke();
            }
        }
    }

    static async Task WriteAsync(Stream stream, Outbox outbox, CancellationToken cancellationToken) {
        await foreach (var message in outbox.Queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) {
            await IpcFraming.WriteAsync(stream, message, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>One connection's outgoing messages, in order.</summary>
    sealed class Outbox {
        volatile bool helloAnswered;

        public Channel<IpcMessage> Queue { get; } =
            Channel.CreateUnbounded<IpcMessage>(new() { SingleReader = true });

        public bool HelloAnswered {
            get => helloAnswered;
            set => helloAnswered = value;
        }
    }
}
