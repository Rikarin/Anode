using Anode.Contracts;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;

namespace Anode.Server.Realtime;

/// <summary>
///     Fans events out to every <c>/ws</c> client. Each client has a bounded outbox; one that falls
///     <see cref="Backlog" /> events behind gets a single <see cref="ResyncEvent" /> instead of the backlog, as in
///     Cathode. Telemetry frames (meters, playhead) will get their own latest-only outboxes.
/// </summary>
public sealed class EventHub {
    public const int Backlog = 1024;

    readonly ConcurrentDictionary<Guid, Channel<RealtimeEvent>> clients = new();

    public int ClientCount => clients.Count;

    public void Publish(RealtimeEvent e) {
        foreach (var outbox in clients.Values) {
            if (!outbox.Writer.TryWrite(e)) {
                // Full: drop what is queued and tell the client to refetch.
                while (outbox.Reader.TryRead(out _)) { }

                outbox.Writer.TryWrite(new ResyncEvent());
            }
        }
    }

    /// <summary>Sends <paramref name="first" />, then every published event, until the socket closes.</summary>
    public async Task ServeAsync(WebSocket socket, RealtimeEvent first, CancellationToken cancellationToken) {
        var id = Guid.NewGuid();
        var outbox = Channel.CreateBounded<RealtimeEvent>(
            new BoundedChannelOptions(Backlog) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite }
        );
        clients[id] = outbox;
        using var closed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var receive = DrainAsync(socket, closed);
        try {
            await SendAsync(socket, first, closed.Token).ConfigureAwait(false);
            await foreach (var e in outbox.Reader.ReadAllAsync(closed.Token).ConfigureAwait(false)) {
                await SendAsync(socket, e, closed.Token).ConfigureAwait(false);
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
}
