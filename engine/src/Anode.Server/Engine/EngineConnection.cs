using Anode.Contracts;
using Anode.Ipc;
using Microsoft.Extensions.Options;
using System.Net.Sockets;
using System.Reflection;

namespace Anode.Server.Engine;

/// <summary>
///     The server's link to anode-host (docs/adr/0001-process-model.md). In <c>ipc</c> mode it connects to the host's
///     Unix domain socket, exchanges hellos, and reconnects after the host restarts; in <c>inprocess</c> mode there is
///     no host and the status says so. <see cref="Changed" /> fires on every status change.
/// </summary>
public sealed class EngineConnection(IOptions<ServerOptions> options, ILogger<EngineConnection> logger)
    : BackgroundService {
    static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);

    readonly EngineOptions engine = options.Value.Engine;

    EngineStatus status = new(EngineState.Disconnected, null, null, "Not connected yet.");

    public static string Version =>
        typeof(EngineConnection).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? "dev";

    public event Action<EngineStatus>? Changed;

    public EngineStatus Status() => Volatile.Read(ref status);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        if (string.Equals(engine.Mode, "inprocess", StringComparison.OrdinalIgnoreCase)) {
            Set(new(EngineState.InProcess, Version, "null", null));
            return;
        }

        string? lastFailure = null;
        while (!stoppingToken.IsCancellationRequested) {
            try {
                await RunConnectionAsync(stoppingToken).ConfigureAwait(false);
                lastFailure = "The host closed the connection.";
            } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                return;
            } catch (Exception e) when (e is SocketException or IOException or InvalidDataException) {
                lastFailure = e.Message;
            }

            if (Status().State != EngineState.Disconnected || Status().Message != lastFailure) {
                Set(new(EngineState.Disconnected, null, null, lastFailure));
            }

            await Task.Delay(RetryDelay, stoppingToken).ConfigureAwait(false);
        }
    }

    async Task RunConnectionAsync(CancellationToken cancellationToken) {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(engine.SocketPath), cancellationToken)
            .ConfigureAwait(false);
        await using var stream = new NetworkStream(socket, false);
        await IpcFraming.WriteAsync(stream, new HelloMessage(Version), cancellationToken).ConfigureAwait(false);
        if (await IpcFraming.ReadAsync(stream, cancellationToken).ConfigureAwait(false) is not HelloMessage hello) {
            throw new InvalidDataException("The host did not answer with a hello.");
        }

        Set(new(EngineState.Connected, hello.Version, hello.Backend, null));
        // Nothing else to exchange yet; reading until the host goes is how a lost host is noticed.
        while (await IpcFraming.ReadAsync(stream, cancellationToken).ConfigureAwait(false) is not null) { }
    }

    void Set(EngineStatus next) {
        Volatile.Write(ref status, next);
        StartupLog.EngineState(logger, next.State, next.HostVersion ?? next.Message);
        Changed?.Invoke(next);
    }
}
