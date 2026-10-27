using Anode.Audio;
using Anode.Audio.Native;
using Anode.Engine;
using Anode.Ipc;
using Anode.Ipc.Segments;
using Anode.Ipc.Telemetry;
using System.Runtime.InteropServices;

namespace Anode.EngineHost;

/// <summary>
///     The engine as anode-host runs it: a device (or the null device) calling the <see cref="AudioEngine" />, the
///     controller, the disk and writer threads, and the telemetry region the server reads. <see cref="HandleAsync" />
///     answers every server → host message of <see cref="IpcMessage" />.
/// </summary>
public sealed class EngineHost : IDisposable {
    readonly SharedSegment? sharedTelemetry;
    readonly unsafe byte* localTelemetry;
    readonly TelemetryWriter telemetry;
    bool started;

    /// <param name="device">The opened (not started) device; the host starts and owns it.</param>
    /// <param name="version">The host's version, for the hello.</param>
    /// <param name="sharedTelemetry">
    ///     Put the telemetry region in shared memory and name it in the hello (anode-host); false keeps it in this
    ///     process (tests).
    /// </param>
    public unsafe EngineHost(IAudioDevice device, string version, bool sharedTelemetry = true) {
        Device = device;
        var latency = device.Latency;
        Controller = new(new(device.SampleRate, device.BufferFrames, latency.Input + latency.Output)) {
            Log = message => Log?.Invoke(message)
        };
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

    public AudioDeviceStatus DeviceStatus => Device.Status;

    /// <summary>Starts the disk thread, the device and the telemetry writer.</summary>
    public void Start() {
        Controller.Start();
        Device.Start(Controller.Engine);
        telemetry.Start();
        started = true;
    }

    /// <summary>
    ///     Carries out one message from the server; returns the reply, or null when the message has none
    ///     (<c>setStrip</c>, <c>setTrackIo</c>, <c>setCycle</c>, <c>setMetronome</c>, and <c>transport</c> unless a
    ///     stop ends a recording pass, which answers <see cref="RecordingFinishedMessage" />).
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
                var takes = Controller.Stop();
                return takes.Count > 0 ? new RecordingFinishedMessage(takes) : null;
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
            _ => message.GetType().Name
        };

    public unsafe void Dispose() {
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
