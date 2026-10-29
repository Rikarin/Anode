using Anode.Contracts;
using Anode.Model.Audio;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace Anode.Server.Tests;

/// <summary>
///     The production layout (docs/adr/0001-process-model.md): anode-host in its own process, the Server talking to it
///     over a Unix domain socket.
/// </summary>
public sealed class TwoProcessTests : IAsyncDisposable {
    readonly StringBuilder hostOutput = new();

    // Unix socket paths are limited to ~104 bytes on macOS.
    readonly string socketPath = Path.Combine(Path.GetTempPath(), $"an-{Guid.NewGuid().ToString("n")[..8]}.sock");

    Process? host;

    public async ValueTask DisposeAsync() {
        await StopHostAsync();
        File.Delete(socketPath);
        lock (hostOutput) {
            TestContext.Current.TestOutputHelper?.WriteLine("anode-host output:\n" + hostOutput);
        }
    }

    [Fact]
    public async Task Server_connects_to_a_separate_host_and_reconnects_after_it_restarts() {
        StartHost();
        await using var server = new ServerFixture(socketPath);
        using var client = server.CreateClient();
        await ServerFixture.Eventually(
            async () => (await server.SystemAsync(client)).Engine.State == EngineState.Connected,
            60
        );
        var engine = (await server.SystemAsync(client)).Engine;
        Assert.Equal("null", engine.Backend);
        Assert.False(string.IsNullOrEmpty(engine.HostVersion));

        await StopHostAsync();
        await ServerFixture.Eventually(async () =>
            (await server.SystemAsync(client)).Engine.State == EngineState.Disconnected
        );
        StartHost();
        await ServerFixture.Eventually(
            async () => (await server.SystemAsync(client)).Engine.State == EngineState.Connected,
            60
        );
    }

    [Fact]
    public async Task Server_opens_imports_plays_and_records_on_the_null_device() {
        var media = Directory.CreateTempSubdirectory("anode-two-process-").FullName;
        try {
            StartHost();
            await using var server = new ServerFixture(socketPath);
            using var client = server.CreateClient();
            await using var events = await EventStream.ConnectAsync(server);
            await ServerFixture.Eventually(
                async () => (await server.SystemAsync(client)).Engine.State == EngineState.Connected,
                60
            );

            var project = await client.CreateProjectAsync("Two Process");
            var file = await client.ImportAsync(TestWav.Write(media, "Loop.wav", frames: 96_000));
            await client.EditAsync(new AddTrackCommand("Playback"), new AddTrackCommand("Record"));
            var tracks = (await client.ProjectAsync()).Tracks;
            await client.EditAsync(
                new AddRegionCommand(tracks[0].Id, file.Id, 0),
                new SetTrackCommand(tracks[1].Id, RecordArmed: true, Input: new(1, false))
            );

            await client.TransportAsync(TransportCommand.Play, 0);
            await events.WaitAsync<TransportEvent>(
                static e => e.Transport is { State: TransportState.Playing, PositionSamples: > 24_000 },
                30
            );
            await events.WaitAsync<MetersEvent>(
                e => e.Tracks.Any(t => t.TrackId == tracks[0].Id && t.PeakLeft > 0),
                30
            );
            await client.TransportAsync(TransportCommand.Stop);
            await events.WaitAsync<TransportEvent>(static e => e.Transport.State == TransportState.Stopped, 30);

            await client.TransportAsync(TransportCommand.Record, 960 * 4);
            await events.WaitAsync<TransportEvent>(
                static e => e.Transport is { State: TransportState.Recording, PositionSamples: > 120_000 },
                30
            );
            await client.TransportAsync(TransportCommand.Stop);
            var recorded = await events.WaitAsync<ProjectChangedEvent>(
                e => e.Delta.Ops.OfType<RegionUpserted>().Any(r => r.Region.TrackId == tracks[1].Id),
                30
            );
            var take = recorded.Delta.Ops.OfType<AudioFileUpserted>().Single().File;
            Assert.Equal(24, take.BitDepth);
            Assert.True(File.Exists(Path.Combine(project.Path, take.Path)));
            Assert.Equal(960 * 4, recorded.Delta.Ops.OfType<RegionUpserted>().Single().Region.StartTick);
        } finally {
            Directory.Delete(media, true);
        }
    }

    [Fact]
    public async Task Cycle_records_takes_punches_in_and_captures_a_flashback_on_the_real_host() {
        // The null device's inputs carry a tone, so every recording has material.
        StartHost("--input-tone");
        await using var server = new ServerFixture(socketPath);
        using var client = server.CreateClient();
        await using var events = await EventStream.ConnectAsync(server);
        await ServerFixture.Eventually(
            async () => (await server.SystemAsync(client)).Engine.State == EngineState.Connected,
            60
        );
        await client.CreateProjectAsync("Phase 3", 48_000);
        await client.EditAsync(new AddTrackCommand("Vocal"));
        var track = (await client.ProjectAsync()).Tracks.Single();
        const long Bar = 960 * 4; // 2 s at 120 BPM: 96,000 samples
        await client.EditAsync(
            new SetTrackCommand(track.Id, RecordArmed: true, Input: new(1, false)),
            new SetCycleCommand(true, 0, Bar)
        );

        // Cycle recording: about two and a half passes make a take folder of three takes, comped on the last.
        await client.TransportAsync(TransportCommand.Record, 0);
        await events.WaitAsync<TransportEvent>(static e => e.Transport.State == TransportState.Recording, 30);
        await Task.Delay(5_200, TestContext.Current.CancellationToken);
        await client.TransportAsync(TransportCommand.Stop);
        var cycled = await events.WaitAsync<ProjectChangedEvent>(
            static e => e.Delta.Ops.OfType<TakeFolderUpserted>().Any(),
            30
        );
        var folder = cycled.Delta.Ops.OfType<TakeFolderUpserted>().Single().Folder;
        Assert.InRange(folder.Takes.Count, 2, 4);
        Assert.Equal((0L, Bar), (folder.StartTick, folder.LengthTicks));
        Assert.Equal(folder.Takes[^1].Id, Assert.Single(folder.Comp).TakeId);

        // Autopunch: recording from bar 1 keeps only bar 3.
        await client.EditAsync(
            new SetCycleCommand(false),
            new SetRecordingCommand(Autopunch: true, PunchInTick: 2 * Bar, PunchOutTick: 3 * Bar)
        );
        await client.TransportAsync(TransportCommand.Record, 0);
        await events.WaitAsync<TransportEvent>(
            static e => e.Transport is { State: TransportState.Recording, PositionTick: > 3 * Bar + 480 },
            30
        );
        await client.TransportAsync(TransportCommand.Stop);
        var punched = await events.WaitAsync<ProjectChangedEvent>(
            static e => e.Delta.Ops.OfType<RegionUpserted>().Any(),
            30
        );
        var region = punched.Delta.Ops.OfType<RegionUpserted>().Single().Region;
        // Placed on the first tick at or after the recorded sample: within a tick (25 frames) of exact.
        Assert.Equal(2 * Bar, region.StartTick);
        Assert.InRange(region.Length, 96_000 - 25, 96_000);

        // Flashback Capture: what the armed track heard while playing, without recording.
        await client.EditAsync(new SetRecordingCommand(Autopunch: false));
        await client.TransportAsync(TransportCommand.Play, 4 * Bar);
        await events.WaitAsync<TransportEvent>(
            static e => e.Transport is { State: TransportState.Playing, PositionTick: > 4 * Bar + 960 },
            30
        );
        Assert.Equal(
            HttpStatusCode.Accepted,
            (await client.PostAsync("/api/v1/transport/capture", null, TestContext.Current.CancellationToken))
            .StatusCode
        );
        var captured = await events.WaitAsync<ProjectChangedEvent>(
            static e => e.Delta.Ops.OfType<RegionUpserted>().Any(r => r.Region.StartTick >= 4 * Bar),
            30
        );
        await client.TransportAsync(TransportCommand.Stop);
        var flashback = captured.Delta.Ops.OfType<RegionUpserted>().Single().Region;
        Assert.Equal(track.Id, flashback.TrackId);
        Assert.InRange(flashback.StartTick, 4 * Bar, 4 * Bar + 1);
        Assert.True(flashback.Length > 24_000, $"Captured {flashback.Length} frames.");
    }

    const long Bar = 960 * 4;

    [Fact]
    public async Task Live_MIDI_step_enters_through_the_real_host_while_MIDI_step_input_is_on() {
        // The on-screen keyboard's path (docs/08-phase-7.md, step 6): /midi/live → the host's liveMidi → its held-key
        // state → stepInputMidi → the Server's steps.
        StartHost();
        await using var server = new ServerFixture(socketPath);
        using var client = server.CreateClient();
        await ServerFixture.Eventually(
            async () => (await server.SystemAsync(client)).Engine.State == EngineState.Connected,
            60
        );
        await client.CreateProjectAsync("Steps");
        await client.EditAsync(new AddTrackCommand(Kind: TrackKind.Instrument));
        var track = (await client.ProjectAsync()).Tracks[0].Id;
        await PutStepInputAsync(client, new(true, TrackId: track, NoteValueTicks: 480, MidiInput: true));
        // D3, then F3 + A3 held together: a note and a chord.
        await LiveAsync(client, track, new LiveMidiEvent(0x90, 50, 90));
        await LiveAsync(client, track, new LiveMidiEvent(0x80, 50, 0));
        await LiveAsync(client, track, new LiveMidiEvent(0x90, 53, 80), new LiveMidiEvent(0x90, 57, 100));
        await LiveAsync(client, track, new LiveMidiEvent(0x80, 53, 0));
        await LiveAsync(client, track, new LiveMidiEvent(0x80, 57, 0));
        await ServerFixture.Eventually(async () => (await client.ProjectAsync()).MidiRegions is [{ Notes.Count: 3 }]);
        Assert.Equal(
            [(50, 90, 0L), (53, 100, 480), (57, 100, 480)],
            (await client.ProjectAsync()).MidiRegions[0].Notes.Select(static n => (n.Pitch, n.Velocity, n.StartTick))
        );
    }

    static async Task PutStepInputAsync(HttpClient client, StepInputRequest request) {
        var response = await client.PutAsJsonAsync(
            "/api/v1/project/step-input",
            request,
            ContractsJsonContext.Default.StepInputRequest,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    static async Task LiveAsync(HttpClient client, string track, params LiveMidiEvent[] events) {
        var response = await client.PostAsJsonAsync(
            "/api/v1/midi/live",
            new(track, events),
            ContractsJsonContext.Default.LiveMidiRequest,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Warped_tracks_bounce_on_the_real_host() {
        var media = Directory.CreateTempSubdirectory("anode-two-process-").FullName;
        try {
            StartHost();
            await using var server = new ServerFixture(socketPath);
            using var client = server.CreateClient();
            await using var events = await EventStream.ConnectAsync(server);
            await ServerFixture.Eventually(
                async () => (await server.SystemAsync(client)).Engine.State == EngineState.Connected,
                60
            );
            var tracks = await GoldenProjectAsync(client, events, media);
            await client.EditAsync(new SetStripCommand(tracks[2].Id, Solo: true));
            // A run of quick edits while stopped (as Flex Pitch's key presses make) must leave the streams fillable.
            var region = (await client.ProjectAsync()).Regions.Single(r => r.TrackId == tracks[2].Id);
            foreach (var cents in new[] { 6_100.0, 6_200, 6_300, 6_400 }) {
                await client.EditAsync(
                    new SetPitchNotesCommand(
                        region.Id,
                        [..region.PitchNotes.Select((n, i) => i == 1 ? n with { TargetCents = cents } : n)]
                    )
                );
            }

            for (var k = 0; k < 2; k++) {
                var response = await client.PostAsJsonAsync(
                    "/api/v1/project/bounce",
                    new($"Pitched {k}", media, 0, 2 * Bar),
                    ContractsJsonContext.Default.BounceRequest,
                    TestContext.Current.CancellationToken
                );
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
                var job = (await response.Content.ReadFromJsonAsync(
                    ContractsJsonContext.Default.Job,
                    TestContext.Current.CancellationToken
                ))!;
                var done = await events.WaitAsync<JobEvent>(
                    e => e.Job.Id == job.Id && e.Job.State is JobState.Done or JobState.Failed,
                    120
                );
                Assert.True(done.Job.State == JobState.Done, done.Job.Message);
                Assert.Equal(4L * 48_000, WavReader.Probe(done.Job.Files[0]).Frames);
            }
        } finally {
            Directory.Delete(media, true);
        }
    }

    /// <summary>
    ///     How long playback takes to start after a locate into stretched regions (docs/06-phase-5.md: a locate costs
    ///     disk-thread time, not a gap): Play from a new position waits for every stream to prime, and the stretchers
    ///     prime from their anchors. Measured on the golden path's project (two Polyphonic drum tracks and a Flex
    ///     Pitch melody), against the same project with flex off.
    /// </summary>
    [Fact]
    public async Task Playback_starts_promptly_after_a_locate_into_stretched_regions() {
        var media = Directory.CreateTempSubdirectory("anode-two-process-").FullName;
        try {
            StartHost();
            await using var server = new ServerFixture(socketPath);
            using var client = server.CreateClient();
            await using var events = await EventStream.ConnectAsync(server);
            await ServerFixture.Eventually(
                async () => (await server.SystemAsync(client)).Engine.State == EngineState.Connected,
                60
            );
            var tracks = await GoldenProjectAsync(client, events, media);
            await client.EditAsync(new SetTrackFlexCommand(tracks[0].Id, Mode: FlexMode.Polyphonic));
            var warped = await LocateTimesAsync();
            var project = await client.ProjectAsync();
            await client.EditAsync(
                [
                    ..project.Regions.Select(static r => new SetPitchNotesCommand(r.Id, [])),
                    ..project.Regions.Select(static r => new SetFlexMarkersCommand(r.Id, [])),
                    ..project.Regions.Select(static r => new SetRegionFlexCommand(r.Id, FlexFollow.Off)),
                    ..project.Tracks.Select(static t => new SetTrackFlexCommand(t.Id, false))
                ]
            );
            Assert.DoesNotContain((await client.ProjectAsync()).Regions, static r => r.Follow != FlexFollow.Off);
            var plain = await LocateTimesAsync();
            var output = TestContext.Current.TestOutputHelper;
            output?.WriteLine($"Play after a locate, stretched: {Describe(warped)}");
            output?.WriteLine($"Play after a locate, unwarped:  {Describe(plain)}");
            Assert.All(warped, static t => Assert.True(t.Audible < 500, $"{t.Audible} ms to audible output"));

            bool Sounding(MetersEvent meters) => meters.Tracks.Any(t => t.TrackId == tracks[2].Id && t.PeakLeft > 0);

            // Lets the last play's meters arrive (they stop with the transport); returns how many there are by then.
            async Task<int> QuietAsync() {
                await Task.Delay(300, TestContext.Current.CancellationToken);
                return events.OfType<MetersEvent>().Count();
            }

            static string Describe(List<(double Request, double Audible)> times) =>
                $"request {Stats(times.Select(static t => t.Request))}; audible {Stats(times.Select(static t => t.Audible))}";

            static string Stats(IEnumerable<double> values) {
                var sorted = values.Order().ToArray();
                return $"median {sorted[sorted.Length / 2]:F0} ms, max {sorted[^1]:F0} ms";
            }

            // Plays from positions inside the regions where the melody sounds, each away from where the last one
            // stopped: how long Play takes to answer (it answers once every stream is primed), and until the melody's
            // meter shows audio (meters come every 33 ms).
            async Task<List<(double Request, double Audible)>> LocateTimesAsync() {
                var times = new List<(double, double)>();
                foreach (var tick in new long[] { 2_400, 5_280, 960, 4_320, 1_680, 3_360, 480, 6_000 }) {
                    var seen = await QuietAsync();
                    var clock = Stopwatch.StartNew();
                    await client.TransportAsync(TransportCommand.Play, tick);
                    var request = clock.Elapsed.TotalMilliseconds;
                    while (!events.OfType<MetersEvent>().Skip(seen).Any(Sounding)) {
                        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"No audio after Play from {tick}.");
                        await Task.Delay(1, TestContext.Current.CancellationToken);
                    }

                    times.Add((request, clock.Elapsed.TotalMilliseconds));
                    await client.TransportAsync(TransportCommand.Stop);
                }

                return times;
            }
        } finally {
            Directory.Delete(media, true);
        }
    }

    /// <summary>
    ///     The golden path's project (docs/06-phase-5.md, the exit criterion): the 100 bpm loop following 120 bpm on a
    ///     Slicing track with a flex marker moved, the humanised loop quantised to 1/16 (Polyphonic), and the melody in
    ///     Flex Pitch with its notes corrected, one transposed and one with its vibrato halved. Returns the tracks.
    /// </summary>
    static async Task<IReadOnlyList<Track>> GoldenProjectAsync(HttpClient client, EventStream events, string media) {
        await client.CreateProjectAsync("Golden");
        var loop = await client.ImportAsync(DrumLoopWav(media, "Loop.wav", false));
        var humanised = await client.ImportAsync(DrumLoopWav(media, "Humanised.wav", true));
        var melody = await client.ImportAsync(MelodyWav(media));
        await client.EditAsync(
            new AddTrackCommand("Loop"),
            new AddTrackCommand("Humanised"),
            new AddTrackCommand("Melody")
        );
        var tracks = (await client.ProjectAsync()).Tracks;
        await client.EditAsync(
            new AddRegionCommand(tracks[0].Id, loop.Id, 0),
            new AddRegionCommand(tracks[1].Id, humanised.Id, 0),
            new AddRegionCommand(tracks[2].Id, melody.Id, 0),
            new SetTrackFlexCommand(tracks[0].Id, true, FlexMode.Slicing),
            new SetTrackFlexCommand(tracks[2].Id, true, FlexMode.Pitch)
        );
        var regions = (await client.ProjectAsync()).Regions;
        var loopRegion = regions.Single(r => r.FileId == loop.Id);
        await client.EditAsync(
            new SetRegionFlexCommand(loopRegion.Id, FlexFollow.On),
            new SetFlexMarkersCommand(loopRegion.Id, [new(57_600, 2_160)]),
            new QuantizeAudioCommand([regions.Single(r => r.FileId == humanised.Id).Id], 240)
        );
        var analyze = await client.PostAsync(
            $"/api/v1/project/files/{melody.Id}/analyze",
            null,
            TestContext.Current.CancellationToken
        );
        var analysing = (await analyze.Content.ReadFromJsonAsync(
            ContractsJsonContext.Default.Job,
            TestContext.Current.CancellationToken
        ))!;
        await events.WaitAsync<JobEvent>(e => e.Job.Id == analysing.Id && e.Job.State == JobState.Done, 60);
        await client.EditAsync(
            new SetPitchNotesCommand(
                regions.Single(r => r.FileId == melody.Id).Id,
                [
                    new(4_680, 33_720, 6_007.02, 6_000, 1, 1, 0, 0),
                    new(35_880, 64_920, 6_194.03, 6_400, 1, 1, 0, 0),
                    new(67_080, 96_120, 6_404.03, 6_400, 1, 1, 0, 0),
                    new(98_280, 127_320, 6_692.06, 6_700, 1, 0.5, 0, 0),
                    new(129_480, 158_520, 6_905.06, 6_900, 1, 1, 0, 0)
                ]
            )
        );
        return tracks;
    }

    /// <summary>The golden path's drum loop: 4 bars at 100 bpm, kicks on the beats and hats between (humanised ±15 ms).</summary>
    static string DrumLoopWav(string folder, string name, bool humanise) {
        const int rate = 48_000;
        const double beat = 0.6;
        var samples = new float[16 * 28_800];
        var random = new Random(7);
        for (var b = 0; b < 16; b++) {
            foreach (var (offset, kick) in new[] { (0.0, true), (0.5, false) }) {
                var shift = humanise && b + offset > 0 ? (random.NextDouble() * 2 - 1) * 0.015 : 0;
                var start = (int)Math.Round(((b + offset) * beat + shift) * rate);
                var length = (int)Math.Round((kick ? 0.15 : 0.04) * rate);
                for (var i = 0; i < length && start + i < samples.Length; i++) {
                    var t = i / (double)rate;
                    samples[start + i] += (float)(kick
                        ? 0.8 * Math.Exp(-t / 0.05) * Math.Sin(2 * Math.PI * 55 * t)
                        : 0.3 * Math.Exp(-t / 0.01) * (random.NextDouble() * 2 - 1));
                }
            }
        }

        var path = Path.Combine(folder, name);
        using var writer = new WavWriter(path, 1, rate, WavEncoding.Pcm24);
        writer.Write(samples);
        return path;
    }

    /// <summary>The golden path's melody: five notes of 0.6 s, 50 ms apart, each a few cents off.</summary>
    static string MelodyWav(string folder) {
        const int rate = 48_000;
        var samples = new float[(int)((5 * 0.65 + 0.5) * rate)];
        (int Midi, double Cents)[] notes = [(60, 7), (62, -6), (64, 4), (67, -8), (69, 5)];
        for (var n = 0; n < notes.Length; n++) {
            var hz = 440 * Math.Pow(2, (notes[n].Midi - 69 + notes[n].Cents / 100) / 12);
            var start = (int)Math.Round((0.1 + n * 0.65) * rate);
            var length = (int)Math.Round(0.6 * rate);
            for (var i = 0; i < length; i++) {
                var envelope = Math.Min(1, Math.Min(i / 480.0, (length - i) / 480.0));
                var phase = 2 * Math.PI * hz * i / rate;
                samples[start + i] = (float)(0.35
                    * envelope
                    * (Math.Sin(phase) + 0.25 * Math.Sin(2 * phase) + 0.1 * Math.Sin(3 * phase)));
            }
        }

        var path = Path.Combine(folder, "Melody.wav");
        using var writer = new WavWriter(path, 1, rate, WavEncoding.Pcm24);
        writer.Write(samples);
        return path;
    }

    void StartHost(params string[] extra) {
        var dll = Path.Combine(
            AppContext.BaseDirectory.Replace(
                Path.Combine("tests", "Anode.Server.Tests"),
                Path.Combine("src", "Anode.Host"),
                StringComparison.Ordinal
            ),
            "anode-host.dll"
        );
        Assert.True(File.Exists(dll), $"anode-host not built at {dll}");
        host = new() {
            StartInfo = new("dotnet", ["exec", dll, "--serve", "--null-audio", "--socket", socketPath, ..extra]) {
                RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        host.OutputDataReceived += (_, e) => Capture(e.Data);
        host.ErrorDataReceived += (_, e) => Capture(e.Data);
        host.Start();
        host.BeginOutputReadLine();
        host.BeginErrorReadLine();
    }

    void Capture(string? line) {
        if (line is not null) {
            lock (hostOutput) {
                hostOutput.AppendLine(line);
            }
        }
    }

    async Task StopHostAsync() {
        if (host is null) {
            return;
        }

        if (!host.HasExited) {
            host.Kill(true);
        }

        await host.WaitForExitAsync(TestContext.Current.CancellationToken);
        host.Dispose();
        host = null;
    }
}
