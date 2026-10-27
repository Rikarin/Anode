using Anode.HostProbe;
using System.Globalization;

// anode-host-probe: a tiny driver for anode-host.
//   smoke --socket <path> [--dir <dir>]      drive a running host over its socket: load, play, cycle, record
//   bench [--null] [--device <uid>] [--tracks 128] [--seconds 10] [--buffer 64]
//                                            measure the audio callback in-process on a device (muted) or the null clock

var command = args.Length > 0 ? args[0] : "help";

string? Option(string name) {
    var i = Array.IndexOf(args, "--" + name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

int Int(string name, int fallback) =>
    Option(name) is { } text ? int.Parse(text, CultureInfo.InvariantCulture) : fallback;

return command switch {
    "smoke" => await Smoke.RunAsync(
        Option("socket") ?? throw new ArgumentException("--socket <path>"),
        Option("dir") ?? Path.Combine(Path.GetTempPath(), "anode-probe")
    ),
    "bench" => await Bench.RunAsync(
        args.Contains("--null"),
        Option("device"),
        Int("tracks", 128),
        Int("seconds", 10),
        Int("buffer", 64)
    ),
    _ => Help()
};

static int Help() {
    Console.WriteLine(
        "anode-host-probe smoke --socket <path> [--dir <dir>] | bench [--null] [--device <uid>] [--tracks n] [--seconds s] [--buffer n]"
    );
    return 2;
}
