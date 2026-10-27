using Anode.Contracts;
using Anode.Ipc;
using Anode.Ipc.Telemetry;
using Anode.Model;
using Anode.Model.Time;
using Anode.Server.Projects;
using Anode.Server.Realtime;
using Microsoft.Extensions.Options;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Channels;

namespace Anode.Server.Engine;

/// <summary>
///     The server's link to anode-host (docs/adr/0001-process-model.md). In <c>ipc</c> mode it connects to the host's
///     Unix domain socket, exchanges hellos, and reconnects after the host restarts; in <c>inprocess</c> mode it talks
///     to a <see cref="FakeHost" /> over an in-memory stream, with the same messages. On every connection it sends the
///     open project whole (<see cref="HostSync" />), then keeps the host in step with every change; it reads the
///     host's telemetry region and publishes <see cref="TransportEvent" />s (about 30 Hz while the transport moves,
///     once when it stops) and <see cref="MetersEvent" />s (about 30 Hz while there is something to show), and applies
///     the host's finished recordings to the project. <see cref="Changed" /> fires on every status change.
/// </summary>
public sealed class EngineConnection(
    IOptions<ServerOptions> options,
    ProjectSession session,
    EventHub hub,
    ILogger<EngineConnection> logger
) : BackgroundService {
    static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);
    static readonly TimeSpan TelemetryInterval = TimeSpan.FromMilliseconds(33);

    readonly EngineOptions engine = options.Value.Engine;
    readonly SegmentPool pool = new();

    EngineStatus status = new(EngineState.Disconnected, null, null, "Not connected yet.");
    Connection? current;
    TransportStatus? transport;

    public static string Version =>
        typeof(EngineConnection).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? "dev";

    public event Action<EngineStatus>? Changed;

    /// <summary>The in-process host, in <c>inprocess</c> mode (for tests).</summary>
    public FakeHost? InProcessHost { get; private set; }

    public bool IsConnected => Volatile.Read(ref current) is not null;

    public EngineStatus Status() => Volatile.Read(ref status);

    /// <summary>The playhead as the host last reported it (stopped at 0 before it has).</summary>
    public TransportStatus Transport() {
        if (Volatile.Read(ref transport) is { } known) {
            return known;
        }

        var view = session.View;
        return new(TransportState.Stopped, 0, 0, view?.State.SampleRate ?? ProjectDefaults.SampleRate);
    }

    /// <summary>
    ///     Carries out a transport request (after the host has every edit made before it), and answers the status it
    ///     leads to; the host's telemetry confirms it within a frame.
    /// </summary>
    public TransportStatus Control(TransportRequest request) {
        var view = session.View;
        if (view is null && request.Command != TransportCommand.Stop) {
            throw ProjectException.Unprocessable("Open a project first.");
        }

        if (request.Tick is < 0 or > Model.Editing.Edits.MaxTick) {
            throw ProjectException.Unprocessable($"Tick {request.Tick} is out of range.");
        }

        var map = view?.State.TempoMap ?? TempoMap.Constant(ProjectDefaults.Tempo);
        var rate = view?.State.SampleRate ?? ProjectDefaults.SampleRate;
        var position = request.Tick is { } tick ? map.SampleAt(tick, rate) : -1;
        var (action, state) = request.Command switch {
            TransportCommand.Play => (TransportAction.Play, TransportState.Playing),
            TransportCommand.Record => (TransportAction.Record, TransportState.Recording),
            TransportCommand.Stop => (TransportAction.Stop, TransportState.Stopped),
            TransportCommand.Locate => (TransportAction.Locate, Transport().State),
            _ => throw ProjectException.Unprocessable($"Unknown transport command {request.Command}.")
        };
        if (request.Command == TransportCommand.Locate && position < 0) {
            throw ProjectException.Unprocessable("Locate needs a tick.");
        }

        if (request.Command == TransportCommand.Record && !view!.State.Tracks.Exists(static t => t.RecordArmed)) {
            throw ProjectException.Unprocessable("Arm a track to record.");
        }

        if (Volatile.Read(ref current) is not { } link
            || !link.Work.Writer.TryWrite(new TransportMessage(action, position))) {
            throw ProjectException.Unprocessable("The engine is not connected.");
        }

        var at = position >= 0 ? position : Transport().PositionSamples;
        return new(state, at, map.TickAtSample(at, rate), rate);
    }

    public override async Task StartAsync(CancellationToken cancellationToken) {
        session.Changed += Kick;
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken) {
        session.Changed -= Kick;
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        if (InProcessHost is { } host) {
            await host.DisposeAsync().ConfigureAwait(false);
        }

        pool.Dispose();
    }

    public override void Dispose() {
        pool.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        if (string.Equals(engine.Mode, "inprocess", StringComparison.OrdinalIgnoreCase)) {
            await RunInProcessAsync(stoppingToken).ConfigureAwait(false);
            return;
        }

        string? lastFailure = null;
        while (!stoppingToken.IsCancellationRequested) {
            try {
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(engine.SocketPath), stoppingToken)
                    .ConfigureAwait(false);
                var stream = new NetworkStream(socket, false);
                await using (stream.ConfigureAwait(false)) {
                    await RunConnectionAsync(stream, false, stoppingToken).ConfigureAwait(false);
                }

                lastFailure = "The host closed the connection.";
            } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                return;
            } catch (Exception e) when (e is SocketException
                                            or IOException
                                            or InvalidDataException
                                            or System.Text.Json.JsonException) {
                lastFailure = e.Message;
            }

            if (Status().State != EngineState.Disconnected || Status().Message != lastFailure) {
                Set(new(EngineState.Disconnected, null, null, lastFailure));
            }

            await Task.Delay(RetryDelay, stoppingToken).ConfigureAwait(false);
        }
    }

    async Task RunInProcessAsync(CancellationToken stoppingToken) {
        var host = new FakeHost(Version);
        InProcessHost = host;
        while (!stoppingToken.IsCancellationRequested) {
            var (server, side) = DuplexStream.CreatePair();
            var serving = host.ServeAsync(side, stoppingToken);
            try {
                await RunConnectionAsync(server, true, stoppingToken).ConfigureAwait(false);
            } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                return;
            } catch (Exception e) when (e is IOException or InvalidDataException) {
                Set(new(EngineState.Disconnected, null, null, e.Message));
            } finally {
                await server.DisposeAsync().ConfigureAwait(false);
                await serving.ConfigureAwait(false);
                await side.DisposeAsync().ConfigureAwait(false);
            }

            await Task.Delay(RetryDelay, stoppingToken).ConfigureAwait(false);
        }
    }

    async Task RunConnectionAsync(Stream stream, bool inProcess, CancellationToken cancellationToken) {
        await IpcFraming.WriteAsync(stream, new HelloMessage(Version), cancellationToken).ConfigureAwait(false);
        if (await IpcFraming.ReadAsync(stream, cancellationToken).ConfigureAwait(false) is not HelloMessage hello) {
            throw new InvalidDataException("The host did not answer with a hello.");
        }

        using var telemetry = TelemetryReader.TryOpen(hello.TelemetryName);
        using var link = new HostLink(stream);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var connection = new Connection(link, Channel.CreateUnbounded<IpcMessage?>());
        pool.Reset();
        connection.Work.Writer.TryWrite(null); // the first sync sends the open project whole
        Volatile.Write(ref current, connection);
        Set(new(inProcess ? EngineState.InProcess : EngineState.Connected, hello.Version, hello.Backend, null));
        var reading = ReadAsync(stream, link, stop.Token);
        var pumping = PumpAsync(connection, stop.Token);
        var publishing = telemetry is null ? Task.CompletedTask : PublishAsync(telemetry, stop.Token);
        try {
            await await Task.WhenAny(reading, pumping).ConfigureAwait(false);
        } finally {
            Volatile.Write(ref current, null);
            Volatile.Write(ref transport, null);
            connection.Work.Writer.TryComplete();
            await stop.CancelAsync().ConfigureAwait(false);
            foreach (var task in new[] { reading, pumping, publishing }) {
                try {
                    await task.ConfigureAwait(false);
                } catch (Exception e) when (e is OperationCanceledException
                                                or IOException
                                                or InvalidDataException
                                                or ObjectDisposedException) {
                    // ending with the connection
                }
            }
        }
    }

    /// <summary>Reads the host's messages until it closes the connection.</summary>
    async Task ReadAsync(Stream stream, HostLink link, CancellationToken cancellationToken) {
        while (await IpcFraming.ReadAsync(stream, cancellationToken).ConfigureAwait(false) is { } message) {
            switch (message) {
                case ModelLoadedMessage loaded:
                    link.ModelLoaded(loaded.Version);
                    break;
                case RecordingFinishedMessage finished:
                    _ = Task.Run(() => ApplyRecording(finished), CancellationToken.None);
                    break;
                case ErrorMessage error:
                    EngineLog.HostError(logger, error.Message, error.Request);
                    link.Error(error);
                    break;
            }
        }
    }

    void ApplyRecording(RecordingFinishedMessage finished) {
        try {
            session.ApplyRecording(finished.Takes);
        } catch (Exception e) when (e is IOException
                                        or UnauthorizedAccessException
                                        or ProjectException
                                        or InvalidOperationException) {
            EngineLog.RecordingFailed(logger, e);
        }
    }

    /// <summary>Sends the host what it is owed, in order: the project's changes first, then each queued message.</summary>
    async Task PumpAsync(Connection connection, CancellationToken cancellationToken) {
        var sync = new HostSync(connection.Link, pool, logger);
        await foreach (var message in connection.Work.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) {
            await sync.SyncAsync(session.View, cancellationToken).ConfigureAwait(false);
            if (message is not null) {
                await connection.Link.SendAsync(message, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Reads the telemetry region about 30 times a second and publishes the playhead and meters.</summary>
    async Task PublishAsync(TelemetryReader telemetry, CancellationToken cancellationToken) {
        using var timer = new PeriodicTimer(TelemetryInterval);
        TransportStatus? last = null;
        var metersLive = false;
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false)) {
            if (telemetry.Read() is not { } frame) {
                continue;
            }

            var view = session.View;
            var now = Status(frame.Header, view);
            Volatile.Write(ref transport, now);
            // While it moves, and once more when it stops or is located.
            var moving = now.State != TransportState.Stopped;
            if (moving || last is null || last.State != now.State || last.PositionSamples != now.PositionSamples) {
                hub.Publish(new TransportEvent(now));
            }

            last = now;
            if (view is not null && Meters(frame, view) is var (meters, live) && (moving || live || metersLive)) {
                hub.Publish(meters);
                metersLive = live;
            }
        }
    }

    static TransportStatus Status(TelemetryHeader header, OpenProjectView? view) {
        var position = header.PositionSamples;
        var rate = view?.State.SampleRate ?? (header.SampleRate > 0 ? header.SampleRate : ProjectDefaults.SampleRate);
        var map = view?.State.TempoMap ?? TempoMap.Constant(ProjectDefaults.Tempo);
        var state = header.State switch {
            TelemetryTransportState.Playing => TransportState.Playing,
            TelemetryTransportState.Recording => TransportState.Recording,
            _ => TransportState.Stopped
        };
        return new(state, position, map.TickAtSample(position, rate), rate);
    }

    /// <summary>The frame's meters by domain track id, and whether any shows a level.</summary>
    static (MetersEvent Meters, bool Live) Meters(TelemetryFrame frame, OpenProjectView view) {
        var meters = new List<TrackMeter>(frame.Tracks.Length);
        var live = frame.Master.PeakLeft > 0 || frame.Master.PeakRight > 0;
        foreach (var entry in frame.Tracks) {
            if (view.Ids.TrackOf(entry.TrackId) is { } id) {
                meters.Add(new(id, entry.PeakLeft, entry.PeakRight));
                live |= entry.PeakLeft > 0 || entry.PeakRight > 0;
            }
        }

        return (new(meters, new("master", frame.Master.PeakLeft, frame.Master.PeakRight)), live);
    }

    void Kick() {
        if (Volatile.Read(ref current) is { } connection) {
            connection.Work.Writer.TryWrite(null);
        }
    }

    void Set(EngineStatus next) {
        Volatile.Write(ref status, next);
        StartupLog.EngineState(logger, next.State, next.HostVersion ?? next.Message);
        Changed?.Invoke(next);
    }

    /// <summary>A live connection: its link, and the queue of work for it (null: sync the project).</summary>
    sealed record Connection(HostLink Link, Channel<IpcMessage?> Work);
}
