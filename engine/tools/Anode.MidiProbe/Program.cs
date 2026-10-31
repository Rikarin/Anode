using Anode.MidiProbe;
using System.Text.Json;
using System.Text.Json.Nodes;

// anode-midiprobe: MIDI hardware and a Link peer for Phase 8's tests (README.md has the commands).
//   anode-midiprobe [serve]                  commands on stdin, one JSON object a line; answers and events on stdout,
//                                            one JSON object a line (the mode Playwright drives)
//   anode-midiprobe run <commands.json> [--output <results.json>]
//                                            runs a JSON array of commands in order, then exits; writes the array of
//                                            answers to --output (else stdout)

var mode = args.Length > 0 ? args[0] : "serve";
using var probe = new Probe();
switch (mode) {
    case "serve":
        return await Serve(probe);
    case "run" when args.Length > 1:
        return Run(probe, args[1], Option("output"));
    default:
        Console.Error.WriteLine("usage: anode-midiprobe [serve] | run <commands.json> [--output <results.json>]");
        return 2;
}

string? Option(string name) {
    var i = Array.IndexOf(args, "--" + name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static async Task<int> Serve(Probe probe) {
    var output = Console.Out;
    var gate = new Lock();

    void Write(JsonNode node) {
        var line = node.ToJsonString();
        lock (gate) {
            output.WriteLine(line);
            output.Flush();
        }
    }

    probe.Event = Write;
    Write(new JsonObject { ["event"] = "ready", ["version"] = Probe.Version });
    while (await Console.In.ReadLineAsync().ConfigureAwait(false) is { } line) {
        if (string.IsNullOrWhiteSpace(line)) {
            continue;
        }

        JsonNode? command;
        try {
            command = JsonNode.Parse(line);
        } catch (JsonException e) {
            Write(new JsonObject { ["ok"] = false, ["error"] = $"not JSON: {e.Message}" });
            continue;
        }

        if (command is not JsonObject request) {
            Write(new JsonObject { ["ok"] = false, ["error"] = "a command is a JSON object" });
            continue;
        }

        if (request["cmd"]?.GetValue<string>() == "quit") {
            Write(new JsonObject { ["id"] = request["id"]?.DeepClone(), ["ok"] = true });
            break;
        }

        // Commands that wait (wait, a script that waits, linkJoin) run beside the reader, so later commands are not
        // held up; the rest run in order.
        if (Probe.Waits(request)) {
            _ = Task.Run(() => Write(probe.Handle(request)));
        } else {
            Write(probe.Handle(request));
        }
    }

    return 0;
}

static int Run(Probe probe, string path, string? output) {
    var commands = JsonNode.Parse(File.ReadAllText(path)) as JsonArray
                   ?? throw new InvalidDataException("run takes a JSON array of commands");
    var results = new JsonArray();
    foreach (var command in commands) {
        results.Add(command is JsonObject request ? probe.Handle(request) : new JsonObject { ["ok"] = false });
    }

    var text = results.ToJsonString(new() { WriteIndented = true });
    if (output is null) {
        Console.WriteLine(text);
    } else {
        File.WriteAllText(output, text);
    }

    return results.All(static r => r?["ok"]?.GetValue<bool>() == true) ? 0 : 1;
}
