using Anode.Ipc;
using System.Net.Sockets;

namespace Anode.EngineHost;

/// <summary>
///     The host's IPC endpoint: listens on a Unix domain socket and serves one anode-server connection at a time (a
///     new connection replaces the old one, as after a server restart). Each connection starts with an exchange of
///     <see cref="HelloMessage" />s.
/// </summary>
public sealed class EngineHostServer(string socketPath, string version, string backend) : IAsyncDisposable {
    // SK3502 misses the IAsyncDisposable on this primary-constructor type; DisposeAsync disposes the source.
#pragma warning disable SK3502
    readonly CancellationTokenSource stopping = new();
#pragma warning restore SK3502
    Socket? listener;
    Task? acceptLoop;

    public string SocketPath { get; } = socketPath;

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

    async Task AcceptLoopAsync(Socket socket, CancellationToken cancellationToken) {
        while (!cancellationToken.IsCancellationRequested) {
            Socket client;
            try {
                client = await socket.AcceptAsync(cancellationToken).ConfigureAwait(false);
            } catch (Exception e) when (e is ObjectDisposedException or SocketException) {
                return;
            }

            await using var stream = new NetworkStream(client, true);
            try {
                await ServeAsync(stream, cancellationToken).ConfigureAwait(false);
            } catch (Exception e) when (e is IOException or SocketException or InvalidDataException) {
                // The server went away mid-frame; wait for the next one.
            }
        }
    }

    async Task ServeAsync(Stream stream, CancellationToken cancellationToken) {
        while (await IpcFraming.ReadAsync(stream, cancellationToken).ConfigureAwait(false) is { } message) {
            IpcMessage reply = message switch {
                HelloMessage => new HelloMessage(version, backend),
                PingMessage ping => new PongMessage(ping.Id),
                _ => new ErrorMessage($"Unsupported message {message.GetType().Name}.")
            };
            await IpcFraming.WriteAsync(stream, reply, cancellationToken).ConfigureAwait(false);
        }
    }
}
