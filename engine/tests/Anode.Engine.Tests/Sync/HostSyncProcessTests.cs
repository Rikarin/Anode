using Anode.Audio.Native;
using Anode.Ipc;
using Anode.Ipc.Segments;
using Anode.Ipc.Telemetry;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace Anode.Engine.Tests;

/// <summary>
///     Phase 8 through the real host (docs/09-phase-8.md, "Integration" 3), with anode-midiprobe on the host's virtual
///     ports and in the Link session: the control routing (a matcher's messages and a control port's SysEx arrive as
///     <c>controlMidi</c>), a surface's feedback (<c>sendMidi</c>), and <c>setSync</c> with its <c>syncState</c>: MIDI
///     Clock and MTC out (spacing, Start/Stop, the pointer and full frame on a locate), MIDI Clock in (tempo, start,
///     stop), MTC in (the chase), and Link (tempo both ways, the clock on the session's beat). Skipped without
///     CoreMIDI.
/// </summary>
public sealed partial class HostProcessTests {
    readonly ConcurrentQueue<IpcMessage> pushes = new();

    sealed class Connection(NetworkStream stream, ConcurrentQueue<IpcMessage> pushes) {
        long pings;

        /// <summary>Sends a request and returns its reply of type <typeparamref name="T" />; everything else is a push.</summary>
        public async Task<T> Request<T>(IpcMessage message) where T : IpcMessage {
            await IpcFraming.WriteAsync(stream, message, Token);
            while (true) {
                var next = await IpcFraming.ReadAsync(stream, Token);
                switch (next) {
                    case T reply:
                        return reply;
                    case ErrorMessage error:
                        Assert.Fail($"{message.GetType().Name}: {error.Message}");
                        break;
                    default:
                        pushes.Enqueue(next!);
                        break;
                }
            }
        }

        /// <summary>Sends a message that has no reply, and waits until the host has handled it.</summary>
        public async Task Send(IpcMessage message) {
            await IpcFraming.WriteAsync(stream, message, Token);
            var id = Interlocked.Increment(ref pings);
            while (await Request<PongMessage>(new PingMessage(id)) is var pong && pong.Id != id) { }
        }

        /// <summary>Reads pushes for up to <paramref name="milliseconds" /> until one satisfies <paramref name="match" />.</summary>
        public async Task<T> Push<T>(Func<T, bool> match, int milliseconds = 5_000) where T : IpcMessage {
            var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
            while (DateTime.UtcNow < deadline) {
                while (pushes.TryDequeue(out var queued)) {
                    if (queued is T t && match(t)) {
                        return t;
                    }
                }

                await Send(new PingMessage(0)); // reads what arrived meanwhile into the queue
                await Task.Delay(20, Token);
            }

            Assert.Fail($"no {typeof(T).Name} as expected within {milliseconds} ms");
            return null!;
        }
    }

    static async Task Until(Func<bool> condition, string what, int milliseconds = 5_000) {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!condition()) {
            Assert.True(DateTime.UtcNow < deadline, what);
            await Task.Delay(20, Token);
        }
    }

    static JsonObject Cmd(string cmd, params (string Name, JsonNode? Value)[] fields) {
        var o = new JsonObject { ["cmd"] = cmd };
        foreach (var (name, value) in fields) {
            o[name] = value;
        }

        return o;
    }

    [Fact]
    public async Task Routes_control_midi_sends_feedback_and_syncs_through_the_virtual_ports() {
        Assert.SkipUnless(ProbeProcess.Supported, "no CoreMIDI here");
        var prefix = $"h{System.Environment.ProcessId}x{Random.Shared.Next(1000)}";
        await StartAsync("--midi-port-prefix", prefix);
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), Token);
        await using var stream = new NetworkStream(socket);
        var host = new Connection(stream, pushes);
        var hello = await host.Request<HelloMessage>(new HelloMessage("test"));
        using var region = SharedSegment.Open(hello.TelemetryName!, TelemetryLayout.Size);
        using (var segment = TestAudio.Segment([new(1, [], [])], 1)) {
            await host.Request<ModelLoadedMessage>(TestAudio.Load(segment, 1, true, [TestAudio.Track(1)], [], dir));
        }

        await host.Push<MidiPortsMessage>(p => p.Ports.Any(port => port.Id == $"{prefix}:in" && port.Input), 10_000);
        await using var probe = await ProbeProcess.StartAsync(Token);
        await probe.Command(Cmd("open", ("in", $"{prefix}:out"), ("out", $"{prefix}:in"), ("timeoutMs", 10_000)));

        // ---- Routing: a matcher's messages go to the server, the rest play; a control port's SysEx goes too.
        await host.Send(new SetControlRoutingMessage([], [new(null, 0xB0, -1, 21)], false));
        await probe.Command(Cmd("send", ("hex", "B01540")));
        await probe.Command(Cmd("send", ("hex", "B01640")));
        var control = await host.Push<ControlMidiMessage>(static m => m.Events.Any(static e => e.Data is [
                    0xB0, 21, 0x40
                ]
            )
        );
        Assert.All(control.Events, e => Assert.Equal($"{prefix}:in", e.Port));
        Assert.DoesNotContain(control.Events, static e => e.Data is [0xB0, 22, _]);
        await host.Send(new SetControlRoutingMessage([$"{prefix}:in"], [], false));
        await probe.Command(Cmd("send", ("hex", "F00000661401F7")));
        await host.Push<ControlMidiMessage>(static m => m.Events.Any(static e =>
                Convert.ToHexString(e.Data) == "F00000661401F7"
            )
        );
        await host.Send(new SetControlRoutingMessage([], [], false));

        // ---- Surface feedback: SysEx and a fader, sent at once.
        await probe.Received(true);
        await host.Send(
            new SendMidiMessage(
                $"{prefix}:out",
                [0xF0, 0x00, 0x00, 0x66, 0x14, 0x12, 0x00, 0x41, 0xF7, 0xE0, 0x00, 0x40]
            )
        );
        var feedback = await probe.Command(Cmd("wait", ("count", 2)));
        Assert.Equal(
            ["E00040", "F000006614120041F7"],
            feedback["messages"]!.AsArray()
                .Select(static m => m!["hex"]!.GetValue<string>())
                .Order(StringComparer.Ordinal)
        );

        // ---- MIDI Clock and MTC out at 120 bpm: even, with Start and Stop; a locate sends the pointer and a full frame.
        var state = await host.Request<SyncStateMessage>(
            new SetSyncMessage(
                EngineSyncSource.Internal,
                [new($"{prefix}:out", true, true, 0)],
                EngineMtcRate.Fps25,
                0,
                false,
                null,
                null
            )
        );
        Assert.Equal(new(EngineSyncSource.Internal, false, null, 0), state);
        await probe.Received(true);
        await host.Send(new TransportMessage(TransportAction.Play, 0));
        await Task.Delay(3_000, Token);
        await host.Send(new TransportMessage(TransportAction.Stop)); // no reply: not recording
        await Task.Delay(200, Token);
        var clock = await probe.Command(Cmd("analyzeClock"));
        TestContext.Current.TestOutputHelper?.WriteLine($"clock out: {clock.ToJsonString()}");
        Assert.InRange(clock["bpm"]!.GetValue<double>(), 119, 121);
        // The null device on a busy machine now and then wakes a whole period late and realigns (its audio stalls, and
        // the clock with it): an interval or two may be off by that; the rest are within half a millisecond.
        if (System.Environment.GetEnvironmentVariable("CI") is null) { // a shared runner's timers (#22)
            Assert.InRange(clock["bpm"]!.GetValue<double>(), 119.9, 120.1);
            Assert.True(clock["outliers"]!.GetValue<int>() <= 2, $"clock: {clock.ToJsonString()}");
            Assert.True(
                clock["p99DeviationMs"]!.GetValue<double>() < 0.5,
                $"clock jitter {clock["p99DeviationMs"]} ms"
            );
        }

        Assert.Equal(
            ["start", "stop"],
            clock["transport"]!.AsArray().Select(static t => t!["kind"]!.GetValue<string>())
        );
        var mtc = await probe.Command(Cmd("analyzeMtc", ("rate", "fps25")));
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"mtc out: drift {mtc["maxDriftFrames"]} frames over {mtc["count"]} positions"
        );
        Assert.True(mtc["count"]!.GetValue<int>() > 30);
        Assert.True(mtc["maxDriftFrames"]!.GetValue<double>() < 1);
        Assert.InRange(mtc["positions"]![0]!["seconds"]!.GetValue<double>(), 0.06, 0.12); // 1¾ frames after the start
        await probe.Received(true);
        await host.Send(new TransportMessage(TransportAction.Locate, 2 * Rate + Rate / 4)); // 2.25 s: 4.5 beats
        var located = await probe.Command(Cmd("wait", ("count", 2)));
        Assert.Equal( // 18 sixteenths; 00:00:02:06 at 25 fps
            ["F07F7F010120000206F7", "F21200"],
            located["messages"]!.AsArray()
                .Select(static m => m!["hex"]!.GetValue<string>())
                .Order(StringComparer.Ordinal)
        );

        // ---- MIDI Clock in at 100 bpm with Start: the host follows its tempo and start, and stops on Stop.
        await host.Request<SyncStateMessage>(
            new SetSyncMessage(EngineSyncSource.MidiClock, [], EngineMtcRate.Fps25, 0, false, $"{prefix}:in", null)
        );
        await host.Send(new TransportMessage(TransportAction.Locate, 0));
        await probe.Command(Cmd("clock", ("bpm", 100)));
        var following = await host.Push<SyncStateMessage>(
            static s => s is { Locked: true, ExternalTempo: > 99.5 and < 100.5 },
            10_000
        );
        TestContext.Current.TestOutputHelper?.WriteLine($"clock in: {following}");
        await Until(() => Header(region).State == TelemetryTransportState.Playing, "the clock's Start did not play");
        await probe.Command(Cmd("clockStop"));
        await Until(() => Header(region).State == TelemetryTransportState.Stopped, "the clock's Stop did not stop");

        // ---- MTC in from 00:00:10:00: the host chases to the position within a frame and plays; it stops with it.
        await host.Request<SyncStateMessage>(
            new SetSyncMessage(EngineSyncSource.Mtc, [], EngineMtcRate.Fps25, 0, false, null, null)
        );
        var started = await probe.Command(Cmd("mtc", ("rate", "fps25"), ("from", "00:00:10:00")));
        var mtcStart = started["start"]!.GetValue<long>();
        var worst = double.MaxValue;
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < deadline) {
            var header = Header(region);
            if (header.State == TelemetryTransportState.Playing && header.HostTimeNs > mtcStart) {
                var expected = 10 + (header.HostTimeNs - mtcStart) / 1e9;
                worst = Math.Abs(header.PositionSamples / (double)Rate - expected);
                if (worst < 1 / 25.0 && DateTime.UtcNow > deadline.AddSeconds(-1.5)) {
                    break;
                }
            }

            await Task.Delay(50, Token);
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"mtc in: {worst * 1000:0.0} ms off the timecode");
        if (System.Environment.GetEnvironmentVariable("CI") is null) { // a shared runner's timers (#22)
            Assert.True(worst < 1 / 25.0, $"the chase is {worst * 25:0.00} frames off");
        }

        Assert.True((await host.Push<SyncStateMessage>(static s => s.Source == EngineSyncSource.Mtc)).Locked);
        await probe.Command(Cmd("mtcStop"));
        await Until(
            () => Header(region).State == TelemetryTransportState.Stopped,
            "the host did not stop with the timecode"
        );

        // ---- Link: the probe joins at 128; the host takes it, plays on the session's beat, and its tempo goes back.
        if (!NativeLink.IsAvailable) {
            return;
        }

        await host.Request<SyncStateMessage>(
            new SetSyncMessage(
                EngineSyncSource.Link,
                [new($"{prefix}:out", true, false, 0)],
                EngineMtcRate.Fps25,
                0,
                false,
                null,
                null
            )
        );
        var joined = await probe.Command(Cmd("linkJoin", ("bpm", 128), ("timeoutMs", 10_000)));
        Assert.SkipWhen(joined["peers"]!.GetValue<int>() == 0, "the Link peers did not find each other");
        var link = await host.Push<SyncStateMessage>(
            static s => s is { LinkPeers: 1, ExternalTempo: > 127.99 and < 128.01 },
            10_000
        );
        Assert.True(link.Locked);
        await host.Send(new TransportMessage(TransportAction.Locate, 0));
        await probe.Received(true);
        await host.Send(new TransportMessage(TransportAction.Play, 0));
        await Task.Delay(5_000, Token); // up to a bar's wait for the phase, then a few bars
        var error = await probe.Command(Cmd("linkClockError", ("quantum", 4)));
        TestContext.Current.TestOutputHelper?.WriteLine($"link: {error.ToJsonString()}");
        Assert.True(error["clocks"]!.GetValue<int>() > 100);
        if (System.Environment.GetEnvironmentVariable("CI") is null) {
            Assert.True(
                error["maxAbsMs"]!.GetValue<double>() < 1,
                $"the beat is {error["maxAbsMs"]} ms off the session's"
            );
        }

        await host.Send(new TransportMessage(TransportAction.Stop));

        using (var segment = TestAudio.Segment([new(1, [], [])], 2, tempo: [TestAudio.Tempo(0, 0, 100)])) {
            await host.Request<ModelLoadedMessage>(TestAudio.Load(segment, 2, true, [TestAudio.Track(1)], [], dir));
        }

        var tempo = 0.0;
        for (var tries = 0; tries < 100 && tempo is < 99.99 or > 100.01; tries++) {
            await Task.Delay(50, Token);
            tempo = (await probe.Command(Cmd("linkState")))["tempo"]!.GetValue<double>();
        }

        Assert.Equal(100, tempo, 2); // Anode's tempo reached the session
    }
}
