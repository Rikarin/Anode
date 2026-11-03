using Anode.Contracts;
using Anode.Ipc;
using Anode.Media.Opus;
using Anode.Server.Engine;
using Anode.Server.Realtime;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;

namespace Anode.Server.Remote;

/// <summary>
///     One listener's stream encoder (<see cref="ListenInFramer" />, workstream E): tap audio in at the project's rate,
///     listen-in frames (header and Opus packet) out. Not thread-safe.
/// </summary>
public interface IListenInEncoder : IDisposable {
    ListenInFormat Format { get; }

    void Write(ReadOnlySpan<float> interleaved, long transportSample, bool playing, bool discontinuity);

    /// <summary>The next frame into <paramref name="frame" />; its length, or 0 when less than a frame is buffered.</summary>
    int Read(Span<byte> frame);
}

/// <summary>Makes a listener's encoder: a seam so the socket's tests run with a fake framer.</summary>
public interface IListenInEncoders {
    IListenInEncoder Create(int projectSampleRate);
}

/// <summary>Encoders over <see cref="ListenInFramer" /> (Opus, 48 kHz, 20 ms frames).</summary>
public sealed class OpusListenInEncoders : IListenInEncoders {
    public IListenInEncoder Create(int projectSampleRate) => new Framed(new(projectSampleRate));

    sealed class Framed(ListenInFramer framer) : IListenInEncoder {
        public ListenInFormat Format => framer.Format;

        public void Write(ReadOnlySpan<float> interleaved, long transportSample, bool playing, bool discontinuity) =>
            framer.Write(interleaved, transportSample, playing, discontinuity);

        public int Read(Span<byte> frame) => framer.Read(frame);

        public void Dispose() => framer.Dispose();
    }
}

/// <summary>Where the master tap ring comes from: the host's (named in its hello); tests give their own.</summary>
public interface IMasterTapSource {
    /// <summary>The ring's identity (its name): when it changes, the reader is opened again.</summary>
    string? Key { get; }

    MasterTapReader? Open();
}

/// <summary>The connected host's ring (<see cref="EngineConnection.MasterTapName" />).</summary>
public sealed class HostMasterTap(EngineConnection engine) : IMasterTapSource {
    public string? Key => engine.MasterTapName;

    public MasterTapReader? Open() => MasterTapReader.TryOpen(engine.MasterTapName);
}

/// <summary>
///     Listen-in (docs/11-phase-10.md, docs/adr/0013-video-and-listen-in.md): the master, as the host's tap writes it,
///     encoded per listener and sent over <c>/api/v1/remote/listen</c>. At most <see cref="ListenIn.MaxListeners" />
///     sockets; the host writes the tap (<see cref="SetMasterTapMessage" />) while any is open, and again after it
///     reconnects. One pump reads the ring every <see cref="PollInterval" /> and feeds each listener's encoder; each
///     socket
///     sends its <see cref="ListenInFormat" />, then its frames from a short queue that drops the oldest when the socket
///     falls behind. <see cref="ListenInEvent" /> goes out when listening starts or stops, a listener comes or goes, or
///     the Server's delay moves by 10 ms or more.
/// </summary>
public sealed class ListenInService : IDisposable {
    /// <summary>How often the pump reads the ring.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);

    /// <summary>Frames a socket may fall behind before the oldest are dropped (1 s of 20 ms frames).</summary>
    public const int Backlog = 50;

    const int ReadFrames = 1 << 14;

    readonly EngineConnection engine;
    readonly EngineDevice device;
    readonly EventHub hub;
    readonly IListenInEncoders encoders;
    readonly IMasterTapSource tap;
    readonly ILogger<ListenInService> logger;
    readonly Lock gate = new();
    readonly List<Listener> listeners = [];
    CancellationTokenSource? pumping;
    Task? pump;
    double? latency;
    ListenInState published = new(false, 0, null);

    public ListenInService(
        EngineConnection engine,
        EngineDevice device,
        EventHub hub,
        IListenInEncoders encoders,
        IMasterTapSource tap,
        ILogger<ListenInService> logger
    ) {
        this.engine = engine;
        this.device = device;
        this.hub = hub;
        this.encoders = encoders;
        this.tap = tap;
        this.logger = logger;
        engine.HostConnected += Reconnected;
    }

    public ListenInState State() {
        lock (gate) {
            return new(listeners.Count > 0, listeners.Count, listeners.Count > 0 ? latency : null);
        }
    }

    /// <summary>A place for a new socket, or null when <see cref="ListenIn.MaxListeners" /> are listening.</summary>
    public Listener? TryJoin() {
        Listener listener;
        lock (gate) {
            if (listeners.Count >= ListenIn.MaxListeners) {
                return null;
            }

            var rate = device.SampleRate ?? ListenIn.SampleRate;
            listener = new(encoders.Create(rate));
            listeners.Add(listener);
            if (listeners.Count == 1) {
                latency = null;
                pumping = new();
                var token = pumping.Token;
                pump = Task.Run(() => PumpAsync(token), CancellationToken.None);
                engine.Post(new SetMasterTapMessage(true));
            }
        }

        Publish();
        return listener;
    }

    /// <summary>The listener's socket closed: its encoder goes, and the tap stops with the last.</summary>
    public void Leave(Listener listener) {
        CancellationTokenSource? stop = null;
        lock (gate) {
            if (!listeners.Remove(listener)) {
                return;
            }

            listener.Dispose();
            if (listeners.Count == 0) {
                stop = pumping;
                pumping = null;
                pump = null;
                engine.Post(new SetMasterTapMessage(false));
            }
        }

        stop?.Cancel();
        Publish();
    }

    /// <summary>Sends the format, then the listener's frames, until the socket closes.</summary>
    public async Task ServeAsync(WebSocket socket, Listener listener, CancellationToken cancellationToken) {
        using var closed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var receive = DrainAsync(socket, closed);
        try {
            await socket.SendAsync(
                    JsonSerializer.SerializeToUtf8Bytes(listener.Format, ContractsJsonContext.Default.ListenInFormat),
                    WebSocketMessageType.Text,
                    true,
                    closed.Token
                )
                .ConfigureAwait(false);
            await foreach (var frame in listener.Frames.ReadAllAsync(closed.Token).ConfigureAwait(false)) {
                await socket.SendAsync(frame, WebSocketMessageType.Binary, true, closed.Token).ConfigureAwait(false);
            }
        } catch (Exception e) when (Gone(e)) {
            // the listener went away
        } finally {
            await closed.CancelAsync().ConfigureAwait(false);
            await receive.ConfigureAwait(false);
            if (socket.State == WebSocketState.CloseReceived) {
                try {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None)
                        .ConfigureAwait(false);
                } catch (Exception e) when (Gone(e)) {
                    // it went before the handshake ended
                }
            }
        }
    }

    /// <summary>Whether <paramref name="e" /> means the socket went away.</summary>
    static bool Gone(Exception e) =>
        e is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException;

    static async Task DrainAsync(WebSocket socket, CancellationTokenSource closed) {
        var buffer = new byte[256];
        try {
            while (socket.State == WebSocketState.Open) {
                var result = await socket.ReceiveAsync(buffer, closed.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) {
                    break;
                }
            }
        } catch (Exception e) when (Gone(e)) {
            // closed
        }

        await closed.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>A host connected again: it starts with the tap off, so turn it on while anyone listens.</summary>
    void Reconnected() {
        lock (gate) {
            if (listeners.Count > 0) {
                engine.Post(new SetMasterTapMessage(true));
            }
        }
    }

    async Task PumpAsync(CancellationToken token) {
        var audio = new float[ReadFrames * 2];
        var runs = new List<TapRun>();
        var frame = new byte[ListenIn.HeaderBytes + OpusEncoder.MaxPacketBytes];
        MasterTapReader? reader = null;
        string? key = null;
        using var timer = new PeriodicTimer(PollInterval);
        try {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false)) {
                if (reader is null || tap.Key != key) {
                    reader?.Dispose();
                    key = tap.Key;
                    reader = tap.Open();
                }

                if (reader is not null) {
                    Pump(reader, audio, runs, frame);
                }
            }
        } catch (OperationCanceledException) {
            // the last listener left
        } catch (Exception e) when (e is InvalidOperationException or ArgumentException) {
            RemoteLog.ListenInFailed(logger, e.Message);
        } finally {
            reader?.Dispose();
        }
    }

    /// <summary>Reads what the ring has and feeds every listener's encoder; queues the frames it makes.</summary>
    void Pump(MasterTapReader reader, float[] audio, List<TapRun> runs, byte[] frame) {
        var behind = reader.Behind;
        if (reader.Read(audio, runs) == 0) {
            return;
        }

        lock (gate) {
            foreach (var listener in listeners) {
                foreach (var run in runs) {
                    listener.Encoder.Write(
                        audio.AsSpan(run.Offset * 2, run.Frames * 2),
                        run.TransportSample,
                        run.Playing,
                        run.Discontinuity
                    );
                }

                int length;
                while ((length = listener.Encoder.Read(frame)) > 0) {
                    listener.Post([..frame.AsSpan(0, length)]);
                }
            }
        }

        // The Server's own delay: what waited in the ring, the frame the encoder gathers, one poll.
        var rate = Math.Max(1, reader.SampleRate);
        Measured(
            behind * 1000.0 / rate
            + 1000.0 * ListenIn.FrameSamples / ListenIn.SampleRate
            + PollInterval.TotalMilliseconds
        );
    }

    void Measured(double ms) {
        bool changed;
        lock (gate) {
            changed = latency is not { } last || Math.Abs(last - ms) >= 10;
            if (changed) {
                latency = Math.Round(ms, 1);
            }
        }

        if (changed) {
            Publish();
        }
    }

    void Publish() {
        var state = State();
        lock (gate) {
            if (state == published) {
                return;
            }

            published = state;
        }

        hub.Publish(new ListenInEvent(state));
    }

    public void Dispose() {
        engine.HostConnected -= Reconnected;
        lock (gate) {
            foreach (var listener in listeners) {
                listener.Dispose();
            }

            listeners.Clear();
            pumping?.Cancel();
            pumping = null;
        }
    }

    /// <summary>A socket's encoder and its queue of frames to send.</summary>
    public sealed class Listener(IListenInEncoder encoder) : IDisposable {
        readonly Channel<byte[]> frames = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(Backlog) {
                SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest
            }
        );

        public IListenInEncoder Encoder { get; } = encoder;

        public ListenInFormat Format { get; } = encoder.Format;

        public ChannelReader<byte[]> Frames => frames.Reader;

        public void Post(byte[] frame) => frames.Writer.TryWrite(frame);

        public void Dispose() {
            frames.Writer.TryComplete();
            Encoder.Dispose();
        }
    }
}

static partial class RemoteLog {
    [LoggerMessage(Level = LogLevel.Warning, Message = "Listen-in stopped: {Message}")]
    public static partial void ListenInFailed(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Information, Message = "Anode is not advertised over DNS-SD: {Message}")]
    public static partial void DiscoveryFailed(ILogger logger, string message);
}
