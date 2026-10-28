using Anode.Dsp.Instruments;
using Anode.Ipc;
using Anode.Ipc.Segments;
using Anode.Ipc.Telemetry;
using Anode.Model.Audio;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace Anode.Server.Engine;

/// <summary>
///     A stand-in for anode-host that honours the IPC contract (<see cref="IpcMessage" />) without audio: the
///     in-process engine mode (<c>--Anode:Engine:Mode=inprocess</c>: tests and UI development) and tests of the
///     server's host link. It maps and validates every model segment it is sent, keeps strips and track IO, runs a
///     transport whose position advances in real time (wrapping in the cycle), writes a telemetry region about 60
///     times a second with plausible meters, and fakes recording: on stop, each armed audio track gets a WAV take of a
///     quiet sine in the record directory, and each armed instrument or external MIDI track a MIDI take of the live
///     MIDI it received (or, without any, a few notes), reported in <see cref="RecordingFinishedMessage" />. Phase 2: it
///     serves a plugin catalogue (<see cref="Catalogue" />) and MIDI ports after its hello, keeps instruments,
///     parameters, MIDI I/O and the MIDI-thru track, answers live MIDI with activity, and reports a state per
///     instrument (the one it was given, else a made-up one). Phase 3: a recording across cycle wraps is one take per
///     track with its <see cref="RecordedTake.Passes" />, and with autopunch (<see cref="SetPunchMessage" />) only
///     the punch range is recorded; while playing, it keeps what armed tracks (and MIDI on armed or thru tracks) would
///     have recorded since playback started, which <see cref="FlashbackCaptureMessage" /> writes as a Flashback
///     recording; and it keeps the file the preview channel plays (<see cref="Preview" />).
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
    readonly Dictionary<int, PlaybackEvent[]> events = [];
    readonly Dictionary<int, EngineTrack> tracks = [];
    readonly Dictionary<int, EngineFile> files = [];
    readonly Dictionary<int, string> states = [];
    readonly Dictionary<int, List<RecordedMidiEvent>> live = [];
    readonly Dictionary<int, List<RecordedMidiEvent>> heard = [];
    readonly CancellationTokenSource stopping;
    EngineStrip master = new(1, 0, false, false);
    string? recordDirectory;
    SetCycleMessage cycle = new(false, 0, 0);
    SetPunchMessage punch = new(false, 0, 0);
    string? preview;
    long playStart = -1;
    long playTimestamp;
    long playedFrames = -1;
    TelemetryTransportState state;
    long anchorPosition;
    long anchorTimestamp;
    long recordStart;
    long modelVersion;
    int thru = -1;
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

    /// <summary>The fake CLAP instrument in <see cref="Catalogue" />.</summary>
    public const string FakeClapId = "org.anode.fake.sine";

    /// <summary>
    ///     What this host can load: the built-in instruments (Anode.Dsp's descriptors, or stand-ins with the same ids
    ///     while those are not filled in), then a fake CLAP instrument with decimal parameter ids.
    /// </summary>
    public static IReadOnlyList<EnginePlugin> Catalogue { get; } = [
        ..BuiltinInstruments.All.Count > 0
            ? BuiltinInstruments.All.Select(EngineCatalog.From)
            : [
                new EnginePlugin(
                    BuiltinInstruments.DefaultId,
                    "Synth",
                    "Anode",
                    "0",
                    "builtin",
                    true,
                    [],
                    [
                        new("cutoff", "Cutoff", 20, 20_000, 8_000, "Hz", 0),
                        new("resonance", "Resonance", 0, 1, 0.2, string.Empty, 0),
                        new("wave", "Waveform", 0, 3, 0, string.Empty, 3, ["Saw", "Square", "Triangle", "Sine"])
                    ]
                ),
                new EnginePlugin(
                    "anode.sampler",
                    "Sampler",
                    "Anode",
                    "0",
                    "builtin",
                    true,
                    [".sfz", ".wav"],
                    [new("volume", "Volume", -60, 6, 0, "dB", 0), new("tune", "Tune", -100, 100, 0, "ct", 0)]
                )
            ],
        new EnginePlugin(
            FakeClapId,
            "Fake Sine",
            "Anode tests",
            "0.1",
            "clap",
            true,
            [],
            [new("0", "Gain", 0, 1, 0.5, string.Empty, 0), new("1", "Detune", -1, 1, 0, string.Empty, 0)]
        )
    ];

    /// <summary>The MIDI ports this host reports.</summary>
    public static IReadOnlyList<EngineMidiPort> Ports { get; } = [
        new("fake-in", "Fake Keyboard", true, false), new("fake-out", "Fake Module", false, true)
    ];

    /// <summary>The tracks as this host knows them (settings, instrument, MIDI I/O).</summary>
    public IReadOnlyDictionary<int, EngineTrack> Tracks {
        get {
            lock (gate) {
                return new Dictionary<int, EngineTrack>(tracks);
            }
        }
    }

    /// <summary>The compiled events of engine track <paramref name="trackId" /> in the loaded model.</summary>
    public IReadOnlyList<PlaybackEvent> EventsOf(int trackId) {
        lock (gate) {
            return events.GetValueOrDefault(trackId) ?? [];
        }
    }

    /// <summary>The autopunch range last set.</summary>
    public SetPunchMessage Punch {
        get {
            lock (gate) {
                return punch;
            }
        }
    }

    /// <summary>The file the preview channel plays, or null.</summary>
    public string? Preview {
        get {
            lock (gate) {
                return preview;
            }
        }
    }

    /// <summary>The MIDI-thru track (−1: none).</summary>
    public int Thru {
        get {
            lock (gate) {
                return thru;
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
                yield return new PluginCatalogMessage(Catalogue);
                yield return new MidiPortsMessage(Ports);
                break;
            case ScanPluginsMessage:
                yield return new PluginCatalogMessage(Catalogue);
                break;
            case SetInstrumentMessage set:
                lock (gate) {
                    if (tracks.TryGetValue(set.TrackId, out var t)) {
                        tracks[set.TrackId] = t with { Instrument = set.Instrument };
                    }

                    Remember(set.TrackId, set.Instrument);
                }

                break;
            case SetParameterMessage parameter:
                lock (gate) {
                    if (tracks.TryGetValue(parameter.TrackId, out var t) && t.Instrument is { } instrument) {
                        var values = new Dictionary<string, double>(instrument.Parameters) {
                            [parameter.ParameterId] = parameter.Value
                        };
                        tracks[parameter.TrackId] = t with { Instrument = instrument with { Parameters = values } };
                    }
                }

                break;
            case SetMidiIoMessage io:
                lock (gate) {
                    if (tracks.TryGetValue(io.TrackId, out var t)) {
                        tracks[io.TrackId] = t with { Midi = io.Midi };
                    }
                }

                break;
            case SetMidiThruMessage set:
                lock (gate) {
                    thru = set.TrackId;
                }

                break;
            case LiveMidiMessage midi:
                yield return Live(midi);
                break;
            case RequestInstrumentStatesMessage:
                yield return States();
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
            case SetPunchMessage set:
                lock (gate) {
                    punch = set;
                }

                break;
            case PreviewFileMessage play:
                lock (gate) {
                    preview = play.Path;
                }

                break;
            case FlashbackCaptureMessage:
                yield return Capture();
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
        var loadedEvents = new Dictionary<int, PlaybackEvent[]>();
        try {
            using var mapped = MappedSegment.Map(load.Segment.Version, load.Segment.Name, load.Segment.Length);
            mapped.ValidateLayout();
            mapped.Verify();
            for (var i = 0; i < TrackCount(mapped); i++) {
                loaded[mapped.TrackId(i)] = PlaylistOf(mapped, i);
                loadedEvents[mapped.TrackId(i)] = EventsOf(mapped, i);
            }
        } catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) {
            return new ErrorMessage(e.Message, "loadModel");
        }

        lock (gate) {
            if (load.Replace) {
                playlists.Clear();
                events.Clear();
                tracks.Clear();
            }

            foreach (var (id, entries) in loaded) {
                playlists[id] = entries;
                events[id] = loadedEvents[id];
            }

            foreach (var track in load.Tracks) {
                tracks[track.Id] = track;
                if (track.Instrument is { State: not null } || !states.ContainsKey(track.Id)) {
                    Remember(track.Id, track.Instrument);
                }
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

    /// <summary>A track's instrument changed: the state it was given, or none yet.</summary>
    void Remember(int trackId, EngineInstrument? instrument) {
        if (instrument?.State is { } state) {
            states[trackId] = state;
        } else {
            states.Remove(trackId);
        }
    }

    /// <summary>Plays live MIDI: kept for the take while the track records, and echoed as activity.</summary>
    MidiActivityMessage Live(LiveMidiMessage message) {
        lock (gate) {
            if (state == TelemetryTransportState.Recording
                && tracks.TryGetValue(message.TrackId, out var track)
                && track.RecordArmed) {
                if (!live.TryGetValue(message.TrackId, out var events)) {
                    live[message.TrackId] = events = [];
                }

                var at = Position();
                events.AddRange(message.Events.Select(e => new RecordedMidiEvent(at, e.Status, e.Data1, e.Data2)));
            } else if (state == TelemetryTransportState.Playing
                       && tracks.TryGetValue(message.TrackId, out var played)
                       && (played.RecordArmed || message.TrackId == thru)) {
                if (!heard.TryGetValue(message.TrackId, out var kept)) {
                    heard[message.TrackId] = kept = [];
                }

                if (kept.Count < MaxLog) {
                    var at = Position();
                    kept.AddRange(message.Events.Select(e => new RecordedMidiEvent(at, e.Status, e.Data1, e.Data2)));
                }
            }
        }

        return new(
            [..message.Events.Select(e => new MidiActivityEntry(message.TrackId, null, e.Status, e.Data1, e.Data2))]
        );
    }

    /// <summary>Every instrument's state: the one it was given, else a made-up one naming its plugin.</summary>
    InstrumentStatesMessage States() {
        lock (gate) {
            return new(
                [
                    ..tracks.Values.Where(static t => t.Instrument is not null)
                        .OrderBy(static t => t.Id)
                        .Select(t => new InstrumentState(
                                t.Id,
                                states.GetValueOrDefault(t.Id)
                                ?? Convert.ToBase64String(
                                    Encoding.UTF8.GetBytes($"fake-state:{t.Instrument!.PluginId}")
                                )
                            )
                        )
                ]
            );
        }
    }

    RecordingFinishedMessage? Transport(TransportMessage message) {
        List<(EngineTrack Track, IReadOnlyList<RecordedPass> Passes)>? takes = null;
        List<RecordedMidiTake> midiTakes = [];
        string? directory;
        lock (gate) {
            var now = Position();
            switch (message.Action) {
                case TransportAction.Play:
                case TransportAction.Record:
                    takes = Finish(midiTakes);
                    EndPlayback();
                    anchorPosition = message.PositionSamples >= 0 ? message.PositionSamples : now;
                    anchorTimestamp = Stopwatch.GetTimestamp();
                    state = message.Action == TransportAction.Play
                        ? TelemetryTransportState.Playing
                        : TelemetryTransportState.Recording;
                    recordStart = anchorPosition;
                    if (state == TelemetryTransportState.Playing) {
                        (playStart, playTimestamp, playedFrames) = (anchorPosition, anchorTimestamp, -1);
                        heard.Clear();
                    }

                    break;
                case TransportAction.Stop:
                    takes = Finish(midiTakes);
                    EndPlayback();
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
            return midiTakes.Count > 0 ? new([], midiTakes) : null;
        }

        return new(Write(directory, takes), midiTakes.Count > 0 ? midiTakes : null);
    }

    /// <summary>Playback stops (or restarts): the Flashback window is what it played.</summary>
    void EndPlayback() {
        if (state == TelemetryTransportState.Playing && playStart >= 0) {
            playedFrames = Elapsed(playTimestamp);
        }
    }

    /// <summary>
    ///     Flashback Capture: what armed tracks heard since playback last started (up to now, or to where it stopped),
    ///     as takes at the positions they were played.
    /// </summary>
    RecordingFinishedMessage Capture() {
        List<(EngineTrack, IReadOnlyList<RecordedPass>)> takes;
        var midiTakes = new List<RecordedMidiTake>();
        string? directory;
        lock (gate) {
            var frames = state == TelemetryTransportState.Playing ? Elapsed(playTimestamp) : playedFrames;
            directory = recordDirectory;
            if (playStart < 0 || frames <= 0 || directory is null) {
                return new([], null, true);
            }

            frames = Math.Min(frames, 600L * SampleRate);
            var passes = Passes(playStart, frames, false);
            takes = [
                ..tracks.Values.Where(static t => t.RecordArmed && !IsMidi(t))
                    .OrderBy(static t => t.Id)
                    .Select(t => (t, passes))
            ];
            var end = playStart + Math.Min(frames, passes[0].Frames);
            foreach (var track in tracks.Values.Where(t => IsMidi(t) && (t.RecordArmed || t.Id == thru)).OrderBy(static t => t.Id)) {
                if (heard.GetValueOrDefault(track.Id) is { Count: > 0 } played) {
                    midiTakes.Add(new(track.Id, playStart, end, [..played]));
                } else if (track.RecordArmed) {
                    midiTakes.Add(new(track.Id, playStart, end, FakeNotes(playStart, end - playStart)));
                }
            }
        }

        return new(takes.Count > 0 ? Write(directory, takes) : [], midiTakes.Count > 0 ? midiTakes : null, true);
    }

    /// <summary>Writes each take's file (a quiet sine as long as its passes) and describes it.</summary>
    List<RecordedTake> Write(string directory, List<(EngineTrack Track, IReadOnlyList<RecordedPass> Passes)> takes) {
        Directory.CreateDirectory(directory);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        var recorded = new List<RecordedTake>();
        foreach (var (track, passes) in takes) {
            var channels = track.Stereo ? 2 : 1;
            var frames = passes.Sum(static p => p.Frames);
            var path = Path.Combine(directory, $"{track.Id}-{stamp}.wav");
            WriteTake(path, channels, frames);
            recorded.Add(new(track.Id, path, passes[0].StartSample, frames, channels, SampleRate, passes));
        }

        return recorded;
    }

    /// <summary>
    ///     The audio takes of the recording that ends now, if one was running, each with its passes; its MIDI takes go
    ///     to <paramref name="midiTakes" />.
    /// </summary>
    List<(EngineTrack, IReadOnlyList<RecordedPass>)>? Finish(List<RecordedMidiTake> midiTakes) {
        if (state != TelemetryTransportState.Recording) {
            return null;
        }

        var played = new Dictionary<int, List<RecordedMidiEvent>>(live);
        live.Clear();

        var elapsed = Math.Clamp(Elapsed(anchorTimestamp), 0, 600L * SampleRate);
        if (elapsed == 0) {
            return null;
        }

        var passes = Passes(recordStart, elapsed, punch.Enabled);
        // MIDI takes keep the first pass (MIDI cycle recording makes no take folders).
        var midiFrames = Math.Min(elapsed, cycle.Enabled && recordStart < cycle.EndSample ? cycle.EndSample - recordStart : elapsed);
        foreach (var track in tracks.Values.Where(static t => t.RecordArmed && IsMidi(t)).OrderBy(static t => t.Id)) {
            var events = played.GetValueOrDefault(track.Id) is { Count: > 0 } heardLive
                ? heardLive
                : FakeNotes(recordStart, midiFrames);
            midiTakes.Add(new(track.Id, recordStart, recordStart + midiFrames, events));
        }

        if (passes.Count == 0) {
            return null;
        }

        return [
            ..tracks.Values.Where(static t => t.RecordArmed && !IsMidi(t))
                .OrderBy(static t => t.Id)
                .Select(t => (t, (IReadOnlyList<RecordedPass>)passes))
        ];
    }

    /// <summary>
    ///     The passes of <paramref name="frames" /> frames played from <paramref name="start" />: one per cycle wrap
    ///     once the playhead is in the cycle, each clipped to the punch range with <paramref name="punched" />; file
    ///     offsets count only what was kept.
    /// </summary>
    List<RecordedPass> Passes(long start, long frames, bool punched) {
        var spans = new List<(long Start, long Frames)>();
        if (cycle.Enabled && cycle.EndSample > cycle.StartSample && start < cycle.EndSample) {
            var first = Math.Min(frames, cycle.EndSample - start);
            spans.Add((start, first));
            var length = cycle.EndSample - cycle.StartSample;
            for (var done = first; done < frames; done += length) {
                spans.Add((cycle.StartSample, Math.Min(length, frames - done)));
            }
        } else {
            spans.Add((start, frames));
        }

        var passes = new List<RecordedPass>();
        long offset = 0;
        foreach (var (from, count) in spans) {
            var (a, b) = (from, from + count);
            if (punched) {
                (a, b) = (Math.Max(a, punch.InSample), Math.Min(b, punch.OutSample));
            }

            if (b > a) {
                passes.Add(new(a, offset, b - a));
                offset += b - a;
            }
        }

        return passes;
    }

    long Elapsed(long since) => (long)(Stopwatch.GetElapsedTime(since).TotalSeconds * SampleRate);

    static bool IsMidi(EngineTrack track) => track.Instrument is not null || track.Midi is not null;

    /// <summary>A few notes for a MIDI take nobody played: an arpeggio, a note every quarter second, in the pass.</summary>
    List<RecordedMidiEvent> FakeNotes(long start, long frames) {
        var events = new List<RecordedMidiEvent>();
        var step = SampleRate / 4;
        int[] pitches = [60, 64, 67, 72];
        for (var i = 0; i < pitches.Length && (i + 1L) * step <= frames; i++) {
            var on = start + i * step;
            events.Add(new(on, 0x90, pitches[i], 100));
            events.Add(new(on + step * 4 / 5, 0x80, pitches[i], 0));
        }

        return events;
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

    static unsafe PlaybackEvent[] EventsOf(MappedSegment segment, int index) {
        var view = segment.Track(index);
        return [..new ReadOnlySpan<PlaybackEvent>(view.Events, (int)view.EventCount)];
    }

    static unsafe AudioPlaylistEntry[] PlaylistOf(MappedSegment segment, int index) {
        var view = segment.Track(index);
        return [..new ReadOnlySpan<AudioPlaylistEntry>(view.Playlist, (int)view.PlaylistCount)];
    }

    static string Name(IpcMessage message) {
        var name = message.GetType().Name.Replace("Message", string.Empty, StringComparison.Ordinal);
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}
