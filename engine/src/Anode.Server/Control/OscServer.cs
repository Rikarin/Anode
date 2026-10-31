using System.Net;
using System.Net.Sockets;

namespace Anode.Server.Control;

/// <summary>
///     The OSC server's socket (docs/09-phase-8.md, "OSC"): UDP on every interface (IPv4 and IPv6) at one port, each
///     datagram handed to <c>received</c> in turn, and datagrams sent from the same socket (so clients see replies come
///     from the port they send to). While it listens it is advertised as <c>_osc._udp</c> over Bonjour
///     (<see cref="Bonjour" />, macOS).
/// </summary>
public sealed class OscServer : IDisposable {
    /// <summary>The Bonjour service type of an OSC server.</summary>
    public const string ServiceType = "_osc._udp";

    readonly Socket socket;
    readonly CancellationTokenSource stopping = new();
    readonly Task receiving;
    readonly IDisposable? advertised;

    OscServer(Socket socket, Action<byte[], IPEndPoint> received, IDisposable? advertised) {
        this.socket = socket;
        this.advertised = advertised;
        Port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        receiving = Task.Run(() => ReceiveAsync(received, stopping.Token));
    }

    /// <summary>The port it listens on.</summary>
    public int Port { get; }

    /// <summary>Whether Bonjour advertises it.</summary>
    public bool Advertised => advertised is not null;

    /// <summary>
    ///     Listens on <paramref name="port" />; throws <see cref="SocketException" /> when the port is taken.
    ///     <paramref name="name" /> is the Bonjour service name (null: not advertised).
    /// </summary>
    public static OscServer Start(int port, Action<byte[], IPEndPoint> received, string? name, ILogger logger) {
        var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
        try {
            socket.DualMode = true;
            socket.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
        } catch {
            socket.Dispose();
            throw;
        }

        var advertised = name is null ? null : Bonjour.Advertise(name, ServiceType, port, logger);
        return new(socket, received, advertised);
    }

    /// <summary>Sends a datagram (dropped when the network refuses it).</summary>
    public void Send(byte[] data, IPEndPoint to) {
        try {
            var target = to.AddressFamily == AddressFamily.InterNetwork
                ? new IPEndPoint(to.Address.MapToIPv6(), to.Port)
                : to;
            socket.SendTo(data, SocketFlags.None, target);
        } catch (Exception e) when (e is SocketException or ObjectDisposedException) {
            // An unreachable client: feedback is best effort, as UDP is.
        }
    }

    async Task ReceiveAsync(Action<byte[], IPEndPoint> received, CancellationToken token) {
        var buffer = new byte[65_536];
        EndPoint any = new IPEndPoint(IPAddress.IPv6Any, 0);
        while (!token.IsCancellationRequested) {
            SocketReceiveFromResult result;
            try {
                result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, token).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                return;
            } catch (ObjectDisposedException) {
                return;
            } catch (SocketException) {
                // An ICMP port unreachable from an earlier send (Windows), or a datagram larger than the buffer.
                continue;
            }

            var from = (IPEndPoint)result.RemoteEndPoint;
            if (from.Address.IsIPv4MappedToIPv6) {
                from = new(from.Address.MapToIPv4(), from.Port);
            }

            received(buffer[..result.ReceivedBytes], from);
        }
    }

    public void Dispose() {
        // Not waiting for the receiving loop: its handler may be waiting for the caller's lock. Closing the socket
        // ends it, and a datagram it hands on after this is the owner's to ignore.
        advertised?.Dispose();
        stopping.Cancel();
        socket.Dispose();
        _ = receiving.ContinueWith(_ => stopping.Dispose(), TaskScheduler.Default);
    }
}
