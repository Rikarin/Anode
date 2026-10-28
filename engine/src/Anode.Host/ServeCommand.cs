using Anode.Audio;
using Anode.Audio.Midi;
using Anode.Audio.Native;
using Anode.EngineHost;
using System.Runtime.InteropServices;

namespace Anode.Host;

/// <summary>
///     The production mode: run the engine on a device (or the null device) and serve anode-server over a Unix domain
///     socket until SIGINT or SIGTERM, or with <c>--watch-stdin</c> until standard input closes
///     (docs/adr/0001-process-model.md). Exits with <see cref="DeviceExitCode" /> when the device is lost or changes.
/// </summary>
static class ServeCommand {
    /// <summary>Exit code when the audio device was lost or changed under the host (docs/adr/0002).</summary>
    public const int DeviceExitCode = 3;

    /// <summary>
    ///     The GC budget that keeps the host collection-free for hours (docs/adr/0001-process-model.md). NativeAOT
    ///     reads it from the environment only, as bare hex, so whoever launches the host must set it.
    /// </summary>
    const string Gen0Budget = "10000000"; // 256 MB

    public static async Task<int> RunAsync(CommandLine options, string version) {
        var socket = options.Text("socket") ?? throw new InvalidOperationException("--serve needs --socket <path>.");
        var rate = options.Int("rate", 48_000);
        var buffer = options.Int("buffer", 64);
        var inputs = Math.Clamp(options.Int("inputs", options.Flag("null-audio") ? 16 : 64), 0, 64);

        using var stop = new CancellationTokenSource();
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop);
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop);
        if (options.Flag("watch-stdin")) {
            StdinWatch.Start(Console.OpenStandardInput(), () => stop.Cancel()); // the launcher is gone
        }

        if (Environment.GetEnvironmentVariable("DOTNET_GCgen0size") is null) {
            Console.Error.WriteLine(
                $"warning: DOTNET_GCgen0size is not set; start the host with DOTNET_GCgen0size={Gen0Budget} so garbage "
                + "collections cannot interrupt playing"
            );
        }

        IAudioDevice device;
        if (options.Flag("null-audio")) {
            device = new NullAudioDevice(rate, buffer, inputs, 2, options.Flag("input-tone")) {
                // Testing aid: the null device reports itself lost after this many milliseconds.
                LoseAfter = options.Text("lose-device-after") is null
                    ? null
                    : TimeSpan.FromMilliseconds(options.Int("lose-device-after", 0))
            };
        } else {
            if (!NativeAudio.IsAvailable) {
                throw new InvalidOperationException(
                    $"No native audio backend on this platform (backend: {(NativeAudio.IsLibraryPresent ? NativeAudio.Backend : "not built")}). Use --null-audio."
                );
            }

            var session = NativeAudioSession.Open(options.Text("device"), rate, buffer, inputs);
            if (options.Flag("mute")) {
                session.OutputGain = 0f; // unattended runs: timing is identical, nothing is heard
            }

            device = session;
        }

        using var host = new EngineHost.EngineHost(device, version, midi: Midi(options));
        host.Log = Print;
        await using var server = new EngineHostServer(socket, host.HandleAsync, Print);
        host.Push = server.Push;
        server.HelloAnswered = host.HelloAnswered;
        host.Start();
        server.Start();
        var hello = host.Hello;
        // The desktop app waits for this line (apps/desktop/src/sidecars.ts).
        Console.WriteLine(
            $"anode-host {version} ({hello.Backend}, {hello.SampleRate / 1000.0:0.#} kHz, {hello.BufferFrames} frames, "
            + $"{hello.InputChannels} in / {hello.OutputChannels} out) serving on {socket}"
        );

        var exit = await WatchDeviceAsync(host, stop.Token).ConfigureAwait(false);
        Console.WriteLine("anode-host stopped");
        return exit;

        void Stop(PosixSignalContext context) {
            context.Cancel = true;
            stop.Cancel();
        }
    }

    /// <summary>
    ///     Polls the device four times a second until <paramref name="cancellationToken" /> ends serving (0), or the device
    ///     is lost or its settings change under the stream: then logs why and returns <see cref="DeviceExitCode" />, so
    ///     the supervisor (the desktop app) starts the host again on the device as it now is, and the server reloads it.
    /// </summary>
    static async Task<int> WatchDeviceAsync(EngineHost.EngineHost host, CancellationToken cancellationToken) {
        try {
            while (true) {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                var status = host.DeviceStatus;
                if (status == AudioDeviceStatus.Ok) {
                    continue;
                }

                Console.Error.WriteLine(
                    status == AudioDeviceStatus.Lost
                        ? "error: the audio device was lost (unplugged, taken away or no longer calling back); exiting "
                        + "so the host is restarted"
                        : "error: the audio device's sample rate, buffer size or default changed under the host; "
                        + "exiting so it is restarted with the new settings"
                );
                return DeviceExitCode;
            }
        } catch (OperationCanceledException) {
            return 0;
        }
    }

    /// <summary>
    ///     MIDI devices through the native shim, unless <c>--no-midi</c> (or the shim is not built); with
    ///     <c>--virtual-midi</c>, one virtual input and output instead (tests and machines without MIDI ports).
    /// </summary>
    static IMidiBackend? Midi(CommandLine options) =>
        options.Flag("virtual-midi") ? new VirtualMidiBackend()
        : !options.Flag("no-midi") && NativeMidiBackend.IsAvailable ? new NativeMidiBackend()
        : null;

    static void Print(string message) => Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");
}
