using Anode.Dsp.Effects;
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
///     recording; and it keeps the file the preview channel plays (<see cref="Preview" />). Phase 4: it lists the built-in
///     effects and a fake VST3 effect with an editor, keeps channel structure, insert parameters, send levels and the
///     mixer settings, reports insert states too, keeps which plugin editors are open, and renders: for
///     <see cref="RenderMessage" /> it writes 32-bit float stereo WAVs of a sine per track (<see cref="RenderFrequency" />,
///     at <see cref="RenderAmplitude" /> times the track's fader gain, its mute and solo heeded; summed and through the
///     master's gain for the mix; before the fader for a freeze), reporting progress, finishing, or stopping when
///     cancelled. <see cref="ChangeParameterAsync" /> and <see cref="ReportProblemAsync" /> play a plugin's own editor
///     and a crash for tests.
/// </summary>
public sealed partial class FakeHost : IAsyncDisposable {
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
    readonly Dictionary<(int Track, string Insert), string> insertStates = [];
    readonly Dictionary<int, CancellationTokenSource> renders = [];
    readonly HashSet<(int Track, string? Insert)> editors = [];
    readonly SemaphoreSlim writing;
    Stream? connection;
    SetMixerMessage mixer = new("all", false, 10);
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
        writing = new(1, 1);
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

    /// <summary>The fake VST3 effect in <see cref="Catalogue" />: a gain with its own editor and a sidechain.</summary>
    public const string FakeVst3Id = "org.anode.fake.gain";

    /// <summary>The level of a rendered track's sine at unity gain.</summary>
    public const float RenderAmplitude = 0.25f;

    /// <summary>The frequency of engine track <paramref name="trackId" />'s rendered sine: 220 Hz, then up a fifth per id.</summary>
    public static double RenderFrequency(int trackId) => 220 * Math.Pow(1.5, trackId % 6);

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
        ),
        ..BuiltinEffects.All.Select(EngineCatalog.From),
        new EnginePlugin(
            FakeVst3Id,
            "Fake Gain",
            "Anode tests",
            "0.1",
            "vst3",
            false,
            [],
            [new("0", "Gain", -24, 24, 0, "dB", 0), new("1", "Mix", 0, 1, 1, string.Empty, 0)],
            "Utility",
            true,
            true
        )
    ];

    /// <summary>The plugin editors open now: (engine track, insert id; null the instrument).</summary>
    public IReadOnlyCollection<(int Track, string? Insert)> Editors {
        get {
            lock (gate) {
                return [..editors];
            }
        }
    }

    /// <summary>The master strip as last set.</summary>
    public EngineStrip Master {
        get {
            lock (gate) {
                return master;
            }
        }
    }

    /// <summary>The mixer settings as last set.</summary>
    public SetMixerMessage Mixer {
        get {
            lock (gate) {
                return mixer;
            }
        }
    }

    /// <summary>Plays a plugin's own editor moving a parameter: the host tells the server.</summary>
    public Task ChangeParameterAsync(int trackId, string? insertId, string parameterId, double value) =>
        SendAsync(new ParameterChangedMessage(trackId, insertId, parameterId, value));

    /// <summary>Plays inserts reporting readings (a compressor's gain reduction).</summary>
    public Task ReportReadingsAsync(IReadOnlyList<InsertReadingEntry> readings) =>
        SendAsync(new InsertReadingsMessage(readings));

    /// <summary>Plays a plugin crashing: the host bypasses it and tells the server.</summary>
    public Task ReportProblemAsync(int trackId, string? insertId, string pluginId, string message) =>
        SendAsync(new PluginProblemMessage(trackId, insertId, pluginId, message));

    async Task SendAsync(IpcMessage message, CancellationToken cancellationToken = default) {
        var stream = Volatile.Read(ref connection) ?? throw new InvalidOperationException("No server is connected.");
        await writing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            await IpcFraming.WriteAsync(stream, message, cancellationToken).ConfigureAwait(false);
        } finally {
            writing.Release();
        }
    }

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

    /// <summary>The playlist of engine track <paramref name="trackId" /> in the loaded model.</summary>
    public AudioPlaylistEntry[] PlaylistOf(int trackId) {
        lock (gate) {
            return [..playlists.GetValueOrDefault(trackId) ?? []];
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
        Volatile.Write(ref connection, stream);
        try {
            while (await IpcFraming.ReadAsync(stream, token).ConfigureAwait(false) is { } message) {
                Log(message);
                foreach (var reply in Handle(message)) {
                    await writing.WaitAsync(token).ConfigureAwait(false);
                    try {
                        await IpcFraming.WriteAsync(stream, reply, token).ConfigureAwait(false);
                    } finally {
                        writing.Release();
                    }
                }
            }
        } catch (Exception e) when (e is OperationCanceledException
                                        or IOException
                                        or InvalidDataException
                                        or ObjectDisposedException) {
            // The server went away (or this host is stopping).
        } finally {
            Interlocked.CompareExchange(ref connection, null, stream);
            lock (gate) {
                foreach (var render in renders.Values) {
                    render.Cancel();
                }
            }
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
        writing.Dispose();
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
                    SetStripOf(strip.TrackId, _ => strip.Strip);
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
                if (!HandleMixer(message)) {
                    yield return new ErrorMessage($"Unsupported message {message.GetType().Name}.", Name(message));
                }

                break;
        }
    }

    /// <summary>The mixer's messages (Phase 4), none of which answers; false for any other message.</summary>
    bool HandleMixer(IpcMessage message) {
        switch (message) {
            case SetChannelMessage channel:
                lock (gate) {
                    SetStripOf(channel.TrackId, _ => channel.Strip);
                    RememberInserts(channel.TrackId, channel.Strip);
                }

                break;
            case SetInsertParameterMessage parameter:
                lock (gate) {
                    SetStripOf(
                        parameter.TrackId,
                        strip => strip with {
                            Inserts = [
                                ..(strip.Inserts ?? []).Select(i => i.Id != parameter.InsertId
                                        ? i
                                        : i with {
                                            Parameters = new Dictionary<string, double>(i.Parameters) {
                                                [parameter.ParameterId] = parameter.Value
                                            }
                                        }
                                )
                            ]
                        }
                    );
                }

                break;
            case SetSendLevelMessage send:
                lock (gate) {
                    SetStripOf(
                        send.TrackId,
                        strip => strip with {
                            Sends = [
                                ..(strip.Sends ?? []).Select(x => x.Id == send.SendId
                                        ? x with { Gain = send.Gain, Pan = send.Pan }
                                        : x
                                )
                            ]
                        }
                    );
                }

                break;
            case SetMixerMessage set:
                lock (gate) {
                    mixer = set;
                }

                break;
            case OpenPluginEditorMessage editor:
                lock (gate) {
                    if (editor.Open) {
                        editors.Add((editor.TrackId, editor.InsertId));
                    } else {
                        editors.Remove((editor.TrackId, editor.InsertId));
                    }
                }

                break;
            case RenderMessage render:
                StartRender(render);
                break;
            case CancelRenderMessage cancel:
                lock (gate) {
                    if (renders.TryGetValue(cancel.JobId, out var running)) {
                        running.Cancel();
                    }
                }

                break;
            default:
                return false;
        }

        return true;
    }

    IpcMessage Load(LoadModelMessage load) {
        var loaded = new Dictionary<int, AudioPlaylistEntry[]>();
        var loadedEvents = new Dictionary<int, PlaybackEvent[]>();
        var loadedWarps = new Dictionary<int, (WarpPoint[][] Warps, PitchPoint[][] Pitches)>();
        try {
            using var mapped = MappedSegment.Map(load.Segment.Version, load.Segment.Name, load.Segment.Length);
            mapped.ValidateLayout();
            mapped.Verify();
            for (var i = 0; i < TrackCount(mapped); i++) {
                loaded[mapped.TrackId(i)] = PlaylistOf(mapped, i);
                loadedEvents[mapped.TrackId(i)] = EventsOf(mapped, i);
                loadedWarps[mapped.TrackId(i)] = WarpsOf(mapped, i);
            }
        } catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) {
            return new ErrorMessage(e.Message, "loadModel");
        }

        lock (gate) {
            if (load.Replace) {
                playlists.Clear();
                warps.Clear();
                pitches.Clear();
                events.Clear();
                tracks.Clear();
            }

            foreach (var (id, entries) in loaded) {
                playlists[id] = entries;
                (warps[id], pitches[id]) = loadedWarps[id];
                events[id] = loadedEvents[id];
            }

            foreach (var track in load.Tracks) {
                tracks[track.Id] = track;
                if (track.Instrument is { State: not null } || !states.ContainsKey(track.Id)) {
                    Remember(track.Id, track.Instrument);
                }

                RememberInserts(track.Id, track.Strip);
            }

            RememberInserts(-1, load.Master);

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

    /// <summary>The states inserts were given (new instances); the others keep theirs.</summary>
    void RememberInserts(int trackId, EngineStrip strip) {
        foreach (var insert in strip.Inserts ?? []) {
            if (insert.State is { } state) {
                insertStates[(trackId, insert.Id)] = state;
            }
        }
    }

    void SetStripOf(int trackId, Func<EngineStrip, EngineStrip> change) {
        if (trackId == -1) {
            master = change(master);
        } else if (tracks.TryGetValue(trackId, out var t)) {
            tracks[trackId] = t with { Strip = change(t.Strip) };
        }
    }

    void StartRender(RenderMessage render) {
        var cancel = new CancellationTokenSource();
        lock (gate) {
            renders[render.JobId] = cancel;
        }

        _ = Task.Run(
            async () => {
                RenderFinishedMessage finished;
                try {
                    finished = new(render.JobId, await RenderAsync(render, cancel.Token).ConfigureAwait(false));
                } catch (OperationCanceledException) {
                    finished = new(render.JobId, [], "Cancelled.");
                } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                    finished = new(render.JobId, [], e.Message);
                } finally {
                    lock (gate) {
                        renders.Remove(render.JobId);
                    }

                    cancel.Dispose();
                }

                try {
                    await SendAsync(finished).ConfigureAwait(false);
                } catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException) {
                    // the server went away
                }
            },
            CancellationToken.None
        );
    }

    /// <summary>Writes the render's files: a sine per track (see the class summary), in tenths, reporting each.</summary>
    async Task<IReadOnlyList<string>> RenderAsync(RenderMessage render, CancellationToken token) {
        var (outputs, masterGain) = Outputs(render);
        var written = new List<string>();
        foreach (var (path, sources) in outputs) {
            await WriteRenderAsync(render, path, sources, masterGain, (written.Count, outputs.Count), token)
                .ConfigureAwait(false);
            written.Add(path);
        }

        return written;
    }

    /// <summary>The files a render writes and the tracks each sums (with their gains), and the gain over them.</summary>
    (List<(string Path, (EngineTrack Track, float Gain)[] Sources)> Outputs, float MasterGain) Outputs(
        RenderMessage render
    ) {
        lock (gate) {
            var soloed = tracks.Values.Any(static t => t.Strip.Solo);
            if (render.Tracks is null) {
                return ([(render.Path, [..tracks.Values.OrderBy(static t => t.Id).Select(t => (t, Gain(t)))])],
                    master.Mute ? 0 : master.Gain);
            }

            var single = IsSingleFile(render);
            return ([
                ..render.Tracks.Where(tracks.ContainsKey)
                    .Select(id => (single ? render.Path : Path.Combine(render.Path, $"{id}.wav"),
                            new[] { (tracks[id], Gain(tracks[id])) })
                    )
            ], 1);

            float Gain(EngineTrack t) {
                if (render.Freeze) {
                    return render.IncludeVolumePan ? t.Strip.Gain : 1;
                }

                return t.Strip.Mute || (soloed && !t.Strip.Solo) ? 0 : t.Strip.Gain;
            }
        }
    }

    /// <summary>
    ///     Whether a render writes its path itself: a freeze of one track to a .wav does; stems, and a bounce in place
    ///     (a freeze of several tracks, or to a folder), write <c>&lt;path&gt;/&lt;track&gt;.wav</c> each.
    /// </summary>
    static bool IsSingleFile(RenderMessage render) =>
        render is { Freeze: true, Tracks.Count: 1 } && Path.GetExtension(render.Path) is ".wav" or ".WAV";

    /// <summary>Writes one render file, reporting progress every tenth (as part <paramref name="part" /> of all).</summary>
    async Task WriteRenderAsync(
        RenderMessage render,
        string path,
        (EngineTrack Track, float Gain)[] sources,
        float masterGain,
        (int Index, int Count) part,
        CancellationToken token
    ) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var writer = new WavWriter(path, 2, SampleRate, WavEncoding.Float32);
        var frames = Math.Max(0, render.EndSample - render.StartSample);
        var block = new float[RenderBlock * 2];
        var (left, right) = (new float[RenderBlock], new float[RenderBlock]);
        var audio = sources.Select(s => AudioOf(s.Track.Id)).ToArray();
        var tenth = Math.Max(1, frames / 10);
        long reported = 0;
        try {
            for (long done = 0; done < frames;) {
                token.ThrowIfCancellationRequested();
                var n = (int)Math.Min(RenderBlock, frames - done);
                var at = render.StartSample + done;
                Mix(sources, audio, at, left.AsSpan(0, n), right.AsSpan(0, n));
                for (var i = 0; i < n; i++) {
                    block[2 * i] = left[i] * masterGain;
                    block[2 * i + 1] = right[i] * masterGain;
                }

                writer.Write(block.AsSpan(0, n * 2));
                done += n;
                if (done / tenth > reported) {
                    reported = done / tenth;
                    var fraction = (part.Index + (double)done / frames) / part.Count;
                    await SendAsync(new RenderProgressMessage(render.JobId, fraction), token).ConfigureAwait(false);
                    await Task.Delay(1, token).ConfigureAwait(false);
                }
            }
        } finally {
            foreach (var playlist in audio) {
                playlist?.Dispose();
            }
        }
    }

    /// <summary>The sources over samples <paramref name="at" />…: each its playlist, or its sine without one.</summary>
    void Mix(
        (EngineTrack Track, float Gain)[] sources,
        TrackAudio?[] audio,
        long at,
        Span<float> left,
        Span<float> right
    ) {
        left.Clear();
        right.Clear();
        var (l, r) = (new float[left.Length], new float[left.Length]);
        for (var s = 0; s < sources.Length; s++) {
            var (track, gain) = sources[s];
            if (audio[s] is not { } playlist) {
                for (var i = 0; i < left.Length; i++) {
                    var x = (float)Sine(track, gain, at + i);
                    left[i] += x;
                    right[i] += x;
                }

                continue;
            }

            Array.Clear(l);
            Array.Clear(r);
            playlist.Render(at, l, r);
            for (var i = 0; i < left.Length; i++) {
                left[i] += l[i] * gain;
                right[i] += r[i] * gain;
            }
        }
    }

    /// <summary>A track's sine at project sample <paramref name="at" />.</summary>
    double Sine(EngineTrack track, float gain, long at) =>
        RenderAmplitude * gain * Math.Sin(2 * Math.PI * RenderFrequency(track.Id) * at / SampleRate);

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

    /// <summary>
    ///     Every instrument's and insert's state: the one it was given, else a made-up one naming its plugin (and the
    ///     insert).
    /// </summary>
    InstrumentStatesMessage States() {
        lock (gate) {
            var all = new List<InstrumentState>();
            foreach (var t in tracks.Values.OrderBy(static t => t.Id)) {
                if (t.Instrument is { } instrument) {
                    all.Add(new(t.Id, states.GetValueOrDefault(t.Id) ?? Fake($"fake-state:{instrument.PluginId}")));
                }

                all.AddRange(InsertStates(t.Id, t.Strip));
            }

            all.AddRange(InsertStates(-1, master));
            return new(all);
        }

        IEnumerable<InstrumentState> InsertStates(int trackId, EngineStrip strip) =>
            (strip.Inserts ?? []).Select(i => new InstrumentState(
                    trackId,
                    insertStates.GetValueOrDefault((trackId, i.Id)) ?? Fake($"fake-state:{i.PluginId}:{i.Id}"),
                    i.Id
                )
            );

        static string Fake(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
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
            foreach (var track in tracks.Values.Where(t => IsMidi(t) && (t.RecordArmed || t.Id == thru))
                         .OrderBy(static t => t.Id)) {
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
        var midiFrames = Math.Min(
            elapsed,
            cycle.Enabled && recordStart < cycle.EndSample ? cycle.EndSample - recordStart : elapsed
        );
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
