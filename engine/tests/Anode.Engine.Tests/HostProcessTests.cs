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
            ("null", Rate, 64, 16, 16),
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

    /// <summary>
    ///     Phase 10 through the real host (docs/11-phase-10.md): the hello names the master tap ring, which fills while
    ///     setMasterTap has it on, with a stamp per block; a 7.1.4 master loads, plays to the device through the
    ///     monitoring's speaker map and bounces to a WAVE_FORMAT_EXTENSIBLE file (or one per speaker); binaural
    ///     monitoring without an HRTF file is refused. Until workstream D's DSP lands the host pans by label (it logs
    ///     what it cannot make), so the mono track's signal is in the centre.
    /// </summary>
    [Fact]
    public async Task Serves_a_surround_master_with_its_tap_monitoring_and_bounces() {
        await StartAsync();
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), Token);
        await using var stream = new NetworkStream(socket);

        async Task<IpcMessage?> Next() {
            while (true) {
                var message = await IpcFraming.ReadAsync(stream, Token);
                if (message is not (PluginCatalogMessage
                    or MidiPortsMessage
                    or MidiActivityMessage
                    or RenderProgressMessage)) {
                    return message;
                }
            }
        }

        await IpcFraming.WriteAsync(stream, new HelloMessage("test"), Token);
        var hello = Assert.IsType<HelloMessage>(await Next());
        Assert.Equal(16, hello.OutputChannels);
        Assert.NotNull(hello.MasterTapName);
        using var region = SharedSegment.Open(hello.TelemetryName!, TelemetryLayout.Size);
        using var tap = SharedSegment.Open(hello.MasterTapName!, MasterTapLayout.Size);
        unsafe {
            var header = (MasterTapHeader*)tap.Pointer;
            Assert.Equal(
                (MasterTapHeader.MagicValue, (ushort)5, Rate),
                (header->Magic, header->Format, header->SampleRate)
            );
        }

        const EngineChannelFormat f714 = EngineChannelFormat.Surround714;
        var file = TestAudio.Write24(dir, "a.wav", 1, 2 * Rate, 1);
        using (var segment = TestAudio.Segment([new(1, [], [TestAudio.Region(0, 2 * Rate, 0)])], 2)) {
            await IpcFraming.WriteAsync(
                stream,
                TestAudio.Load(
                    segment,
                    2,
                    true,
                    [
                        TestAudio.Track(
                            1,
                            false,
                            MultichannelTests.Strip(outputFormat: f714, panner: MultichannelTests.Surround(0))
                        )
                    ],
                    [new(0, file, 1, Rate, 2 * Rate)],
                    dir,
                    MultichannelTests.Strip(input: f714, outputFormat: f714)
                ),
                Token
            );
            Assert.Equal(new ModelLoadedMessage(2), await Next());
        }

        var refused = Assert.IsType<ErrorMessage>(await Request(new SetMonitoringMessage(true, null, [])));
        Assert.Contains("HRTF", refused.Message, StringComparison.Ordinal);
        await IpcFraming.WriteAsync(stream, new SetMonitoringMessage(false, null, [-1, -1, 13]), Token);
        await IpcFraming.WriteAsync(stream, new SetMasterTapMessage(true), Token);
        Assert.IsType<PongMessage>(await Request(new PingMessage(2)));
        await IpcFraming.WriteAsync(stream, new TransportMessage(TransportAction.Play, 0), Token);
        for (var polls = 0; Header(region).PositionSamples < Rate / 2; polls++) {
            Assert.True(polls < 500, "the playhead did not move");
            await Task.Delay(20, Token);
        }

        unsafe {
            var stamps = new List<TapStamp>();
            var (count, written) = MasterTapWriter.Read(tap.Pointer, 0, [], stamps);
            Assert.True(written > Rate / 4, $"{written} frames tapped");
            Assert.Equal(written, count * 64); // a stamp per 64-frame block
            Assert.Contains(stamps, static s => (s.Flags & TapStamp.FlagPlaying) != 0 && s.TransportSample > 0);
        }

        await IpcFraming.WriteAsync(stream, new SetMasterTapMessage(false), Token);
        await IpcFraming.WriteAsync(stream, new TransportMessage(TransportAction.Stop), Token);
        Assert.IsType<PongMessage>(await Request(new PingMessage(3)));

        foreach (var (job, split) in new[] { (11, false), (12, true) }) {
            await IpcFraming.WriteAsync(
                stream,
                new RenderMessage(job, 0, Rate / 2, Path.Combine(dir, $"mix{job}.wav"), SplitChannels: split),
                Token
            );
            RenderFinishedMessage finished;
            while (true) {
                if (await Next() is RenderFinishedMessage f) {
                    finished = f;
                    break;
                }
            }

            Assert.Null(finished.Error);
            if (split) {
                Assert.Equal(12, finished.Files.Count);
                var (centre, channels, _) = MultichannelTests.Wav.Read(finished.Files[2]);
                Assert.EndsWith(".M+000.wav", finished.Files[2], StringComparison.Ordinal);
                Assert.Equal(1, channels);
                Assert.Equal(TestAudio.Sample(1_000, 1), centre[1_000]);
            } else {
                var (data, channels, mask) = MultichannelTests.Wav.Read(Assert.Single(finished.Files));
                Assert.Equal((12, 0x2D63Fu), (channels, mask));
                Assert.Equal(TestAudio.Sample(1_000, 1), data[1_000 * 12 + 2]); // the centre, third in both orders
                Assert.Equal(0f, data[1_000 * 12]);
            }
        }

        return;

        async Task<IpcMessage?> Request(IpcMessage message) {
            await IpcFraming.WriteAsync(stream, message, Token);
            return await Next();
        }
    }

    /// <summary>
    ///     Phase 5 through the real host: a format-3 segment with a warped region (its warp points in the segment) loads,
    ///     plays, and bounces in place over the socket, with the region's onsets where its warp map puts them.
    /// </summary>
    [Fact]
    public async Task Loads_plays_and_bounces_a_warped_region() {
        await StartAsync();
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), Token);
        await using var stream = new NetworkStream(socket);

        // The next message that is not one the host pushes unasked.
        async Task<IpcMessage?> Next() {
            while (true) {
                var message = await IpcFraming.ReadAsync(stream, Token);
                if (message is not (PluginCatalogMessage
                    or MidiPortsMessage
                    or MidiActivityMessage
                    or RenderProgressMessage)) {
                    return message;
                }
            }
        }

        await IpcFraming.WriteAsync(stream, new HelloMessage("test"), Token);
        var hello = Assert.IsType<HelloMessage>(await Next());
        using var region = SharedSegment.Open(hello.TelemetryName!, TelemetryLayout.Size);
        var file = FlexPlaybackTests.WriteBursts(dir, "bursts.wav", 2);
        const long start = 10_000;
        using (var segment = TestAudio.Segment(
                   [
                       new(
                           1,
                           [],
                           [FlexPlaybackTests.Warped(start, 0, FlexAlgorithm.Polyphonic)],
                           FlexPlaybackTests.Map()
                       )
                   ],
                   3
               )) {
            await IpcFraming.WriteAsync(
                stream,
                TestAudio.Load(segment, 3, true, [TestAudio.Track(1, true)], [new(0, file, 2, Rate, 80_000)], dir),
                Token
            );
            Assert.Equal(new ModelLoadedMessage(3), await Next());
        }

        await IpcFraming.WriteAsync(stream, new TransportMessage(TransportAction.Play, start), Token);
        for (var polls = 0; Header(region).PositionSamples < start + Rate / 2; polls++) {
            Assert.True(polls < 500, "the playhead did not move");
            await Task.Delay(20, Token);
        }

        await IpcFraming.WriteAsync(stream, new TransportMessage(TransportAction.Stop), Token);
        var end = start + FlexPlaybackTests.MapLength + Rate / 4;
        var bounce = Path.Combine(dir, "bounce");
        await IpcFraming.WriteAsync(
            stream,
            new RenderMessage(9, 0, end, bounce, [1], true, IncludeVolumePan: true),
            Token
        );
        RenderFinishedMessage finished;
        while (true) {
            if (await Next() is RenderFinishedMessage f) {
                finished = f;
                break;
            }
        }

        Assert.Null(finished.Error);
        var data = RenderTests.ReadFloat(Assert.Single(finished.Files), out _);
        Assert.Equal(2 * end, data.Length);
        var onsets = FlexPlaybackTests.Onsets(Enumerable.Range(0, (int)end).Select(f => data[2 * f]).ToArray());
        var expected = FlexPlaybackTests.MapTargets.Select(static t => start + t).ToArray();
        Assert.Equal(expected.Length, onsets.Count);
        for (var k = 0; k < expected.Length; k++) {
            Assert.InRange(onsets[k], expected[k] - Rate * 3 / 1000, expected[k] + Rate * 3 / 1000);
        }

        lock (output) {
            Assert.DoesNotContain("misbehaving", output.ToString(), StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     Phase 6 through the real host: a format-4 segment with tempo points and a Live Loops audio cell loads; while
    ///     the null device plays, a launch lands on the next bar and a stop on the next quarter note, the host pushing
    ///     each state change with its sample; the preview sync message is taken.
    /// </summary>
    [Fact]
    public async Task Launches_and_stops_a_cell_on_its_boundaries_and_pushes_its_states() {
        await StartAsync();
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), Token);
        await using var stream = new NetworkStream(socket);
        var cellStates = new List<CellStateMessage>();

        // The next message that is not one the host pushes unasked (cell states are kept).
        async Task<IpcMessage?> Next() {
            while (true) {
                var message = await IpcFraming.ReadAsync(stream, Token);
                if (message is CellStateMessage state) {
                    cellStates.Add(state);
                    continue;
                }

                if (message is not (PluginCatalogMessage or MidiPortsMessage or MidiActivityMessage)) {
                    return message;
                }
            }
        }

        async Task WaitForState(EngineCellState state) {
            for (var pings = 0; !cellStates.Exists(s => s.State == state); pings++) {
                Assert.True(pings < 300, $"no {state} state");
                await Task.Delay(20, Token);
                await IpcFraming.WriteAsync(stream, new PingMessage(pings), Token);
                Assert.IsType<PongMessage>(await Next());
            }
        }

        await IpcFraming.WriteAsync(stream, new HelloMessage("test"), Token);
        Assert.IsType<HelloMessage>(await Next());
        var file = TestAudio.Write24(dir, "loop.wav", 1, Rate, 4);
        using (var segment = TestAudio.Segment(
                   [new(1, [], [])],
                   5,
                   tempo: [TestAudio.Tempo(0, 0, 120)],
                   cells: [new(0, 1, CellMode.Loop, Rate, [], [TestAudio.Region(0, Rate, 0)])]
               )) {
            await IpcFraming.WriteAsync(
                stream,
                TestAudio.Load(
                    segment,
                    5,
                    true,
                    [TestAudio.Track(1) with { LiveLoopsActive = true }],
                    [new(0, file, 1, Rate, Rate)],
                    dir
                ),
                Token
            );
            Assert.Equal(new ModelLoadedMessage(5), await Next());
        }

        await IpcFraming.WriteAsync(stream, new SetPreviewSyncMessage(1.1, 2, true), Token);
        await IpcFraming.WriteAsync(stream, new TransportMessage(TransportAction.Play, 0), Token);
        await IpcFraming.WriteAsync(stream, new LaunchCellsMessage([new(0, EngineQuantize.Bar)]), Token);
        await WaitForState(EngineCellState.Playing);
        var queued = cellStates.Single(static s => s.State == EngineCellState.Queued);
        var playing = cellStates.Single(static s => s.State == EngineCellState.Playing);
        Assert.Equal(0, playing.Cell);
        Assert.Equal(queued.Sample, playing.Sample);
        Assert.Equal(0, playing.Sample % (2 * Rate)); // a bar line at 120 bpm

        await IpcFraming.WriteAsync(stream, new StopCellsMessage([new(1, EngineQuantize.Quarter)]), Token);
        await WaitForState(EngineCellState.Stopped);
        var stopping = cellStates.Single(static s => s.State == EngineCellState.Stopping);
        var stopped = cellStates.Single(static s => s.State == EngineCellState.Stopped);
        Assert.Equal(stopping.Sample, stopped.Sample);
        Assert.Equal(0, stopped.Sample % (Rate / 2));
        Assert.True(stopped.Sample > playing.Sample);
        lock (output) {
            Assert.DoesNotContain("misbehaving", output.ToString(), StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     Phase 7 through the real host: MIDI step input forwarding (live MIDI made into a chord step, pushed as
    ///     <c>stepInputMidi</c>, and nothing once it is off), a looping range preview, and <c>setScript</c> refused for a
    ///     slot that does not run scripts. (Scripter itself is workstream D's; its round trip runs at integration.)
    /// </summary>
    [Fact]
    public async Task Forwards_step_input_previews_a_range_and_refuses_a_script_without_a_slot() {
        await StartAsync();
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), Token);
        await using var stream = new NetworkStream(socket);
        var steps = new List<StepInputMidiMessage>();

        // The next message that is not one the host pushes unasked (steps are kept).
        async Task<IpcMessage?> Next() {
            while (true) {
                var message = await IpcFraming.ReadAsync(stream, Token);
                if (message is StepInputMidiMessage step) {
                    steps.Add(step);
                    continue;
                }

                if (message is not (PluginCatalogMessage or MidiPortsMessage or MidiActivityMessage)) {
                    return message;
                }
            }
        }

        async Task<IpcMessage?> Ping() {
            await IpcFraming.WriteAsync(stream, new PingMessage(7), Token);
            return await Next();
        }

        await IpcFraming.WriteAsync(stream, new HelloMessage("test"), Token);
        Assert.IsType<HelloMessage>(await Next());
        using (var segment = TestAudio.Segment([new(1, [], [])], 2)) {
            await IpcFraming.WriteAsync(
                stream,
                TestAudio.Load(segment, 2, true, [new(1, -1, true, false, false, TestAudio.Unity)], [], dir),
                Token
            );
            Assert.Equal(new ModelLoadedMessage(2), await Next());
        }

        await IpcFraming.WriteAsync(stream, new SetStepInputForwardMessage(true), Token);
        await IpcFraming.WriteAsync(
            stream,
            new LiveMidiMessage(1, [new(0x90, 60, 90), new(0x90, 67, 100), new(0x80, 67, 0), new(0x80, 60, 0)]),
            Token
        );
        Assert.IsType<PongMessage>(await Ping());
        var step = Assert.Single(steps);
        Assert.Equal([60, 67], step.Pitches);
        Assert.Equal(100, step.Velocity);
        await IpcFraming.WriteAsync(stream, new SetStepInputForwardMessage(false), Token);
        await IpcFraming.WriteAsync(stream, new LiveMidiMessage(1, [new(0x90, 62, 90), new(0x80, 62, 0)]), Token);
        Assert.IsType<PongMessage>(await Ping());
        Assert.Single(steps);

        var file = TestAudio.Write24(dir, "range.wav", 1, Rate, 3);
        await IpcFraming.WriteAsync(stream, new PreviewRangeMessage(file, 100, 600, true, 0.5f), Token);
        Assert.IsType<PongMessage>(await Ping()); // taken without a reply
        await IpcFraming.WriteAsync(stream, new PreviewRangeMessage(file, 600, 100, true, 0.5f), Token);
        Assert.Equal("previewRange", Assert.IsType<ErrorMessage>(await Next()).Request);
        await IpcFraming.WriteAsync(stream, new SetScriptMessage(1, "none", "Trace('hi')"), Token);
        Assert.Equal("setScript", Assert.IsType<ErrorMessage>(await Next()).Request);
        lock (output) {
            Assert.DoesNotContain("misbehaving", output.ToString(), StringComparison.Ordinal);
        }
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
