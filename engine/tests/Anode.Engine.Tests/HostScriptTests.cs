using Anode.Audio;
using Anode.EngineHost;
using Anode.Ipc;
using System.Net.Sockets;

namespace Anode.Engine.Tests;

/// <summary>
///     Phase 7's messages over the host's socket, in this process on the null device (its real-time clock, and the
///     control thread's timers): a script slot from a model load and <c>setScript</c> answering
///     <c>slotParameters</c>, its trace as <c>scriptLog</c> and its failure as <c>pluginProblem</c>;
///     <c>setInsertParameter</c> on a run-time parameter; MIDI step input forwarding (<c>setStepInputForward</c>,
///     <c>stepInputMidi</c>); and <c>previewRange</c>.
/// </summary>
public sealed class HostScriptTests : IAsyncLifetime {
    const int Rate = 48_000;
    readonly string dir = TestAudio.TempDirectory();
    readonly string socketPath = Path.Combine(Path.GetTempPath(), $"an-{Guid.NewGuid().ToString("n")[..8]}.sock");
    readonly List<IpcMessage> pushed = [];
    EngineHost.EngineHost? host;
    EngineHostServer? server;
    FakeMidi midi = null!; // the host owns and disposes it
    Socket? socket;
    NetworkStream? stream;

    static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() {
        midi = new();
        host = new(
            new NullAudioDevice(Rate, 64, 2, 2, true),
            "test",
            false,
            TestMidiEffects.Registry(),
            midi,
            [MidiFixtures.ClapFolder]
        );
        server = new(socketPath, host.HandleAsync);
        host.Push = server.Push;
        server.HelloAnswered = host.HelloAnswered;
        host.Start();
        server.Start();
        socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), Token);
        stream = new(socket);
    }

    public async ValueTask DisposeAsync() {
        if (stream is not null) {
            await stream.DisposeAsync();
        }

        socket?.Dispose();
        if (server is not null) {
            await server.DisposeAsync();
        }

        host?.Dispose();
        Directory.Delete(dir, true);
    }

    static bool IsPush(IpcMessage? message) =>
        message is PluginCatalogMessage
            or MidiPortsMessage
            or MidiActivityMessage
            or SlotParametersMessage
            or ScriptLogMessage
            or PluginProblemMessage
            or StepInputMidiMessage;

    /// <summary>The next reply, setting aside what the host pushes.</summary>
    async Task<IpcMessage?> Reply() {
        while (true) {
            var message = await IpcFraming.ReadAsync(stream!, Token).WaitAsync(TimeSpan.FromSeconds(5), Token);
            if (IsPush(message)) {
                lock (pushed) {
                    pushed.Add(message!);
                }

                continue;
            }

            return message;
        }
    }

    async Task<IpcMessage?> Request(IpcMessage message) {
        await IpcFraming.WriteAsync(stream!, message, Token);
        return await Reply();
    }

    /// <summary>Sends a message that may not answer; returns its reply if one comes, after a ping round trip.</summary>
    async Task<IpcMessage?> Send(IpcMessage message) {
        await IpcFraming.WriteAsync(stream!, message, Token);
        var next = await Request(new PingMessage(99));
        if (next is PongMessage) {
            return null;
        }

        Assert.IsType<PongMessage>(await Reply());
        return next;
    }

    /// <summary>Reads pushes until one matches (a ping keeps the stream moving).</summary>
    async Task<T> Pushed<T>(Func<T, bool>? match = null) where T : IpcMessage {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true) {
            lock (pushed) {
                if (pushed.OfType<T>().FirstOrDefault(m => match?.Invoke(m) ?? true) is { } found) {
                    return found;
                }
            }

            Assert.True(DateTime.UtcNow < deadline, $"no {typeof(T).Name} pushed");
            Assert.IsType<PongMessage>(await Request(new PingMessage(1)));
            await Task.Delay(10, Token);
        }
    }

    [Fact]
    public async Task Scripts_report_their_parameters_log_and_problems() {
        Assert.IsType<HelloMessage>(await Request(new HelloMessage("server")));
        var track = MidiFixtures.InstrumentTrack(1) with {
            Strip = TestAudio.Unity with {
                MidiEffects = [
                    new(
                        "s",
                        TestMidiEffects.Script,
                        false,
                        new Dictionary<string, double> { ["shift"] = 2 },
                        Source: "param shift 0\ntrace"
                    )
                ]
            }
        };
        using (var segment = TestAudio.Segment([new(1, [], [])], 1)) {
            Assert.IsType<ModelLoadedMessage>(await Request(TestAudio.Load(segment, 1, true, [track], [], dir)));
        }

        var loaded = await Pushed<SlotParametersMessage>();
        Assert.Equal((1, "s", "shift"), (loaded.TrackId, loaded.SlotId, Assert.Single(loaded.Parameters).Id));
        Assert.Null(await Send(new SetInsertParameterMessage(1, "s", "shift", 5)));
        Assert.IsType<ErrorMessage>(await Send(new SetInsertParameterMessage(1, "s", "nope", 5)));

        // Played notes are traced (60 + 5), and the log reaches the server.
        Assert.Null(await Send(new LiveMidiMessage(1, [new(0x90, 60, 100), new(0x80, 60, 0)])));
        var log = await Pushed<ScriptLogMessage>();
        Assert.Equal((1, "s", 0), (log.TrackId, log.SlotId, log.Dropped));
        Assert.Equal(["note 65"], log.Lines);

        // A new script answers with its parameters; one that stops while it runs is reported with its line.
        Assert.Null(await Send(new SetScriptMessage(1, "s", "param depth 0\ncrash 61")));
        await Pushed<SlotParametersMessage>(static m => m.Parameters is [{ Id: "depth" }]);
        Assert.Null(await Send(new LiveMidiMessage(1, [new(0x90, 61, 100)])));
        var problem = await Pushed<PluginProblemMessage>();
        Assert.Equal(
            (1, "s", "line 2: InternalError: interrupted"),
            (problem.TrackId, problem.InsertId, problem.Message)
        );
        var error = Assert.IsType<ErrorMessage>(await Send(new SetScriptMessage(1, "missing", "trace")));
        Assert.Equal("setScript", error.Request);
    }

    [Fact]
    public async Task Step_input_is_forwarded_while_on_and_a_range_previews() {
        Assert.IsType<HelloMessage>(await Request(new HelloMessage("server")));
        using (var segment = TestAudio.Segment([new(1, [], [])], 1)) {
            Assert.IsType<ModelLoadedMessage>(
                await Request(TestAudio.Load(segment, 1, true, [MidiFixtures.InstrumentTrack(1)], [], dir))
            );
        }

        Assert.Null(await Send(new SetMidiThruMessage(1)));
        Assert.Null(await Send(new SetStepInputForwardMessage(true)));
        midi.Play(0, 0x90, 60, 100);
        midi.Play(0, 0x90, 64, 120);
        midi.Play(0, 0x80, 60, 0);
        var step = await Pushed<StepInputMidiMessage>();
        Assert.Equal([60, 64], step.Pitches);
        Assert.Equal(120, step.Velocity);

        var file = TestAudio.Write24(dir, "a.wav", 2, Rate, 1);
        Assert.Null(await Send(new PreviewRangeMessage(file, 1_000, 2_000, true, 0.5f)));
        Assert.True(host!.Controller.Engine.Preview is { Loop: true, RangeStart: 1_000, RangeFrames: 1_000 });
        var refused = Assert.IsType<ErrorMessage>(await Send(new PreviewRangeMessage(file, 2_000, 1_000, false, 1)));
        Assert.Equal("previewRange", refused.Request);
        Assert.Null(await Send(new PreviewFileMessage(null, 1)));
        Assert.Null(host.Controller.Engine.Preview);
    }
}
