using Anode.Audio.Native;
using Anode.EngineHost;
using Anode.Host;
using System.Reflection;
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
        "serve" => await ServeCommand.RunAsync(options, version).ConfigureAwait(false),
        _ => Usage()
    };
} catch (Exception e) when (e is InvalidOperationException
                                or DllNotFoundException
                                or IOException
                                or ArgumentException
                                or System.Net.Sockets.SocketException) {
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

int Usage() {
    Console.WriteLine(
        $"""
         anode-host {version}

           --serve --socket <path> [--device <uid>] [--rate 48000] [--buffer 64] [--inputs <n>]
                   [--null-audio [--input-tone]] [--mute] [--watch-stdin]
                                           run the engine for anode-server until SIGINT or SIGTERM
                                           (--device: a device uid from --devices, default the system output;
                                           --inputs: device inputs to open, default all (16 on --null-audio);
                                           --input-tone: the null device's inputs carry a 440 Hz tone;
                                           --mute: silence the device output; --watch-stdin: also stop when
                                           standard input closes). Exit code 3: the device was lost or changed.
           --devices [--json]              list audio devices
           --version
         """
    );
    // Plain usage (no arguments, or --help) succeeds; arguments that name no command are an error.
    return args.Length == 0 || options.Command == "help" ? 0 : 2;
}
