using Anode.Audio.Native;
using Anode.EngineHost;
using Anode.Host;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

// Anode engine host: a NativeAOT executable. --serve runs the engine for anode-server over a Unix domain socket
// (docs/adr/0001-process-model.md); the other commands are diagnostics.

var version = typeof(EngineHostServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion
    ?? "dev";
var options = CommandLine.Parse(args);

try {
    return options.Command switch {
        "version" => PrintVersion(),
        "devices" => ListDevices(),
        "serve" => await ServeAsync().ConfigureAwait(false),
        _ => Usage()
    };
} catch (Exception e) when (e is InvalidOperationException or DllNotFoundException or IOException) {
    Console.Error.WriteLine($"error: {e.Message}");
    return 1;
}

int PrintVersion() {
    Console.WriteLine($"anode-host {version}");
    return 0;
}

int ListDevices() {
    var devices = NativeAudio.IsAvailable ? NativeAudio.Devices() : [];
    if (options.Flag("json")) {
        Console.WriteLine(JsonSerializer.Serialize(devices, HostJsonContext.Default.IReadOnlyListAudioDeviceInfo));
        return 0;
    }

    Console.WriteLine(
        $"backend: {(NativeAudio.IsLibraryPresent ? NativeAudio.Backend : "unavailable (build native/)")}"
    );
    foreach (var d in devices) {
        Console.WriteLine($"{d.Uid}  {d.Name}  in {d.InputChannels}  out {d.OutputChannels}  {d.SampleRate} Hz");
    }

    return 0;
}

async Task<int> ServeAsync() {
    var socket = options.Text("socket") ?? throw new InvalidOperationException("--serve needs --socket <path>.");
    var backend = options.Flag("null-audio") || !NativeAudio.IsLibraryPresent ? "null" : NativeAudio.Backend;
    using var stop = new CancellationTokenSource();
    using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop);
    using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop);
    if (options.Flag("watch-stdin")) {
        StdinWatch.Start(Console.OpenStandardInput(), () => stop.Cancel());
    }

    await using var server = new EngineHostServer(socket, version, backend);
    server.Start();
    // The desktop app waits for this line (apps/desktop/src/sidecars.ts).
    Console.WriteLine($"anode-host {version} ({backend}) serving on {socket}");
    try {
        await Task.Delay(Timeout.Infinite, stop.Token).ConfigureAwait(false);
    } catch (OperationCanceledException) {
        // SIGINT, SIGTERM or the lifeline
    }

    return 0;

    void Stop(PosixSignalContext context) {
        context.Cancel = true;
        stop.Cancel();
    }
}

int Usage() {
    Console.WriteLine(
        $"""
         anode-host {version}

           --serve --socket <path> [--null-audio] [--watch-stdin]
                                           run the engine for anode-server until SIGINT or SIGTERM
                                           (--watch-stdin: also stop when standard input closes)
           --devices [--json]              list audio devices
           --version
         """
    );
    // Plain usage (no arguments, or --help) succeeds; arguments that name no command are an error.
    return args.Length == 0 || options.Command == "help" ? 0 : 2;
}
