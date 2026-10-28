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
    readonly IReadOnlyList<string>? clapFolders;
    readonly Lock scanGate = new();
    readonly Lock pushGate = new();
    readonly Lock renderGate = new();
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
    public unsafe EngineHost(
        IAudioDevice device,
        string version,
        bool sharedTelemetry = true,
        PluginRegistry? plugins = null,
        IMidiBackend? midi = null,
        IReadOnlyList<string>? clapFolders = null
    ) {
        Device = device;
        this.clapFolders = clapFolders;
        var latency = device.Latency;
        Controller = new(
            new(device.SampleRate, device.BufferFrames, latency.Input + latency.Output, latency.Output),
            plugins,
            midi
        ) { Log = message => Log?.Invoke(message) };
        Controller.Plugins.Log = message => Log?.Invoke(message);
        Controller.Midi.PortsChanged = ports => Push?.Invoke(new MidiPortsMessage(ports));
        Controller.Midi.Activity = batch => Push?.Invoke(batch);
        Controller.ParameterChanged = changed => Push?.Invoke(changed);
        Controller.PluginProblem = problem => Push?.Invoke(problem);
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
        started = true;
        _ = ScanAsync(true);
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
                await Task.Run(() => Controller.Plugins.ScanAll(clapFolders)).ConfigureAwait(false);
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
    ///     <c>previewFile</c>, and <c>transport</c> unless a stop ends a recording pass, which answers
    ///     <see cref="RecordingFinishedMessage" />). <c>flashbackCapture</c> answers a
    ///     <see cref="RecordingFinishedMessage" /> with <c>Flashback</c> set (no takes when nothing was played).
    ///     <c>scanPlugins</c>
    ///     answers the new <see cref="PluginCatalogMessage" />, <c>requestInstrumentStates</c>
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
                case SetChannelMessage channel:
                    return Controller.SetChannel(channel.TrackId, channel.Strip)
                        ? null
                        : new ErrorMessage($"No track {channel.TrackId}.", "setChannel");
                case SetInsertParameterMessage parameter:
                    return Controller.SetInsertParameter(
                        parameter.TrackId,
                        parameter.InsertId,
                        parameter.ParameterId,
                        parameter.Value
                    )
                        ? null
                        : new ErrorMessage(
                            $"Track {parameter.TrackId} has no insert '{parameter.InsertId}' with a parameter "
                            + $"'{parameter.ParameterId}'.",
                            "setInsertParameter"
                        );
                case SetSendLevelMessage send:
                    return Controller.SetSendLevel(send.TrackId, send.SendId, send.Gain, send.Pan)
                        ? null
                        : new ErrorMessage($"Track {send.TrackId} has no send '{send.SendId}'.", "setSendLevel");
                case SetMixerMessage mixer:
                    Controller.SetMixer(mixer.Pdc, mixer.LowLatency, mixer.LowLatencyLimitMs);
                    return null;
                case RenderMessage render:
                    Render(render);
                    return null;
                case CancelRenderMessage cancel:
                    CancelRender(cancel.JobId);
                    return null;
                case OpenPluginEditorMessage editor:
                    return Controller.OpenPluginEditor(editor.TrackId, editor.InsertId, editor.Open)
                        ? null
                        : new ErrorMessage(
                            $"Track {editor.TrackId} has no plugin editor"
                            + (editor.InsertId is null ? "." : $" for insert '{editor.InsertId}'."),
                            "openPluginEditor"
                        );
                default:
                    return new ErrorMessage($"The host does not take '{Name(message)}' messages.", Name(message));
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
            _ => message.GetType().Name
        };

    public unsafe void Dispose() {
        lock (renderGate) {
            foreach (var cancel in renders.Values) {
                cancel.Cancel();
            }
        }

        telemetry.Dispose();
        if (started) {
            Device.Stop();
        }

        Controller.Dispose();
        Device.Dispose();
        sharedTelemetry?.Dispose();
        if (localTelemetry is not null) {
            NativeMemory.AlignedFree(localTelemetry);
        }
    }
}
