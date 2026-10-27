using Anode.Contracts;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;

namespace Anode.Server.Realtime;

/// <summary>
///     Fans events out to every <c>/ws</c> client. Each client has a bounded outbox of events that are never dropped;
///     one that falls <see cref="Backlog" /> events behind gets a single <see cref="ResyncEvent" /> instead of the
///     backlog, as in Cathode. Telemetry frames (<see cref="TransportEvent" />, <see cref="MetersEvent" />) go to
///     latest-only slots instead: a slow client skips frames rather than falling behind on them.
/// </summary>
public sealed class EventHub {
    public const int Backlog = 1024;

    readonly ConcurrentDictionary<Guid, Client> clients = new();

    public int ClientCount => clients.Count;

    public void Publish(RealtimeEvent e) {
        foreach (var client in clients.Values) {
            client.Post(e);
        }
    }

    /// <summary>Sends <paramref name="first" />, then every published event, until the socket closes.</summary>
    public async Task ServeAsync(WebSocket socket, RealtimeEvent first, CancellationToken cancellationToken) {
        var id = Guid.NewGuid();
        var client = new Client();
        clients[id] = client;
        using var closed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var receive = DrainAsync(socket, closed);
        try {
            await SendAsync(socket, first, closed.Token).ConfigureAwait(false);
            while (true) {
                if (client.Next() is { } e) {
                    await SendAsync(socket, e, closed.Token).ConfigureAwait(false);
                } else {
                    await client.WaitAsync(closed.Token).ConfigureAwait(false);
                }
            }
        } catch (Exception e) when (e is OperationCanceledException or WebSocketException) {
            // The client went away.
        } finally {
            clients.TryRemove(id, out _);
            await closed.CancelAsync().ConfigureAwait(false);
            await receive.ConfigureAwait(false);
            if (socket.State == WebSocketState.CloseReceived) {
                // Complete the client's close handshake.
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
    }

    static Task SendAsync(WebSocket socket, RealtimeEvent e, CancellationToken cancellationToken) =>
        socket.SendAsync(
            JsonSerializer.SerializeToUtf8Bytes(e, ContractsJsonContext.Default.RealtimeEvent),
            WebSocketMessageType.Text,
            true,
            cancellationToken
        );

    /// <summary>Reads (and ignores) what the client sends, to notice when it closes.</summary>
    static async Task DrainAsync(WebSocket socket, CancellationTokenSource closed) {
        var buffer = new byte[1024];
        try {
            while (socket.State == WebSocketState.Open) {
                var result = await socket.ReceiveAsync(buffer, closed.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) {
                    break;
                }
            }
        } catch (Exception e) when (e is OperationCanceledException or WebSocketException) {
            // closed
        }

        await closed.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>One client's outboxes: the events, and the latest transport and meters frames.</summary>
    sealed class Client {
        readonly Channel<RealtimeEvent> events = Channel.CreateBounded<RealtimeEvent>(
            new BoundedChannelOptions(Backlog) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite }
        );

        readonly Channel<bool> wake = Channel.CreateBounded<bool>(
            new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite }
        );

        RealtimeEvent? transport;
        RealtimeEvent? meters;

        public void Post(RealtimeEvent e) {
            switch (e) {
                case TransportEvent:
                    Volatile.Write(ref transport, e);
                    break;
                case MetersEvent:
                    Volatile.Write(ref meters, e);
                    break;
                default:
                    if (!events.Writer.TryWrite(e)) {
                        // Full: drop what is queued and tell the client to refetch.
                        while (events.Reader.TryRead(out _)) { }

                        events.Writer.TryWrite(new ResyncEvent());
                    }

                    break;
            }

            wake.Writer.TryWrite(true);
        }

        /// <summary>The next frame to send: events first, then the latest transport, then the latest meters.</summary>
        public RealtimeEvent? Next() =>
            events.Reader.TryRead(out var e)
                ? e
                : Interlocked.Exchange(ref transport, null) ?? Interlocked.Exchange(ref meters, null);

        public async Task WaitAsync(CancellationToken cancellationToken) =>
            await wake.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    }
}
