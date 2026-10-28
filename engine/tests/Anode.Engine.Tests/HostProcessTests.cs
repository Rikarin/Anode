using Anode.Ipc;
using Anode.Ipc.Segments;
using Anode.Ipc.Telemetry;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace Anode.Engine.Tests;

/// <summary>
///     anode-host as the desktop app runs it: a separate process on the null device, its readiness line, the socket,
///     the telemetry region in shared memory, the catalogue (with the test CLAP plugin on CLAP_PATH) and MIDI ports
///     after the hello, an instrument track recording live MIDI, a clean exit on SIGTERM and exit code 3 when the
///     device is lost.
/// </summary>
public sealed partial class HostProcessTests : IDisposable {
    const int Rate = 48_000;
    readonly StringBuilder output = new();
    readonly string dir = TestAudio.TempDirectory();
    readonly string socketPath = Path.Combine(Path.GetTempPath(), $"an-{Guid.NewGuid().ToString("n")[..8]}.sock");
    readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Process? host;

    static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() {
        if (host is { HasExited: false }) {
            host.Kill(true);
            host.WaitForExit();
        }

        host?.Dispose();
        File.Delete(socketPath);
        Directory.Delete(dir, true);
        lock (output) {
            TestContext.Current.TestOutputHelper?.WriteLine("anode-host output:\n" + output);
        }
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int pid, int signal);

    async Task StartAsync(params string[] extra) {
        var dll = Path.Combine(
            AppContext.BaseDirectory.Replace(
                Path.Combine("tests", "Anode.Engine.Tests"),
                Path.Combine("src", "Anode.Host"),
                StringComparison.Ordinal
            ),
            "anode-host.dll"
        );
        Assert.True(File.Exists(dll), $"anode-host not built at {dll}");
        host = new() {
            StartInfo = new(
                "dotnet",
                [
                    "exec", dll, "--serve", "--null-audio", "--socket", socketPath, "--data-dir",
                    Path.Combine(dir, "data"), ..extra
                ]
            ) {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                Environment = { ["CLAP_PATH"] = MidiFixtures.ClapFolder }
            }
        };
        host.OutputDataReceived += (_, e) => Capture(e.Data);
        host.ErrorDataReceived += (_, e) => Capture(e.Data);
        host.Start();
        host.BeginOutputReadLine();
        host.BeginErrorReadLine();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);
    }

    void Capture(string? line) {
        if (line is null) {
            return;
        }

        lock (output) {
            output.AppendLine(line);
        }

        if (line.Contains(" serving on ", StringComparison.Ordinal)) {
            ready.TrySetResult();
        }
    }

    static unsafe TelemetryHeader Header(SharedSegment region) => *(TelemetryHeader*)region.Pointer;

    [Fact]
    public async Task Serves_plays_records_and_exits_cleanly_on_sigterm() {
        await StartAsync("--input-tone");
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), Token);
        await using var stream = new NetworkStream(socket);

        var pushed = new List<IpcMessage>();

        // The next reply, setting aside what the host pushes unasked.
        async Task<IpcMessage?> Reply() {
            while (true) {
                var message = await IpcFraming.ReadAsync(stream, Token);
                if (message is PluginCatalogMessage or MidiPortsMessage or MidiActivityMessage) {
                    pushed.Add(message);
                    continue;
                }

                return message;
            }
        }

        async Task<IpcMessage?> Request(IpcMessage message) {
            await IpcFraming.WriteAsync(stream, message, Token);
            return await Reply();
        }

        var hello = Assert.IsType<HelloMessage>(await Request(new HelloMessage("test")));
        Assert.Equal(
            ("null", Rate, 64, 16, 2),
            (hello.Backend, hello.SampleRate, hello.BufferFrames, hello.InputChannels, hello.OutputChannels)
        );
        using var region = SharedSegment.Open(hello.TelemetryName!, TelemetryLayout.Size);

        var file = TestAudio.Write24(dir, "a.wav", 1, 4 * Rate, 1);
        using (var segment = TestAudio.Segment(
                   [new(1, [], [TestAudio.Region(0, 4 * Rate, 0)]), new(2, [], []), new(3, [], [])],
                   7
               )) {
            var loaded = await Request(
                TestAudio.Load(
                    segment,
                    7,
                    true,
                    [
                        TestAudio.Track(1), TestAudio.Track(2, input: 0, armed: true),
                        MidiFixtures.InstrumentTrack(
                            3,
                            new(MidiFixtures.ClapId, new Dictionary<string, double> { ["1"] = 0.7, ["7"] = 0 }),
                            true
                        )
                    ],
                    [new(0, file, 1, Rate, 4 * Rate)],
                    dir
                )
            );
            Assert.Equal(new ModelLoadedMessage(7), loaded);
        }

        // After the hello: the ports, and the catalogue with the CLAP plugin once the scan is done.
        Assert.Contains(pushed, static m => m is MidiPortsMessage);
        var catalog = Assert.IsType<PluginCatalogMessage>(Assert.Single(pushed, static m => m is PluginCatalogMessage));
        Assert.Contains(catalog.Plugins, static p => p is { Id: MidiFixtures.ClapId, Format: "clap" });

        await IpcFraming.WriteAsync(stream, new TransportMessage(TransportAction.Record, Rate), Token);
        for (var polls = 0; Header(region).PositionSamples < Rate + Rate / 2; polls++) {
            Assert.True(polls < 500, "the playhead did not move");
            await Task.Delay(20, Token);
        }

        Assert.Equal(TelemetryTransportState.Recording, Header(region).State);
        Assert.Equal(7, Header(region).ModelVersion);
        await IpcFraming.WriteAsync(stream, new LiveMidiMessage(3, [new(0x90, 60, 100)]), Token);
        await Task.Delay(100, Token);
        await IpcFraming.WriteAsync(stream, new LiveMidiMessage(3, [new(0x80, 60, 0)]), Token);
        Assert.IsType<PongMessage>(await Request(new PingMessage(1)));
        var states = Assert.IsType<InstrumentStatesMessage>(await Request(new RequestInstrumentStatesMessage()));
        Assert.Equal(3, Assert.Single(states.States).TrackId);
        Assert.NotNull(states.States[0].State);
        var finished = Assert.IsType<RecordingFinishedMessage>(
            await Request(new TransportMessage(TransportAction.Stop))
        );
        var take = Assert.Single(finished.Takes);
        Assert.Equal((2, Rate), (take.TrackId, take.StartSample));
        Assert.True(File.Exists(take.Path));
        var midi = Assert.Single(finished.MidiTakes!);
        Assert.Equal((3, Rate), (midi.TrackId, midi.StartSample));
        Assert.Equal([(0x90, 60), (0x80, 60)], midi.Events.Select(static e => (e.Status, e.Data1)));
        Assert.InRange(midi.Events[1].Sample - midi.Events[0].Sample, Rate / 20, Rate / 2); // about 100 ms apart
        Assert.Contains(pushed, static m => m is MidiActivityMessage { Events: [{ TrackId: 3 }, ..] });
        lock (output) {
            Assert.DoesNotContain("misbehaving", output.ToString(), StringComparison.Ordinal);
        }

        Assert.Equal(0, Kill(host!.Id, 15)); // SIGTERM
        await host.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(0, host.ExitCode);
        Assert.False(File.Exists(socketPath));
        Assert.Throws<IOException>(() => SharedSegment.Open(hello.TelemetryName!, TelemetryLayout.Size).Dispose());
    }

    [Fact]
    public async Task Exits_with_code_3_when_the_device_is_lost() {
        await StartAsync("--lose-device-after", "300");
        await host!.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(3, host.ExitCode);
        lock (output) {
            Assert.Contains("audio device was lost", output.ToString(), StringComparison.Ordinal);
        }
    }
}
