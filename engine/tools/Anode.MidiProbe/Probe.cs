using Anode.Audio;
using Anode.Audio.Midi;
using Anode.Audio.Native;
using Anode.Ipc;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Anode.MidiProbe;

/// <summary>A message the probe received: its host time (the packet's timestamp), the input's uid, and its bytes.</summary>
public sealed record Received(long TimeNs, string Port, byte[] Data);

/// <summary>
///     The probe (README.md): CoreMIDI through cth_audio (Anode's virtual ports, or any named pair), a recorder of
///     everything that arrives, scripted sends, MIDI Clock and MTC sources, control-surface helpers, and a Link peer.
///     <see cref="Handle" /> carries out one command and answers it; <see cref="Event" /> receives what the probe says
///     unasked (messages as they arrive, when watching).
/// </summary>
public sealed partial class Probe : IDisposable {
    readonly NativeMidiBackend midi = new();
    readonly Lock midiGate = new();
    readonly object recordGate = new(); // Monitor.Wait and PulseAll: a plain object
    readonly List<Received> received = [];
    readonly Thread reader;
    readonly byte[] sysex = new byte[16384];
    volatile bool running = true;
    IReadOnlyList<MidiPortInfo> ports = [];
    string? inPort; // the input the probe records from (null: every input)
    string? outPort; // the output it sends to
    bool watch;
    bool huiPing;
    readonly List<string> virtuals = [];

    public Probe() {
        if (!NativeMidiBackend.IsAvailable) {
            throw new InvalidOperationException("cth_audio has no MIDI backend here (build engine/native/build.sh).");
        }

        Rescan();
        reader = new(Read) { IsBackground = true, Name = "midiprobe reader" };
        reader.Start();
    }

    public static string Version => typeof(Probe).Assembly.GetName().Version?.ToString() ?? "0";

    /// <summary>Where unasked output goes (serve mode: a line on stdout).</summary>
    public Action<JsonNode>? Event { get; set; }

    /// <summary>Whether a command waits for something (serve mode runs it beside the others).</summary>
    public static bool Waits(JsonObject request) =>
        Text(request, "cmd") is "wait" or "linkJoin" or "open"
        || (Text(request, "cmd") == "script" && request["wait"]?.GetValue<bool>() != false);

    /// <summary>Carries out one command; the answer carries its <c>id</c>, <c>ok</c> and what it returns.</summary>
    public JsonObject Handle(JsonObject request) {
        JsonObject answer;
        try {
            answer = Text(request, "cmd") switch {
                "ports" => Ports(),
                "open" => Open(request),
                "virtual" => Virtual(request),
                "now" => new() { ["now"] = Now() },
                "send" => Send(request),
                "script" => Script(request),
                "watch" => Watch(request),
                "received" => ReceivedMessages(request),
                "clear" => Clear(),
                "wait" => Wait(request),
                "clock" => StartClock(request),
                "clockTempo" => ClockTempo(request),
                "clockStop" => StopClock(request),
                "mtc" => StartMtc(request),
                "mtcStop" => StopMtc(),
                "analyzeClock" => Analysis.Clock(Snapshot(request)),
                "analyzeMtc" => Analysis.Mtc(Snapshot(request), Rate(request)),
                "mcuFader" => McuFader(request),
                "mcuButton" => McuButton(request),
                "mcuVpot" => McuVpot(request),
                "mcuState" => Surfaces.McuState(Snapshot(request), request["extender"]?.GetValue<bool>() == true),
                "huiFader" => HuiFader(request),
                "huiPing" => HuiPing(request),
                "linkJoin" => LinkJoin(request),
                "linkTempo" => LinkTempo(request),
                "linkPlay" => LinkPlay(request),
                "linkState" => LinkState(request),
                "linkClockError" => LinkClockError(request),
                "linkLeave" => LinkLeave(),
                var other => throw new ArgumentException($"unknown command '{other}'")
            };
            answer["ok"] = true;
        } catch (Exception e) when (e is ArgumentException
                                        or InvalidOperationException
                                        or FormatException
                                        or TimeoutException
                                        or InvalidCastException
                                        or KeyNotFoundException
                                        or DllNotFoundException) {
            answer = new() { ["ok"] = false, ["error"] = e.Message };
        }

        if (request["id"] is { } id) {
            answer["id"] = id.DeepClone();
        }

        return answer;
    }

    // ---- Ports ---------------------------------------------------------------------------------------------------------

    void Rescan() {
        lock (midiGate) {
            midi.Open();
            ports = midi.Ports();
        }
    }

    JsonObject Ports() {
        Rescan();
        return new() { ["ports"] = new JsonArray([..ports.Select(PortJson)]) };
    }

    static JsonObject PortJson(MidiPortInfo p) =>
        new() { ["uid"] = p.Uid, ["name"] = p.Name, ["input"] = p.IsInput };

    /// <summary>The port named by uid or display name, inputs or outputs; null when none is listed.</summary>
    MidiPortInfo? Find(string name, bool input) =>
        ports.FirstOrDefault(p => p.IsInput == input && p.Uid == name)
        ?? ports.FirstOrDefault(p => p.IsInput == input && p.Name == name);

    /// <summary>
    ///     <c>open</c>: the pair the probe talks to: <c>in</c>, the input it records (default Anode's output,
    ///     <c>anode:out</c>), and <c>out</c>, the output it sends to (default Anode's input, <c>anode:in</c>); either by
    ///     uid or name. Waits up to <c>timeoutMs</c> (5000) for them to appear.
    /// </summary>
    JsonObject Open(JsonObject request) {
        var input = Text(request, "in") ?? "anode:out";
        var output = Text(request, "out") ?? "anode:in";
        var deadline = Environment.TickCount64 + Int(request, "timeoutMs", 5000);
        MidiPortInfo? i, o;
        while (true) {
            Rescan();
            i = Find(input, true);
            o = Find(output, false);
            if ((i is not null && o is not null) || Environment.TickCount64 > deadline) {
                break;
            }

            Thread.Sleep(50);
        }

        if (i is null || o is null) {
            throw new TimeoutException($"ports not found: {(i is null ? $"input '{input}' " : "")}{(o is null ? $"output '{output}'" : "")}");
        }

        inPort = i.Uid;
        outPort = o.Uid;
        return new() { ["in"] = PortJson(i), ["out"] = PortJson(o) };
    }

    /// <summary>
    ///     <c>virtual</c>: publishes the probe's own pair, "&lt;name&gt; In" (uid <c>&lt;prefix&gt;:in</c>, which Anode
    ///     sees as an output) and "&lt;name&gt; Out" (<c>&lt;prefix&gt;:out</c>, an input for Anode), and talks to
    ///     them: it records what Anode sends to its In and sends from its Out.
    /// </summary>
    JsonObject Virtual(JsonObject request) {
        var prefix = Text(request, "prefix") ?? "probe";
        var name = Text(request, "name") ?? "Anode Probe";
        lock (midiGate) {
            foreach (var (suffix, isInput) in new[] { ("in", true), ("out", false) }) {
                var uid = $"{prefix}:{suffix}";
                if (virtuals.Contains(uid)) {
                    continue;
                }

                var result = midi.OpenVirtual($"{name} {(isInput ? "In" : "Out")}", uid, isInput);
                if (result != 0) {
                    throw new InvalidOperationException(
                        result == IMidiBackend.VirtualUnsupported
                            ? "this MIDI backend has no virtual ports"
                            : $"the virtual port {uid} could not be opened: {NativeMidiBackend.LastError()}"
                    );
                }

                virtuals.Add(uid);
            }
        }

        Rescan();
        inPort = $"{prefix}:in";
        outPort = $"{prefix}:out";
        return new() {
            ["in"] = inPort,
            ["out"] = outPort,
            ["anodeInput"] = outPort, // what Anode names them by: it hears the probe's Out, sends to its In
            ["anodeOutput"] = inPort
        };
    }

    // ---- Receiving ------------------------------------------------------------------------------------------------------

    void Read() {
        Span<MidiInputMessage> batch = new MidiInputMessage[256];
        while (running) {
            int count;
            IReadOnlyList<MidiPortInfo> known;
            var sysexList = new List<Received>();
            lock (midiGate) {
                known = ports;
                count = midi.Read(batch);
                int length;
                while ((length = midi.ReadSystemExclusive(sysex, out var timeNs, out var source)) > 0) {
                    sysexList.Add(new(timeNs, PortOf(known, source), sysex.AsSpan(0, length).ToArray()));
                }
            }

            for (var i = 0; i < count; i++) {
                var m = batch[i];
                var data = new[] { m.Status, m.Data1, m.Data2 }.AsSpan(0, Length(m.Status)).ToArray();
                Take(new(m.TimeNs, PortOf(known, m.Source), data));
            }

            foreach (var s in sysexList) {
                Take(s);
            }

            if (count == 0 && sysexList.Count == 0) {
                Thread.Sleep(1);
            }
        }
    }

    static string PortOf(IReadOnlyList<MidiPortInfo> known, int source) =>
        (uint)source < (uint)known.Count ? known[source].Uid : "";

    void Take(Received message) {
        if (inPort is not null && message.Port != inPort) {
            return;
        }

        lock (recordGate) {
            received.Add(message);
            Monitor.PulseAll(recordGate);
        }

        if (huiPing && message.Data is [0x90, 0x00, 0x00]) {
            SendBytes([0x90, 0x00, 0x7F], 0); // HUI: the host's ping, answered
        }

        if (watch) {
            Event?.Invoke(new JsonObject { ["event"] = "midi", ["t"] = message.TimeNs, ["hex"] = Hex(message.Data) });
        }
    }

    JsonObject Watch(JsonObject request) {
        watch = request["on"]?.GetValue<bool>() != false;
        return [];
    }

    JsonObject Clear() {
        lock (recordGate) {
            received.Clear();
        }

        return [];
    }

    /// <summary>What was received since <c>since</c> (host ns), optionally only messages whose hex starts with <c>prefix</c>.</summary>
    List<Received> Snapshot(JsonObject request) {
        var since = Long(request, "since", long.MinValue);
        var prefix = Text(request, "prefix");
        lock (recordGate) {
            return [
                ..received.Where(r => r.TimeNs >= since && (prefix is null || Hex(r.Data).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            ];
        }
    }

    JsonObject ReceivedMessages(JsonObject request) {
        var list = Snapshot(request);
        if (request["clear"]?.GetValue<bool>() == true) {
            Clear();
        }

        return new() {
            ["count"] = list.Count,
            ["messages"] = new JsonArray([
                ..list.Select(static r => new JsonObject { ["t"] = r.TimeNs, ["hex"] = Hex(r.Data) })
            ])
        };
    }

    /// <summary><c>wait</c>: until <c>count</c> (1) messages (matching <c>prefix</c>, since <c>since</c>) are in.</summary>
    JsonObject Wait(JsonObject request) {
        var count = Int(request, "count", 1);
        var deadline = Environment.TickCount64 + Int(request, "timeoutMs", 5000);
        while (true) {
            var list = Snapshot(request);
            if (list.Count >= count) {
                return new() {
                    ["count"] = list.Count,
                    ["messages"] = new JsonArray([
                        ..list.Select(static r => new JsonObject { ["t"] = r.TimeNs, ["hex"] = Hex(r.Data) })
                    ])
                };
            }

            var left = deadline - Environment.TickCount64;
            if (left <= 0) {
                throw new TimeoutException($"{list.Count} of {count} messages arrived");
            }

            lock (recordGate) {
                Monitor.Wait(recordGate, (int)Math.Min(left, 100));
            }
        }
    }

    // ---- Sending --------------------------------------------------------------------------------------------------------

    void SendBytes(ReadOnlySpan<byte> data, long timeNs) {
        var port = outPort ?? throw new InvalidOperationException("no output: open or virtual first");
        bool sent;
        lock (midiGate) {
            sent = midi.Send(port, data, timeNs);
        }

        if (!sent) {
            throw new InvalidOperationException($"the output '{port}' did not take {Hex(data.ToArray())}");
        }
    }

    /// <summary><c>send</c>: <c>data</c> (bytes) or <c>hex</c> now, or at host time <c>at</c> (ns), or <c>inMs</c> from now.</summary>
    JsonObject Send(JsonObject request) {
        var data = Bytes(request);
        var at = request["at"] is not null ? Long(request, "at", 0)
            : request["inMs"] is not null ? Now() + (long)(Double(request, "inMs", 0) * 1e6)
            : 0;
        SendBytes(data, at);
        return new() { ["sent"] = data.Length, ["at"] = at == 0 ? Now() : at };
    }

    /// <summary>
    ///     <c>script</c>: <c>events</c>, each <c>{ "t": ms from the start, "data" | "hex" }</c>, sent timestamped from
    ///     <c>startInMs</c> (20) from now; with <c>wait</c> (true) it answers once the last is due.
    /// </summary>
    JsonObject Script(JsonObject request) {
        var events = request["events"] as JsonArray ?? throw new ArgumentException("script needs events");
        var start = Now() + (long)(Double(request, "startInMs", 20) * 1e6);
        var last = start;
        foreach (var e in events.OfType<JsonObject>()) {
            var at = start + (long)(Double(e, "t", 0) * 1e6);
            // CoreMIDI may deliver early with the timestamp; send each close to its time so the receiver sees it then.
            var lead = at - Now() - 2_000_000;
            if (lead > 0) {
                Thread.Sleep(TimeSpan.FromTicks(lead / 100));
            }

            SendBytes(Bytes(e), at);
            last = at;
        }

        return new() { ["start"] = start, ["end"] = last, ["sent"] = events.Count };
    }

    static long Now() => Realtime.NowNanoseconds();

    // ---- Helpers --------------------------------------------------------------------------------------------------------

    static int Length(byte status) =>
        status switch {
            < 0xF0 => (status & 0xF0) is 0xC0 or 0xD0 ? 2 : 3,
            0xF1 or 0xF3 => 2,
            0xF2 => 3,
            _ => 1
        };

    public static string Hex(byte[] data) => Convert.ToHexString(data);

    static string? Text(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static int Int(JsonObject o, string name, int fallback) => o[name] is JsonValue v ? v.GetValue<int>() : fallback;

    static long Long(JsonObject o, string name, long fallback) => o[name] is JsonValue v ? v.GetValue<long>() : fallback;

    static double Double(JsonObject o, string name, double fallback) =>
        o[name] is JsonValue v ? v.GetValue<double>() : fallback;

    static EngineMtcRate Rate(JsonObject o) =>
        Text(o, "rate") switch {
            "fps24" or "24" => EngineMtcRate.Fps24,
            "fps2997Drop" or "29.97" or "29.97df" => EngineMtcRate.Fps2997Drop,
            "fps30" or "30" => EngineMtcRate.Fps30,
            _ => EngineMtcRate.Fps25
        };

    /// <summary>A command's bytes: <c>data</c> (an array of numbers) or <c>hex</c> ("90 3C 64" or "903C64").</summary>
    static byte[] Bytes(JsonObject o) {
        if (o["data"] is JsonArray array) {
            return [..array.Select(static n => checked((byte)n!.GetValue<int>()))];
        }

        if (Text(o, "hex") is { } hex) {
            return Convert.FromHexString(hex.Replace(" ", "", StringComparison.Ordinal));
        }

        throw new ArgumentException("no data or hex");
    }

    static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    public void Dispose() {
        running = false;
        StopClockThread();
        StopMtcThread();
        reader.Join();
        link?.Dispose();
        midi.Dispose();
    }
}
