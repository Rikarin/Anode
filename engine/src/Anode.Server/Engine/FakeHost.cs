using Anode.Ipc;
using Anode.Ipc.Segments;
using Anode.Ipc.Telemetry;
using Anode.Model.Audio;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;

namespace Anode.Server.Engine;

/// <summary>
///     A stand-in for anode-host that honours the IPC contract (<see cref="IpcMessage" />) without audio: the
///     in-process engine mode (<c>--Anode:Engine:Mode=inprocess</c>: tests and UI development) and tests of the
///     server's host link. It maps and validates every model segment it is sent, keeps strips and track IO, runs a
///     transport whose position advances in real time (wrapping in the cycle), writes a telemetry region about 60
///     times a second with plausible meters, and fakes recording: on stop, each armed track gets a WAV take of a quiet
///     sine in the record directory, reported in <see cref="RecordingFinishedMessage" />.
/// </summary>
public sealed class FakeHost : IAsyncDisposable {
    public const string Backend = "null";
    const int MaxLog = 4096;
    const double TakeFrequency = 440;
    const float TakeAmplitude = 0.25f;

    static readonly TimeSpan TelemetryInterval = TimeSpan.FromMilliseconds(16);

    readonly string version;
    readonly TelemetryWriter telemetry;
    readonly Lock gate = new();
    readonly List<IpcMessage> received = [];
    readonly Dictionary<int, AudioPlaylistEntry[]> playlists = [];
    readonly Dictionary<int, EngineTrack> tracks = [];
    readonly Dictionary<int, EngineFile> files = [];
    readonly CancellationTokenSource stopping;
    EngineStrip master = new(1, 0, false, false);
    string? recordDirectory;
    SetCycleMessage cycle = new(false, 0, 0);
    TelemetryTransportState state;
    long anchorPosition;
    long anchorTimestamp;
    long recordStart;
    long modelVersion;
    Task? telemetryLoop;
    Socket? listener;
    Task? acceptLoop;
    int disposed;

    public FakeHost(string version, int sampleRate = 48_000) {
        this.version = version;
        SampleRate = sampleRate;
        stopping = new();
        telemetry = TelemetryWriter.Create("an" + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()[..3]) + "t", 0);
    }

    public int SampleRate { get; }

    public string TelemetryName => telemetry.Name;

    /// <summary>Every message received (the latest <c>4096</c>), for tests.</summary>
    public IReadOnlyList<IpcMessage> Received {
        get {
            lock (gate) {
                return [..received];
            }
        }
    }

    /// <summary>The model version last loaded, and the engine ids of the tracks it holds.</summary>
    public (long Version, IReadOnlyList<int> Tracks) Model {
        get {
            lock (gate) {
                return (modelVersion, [..playlists.Keys.Order()]);
            }
        }
    }

    public TelemetryTransportState State {
        get {
            lock (gate) {
                return state;
            }
        }
    }

    /// <summary>Serves connections on a Unix domain socket, one at a time, as anode-host does.</summary>
    public void Listen(string socketPath) {
        if (File.Exists(socketPath)) {
            File.Delete(socketPath);
        }

        listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(1);
        acceptLoop = AcceptAsync(listener, socketPath, stopping.Token);
    }

    /// <summary>Serves one server connection until it closes.</summary>
    public async Task ServeAsync(Stream stream, CancellationToken cancellationToken) {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping.Token);
        telemetryLoop ??= TelemetryLoopAsync(stopping.Token);
        var token = linked.Token;
        try {
            while (await IpcFraming.ReadAsync(stream, token).ConfigureAwait(false) is { } message) {
                Log(message);
                foreach (var reply in Handle(message)) {
                    await IpcFraming.WriteAsync(stream, reply, token).ConfigureAwait(false);
                }
            }
        } catch (Exception e) when (e is OperationCanceledException
                                        or IOException
                                        or InvalidDataException
                                        or ObjectDisposedException) {
            // The server went away (or this host is stopping).
        }
    }

    public async ValueTask DisposeAsync() {
        if (Interlocked.Exchange(ref disposed, 1) != 0) {
            return;
        }

        await stopping.CancelAsync().ConfigureAwait(false);
        listener?.Dispose();
        foreach (var task in new[] { acceptLoop, telemetryLoop }) {
            if (task is not null) {
                try {
                    await task.ConfigureAwait(false);
                } catch (OperationCanceledException) {
                    // stopping
                }
            }
        }

        telemetry.Dispose();
        stopping.Dispose();
    }

    async Task AcceptAsync(Socket socket, string socketPath, CancellationToken cancellationToken) {
        try {
            while (!cancellationToken.IsCancellationRequested) {
                Socket client;
                try {
                    client = await socket.AcceptAsync(cancellationToken).ConfigureAwait(false);
                } catch (Exception e) when (e is ObjectDisposedException or SocketException) {
                    return;
                }

                var stream = new NetworkStream(client, true);
                await using (stream.ConfigureAwait(false)) {
                    await ServeAsync(stream, cancellationToken).ConfigureAwait(false);
                }
            }
        } finally {
            File.Delete(socketPath);
        }
    }

    IEnumerable<IpcMessage> Handle(IpcMessage message) {
        switch (message) {
            case HelloMessage:
                yield return new HelloMessage(version, Backend, SampleRate, 256, 2, 2, TelemetryName);
                break;
            case PingMessage ping:
                yield return new PongMessage(ping.Id);
                break;
            case LoadModelMessage load:
                yield return Load(load);
                break;
            case SetStripMessage strip:
                lock (gate) {
                    if (strip.TrackId == -1) {
                        master = strip.Strip;
                    } else if (tracks.TryGetValue(strip.TrackId, out var t)) {
                        tracks[strip.TrackId] = t with { Strip = strip.Strip };
                    }
                }

                break;
            case SetTrackIoMessage io:
                lock (gate) {
                    if (tracks.TryGetValue(io.TrackId, out var t)) {
                        tracks[io.TrackId] = t with {
                            InputChannel = io.InputChannel,
                            Stereo = io.Stereo,
                            RecordArmed = io.RecordArmed,
                            Monitoring = io.Monitoring
                        };
                    }
                }

                break;
            case SetCycleMessage c:
                lock (gate) {
                    Rebase();
                    cycle = c;
                }

                break;
            case SetMetronomeMessage:
                break;
            case TransportMessage transport:
                if (Transport(transport) is { } finished) {
                    yield return finished;
                }

                break;
            default:
                yield return new ErrorMessage($"Unsupported message {message.GetType().Name}.", Name(message));
                break;
        }
    }

    IpcMessage Load(LoadModelMessage load) {
        var loaded = new Dictionary<int, AudioPlaylistEntry[]>();
        try {
            using var mapped = MappedSegment.Map(load.Segment.Version, load.Segment.Name, load.Segment.Length);
            mapped.ValidateLayout();
            mapped.Verify();
            for (var i = 0; i < TrackCount(mapped); i++) {
                loaded[mapped.TrackId(i)] = PlaylistOf(mapped, i);
            }
        } catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) {
            return new ErrorMessage(e.Message, "loadModel");
        }

        lock (gate) {
            if (load.Replace) {
                playlists.Clear();
                tracks.Clear();
            }

            foreach (var (id, entries) in loaded) {
                playlists[id] = entries;
            }

            foreach (var track in load.Tracks) {
                tracks[track.Id] = track;
            }

            foreach (var file in load.Files) {
                files[file.Id] = file;
            }

            master = load.Master;
            recordDirectory = load.RecordDirectory;
            modelVersion = load.Segment.Version;
        }

        return new ModelLoadedMessage(load.Segment.Version);
    }

    RecordingFinishedMessage? Transport(TransportMessage message) {
        List<(EngineTrack Track, long Start, long Frames)>? takes = null;
        string? directory;
        lock (gate) {
            var now = Position();
            switch (message.Action) {
                case TransportAction.Play:
                case TransportAction.Record:
                    takes = Finish();
                    anchorPosition = message.PositionSamples >= 0 ? message.PositionSamples : now;
                    anchorTimestamp = Stopwatch.GetTimestamp();
                    state = message.Action == TransportAction.Play
                        ? TelemetryTransportState.Playing
                        : TelemetryTransportState.Recording;
                    recordStart = anchorPosition;
                    break;
                case TransportAction.Stop:
                    takes = Finish();
                    anchorPosition = now;
                    state = TelemetryTransportState.Stopped;
                    break;
                case TransportAction.Locate:
                    anchorPosition = Math.Max(0, message.PositionSamples);
                    anchorTimestamp = Stopwatch.GetTimestamp();
                    break;
            }

            directory = recordDirectory;
        }

        if (takes is not { Count: > 0 } || directory is null) {
            return null;
        }

        Directory.CreateDirectory(directory);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        var recorded = new List<RecordedTake>();
        foreach (var (track, start, frames) in takes) {
            var channels = track.Stereo ? 2 : 1;
            var path = Path.Combine(directory, $"{track.Id}-{stamp}.wav");
            WriteTake(path, channels, frames);
            recorded.Add(new(track.Id, path, start, frames, channels, SampleRate));
        }

        return new(recorded);
    }

    /// <summary>The takes of the recording that ends now, if one was running.</summary>
    List<(EngineTrack, long, long)>? Finish() {
        if (state != TelemetryTransportState.Recording) {
            return null;
        }

        var elapsed = (long)(Stopwatch.GetElapsedTime(anchorTimestamp).TotalSeconds * SampleRate);
        if (cycle.Enabled && recordStart < cycle.EndSample) {
            elapsed = Math.Min(elapsed, cycle.EndSample - recordStart);
        }

        elapsed = Math.Clamp(elapsed, 0, 600L * SampleRate);
        return elapsed == 0
            ? null
            : [
                ..tracks.Values.Where(static t => t.RecordArmed)
                    .OrderBy(static t => t.Id)
                    .Select(t => (t, recordStart, elapsed))
            ];
    }

    void WriteTake(string path, int channels, long frames) {
        using var writer = new WavWriter(path, channels, SampleRate, WavEncoding.Pcm24);
        var block = new float[4096 * channels];
        for (long done = 0; done < frames;) {
            var n = (int)Math.Min(4096, frames - done);
            for (var i = 0; i < n; i++) {
                var x = TakeAmplitude * (float)Math.Sin(2 * Math.PI * TakeFrequency * (done + i) / SampleRate);
                for (var c = 0; c < channels; c++) {
                    block[i * channels + c] = x;
                }
            }

            writer.Write(block.AsSpan(0, n * channels));
            done += n;
        }
    }

    /// <summary>The playhead now: the anchor plus the time since, wrapped in the cycle once it reached it.</summary>
    long Position() {
        if (state == TelemetryTransportState.Stopped) {
            return anchorPosition;
        }

        var position = anchorPosition + (long)(Stopwatch.GetElapsedTime(anchorTimestamp).TotalSeconds * SampleRate);
        if (cycle.Enabled
            && cycle.EndSample > cycle.StartSample
            && anchorPosition < cycle.EndSample
            && position >= cycle.EndSample) {
            position = cycle.StartSample + (position - cycle.StartSample) % (cycle.EndSample - cycle.StartSample);
        }

        return position;
    }

    /// <summary>Restarts the clock from the current position (before the cycle changes under it).</summary>
    void Rebase() {
        anchorPosition = Position();
        anchorTimestamp = Stopwatch.GetTimestamp();
    }

    async Task TelemetryLoopAsync(CancellationToken cancellationToken) {
        using var timer = new PeriodicTimer(TelemetryInterval);
        long callbacks = 0;
        try {
            do {
                WriteTelemetry(callbacks += 16);
            } while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        } catch (OperationCanceledException) {
            // stopping
        }
    }

    void WriteTelemetry(long callbacks) {
        TelemetryHeader header;
        MeterEntry[] meters;
        MeterEntry masterMeter;
        lock (gate) {
            var position = Position();
            header = new() {
                PositionSamples = position,
                HostTimeNs = (long)(Stopwatch.GetTimestamp() * (1e9 / Stopwatch.Frequency)),
                State = state,
                SampleRate = SampleRate,
                Callbacks = callbacks,
                ModelVersion = modelVersion
            };
            var soloed = tracks.Values.Any(static t => t.Strip.Solo);
            meters = [
                ..tracks.Values.OrderBy(static t => t.Id)
                    .Select(t => new MeterEntry {
                            TrackId = t.Id,
                            PeakLeft = Level(t, position, soloed),
                            PeakRight = Level(t, position, soloed)
                        }
                    )
            ];
            var gain = master.Mute ? 0 : master.Gain;
            var peak = meters.Length == 0 ? 0 : meters.Max(static m => m.PeakLeft) * gain;
            masterMeter = new() { TrackId = -1, PeakLeft = peak, PeakRight = peak };
        }

        telemetry.Write(header, meters, masterMeter);
    }

    /// <summary>A plausible level: the take's while recording or monitoring, half scale while a region plays.</summary>
    float Level(EngineTrack track, long position, bool soloed) {
        if (track.Strip.Mute || (soloed && !track.Strip.Solo)) {
            return 0;
        }

        if ((state == TelemetryTransportState.Recording && track.RecordArmed) || track.Monitoring) {
            return TakeAmplitude * track.Strip.Gain;
        }

        if (state == TelemetryTransportState.Stopped || !playlists.TryGetValue(track.Id, out var entries)) {
            return 0;
        }

        foreach (var entry in entries) {
            if (position >= entry.Start && position < entry.Start + entry.Length) {
                return 0.5f * entry.Gain * track.Strip.Gain;
            }
        }

        return 0;
    }

    void Log(IpcMessage message) {
        lock (gate) {
            received.Add(message);
            if (received.Count > MaxLog) {
                received.RemoveRange(0, received.Count - MaxLog);
            }
        }
    }

    static unsafe int TrackCount(MappedSegment segment) => segment.Header->TrackCount;

    static unsafe AudioPlaylistEntry[] PlaylistOf(MappedSegment segment, int index) {
        var view = segment.Track(index);
        return [..new ReadOnlySpan<AudioPlaylistEntry>(view.Playlist, (int)view.PlaylistCount)];
    }

    static string Name(IpcMessage message) {
        var name = message.GetType().Name.Replace("Message", string.Empty, StringComparison.Ordinal);
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}
