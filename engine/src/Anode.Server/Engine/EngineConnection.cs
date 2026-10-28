using Anode.Contracts;
using Anode.Ipc;
using Anode.Ipc.Telemetry;
using Anode.Model;
using Anode.Model.Compile;
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
///     the host's finished recordings to the project. It keeps the host's plugin catalogue and MIDI ports
///     (<see cref="EngineCatalog" />), relays its MIDI activity as <see cref="MidiActivityEvent" />s with track ids, and
///     carries live MIDI, the MIDI-thru track and instrument-state requests to it. Phase 4: it keeps
///     <see cref="TransportClock" /> (whose stops end automation passes), runs renders for jobs
///     (<see cref="RenderAsync" />), relays plugin editor windows, applies parameter changes from plugins' own editors
///     as edits and relays plugin problems as <see cref="PluginProblemEvent" />s. <see cref="Changed" /> fires on every
///     status change.
/// </summary>
public sealed class EngineConnection(
    IOptions<ServerOptions> options,
    ProjectSession session,
    EngineDevice device,
    EngineCatalog catalog,
    EventHub hub,
    TransportClock clock,
    ILogger<EngineConnection> logger
) : BackgroundService {
    public const int MaxLiveEvents = 1024;

    /// <summary>The preview channel's level: −6 dB, so a full-scale file does not jump out over the mix.</summary>
    public const float PreviewGain = 0.5f;

    static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);
    static readonly TimeSpan TelemetryInterval = TimeSpan.FromMilliseconds(33);
    static readonly TimeSpan StatesTimeout = TimeSpan.FromSeconds(3);

    readonly EngineOptions engine = options.Value.Engine;
    readonly SegmentPool pool = new();

    EngineStatus status = new(EngineState.Disconnected, null, null, "Not connected yet.");
    Connection? current;
    TransportStatus? transport;
    string? thru;
    List<InsertReading>? pendingReadings;

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

    /// <summary>
    ///     Plays live MIDI on a track now (the on-screen keyboard); false when the open project has no such track.
    /// </summary>
    public bool SendLiveMidi(LiveMidiRequest request) {
        var view = session.View ?? throw ProjectException.NoProject();
        if (request.TrackId is null || view.State.FindTrack(request.TrackId) is not { } track) {
            return false;
        }

        if (!Model.Editing.Edits.IsMidiKind(track.Kind)) {
            throw ProjectException.Unprocessable($"Track '{track.Id}' is an audio track: it plays no MIDI.");
        }

        if (request.Events is not { Count: > 0 and <= MaxLiveEvents }) {
            throw ProjectException.Unprocessable($"Send 1…{MaxLiveEvents} MIDI events.");
        }

        var events = new EngineMidiEvent[request.Events.Count];
        for (var i = 0; i < events.Length; i++) {
            if (request.Events[i] is not {
                    Status: >= 0x80 and <= 0xEF, Data1: >= 0 and <= 127, Data2: >= 0 and <= 127
                } e) {
                throw ProjectException.Unprocessable(
                    "A MIDI event is a channel message: status 0x80…0xEF, data bytes 0…127."
                );
            }

            events[i] = new(e.Status, e.Data1, e.Data2);
        }

        Send(new LiveMidiMessage(view.Ids.Track(track.Id), events));
        return true;
    }

    /// <summary>
    ///     Makes <paramref name="request" />'s track the one that hears live MIDI when none is armed; false when the
    ///     open project has no such track.
    /// </summary>
    public bool SetThru(MidiThruRequest request) {
        if (request.TrackId is { } id && session.View?.State.FindTrack(id) is null) {
            return false;
        }

        Volatile.Write(ref thru, request.TrackId);
        Kick();
        return true;
    }

    /// <summary>
    ///     Flashback Capture: asks the host to write what the armed tracks heard since playback last started; its
    ///     answer becomes an undoable "Flashback Capture" step (<see cref="ProjectSession.ApplyRecording" />).
    /// </summary>
    public void Capture() {
        if (session.View is null) {
            throw ProjectException.NoProject();
        }

        Send(new FlashbackCaptureMessage());
    }

    /// <summary>
    ///     Plays an audio file on the server's disk through the engine's preview channel (straight to the master), or
    ///     with a null path stops it.
    /// </summary>
    public void Preview(string? path) {
        if (path is null) {
            Send(new PreviewFileMessage(null, 0));
            return;
        }

        string full;
        try {
            full = Path.GetFullPath(path);
        } catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) {
            throw ProjectException.Unprocessable($"'{path}' is not a file path: {e.Message}");
        }

        if (!File.Exists(full)) {
            throw ProjectException.NotFound($"No file '{full}'.");
        }

        if (Library.FileBrowser.KindOf(full, false) != FileKind.Audio) {
            throw ProjectException.Unprocessable($"'{full}' is not an audio file.");
        }

        Send(new PreviewFileMessage(full, PreviewGain));
    }

    /// <summary>Asks the host to rescan its plugin folders; the new catalogue follows as <c>pluginsChanged</c>.</summary>
    public void Rescan() => Send(new ScanPluginsMessage());

    /// <summary>
    ///     Opens or closes a plugin's own editor window: an insert of a track (or of the master without a track), or a
    ///     track's instrument without an insert. 404 for an unknown track or insert, 409 when the plugin has no editor of
    ///     its own or no engine runs.
    /// </summary>
    public void OpenEditor(PluginEditorRequest request) {
        var view = session.View ?? throw ProjectException.NoProject();
        string pluginId;
        var engineTrack = -1;
        if (request.TrackId is not null) {
            var track = view.State.FindTrack(request.TrackId)
                ?? throw ProjectException.NotFound($"No track '{request.TrackId}'.");
            if (!Stacks.IsPlayable(track)) {
                throw ProjectException.Unprocessable($"Track '{track.Name}' has no plugins.");
            }

            engineTrack = view.Ids.Track(track.Id);
            pluginId = request.InsertId is null
                ? track.Instrument?.PluginId
                ?? throw ProjectException.NotFound($"Track '{track.Name}' has no instrument.")
                : track.Strip.Inserts.FirstOrDefault(i => i.Id == request.InsertId)?.PluginId
                ?? throw ProjectException.NotFound($"No insert '{request.InsertId}' on track '{track.Name}'.");
        } else {
            pluginId = view.State.Master.Inserts.FirstOrDefault(i => i.Id == request.InsertId)?.PluginId
                ?? throw ProjectException.NotFound($"No insert '{request.InsertId}' on the master.");
        }

        if (request.Open && catalog.ForEdits.Find(pluginId) is not { HasEditor: true }) {
            throw ProjectException.Conflict($"Plugin '{pluginId}' has no editor of its own: use the generic one.");
        }

        if (Volatile.Read(ref current) is not { } link
            || !link.Work.Writer.TryWrite(new OpenPluginEditorMessage(engineTrack, request.InsertId, request.Open))) {
            throw ProjectException.Conflict("No engine runs to open the plugin's window.");
        }
    }

    /// <summary>
    ///     Renders through the host (after it has every edit made before): progress fractions go to
    ///     <paramref name="progress" />; cancelling asks the host to stop (the answer then carries its error). Throws
    ///     <see cref="ProjectException" /> without an engine; a lost connection fails the render.
    /// </summary>
    public async Task<RenderFinishedMessage> RenderAsync(
        RenderMessage message,
        Action<double> progress,
        CancellationToken cancellationToken
    ) {
        if (Volatile.Read(ref current) is not { } link) {
            throw ProjectException.Conflict("The engine is not connected.");
        }

        var render = link.StartRender(message.JobId, progress);
        if (!link.Work.Writer.TryWrite(message)) {
            link.EndRender(message.JobId);
            throw ProjectException.Conflict("The engine is not connected.");
        }

        await using (cancellationToken.Register(() => link.Work.Writer.TryWrite(new CancelRenderMessage(message.JobId)))
                         .ConfigureAwait(false)) {
            try {
                return await render.ConfigureAwait(false);
            } finally {
                link.EndRender(message.JobId);
            }
        }
    }

    /// <summary>
    ///     The plugins' opaque states by state key (<see cref="Model.Storage.ProjectPackage.StateKey" />: a track id for
    ///     an instrument, track and insert id for an insert; a null state: the plugin keeps none), as the host reports
    ///     them, or null without an engine or when it does not answer in time.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, byte[]?>?> InstrumentStatesAsync(
        CancellationToken cancellationToken
    ) {
        if (Volatile.Read(ref current) is not { } link || session.View is not { } view) {
            return null;
        }

        var pending = link.States();
        if (!link.Work.Writer.TryWrite(new RequestInstrumentStatesMessage())) {
            return null;
        }

        InstrumentStatesMessage answer;
        try {
            answer = await pending.WaitAsync(StatesTimeout, cancellationToken).ConfigureAwait(false);
        } catch (TimeoutException) {
            EngineLog.StatesTimedOut(logger);
            return null;
        }

        return Keyed(answer, view);
    }

    /// <summary>The host's answer by state key, its unknown tracks and undecodable states left out.</summary>
    Dictionary<string, byte[]?> Keyed(InstrumentStatesMessage answer, OpenProjectView view) {
        var states = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        foreach (var state in answer.States ?? []) {
            var trackId = state?.TrackId == -1 && state.InsertId is not null
                ? null
                : view.Ids.TrackOf(state?.TrackId ?? -1);
            if (state is null || (trackId is null && state.InsertId is null)) {
                continue;
            }

            try {
                states[Model.Storage.ProjectPackage.StateKey(trackId, state.InsertId)] =
                    state.State is { Length: > 0 } text ? Convert.FromBase64String(text) : null;
            } catch (FormatException) {
                EngineLog.BadState(logger, state.TrackId);
            }
        }

        return states;
    }

    void Send(IpcMessage message) {
        if (Volatile.Read(ref current) is not { } link || !link.Work.Writer.TryWrite(message)) {
            throw ProjectException.Unprocessable("The engine is not connected.");
        }
    }

    public override async Task StartAsync(CancellationToken cancellationToken) {
        session.Changed += Kick;
        clock.Stopped += session.EndPass;
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken) {
        session.Changed -= Kick;
        clock.Stopped -= session.EndPass;
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
        device.Set(hello.SampleRate);
        Set(
            new(inProcess ? EngineState.InProcess : EngineState.Connected, hello.Version, hello.Backend, RateMismatch())
        );
        var reading = ReadAsync(stream, connection, stop.Token);
        var pumping = PumpAsync(connection, stop.Token);
        var publishing = telemetry is null ? Task.CompletedTask : PublishAsync(telemetry, stop.Token);
        try {
            await await Task.WhenAny(reading, pumping).ConfigureAwait(false);
        } finally {
            Volatile.Write(ref current, null);
            Volatile.Write(ref transport, null);
            connection.Work.Writer.TryComplete();
            connection.CancelStates();
            connection.FailRenders("The engine connection was lost.");
            clock.Update(false, 0);
            catalog.Clear();
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
    async Task ReadAsync(Stream stream, Connection connection, CancellationToken cancellationToken) {
        var link = connection.Link;
        while (await IpcFraming.ReadAsync(stream, cancellationToken).ConfigureAwait(false) is { } message) {
            switch (message) {
                case ModelLoadedMessage loaded:
                    link.ModelLoaded(loaded.Version);
                    break;
                case PluginCatalogMessage plugins:
                    catalog.SetPlugins(plugins.Plugins ?? []);
                    break;
                case MidiPortsMessage ports:
                    catalog.SetPorts(ports.Ports ?? []);
                    break;
                case MidiActivityMessage activity:
                    Relay(activity);
                    break;
                case InstrumentStatesMessage states:
                    connection.StatesArrived(states);
                    break;
                case RecordingFinishedMessage finished:
                    _ = Task.Run(() => ApplyRecording(finished), CancellationToken.None);
                    break;
                case RenderProgressMessage progress:
                    connection.RenderProgress(progress);
                    break;
                case RenderFinishedMessage done:
                    connection.RenderFinished(done);
                    break;
                case ParameterChangedMessage changed when changed.ParameterId is not null:
                    // In order, here: a plugin's editor sends its moves one after another.
                    session.ApplyHostParameter(changed.TrackId, changed.InsertId, changed.ParameterId, changed.Value);
                    break;
                case PluginProblemMessage problem:
                    Relay(problem);
                    break;
                case InsertReadingsMessage readings:
                    Keep(readings);
                    break;
                case ErrorMessage error:
                    EngineLog.HostError(logger, error.Message, error.Request);
                    link.Error(error);
                    break;
            }
        }
    }

    /// <summary>The inserts' latest readings with domain track ids, for the next meters frame.</summary>
    void Keep(InsertReadingsMessage message) {
        var ids = session.View?.Ids;
        var readings = new List<InsertReading>();
        foreach (var r in message.Readings ?? []) {
            if (r?.InsertId is null || !float.IsFinite(r.Value)) {
                continue;
            }

            var trackId = r.TrackId < 0 ? null : ids?.TrackOf(r.TrackId);
            if (r.TrackId < 0 || trackId is not null) {
                readings.Add(new(trackId, r.InsertId, r.Value));
            }
        }

        Volatile.Write(ref pendingReadings, readings);
    }

    /// <summary>A plugin the host bypassed (it crashed or did not load), with domain ids.</summary>
    void Relay(PluginProblemMessage problem) {
        var trackId = problem.TrackId >= 0 ? session.View?.Ids.TrackOf(problem.TrackId) : null;
        EngineLog.PluginProblem(logger, problem.PluginId ?? string.Empty, problem.Message ?? string.Empty);
        hub.Publish(
            new PluginProblemEvent(
                trackId,
                problem.InsertId,
                problem.PluginId ?? string.Empty,
                problem.Message ?? string.Empty
            )
        );
    }

    /// <summary>The host's MIDI activity, with domain track ids, for keyboards and activity lights.</summary>
    void Relay(MidiActivityMessage activity) {
        if (activity.Events is not { Count: > 0 } entries) {
            return;
        }

        var ids = session.View?.Ids;
        hub.Publish(
            new MidiActivityEvent(
                [
                    ..entries.Where(static e => e is not null)
                        .Select(e => new MidiActivity(
                                e.TrackId >= 0 ? ids?.TrackOf(e.TrackId) : null,
                                e.Device,
                                e.Status,
                                e.Data1,
                                e.Data2
                            )
                        )
                ]
            )
        );
    }

    void ApplyRecording(RecordingFinishedMessage finished) {
        try {
            session.ApplyRecording(finished.Takes ?? [], finished.MidiTakes, finished.Flashback);
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
            await sync.SyncAsync(session.View, Volatile.Read(ref thru), cancellationToken).ConfigureAwait(false);
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
            clock.Update(now.State != TransportState.Stopped, now.PositionTick);
            // While it moves, and once more when it stops or is located.
            var moving = now.State != TransportState.Stopped;
            if (moving || last is null || last.State != now.State || last.PositionSamples != now.PositionSamples) {
                hub.Publish(new TransportEvent(now));
            }

            last = now;
            var readings = Interlocked.Exchange(ref pendingReadings, null);
            if (view is not null
                && Meters(frame, view) is var (meters, live)
                && (moving || live || metersLive || readings is { Count: > 0 })) {
                hub.Publish(readings is { Count: > 0 } ? meters with { Readings = readings } : meters);
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
            // A project opened at another rate than the device (or a matching one replaced it).
            var now = Status();
            if (now.State != EngineState.Disconnected && RateMismatch() is var mismatch && now.Message != mismatch) {
                Set(now with { Message = mismatch });
            }
        }
    }

    void Set(EngineStatus next) {
        Volatile.Write(ref status, next);
        StartupLog.EngineState(logger, next.State, next.HostVersion ?? next.Message);
        Changed?.Invoke(next);
    }

    /// <summary>
    ///     A live connection: its link, the queue of work for it (null: sync the project), and the instrument-state
    ///     request waiting for the host's answer.
    /// </summary>
    sealed class Connection(HostLink link, Channel<IpcMessage?> work) {
        readonly Lock gate = new();
        TaskCompletionSource<InstrumentStatesMessage>? states;

        public HostLink Link { get; } = link;
        public Channel<IpcMessage?> Work { get; } = work;

        /// <summary>The answer to the pending (or a new) state request.</summary>
        public Task<InstrumentStatesMessage> States() {
            lock (gate) {
                states ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
                return states.Task;
            }
        }

        public void StatesArrived(InstrumentStatesMessage message) {
            TaskCompletionSource<InstrumentStatesMessage>? done;
            lock (gate) {
                done = states;
                states = null;
            }

            done?.TrySetResult(message);
        }

        readonly Dictionary<int, (TaskCompletionSource<RenderFinishedMessage> Done, Action<double> Progress)> renders =
            [];

        public Task<RenderFinishedMessage> StartRender(int jobId, Action<double> progress) {
            var done = new TaskCompletionSource<RenderFinishedMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            lock (gate) {
                renders[jobId] = (done, progress);
            }

            return done.Task;
        }

        public void EndRender(int jobId) {
            lock (gate) {
                renders.Remove(jobId);
            }
        }

        public void RenderProgress(RenderProgressMessage message) {
            Action<double>? progress;
            lock (gate) {
                progress = renders.TryGetValue(message.JobId, out var r) ? r.Progress : null;
            }

            progress?.Invoke(Math.Clamp(message.Fraction, 0, 1));
        }

        public void RenderFinished(RenderFinishedMessage message) {
            TaskCompletionSource<RenderFinishedMessage>? done;
            lock (gate) {
                done = renders.TryGetValue(message.JobId, out var r) ? r.Done : null;
            }

            done?.TrySetResult(message);
        }

        public void FailRenders(string error) {
            List<(int, TaskCompletionSource<RenderFinishedMessage>)> pending;
            lock (gate) {
                pending = [..renders.Select(static r => (r.Key, r.Value.Done))];
            }

            foreach (var (id, done) in pending) {
                done.TrySetResult(new(id, [], error));
            }
        }

        public void CancelStates() {
            TaskCompletionSource<InstrumentStatesMessage>? done;
            lock (gate) {
                done = states;
                states = null;
            }

            done?.TrySetResult(new([]));
        }
    }

    /// <summary>Why the open project and the device disagree on the sample rate, or null when they agree.</summary>
    string? RateMismatch() =>
        device.SampleRate is { } rate && session.View?.State.SampleRate is { } project && project != rate
            ? $"The audio device runs at {rate} Hz and the project at {project} Hz: playback is off-speed and recording "
            + "is refused. Set the device to the project's rate."
            : null;
}
