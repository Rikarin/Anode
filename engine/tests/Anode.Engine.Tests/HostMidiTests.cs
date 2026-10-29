using Anode.Audio;
using Anode.EngineHost;
using Anode.Ipc;
using Anode.Ipc.Segments;
using System.Net.Sockets;

namespace Anode.Engine.Tests;

/// <summary>
///     The Phase 2 messages over the host's socket, in this process on the null device: the catalogue and the MIDI
///     ports after the hello and on changes, instruments, parameters, MIDI I/O, thru, live MIDI and its activity, and
///     instrument states.
/// </summary>
public sealed class HostMidiTests : IAsyncLifetime {
    const int Rate = 48_000;
    readonly string dir = TestAudio.TempDirectory();
    readonly string socketPath = Path.Combine(Path.GetTempPath(), $"an-{Guid.NewGuid().ToString("n")[..8]}.sock");
    readonly List<IpcMessage> pushed = [];
    readonly List<string> log = [];
    EngineHost.EngineHost? host;
    EngineHostServer? server;
    FakeMidi midi = null!; // the host owns and disposes it
    Socket? socket;
    NetworkStream? stream;

    EngineHost.EngineHost Host => host!;

    static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() {
        var (registry, _) = MidiFixtures.Registry();
        midi = new();
        host = new(new NullAudioDevice(Rate, 64, 2, 2), "test", false, registry, midi, [MidiFixtures.ClapFolder]);
        host.Log = message => {
            lock (log) {
                log.Add(message);
            }
        };
        server = new(socketPath, Host.HandleAsync);
        Host.Push = server.Push;
        server.HelloAnswered = Host.HelloAnswered;
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

    /// <summary>The next reply, setting aside what the host pushes.</summary>
    async Task<IpcMessage?> Reply() {
        while (true) {
            var message = await IpcFraming.ReadAsync(stream!, Token).WaitAsync(TimeSpan.FromSeconds(5), Token);
            if (message is PluginCatalogMessage or MidiPortsMessage or MidiActivityMessage) {
                lock (pushed) {
                    pushed.Add(message);
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

    async Task LoadAsync(params EngineTrack[] tracks) {
        using var segment = TestAudio.Segment([..tracks.Select(static t => new TrackModel(t.Id, [], []))], 1);
        Assert.IsType<ModelLoadedMessage>(await Request(TestAudio.Load(segment, 1, true, tracks, [], dir)));
    }

    [Fact]
    public async Task The_catalogue_and_ports_follow_the_hello_and_a_rescan_answers_the_catalogue() {
        Assert.IsType<HelloMessage>(await Request(new HelloMessage("server")));
        var catalog = await Pushed<PluginCatalogMessage>();
        // Instruments: built-ins first (Anode's, then the test's), then CLAP.
        Assert.Equal(
            [
                .. Anode.Dsp.Instruments.BuiltinInstruments.All.Select(static d => (d.Id, "builtin")),
                (TestInstrument.Id, "builtin"), (MidiFixtures.ClapId, "clap")
            ],
            catalog.Plugins.Where(static p => p.IsInstrument).Select(static p => (p.Id, p.Format))
        );
        // The built-in effects follow the built-in instruments (Phase 4).
        Assert.Contains(
            catalog.Plugins,
            static p => p is { Id: "anode.channel-eq", IsInstrument: false, Category: "EQ" }
        );
        var test = catalog.Plugins.Single(static p => p.Id == TestInstrument.Id);
        Assert.Equal([".txt"], test.FileTypes);
        Assert.Equal(["sine", "square", "saw"], test.Parameters[1].ValueNames);
        Assert.Equal(2, test.Parameters[1].Steps);
        var ports = await Pushed<MidiPortsMessage>();
        Assert.Equal(["in-a", "in-b", "out-a"], ports.Ports.Select(static p => p.Id));

        // scanPlugins answers with a new catalogue (set aside with the pushes, as the server reads it the same way).
        await IpcFraming.WriteAsync(stream!, new ScanPluginsMessage(), Token);
        var rescanned = await Pushed<PluginCatalogMessage>(m => !ReferenceEquals(m, catalog));
        Assert.Equal(catalog.Plugins.Select(static p => p.Id), rescanned.Plugins.Select(static p => p.Id));

        // A device plugged in: the ports again.
        midi.SetPorts([..midi.Ports(), new("in-c", "Pads", true)]);
        var replugged = await Pushed<MidiPortsMessage>(static m => m.Ports.Count == 4);
        Assert.Equal("in-c", replugged.Ports[3].Id);
    }

    [Fact]
    public async Task Instruments_parameters_midi_io_live_midi_and_states() {
        Assert.IsType<HelloMessage>(await Request(new HelloMessage("server")));
        await LoadAsync(MidiFixtures.InstrumentTrack(1), TestAudio.Track(2));
        Assert.Null(
            await Send(new SetInstrumentMessage(2, new(MidiFixtures.ClapId, new Dictionary<string, double>())))
        );
        Assert.IsType<Plugins.Clap.ClapInstrument>(Host.Controller.Engine.Root.Tracks[1].Instrument!.Instrument);
        Assert.Equal(
            "setInstrument",
            Assert.IsType<ErrorMessage>(await Send(new SetInstrumentMessage(1, MidiFixtures.Instrument("nope"))))
                .Request
        );
        Assert.Equal(
            "setInstrument",
            Assert.IsType<ErrorMessage>(await Send(new SetInstrumentMessage(9, MidiFixtures.Instrument()))).Request
        );
        Assert.Null(await Send(new SetInstrumentMessage(1, MidiFixtures.Instrument())));
        Assert.Null(await Send(new SetParameterMessage(2, "1", 0.25)));
        Assert.Equal(
            "setParameter",
            Assert.IsType<ErrorMessage>(await Send(new SetParameterMessage(2, "gain", 0.25))).Request
        );
        Assert.Null(await Send(new SetMidiIoMessage(1, new("in-b", 0, null, 1))));
        Assert.Equal(
            "setMidiIo",
            Assert.IsType<ErrorMessage>(await Send(new SetMidiIoMessage(9, new(null, 0, null, 1)))).Request
        );
        Assert.Equal(new("in-b", 0, null, 1), Host.Controller.Engine.Root.Tracks[0].MidiIo);
        Assert.Null(await Send(new SetMidiThruMessage(2)));

        // Live MIDI from the server and from a device, then the activity they make.
        Assert.Null(await Send(new LiveMidiMessage(1, [new(0x90, 60, 100)])));
        Assert.Equal(
            "liveMidi",
            Assert.IsType<ErrorMessage>(await Send(new LiveMidiMessage(9, [new(0x90, 60, 100)]))).Request
        );
        midi.Play(0, 0x90, 64, 90);
        var activity = await Pushed<MidiActivityMessage>(static m => m.Events.Any(static e => e.Device == "in-a"));
        Assert.Contains(new(2, "in-a", 0x90, 64, 90), activity.Events); // thru: nothing is armed
        lock (pushed) {
            Assert.Contains(
                pushed.OfType<MidiActivityMessage>().SelectMany(static m => m.Events),
                static e => e == new MidiActivityEntry(1, null, 0x90, 60, 100)
            );
        }

        var states = Assert.IsType<InstrumentStatesMessage>(await Request(new RequestInstrumentStatesMessage()));
        Assert.Equal([(1, false), (2, true)], states.States.Select(static s => (s.TrackId, s.State is not null)));
        lock (log) {
            Assert.DoesNotContain(log, static m => m.Contains("misbehaving", StringComparison.Ordinal));
        }
    }
}
