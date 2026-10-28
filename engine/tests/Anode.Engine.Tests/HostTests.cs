using Anode.Audio;
using Anode.EngineHost;
using Anode.Ipc;
using Anode.Ipc.Segments;
using Anode.Ipc.Telemetry;
using System.Net.Sockets;

namespace Anode.Engine.Tests;

/// <summary>
///     The host as anode-server sees it, in this process: the null device's real-time clock driving the engine, the
///     disk and writer threads running, messages over the Unix socket and the telemetry region in shared memory.
/// </summary>
public sealed class HostTests : IAsyncLifetime {
    const int Rate = 48_000;
    readonly string dir = TestAudio.TempDirectory();
    readonly string socketPath = Path.Combine(Path.GetTempPath(), $"an-{Guid.NewGuid().ToString("n")[..8]}.sock");
    EngineHost.EngineHost? host;
    EngineHostServer? server;
    Socket? socket;
    NetworkStream? stream;

    EngineHost.EngineHost Host => host!;

    static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() {
        host = new(new NullAudioDevice(Rate, 64, 2, 2, true), "test");
        host.Start();
        server = new(socketPath, Host.HandleAsync);
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

    async Task<IpcMessage?> Request(IpcMessage message) {
        await IpcFraming.WriteAsync(stream!, message, Token);
        return await IpcFraming.ReadAsync(stream!, Token);
    }

    Task Send(IpcMessage message) => IpcFraming.WriteAsync(stream!, message, Token);

    async Task<T> Expect<T>(IpcMessage message) where T : IpcMessage {
        var reply = await Request(message);
        return Assert.IsType<T>(reply);
    }

    unsafe TelemetryHeader Telemetry(out MeterEntry[] meters) {
        meters = new MeterEntry[TelemetryHeader.MaxTracks + 1];
        Assert.True(TelemetryWriter.TryRead(Host.TelemetryRegion, out var header, meters));
        return header;
    }

    static unsafe TelemetryHeader Header(SharedSegment region) => *(TelemetryHeader*)region.Pointer;

    static async Task Eventually(Func<bool> condition, double seconds = 5) {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition()) {
            Assert.True(DateTime.UtcNow < deadline, "condition not met in time");
            await Task.Delay(10, Token);
        }
    }

    async Task LoadAsync(long version, EngineTrack[] tracks, TrackModel[] models, EngineFile[] files) {
        using var segment = TestAudio.Segment(models, version);
        var loaded = await Expect<ModelLoadedMessage>(TestAudio.Load(segment, version, true, tracks, files, dir));
        Assert.Equal(version, loaded.Version);
    }

    [Fact]
    public async Task Hello_describes_the_device_and_names_a_telemetry_region_the_server_can_map() {
        var hello = await Expect<HelloMessage>(new HelloMessage("server"));
        Assert.Equal("test", hello.Version);
        Assert.Equal("null", hello.Backend);
        Assert.Equal(
            (Rate, 64, 2, 2),
            (hello.SampleRate, hello.BufferFrames, hello.InputChannels, hello.OutputChannels)
        );
        Assert.StartsWith("shm:/an-tl-", hello.TelemetryName);

        // The server's side: map the region read-only by its name.
        using var region = SharedSegment.Open(hello.TelemetryName!, TelemetryLayout.Size);
        await Eventually(() => Header(region).Callbacks > 10);
        Assert.Equal(TelemetryHeader.MagicValue, Header(region).Magic);
        Assert.Equal(Rate, Header(region).SampleRate);
        Assert.Equal(new PongMessage(5), await Request(new PingMessage(5)));
    }

    [Fact]
    public async Task Plays_a_loaded_model_and_reports_the_playhead_and_meters() {
        var file = TestAudio.Write24(dir, "a.wav", 1, 4 * Rate, 1);
        await LoadAsync(
            1,
            [TestAudio.Track(3)],
            [new(3, [], [TestAudio.Region(0, 4 * Rate, 0)])],
            [new(0, file, 1, Rate, 4 * Rate)]
        );
        await Eventually(() => Telemetry(out _).ModelVersion == 1);
        await Send(new TransportMessage(TransportAction.Play, Rate));
        await Eventually(() => Telemetry(out _).State == TelemetryTransportState.Playing);
        var first = Telemetry(out _);
        Assert.InRange(first.PositionSamples, Rate, Rate + Rate / 2);
        await Task.Delay(300, Token);
        var later = Telemetry(out var meters);
        var elapsed = (later.HostTimeNs - first.HostTimeNs) / 1e9;
        // It moves, and never ahead of real time. How far it gets behind measures the machine, not the engine: when the
        // whole test solution runs in parallel the null device's clock thread can be starved (it once reached 58 %).
        Assert.InRange(later.PositionSamples - first.PositionSamples, 1, elapsed * Rate + 2_048);
        Assert.Equal(1, later.TrackCount);
        Assert.Equal(3, meters[0].TrackId);
        Assert.Equal(-1, meters[1].TrackId);
        await Eventually(() => {
                Telemetry(out var m);
                return m[0].PeakLeft > 0.1f && m[1].PeakLeft > 0.1f;
            }
        );
        Assert.Equal(0, later.Underruns);

        Assert.Null(await SendAndPeek(new TransportMessage(TransportAction.Stop)));
        await Eventually(() => Telemetry(out _).State == TelemetryTransportState.Stopped);
    }

    /// <summary>Sends a message that may not answer; returns its reply if one comes, after a ping round trip.</summary>
    async Task<IpcMessage?> SendAndPeek(IpcMessage message) {
        await Send(message);
        var next = await Request(new PingMessage(99));
        if (next is PongMessage) {
            return null;
        }

        Assert.IsType<PongMessage>(await IpcFraming.ReadAsync(stream!, Token));
        return next;
    }

    [Fact]
    public async Task Records_armed_tracks_and_answers_recording_finished() {
        await LoadAsync(
            1,
            [
                TestAudio.Track(4, input: 0, armed: true, monitoring: true),
                TestAudio.Track(5, true, input: 0, armed: true), TestAudio.Track(6)
            ],
            [new(4, [], []), new(5, [], []), new(6, [], [])],
            []
        );
        await Send(new SetMetronomeMessage(false, 1f, false));
        await Send(new TransportMessage(TransportAction.Record, 24_000));
        await Eventually(() => Telemetry(out _).State == TelemetryTransportState.Recording);
        await Eventually(() => Telemetry(out var m).PositionSamples > 24_000 + Rate / 2 && m[0].PeakLeft > 0.05f);
        var finished = Assert.IsType<RecordingFinishedMessage>(
            await Request(new TransportMessage(TransportAction.Stop))
        );
        Assert.Equal([4, 5], finished.Takes.Select(static t => t.TrackId).Order());
        foreach (var take in finished.Takes) {
            Assert.Equal(24_000, take.StartSample);
            Assert.Equal(Rate, take.SampleRate);
            Assert.Equal(take.TrackId == 5 ? 2 : 1, take.Channels);
            Assert.InRange(take.Frames, Rate / 2, 3 * Rate);
            Assert.StartsWith(Path.Combine(dir, $"{take.TrackId}-"), take.Path);
            var samples = TestAudio.Read24(take.Path, out var channels);
            Assert.Equal(take.Channels, channels);
            Assert.Equal(take.Frames * channels, samples.Length);
            Assert.InRange(samples.Max(), 800_000, 850_000); // the null device's −20 dBFS tone
        }

        // Stopping again finishes nothing.
        Assert.Null(await SendAndPeek(new TransportMessage(TransportAction.Stop)));
    }

    [Fact]
    public async Task Count_in_holds_the_playhead_before_recording() {
        PlaybackEvent[] clicks =
            [..Enumerable.Range(0, 64).Select(static i => TestAudio.Click(i * 6_000L, i % 4 == 0))];
        await LoadAsync(
            1,
            [TestAudio.Track(1, input: 0, armed: true)],
            [new(1, [], []), new(TrackEntry.MetronomeTrack, clicks, [])],
            []
        );
        await Send(new SetMetronomeMessage(true, 0.5f, true));
        await Send(new TransportMessage(TransportAction.Record, 48_000));
        await Eventually(() => Telemetry(out _).State == TelemetryTransportState.Recording);
        Assert.Equal(48_000, Telemetry(out _).PositionSamples); // the bar of 24,000 frames is still counting in
        await Eventually(() => Telemetry(out _).PositionSamples > 48_000);
        var finished = Assert.IsType<RecordingFinishedMessage>(
            await Request(new TransportMessage(TransportAction.Stop))
        );
        Assert.Equal(48_000, Assert.Single(finished.Takes).StartSample);
    }

    [Fact]
    public async Task A_pass_with_nothing_armed_still_finishes_with_no_takes() {
        await LoadAsync(1, [TestAudio.Track(1)], [new(1, [], [])], []);
        await Send(new TransportMessage(TransportAction.Record, 0));
        await Eventually(() => Telemetry(out _).State == TelemetryTransportState.Recording);
        var finished = Assert.IsType<RecordingFinishedMessage>(
            await Request(new TransportMessage(TransportAction.Stop))
        );
        Assert.Empty(finished.Takes);
    }

    [Fact]
    public async Task Bad_requests_get_errors_and_the_host_carries_on() {
        var error = await Expect<ErrorMessage>(new TransportMessage(TransportAction.Record));
        Assert.Equal("transport", error.Request);
        Assert.Equal("setStrip", (await Expect<ErrorMessage>(new SetStripMessage(42, TestAudio.Unity))).Request);
        Assert.Equal(
            "setTrackIo",
            (await Expect<ErrorMessage>(new SetTrackIoMessage(42, 0, false, true, false))).Request
        );
        Assert.Equal("transport", (await Expect<ErrorMessage>(new TransportMessage(TransportAction.Locate))).Request);
        var load = await Expect<ErrorMessage>(
            new LoadModelMessage(new("shm:/an-missing-segment", 4096, 1), true, [], [], TestAudio.Unity, dir)
        );
        Assert.Equal("loadModel", load.Request);
        Assert.Equal("modelLoaded", (await Expect<ErrorMessage>(new ModelLoadedMessage(1))).Request);
        Assert.Equal(new PongMessage(1), await Request(new PingMessage(1)));
    }

    [Fact]
    public async Task Strips_track_io_cycle_and_per_track_loads_apply() {
        var file = TestAudio.Write24(dir, "b.wav", 1, 4 * Rate, 2);
        await LoadAsync(
            1,
            [TestAudio.Track(1), TestAudio.Track(2)],
            [new(1, [], [TestAudio.Region(0, 4 * Rate, 0)]), new(2, [], [])],
            [new(0, file, 1, Rate, 4 * Rate)]
        );
        await Send(new SetStripMessage(1, new(0.5f, -1f, false, false)));
        await Send(new SetStripMessage(-1, new(0.25f, 0f, false, false)));
        await Send(new SetTrackIoMessage(2, 1, true, true, true));
        await Send(new SetCycleMessage(true, 0, Rate / 2));
        Assert.Equal(new PongMessage(2), await Request(new PingMessage(2)));
        var root = Host.Controller.Engine.Root;
        Assert.Equal(new(0.5f, -1f, false, false), root.Tracks[0].Strip.Settings);
        Assert.Equal(0.25f, Host.Controller.Engine.Master.Gain);
        Assert.True(root.Tracks[1].Stereo);
        Assert.Equal((1, true, true), (root.Tracks[1].InputChannel, root.Tracks[1].Armed, root.Tracks[1].Monitoring));

        await Send(new TransportMessage(TransportAction.Play, 0));
        await Eventually(() => Telemetry(out _).State == TelemetryTransportState.Playing);
        await Task.Delay(700, Token);
        Assert.InRange(Telemetry(out _).PositionSamples, 0, Rate / 2); // looping in the half-second cycle

        // A per-track segment replaces one track's playlist and keeps the other.
        using var patch = TestAudio.Segment([new(2, [], [TestAudio.Region(0, Rate, 0)])], 2);
        var loaded = await Expect<ModelLoadedMessage>(TestAudio.Load(patch, 2, false, [], [], dir));
        Assert.Equal(2, loaded.Version);
        Assert.Equal(2, Host.Controller.Engine.Root.Tracks.Length);
        Assert.Equal(1, Host.Controller.Engine.Root.Views[1].PlaylistCount);
        await Eventually(() => Telemetry(out _).ModelVersion == 2);
        Assert.Equal(0, Telemetry(out _).Underruns);
    }

    [Fact]
    public async Task Cycle_recording_reports_one_file_with_its_passes() {
        await LoadAsync(1, [TestAudio.Track(4, input: 0, armed: true)], [new(4, [], [])], []);
        await Send(new SetMetronomeMessage(false, 1f, false));
        await Send(new SetCycleMessage(true, 24_000, 36_000));
        await Send(new TransportMessage(TransportAction.Record, 24_000));
        await Eventually(() => Telemetry(out _).State == TelemetryTransportState.Recording);
        await Task.Delay(800, Token); // three passes of a quarter second, or more
        var finished = Assert.IsType<RecordingFinishedMessage>(
            await Request(new TransportMessage(TransportAction.Stop))
        );
        Assert.False(finished.Flashback);
        var take = Assert.Single(finished.Takes);
        Assert.Equal(24_000, take.StartSample);
        var passes = take.Passes!;
        Assert.True(passes.Count >= 3, $"{passes.Count} passes");
        Assert.All(passes, static p => Assert.Equal(24_000, p.StartSample));
        Assert.All(passes.SkipLast(1), static p => Assert.Equal(12_000, p.Frames));
        Assert.Equal(take.Frames, passes.Sum(static p => p.Frames));
        Assert.Equal(passes.Select(static (p, i) => i * 12_000L), passes.Select(static p => p.FileOffset));
        Assert.Equal(take.Frames, TestAudio.Read24(take.Path, out _).Length);
    }

    [Fact]
    public async Task Autopunch_keeps_only_the_punch_range() {
        await LoadAsync(1, [TestAudio.Track(4, input: 0, armed: true)], [new(4, [], [])], []);
        await Send(new SetPunchMessage(true, 30_000, 40_000));
        await Send(new TransportMessage(TransportAction.Record, 24_000));
        await Eventually(() => Telemetry(out _).PositionSamples > 45_000);
        var finished = Assert.IsType<RecordingFinishedMessage>(
            await Request(new TransportMessage(TransportAction.Stop))
        );
        var take = Assert.Single(finished.Takes);
        Assert.Equal((30_000, 10_000), (take.StartSample, take.Frames));
        Assert.Null(take.Passes);
        Assert.InRange(TestAudio.Read24(take.Path, out _).Max(), 800_000, 850_000); // the input tone

        // A punch range the pass never reaches records nothing.
        await Send(new SetPunchMessage(true, 40 * Rate, 41 * Rate));
        await Send(new TransportMessage(TransportAction.Record, 0));
        await Eventually(() => Telemetry(out _).PositionSamples > 1_000);
        var none = Assert.IsType<RecordingFinishedMessage>(await Request(new TransportMessage(TransportAction.Stop)));
        Assert.Empty(none.Takes);
    }

    [Fact]
    public async Task Flashback_capture_writes_what_was_played_while_not_recording() {
        await LoadAsync(1, [TestAudio.Track(4, true, input: 0, armed: true)], [new(4, [], [])], []);
        var empty = Assert.IsType<RecordingFinishedMessage>(await Request(new FlashbackCaptureMessage()));
        Assert.True(empty.Flashback);
        Assert.Empty(empty.Takes); // nothing played yet

        await Send(new TransportMessage(TransportAction.Play, Rate));
        await Eventually(() => Telemetry(out _).PositionSamples > Rate + Rate / 2);
        var captured = Assert.IsType<RecordingFinishedMessage>(await Request(new FlashbackCaptureMessage()));
        Assert.True(captured.Flashback);
        var take = Assert.Single(captured.Takes);
        Assert.Equal((4, 2, Rate), (take.TrackId, take.Channels, take.StartSample));
        Assert.InRange(take.Frames, Rate / 2, 4 * Rate);
        var samples = TestAudio.Read24(take.Path, out var channels);
        Assert.Equal(take.Frames * channels, samples.Length);
        Assert.InRange(samples.Max(), 800_000, 850_000);
        Assert.Null(await SendAndPeek(new TransportMessage(TransportAction.Stop))); // playing, not recording
    }

    [Fact]
    public async Task Preview_plays_a_file_to_the_master_until_stopped() {
        var file = TestAudio.Write24(dir, "preview.wav", 1, 10 * Rate, 3);
        await LoadAsync(1, [TestAudio.Track(1)], [new(1, [], [])], []);
        Assert.Null(await SendAndPeek(new PreviewFileMessage(file, 1f)));
        await Eventually(() => {
                Telemetry(out var m);
                return m[1] is { TrackId: -1, PeakLeft: > 0.1f };
            }
        );
        Assert.Equal(TelemetryTransportState.Stopped, Telemetry(out _).State);
        Assert.Null(await SendAndPeek(new PreviewFileMessage(null, 1f)));
        Assert.Null(Host.Controller.Engine.Preview);
        var error = await Expect<ErrorMessage>(new PreviewFileMessage(Path.Combine(dir, "missing.wav"), 1f));
        Assert.Equal("previewFile", error.Request);
    }
}
