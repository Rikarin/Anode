using Anode.Audio;
using Anode.Audio.Midi;
using Anode.Audio.Native;
using Anode.Engine;
using Anode.Engine.Plugins;
using Anode.Ipc;
using Anode.Ipc.Segments;
using Anode.Ipc.Telemetry;
using System.Runtime.InteropServices;

namespace Anode.EngineHost;

/// <summary>
///     The engine as anode-host runs it: a device (or the null device) calling the <see cref="AudioEngine" />, the
///     controller, the disk, writer and MIDI threads, the plugin catalogue and the telemetry region the server reads.
///     <see cref="HandleAsync" /> answers every server → host message of <see cref="IpcMessage" />; what the host says
///     unasked (the catalogue and the MIDI ports after the hello and when they change, MIDI activity) goes through
///     <see cref="Push" />.
/// </summary>
public sealed class EngineHost : IDisposable {
    readonly SharedSegment? sharedTelemetry;
    readonly unsafe byte* localTelemetry;
    readonly TelemetryWriter telemetry;
    Timer? readings;
    Timer? syncTimer;
    SyncStateMessage? lastSync;
    long lastSyncPush;
    readonly IReadOnlyList<string>? clapFolders;
    readonly Lock scanGate = new();
    readonly Lock pushGate = new();
    readonly Lock renderGate = new();
    readonly IPluginFactory? factory;
    readonly Dictionary<int, CancellationTokenSource> renders = [];
    Task scanning = Task.CompletedTask;
    bool catalogReady;
    bool started;

    /// <param name="device">The opened (not started) device; the host starts and owns it.</param>
    /// <param name="version">The host's version, for the hello.</param>
    /// <param name="sharedTelemetry">
    ///     Put the telemetry region in shared memory and name it in the hello (anode-host); false keeps it in this
    ///     process (tests).
    /// </param>
    /// <param name="plugins">The instruments it can load (default: the built-ins, then the CLAP scan).</param>
    /// <param name="midi">The MIDI devices (null: none).</param>
    /// <param name="clapFolders">Where to scan for CLAP plugins (null: CLAP_PATH and the standard folders).</param>
    /// <param name="factory">
    ///     Third-party plugin hosting (anode-host: <see cref="SandboxPluginFactory" />). With one, CLAP plugins are
    ///     hosted (and scanned) by it, in sandboxes; without, the Phase 2 in-process CLAP host serves CLAP instruments.
    /// </param>
    /// <param name="virtualMidiPrefix">
    ///     With the native MIDI backend (or only its own ports, <see cref="OwnPortsMidiBackend" />), the virtual ports' uid
    ///     prefix (Phase 8; "anode": "Anode In" <c>anode:in</c> and
    ///     "Anode Out" <c>anode:out</c>); null publishes none.
    /// </param>
    public unsafe EngineHost(
        IAudioDevice device,
        string version,
        bool sharedTelemetry = true,
        PluginRegistry? plugins = null,
        IMidiBackend? midi = null,
        IReadOnlyList<string>? clapFolders = null,
        IPluginFactory? factory = null,
        string? virtualMidiPrefix = "anode"
    ) {
        Device = device;
        this.factory = factory;
        this.clapFolders = clapFolders;
        var latency = device.Latency;
        Controller = new(
            new(
                device.SampleRate,
                device.BufferFrames,
                latency.Input + latency.Output,
                latency.Output,
                VirtualMidiPrefix: midi is NativeMidiBackend or OwnPortsMidiBackend ? virtualMidiPrefix : null
            ),
            plugins,
            midi
        ) { Log = message => Log?.Invoke(message) };
        if (factory is not null) {
            Controller.Plugins.Factory = factory;
        }

        Controller.Plugins.Log = message => Log?.Invoke(message);
        Controller.Midi.PortsChanged = ports => Push?.Invoke(new MidiPortsMessage(ports));
        Controller.Midi.Activity = batch => Push?.Invoke(batch);
        Controller.ParameterChanged = changed => Push?.Invoke(changed);
        Controller.PluginProblem = problem => Push?.Invoke(problem);
        Controller.CellState = state => Push?.Invoke(state);
        Controller.SlotParameters = parameters => Push?.Invoke(parameters);
        Controller.ScriptLog = log => Push?.Invoke(log);
        Controller.Midi.StepInput = step => Push?.Invoke(step);
        Controller.Midi.ControlMidi = batch => Push?.Invoke(batch);
        string? telemetryName = null;
        byte* region;
        if (sharedTelemetry) {
            this.sharedTelemetry = SharedSegment.Create(
                SegmentWriter.DefaultBackend,
                SharedSegment.DefaultDirectory(),
                $"an-tl-{Environment.ProcessId}",
                Random.Shared.Next(1 << 16),
                TelemetryLayout.Size
            );
            region = this.sharedTelemetry.Pointer;
            telemetryName = this.sharedTelemetry.Name;
        } else {
            localTelemetry = (byte*)NativeMemory.AlignedAlloc((nuint)TelemetryLayout.Size, 64);
            region = localTelemetry;
        }

        TelemetryRegion = region;
        telemetry = new(Controller.Engine, region, () => device.Overloads);
        Hello = new(
            version,
            device.Backend,
            device.SampleRate,
            device.BufferFrames,
            device.InputChannels,
            device.OutputChannels,
            telemetryName
        );
    }

    public IAudioDevice Device { get; }

    public EngineController Controller { get; }

    /// <summary>The host's answer to the server's hello.</summary>
    public HelloMessage Hello { get; }

    /// <summary>The telemetry region (<see cref="TelemetryLayout.Size" /> bytes).</summary>
    public unsafe byte* TelemetryRegion { get; }

    /// <summary>Where the host reports what the server need not act on (missing files, dropped frames).</summary>
    public Action<string>? Log { get; set; }

    /// <summary>
    ///     Sends a message the host offers unasked to the connected server (<see cref="EngineHostServer.Push" />); any
    ///     thread.
    /// </summary>
    public Action<IpcMessage>? Push { get; set; }

    public AudioDeviceStatus DeviceStatus => Device.Status;

    /// <summary>Starts the disk and MIDI threads, the device, the telemetry writer and the first CLAP scan.</summary>
    public void Start() {
        Controller.Start();
        Device.Start(Controller.Engine);
        telemetry.Start();
        readings = new(_ => PushReadings(), null, ReadingsInterval, ReadingsInterval);
        syncTimer = new(_ => PushSyncState(false), null, SyncCheckInterval, SyncCheckInterval);
        started = true;
        _ = ScanAsync(true);
    }

    /// <summary>How often the inserts' readings are pushed (at most 15 times a second).</summary>
    static readonly TimeSpan ReadingsInterval = TimeSpan.FromSeconds(1.0 / 15);

    /// <summary>
    ///     How often the sync state is checked: pushed on a change (at most 10 a second), and every 250 ms while
    ///     external.
    /// </summary>
    static readonly TimeSpan SyncCheckInterval = TimeSpan.FromMilliseconds(100);

    const long SyncRepeatMs = 250;

    /// <summary>
    ///     Pushes the sync state when it changed (the external tempo by 0.01 bpm or more), or every 250 ms while the
    ///     source is external, once the server has sent its first <c>setSync</c>; <paramref name="force" /> (that
    ///     answer) records it as pushed without pushing. Returns the state.
    /// </summary>
    SyncStateMessage PushSyncState(bool force) {
        var state = Controller.SyncState();
        var now = Environment.TickCount64;
        bool push;
        lock (pushGate) {
            var previous = lastSync;
            if (previous is null && !force) {
                return state; // nothing is pushed before the server's first setSync
            }

            var changed = previous is null
                || previous.Source != state.Source
                || previous.Locked != state.Locked
                || previous.LinkPeers != state.LinkPeers
                || previous.ExternalTempo.HasValue != state.ExternalTempo.HasValue
                || Math.Abs((previous.ExternalTempo ?? 0) - (state.ExternalTempo ?? 0)) >= 0.01;
            push = force
                || changed
                || (state.Source != EngineSyncSource.Internal && now - lastSyncPush >= SyncRepeatMs);
            if (push) {
                lastSync = state;
                lastSyncPush = now;
            }
        }

        if (push && !force) {
            Push?.Invoke(state);
        }

        return state;
    }

    /// <summary>Pushes the inserts' readings when any insert has one.</summary>
    void PushReadings() {
        if (Push is not { } push) {
            return;
        }

        var list = Controller.InsertReadings();
        if (list.Count > 0) {
            push(new InsertReadingsMessage(list));
        }
    }

    /// <summary>
    ///     The server's hello was answered (<see cref="EngineHostServer.HelloAnswered" />): the catalogue (once the
    ///     first scan is done; until then, the scan pushes it) and the MIDI ports follow it.
    /// </summary>
    public void HelloAnswered() {
        lock (pushGate) { // with the scan's push: exactly one catalogue follows the hello
            if (catalogReady) {
                Push?.Invoke(Catalog());
            }
        }

        Push?.Invoke(new MidiPortsMessage(Controller.Midi.Ports));
    }

    /// <summary>The catalogue as the host reports it: built-ins first, then CLAP.</summary>
    public PluginCatalogMessage Catalog() => new(Controller.Plugins.Catalog());

    /// <summary>
    ///     Scans the CLAP folders, one scan at a time (a request during a scan scans again after it), and returns the
    ///     new catalogue; <paramref name="push" /> also pushes it to the server.
    /// </summary>
    public Task<PluginCatalogMessage> ScanAsync(bool push) {
        lock (scanGate) {
            Controller.Plugins.BeginScan(); // from now on, loading an unknown plugin waits for the scan
            var next = Scan(scanning);
            scanning = next;
            return next;
        }

        async Task<PluginCatalogMessage> Scan(Task before) {
            try {
                await before.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                await Task.Run(() => Controller.Plugins.ScanAll(clapFolders, factory is null)).ConfigureAwait(false);
            } finally {
                Controller.Plugins.EndScan();
            }

            var catalog = Catalog();
            lock (pushGate) {
                catalogReady = true;
                if (push) {
                    Push?.Invoke(catalog);
                }
            }

            return catalog;
        }
    }

    /// <summary>
    ///     Carries out one message from the server; returns the reply, or null when the message has none
    ///     (<c>setStrip</c>, <c>setTrackIo</c>, <c>setCycle</c>, <c>setMetronome</c>, <c>setInstrument</c>,
    ///     <c>setParameter</c>, <c>setMidiIo</c>, <c>setMidiThru</c>, <c>liveMidi</c>, <c>setPunch</c>,
    ///     <c>previewFile</c>, <c>previewRange</c>, <c>setPreviewSync</c>, <c>setScript</c>, <c>setStepInputForward</c>,
    ///     <c>launchCells</c>, <c>stopCells</c> (the launcher's state changes are pushed as
    ///     <see cref="CellStateMessage" />s), <c>setControlRouting</c>, <c>sendMidi</c> (unless no port took it: an
    ///     error), and <c>transport</c> unless a stop ends a recording pass, which answers
    ///     <see cref="RecordingFinishedMessage" />). <c>flashbackCapture</c> answers a
    ///     <see cref="RecordingFinishedMessage" /> with <c>Flashback</c> set (no takes when nothing was played).
    ///     <c>scanPlugins</c>
    ///     answers the new <see cref="PluginCatalogMessage" />, <c>setSync</c> the <see cref="SyncStateMessage" />
    ///     (which is also pushed when it changes, and every 250 ms while the source is external),
    ///     <c>requestInstrumentStates</c>
    ///     <see cref="InstrumentStatesMessage" />; a request it cannot carry out answers <see cref="ErrorMessage" />.
    /// </summary>
    public async ValueTask<IpcMessage?> HandleAsync(IpcMessage message, CancellationToken cancellationToken) {
        try {
            switch (message) {
                case HelloMessage:
                    return Hello;
                case PingMessage ping:
                    return new PongMessage(ping.Id);
                case LoadModelMessage load:
                    return new ModelLoadedMessage(Controller.LoadModel(load));
                case SetStripMessage strip:
                    return Controller.SetStrip(strip.TrackId, strip.Strip)
                        ? null
                        : new ErrorMessage($"No track {strip.TrackId}.", "setStrip");
                case SetTrackIoMessage io:
                    return Controller.SetTrackIo(io.TrackId, io.InputChannel, io.Stereo, io.RecordArmed, io.Monitoring)
                        ? null
                        : new ErrorMessage($"No track {io.TrackId}.", "setTrackIo");
                case SetCycleMessage cycle:
                    Controller.SetCycle(cycle.Enabled, cycle.StartSample, cycle.EndSample);
                    return null;
                case SetMetronomeMessage metronome:
                    Controller.SetMetronome(metronome.Enabled, metronome.Gain, metronome.CountIn);
                    return null;
                case TransportMessage transport:
                    return await TransportAsync(transport, cancellationToken).ConfigureAwait(false);
                case ScanPluginsMessage:
                    return await ScanAsync(false).WaitAsync(cancellationToken).ConfigureAwait(false);
                case SetInstrumentMessage set:
                    Controller.SetInstrument(set.TrackId, set.Instrument);
                    return null;
                case SetParameterMessage parameter:
                    return Controller.SetParameter(parameter.TrackId, parameter.ParameterId, parameter.Value)
                        ? null
                        : new ErrorMessage(
                            $"Track {parameter.TrackId} has no instrument parameter '{parameter.ParameterId}'.",
                            "setParameter"
                        );
                case SetMidiIoMessage io:
                    return Controller.SetMidiIo(io.TrackId, io.Midi)
                        ? null
                        : new ErrorMessage($"No track {io.TrackId}.", "setMidiIo");
                case SetMidiThruMessage thru:
                    Controller.SetMidiThru(thru.TrackId);
                    return null;
                case LiveMidiMessage live:
                    return Controller.LiveMidi(live.TrackId, live.Events)
                        ? null
                        : new ErrorMessage($"No track {live.TrackId}.", "liveMidi");
                case RequestInstrumentStatesMessage:
                    return new InstrumentStatesMessage(Controller.InstrumentStates());
                case SetPunchMessage punch:
                    Controller.SetPunch(punch.Enabled, punch.InSample, punch.OutSample);
                    return null;
                case FlashbackCaptureMessage:
                    var captured = await Task.Run(Controller.FlashbackCapture, cancellationToken).ConfigureAwait(false);
                    return new RecordingFinishedMessage(captured.Takes, captured.MidiTakes, true);
                case PreviewFileMessage preview:
                    Controller.Preview(preview.Path, preview.Gain);
                    return null;
                case SetPreviewSyncMessage sync:
                    Controller.SetPreviewSync(sync.TempoRatio, sync.TransposeSemitones, sync.StartOnBar);
                    return null;
                case LaunchCellsMessage launch:
                    Controller.LaunchCells(launch.Cells, launch.Stops);
                    return null;
                case StopCellsMessage stop:
                    Controller.StopCells(stop.Stops);
                    return null;
                default:
                    return Mixer(message, out var reply)
                        ? reply
                        : new ErrorMessage($"The host does not take '{Name(message)}' messages.", Name(message));
            }
        } catch (Exception e) when (e is IOException
                                        or InvalidDataException
                                        or InvalidOperationException
                                        or UnauthorizedAccessException
                                        or ArgumentException) {
            return new ErrorMessage(e.Message, Name(message));
        }
    }

    /// <summary>
    ///     The Phase 4 messages: channel structure, insert parameters, send levels, mixer settings, renders and plugin
    ///     editors; then Phase 7's. False when <paramref name="message" /> is none of them.
    /// </summary>
    bool Mixer(IpcMessage message, out IpcMessage? reply) {
        reply = null;
        switch (message) {
            case SetChannelMessage channel:
                reply = Refused(
                    Controller.SetChannel(channel.TrackId, channel.Strip),
                    $"No track {channel.TrackId}.",
                    "setChannel"
                );
                return true;
            case SetInsertParameterMessage p:
                reply = Refused(
                    Controller.SetInsertParameter(p.TrackId, p.InsertId, p.ParameterId, p.Value),
                    $"Track {p.TrackId} has no insert '{p.InsertId}' with a parameter '{p.ParameterId}'.",
                    "setInsertParameter"
                );
                return true;
            case SetSendLevelMessage send:
                reply = Refused(
                    Controller.SetSendLevel(send.TrackId, send.SendId, send.Gain, send.Pan),
                    $"Track {send.TrackId} has no send '{send.SendId}'.",
                    "setSendLevel"
                );
                return true;
            case SetMixerMessage mixer:
                Controller.SetMixer(mixer.Pdc, mixer.LowLatency, mixer.LowLatencyLimitMs);
                return true;
            case RenderMessage render:
                Render(render);
                return true;
            case CancelRenderMessage cancel:
                CancelRender(cancel.JobId);
                return true;
            case OpenPluginEditorMessage editor:
                reply = Refused(
                    Controller.OpenPluginEditor(editor.TrackId, editor.InsertId, editor.Open),
                    $"Track {editor.TrackId} has no plugin editor for {editor.InsertId ?? "its instrument"}.",
                    "openPluginEditor"
                );
                return true;
            default:
                return Phase7(message, out reply);
        }
    }

    /// <summary>
    ///     The Phase 7 messages: a script slot's source, MIDI step input forwarding and a file range's preview. False
    ///     when <paramref name="message" /> is none of them.
    /// </summary>
    bool Phase7(IpcMessage message, out IpcMessage? reply) {
        reply = null;
        switch (message) {
            case SetScriptMessage script:
                reply = Refused(
                    Controller.SetScript(script.TrackId, script.SlotId, script.Source),
                    $"Track {script.TrackId} has no script slot '{script.SlotId}'.",
                    "setScript"
                );
                return true;
            case SetStepInputForwardMessage forward:
                Controller.SetStepInputForward(forward.On);
                return true;
            case PreviewRangeMessage range:
                Controller.PreviewRange(range.Path, range.StartFrame, range.EndFrame, range.Loop, range.Gain);
                return true;
            default:
                return Phase8(message, out reply);
        }
    }

    /// <summary>
    ///     The Phase 8 messages: the control routing, a surface's feedback and the sync settings (answered with the sync
    ///     state). False when <paramref name="message" /> is none of them.
    /// </summary>
    bool Phase8(IpcMessage message, out IpcMessage? reply) {
        reply = null;
        switch (message) {
            case SetControlRoutingMessage routing:
                Controller.SetControlRouting(routing);
                return true;
            case SendMidiMessage send:
                reply = Refused(
                    Controller.SendMidi(send.Port, send.Data),
                    $"No MIDI output '{send.Port}' took the message.",
                    "sendMidi"
                );
                return true;
            case SetSyncMessage sync:
                Controller.SetSync(sync);
                reply = PushSyncState(true);
                return true;
            default:
                return false;
        }
    }

    static ErrorMessage? Refused(bool done, string error, string request) => done ? null : new(error, request);

    /// <summary>
    ///     Starts render job <paramref name="message" /> in the background: <see cref="RenderProgressMessage" />s, then
    ///     one <see cref="RenderFinishedMessage" /> (with an error when it failed or was cancelled), are pushed.
    /// </summary>
    void Render(RenderMessage message) {
        var cancel = new CancellationTokenSource();
        lock (renderGate) {
            if (!renders.TryAdd(message.JobId, cancel)) {
                cancel.Dispose();
                Push?.Invoke(
                    new RenderFinishedMessage(message.JobId, [], $"Job {message.JobId} is already rendering.")
                );
                return;
            }
        }

        _ = Task.Run(async () => {
                RenderFinishedMessage finished;
                try {
                    var files = await Controller.RenderAsync(
                            message,
                            fraction => Push?.Invoke(new RenderProgressMessage(message.JobId, fraction)),
                            cancel.Token
                        )
                        .ConfigureAwait(false);
                    finished = new(message.JobId, files);
                } catch (OperationCanceledException) {
                    finished = new(message.JobId, [], "cancelled");
                } catch (Exception e) when (e is IOException
                                                or InvalidOperationException
                                                or InvalidDataException
                                                or UnauthorizedAccessException
                                                or ArgumentException) {
                    finished = new(message.JobId, [], e.Message);
                } finally {
                    lock (renderGate) {
                        renders.Remove(message.JobId);
                    }

                    cancel.Dispose();
                }

                Push?.Invoke(finished);
            }
        );
    }

    void CancelRender(int jobId) {
        lock (renderGate) {
            if (renders.TryGetValue(jobId, out var cancel)) {
                cancel.Cancel();
            }
        }
    }

    async ValueTask<IpcMessage?> TransportAsync(TransportMessage message, CancellationToken cancellationToken) {
        switch (message.Action) {
            case TransportAction.Play:
                await Controller.PlayAsync(message.PositionSamples, cancellationToken).ConfigureAwait(false);
                return null;
            case TransportAction.Record:
                await Controller.RecordAsync(message.PositionSamples, cancellationToken).ConfigureAwait(false);
                return null;
            case TransportAction.Locate:
                if (message.PositionSamples < 0) {
                    return new ErrorMessage("Locate needs a position.", "transport");
                }

                Controller.Locate(message.PositionSamples);
                return null;
            default:
                var result = Controller.Stop();
                return result is null ? null : new RecordingFinishedMessage(result.Takes, result.MidiTakes);
        }
    }

    static string Name(IpcMessage message) =>
        message switch {
            HelloMessage => "hello",
            PingMessage => "ping",
            PongMessage => "pong",
            ErrorMessage => "error",
            LoadModelMessage => "loadModel",
            ModelLoadedMessage => "modelLoaded",
            SetStripMessage => "setStrip",
            SetTrackIoMessage => "setTrackIo",
            TransportMessage => "transport",
            SetCycleMessage => "setCycle",
            SetMetronomeMessage => "setMetronome",
            RecordingFinishedMessage => "recordingFinished",
            PluginCatalogMessage => "pluginCatalog",
            ScanPluginsMessage => "scanPlugins",
            MidiPortsMessage => "midiPorts",
            SetInstrumentMessage => "setInstrument",
            SetParameterMessage => "setParameter",
            SetMidiIoMessage => "setMidiIo",
            SetMidiThruMessage => "setMidiThru",
            LiveMidiMessage => "liveMidi",
            MidiActivityMessage => "midiActivity",
            RequestInstrumentStatesMessage => "requestInstrumentStates",
            InstrumentStatesMessage => "instrumentStates",
            SetPunchMessage => "setPunch",
            FlashbackCaptureMessage => "flashbackCapture",
            PreviewFileMessage => "previewFile",
            SetChannelMessage => "setChannel",
            SetInsertParameterMessage => "setInsertParameter",
            SetSendLevelMessage => "setSendLevel",
            SetMixerMessage => "setMixer",
            RenderMessage => "render",
            CancelRenderMessage => "cancelRender",
            RenderProgressMessage => "renderProgress",
            RenderFinishedMessage => "renderFinished",
            OpenPluginEditorMessage => "openPluginEditor",
            ParameterChangedMessage => "parameterChanged",
            PluginProblemMessage => "pluginProblem",
            InsertReadingsMessage => "insertReadings",
            LaunchCellsMessage => "launchCells",
            StopCellsMessage => "stopCells",
            CellStateMessage => "cellState",
            SetPreviewSyncMessage => "setPreviewSync",
            SlotParametersMessage => "slotParameters",
            ScriptLogMessage => "scriptLog",
            SetScriptMessage => "setScript",
            SetStepInputForwardMessage => "setStepInputForward",
            StepInputMidiMessage => "stepInputMidi",
            PreviewRangeMessage => "previewRange",
            SetControlRoutingMessage => "setControlRouting",
            ControlMidiMessage => "controlMidi",
            SendMidiMessage => "sendMidi",
            SetSyncMessage => "setSync",
            SyncStateMessage => "syncState",
            _ => message.GetType().Name
        };

    public unsafe void Dispose() {
        lock (renderGate) {
            foreach (var cancel in renders.Values) {
                cancel.Cancel();
            }
        }

        readings?.Dispose();
        syncTimer?.Dispose();
        telemetry.Dispose();
        if (started) {
            Device.Stop();
        }

        Controller.Dispose();
        (factory as IDisposable)?.Dispose();
        Device.Dispose();
        sharedTelemetry?.Dispose();
        if (localTelemetry is not null) {
            NativeMemory.AlignedFree(localTelemetry);
        }
    }
}
