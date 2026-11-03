using Anode.Contracts;
using Anode.Interchange.Adm;
using Anode.Media.Opus;
using Anode.Model.Audio;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Anode.Server.Tests;

/// <summary>
///     Phase 10 at integration (docs/11-phase-10.md, "Integration" step 3): anode-host in its own process with workstream
///     A's multichannel graph and D's spatial DSP (and SADIE II set), the Server with its real registrations (D's ADM
///     writer, E's Opus encoder and listen-in framer, B's tap reader and pump): a 7.1.4 bounce of a Surround-Panner track
///     and an object, a stereo effect on a 7.1.4 bus, monitoring (the BS.775 down-mix and binaural), the ADM export's bed
///     and object, and listen-in from the master tap.
/// </summary>
public sealed class Phase10TwoProcessTests : IAsyncDisposable {
    const int Rate = 48_000;
    const int Bar = 3840; // 120 bpm: a bar is 2 s

    static CancellationToken Token => TestContext.Current.CancellationToken;

    readonly StringBuilder hostOutput = new();
    readonly string media = Directory.CreateTempSubdirectory("anode-phase10-two-").FullName;
    readonly string socketPath = Path.Combine(Path.GetTempPath(), $"an-{Guid.NewGuid().ToString("n")[..8]}.sock");
    Process? host;
    ServerFixture? server;
    HttpClient? client;
    EventStream? events;

    HttpClient Client => client!;

    public async ValueTask DisposeAsync() {
        if (events is not null) {
            await events.DisposeAsync();
        }

        client?.Dispose();
        if (server is not null) {
            await server.DisposeAsync();
        }

        if (host is not null) {
            if (!host.HasExited) {
                host.Kill(true);
            }

            await host.WaitForExitAsync(CancellationToken.None);
            host.Dispose();
        }

        File.Delete(socketPath);
        Directory.Delete(media, true);
        lock (hostOutput) {
            TestContext.Current.TestOutputHelper?.WriteLine("anode-host output:\n" + hostOutput);
        }
    }

    async Task StartAsync() {
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
            StartInfo = new("dotnet", ["exec", dll, "--serve", "--null-audio", "--socket", socketPath]) {
                RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        host.OutputDataReceived += (_, e) => Capture(e.Data);
        host.ErrorDataReceived += (_, e) => Capture(e.Data);
        host.Start();
        host.BeginOutputReadLine();
        host.BeginErrorReadLine();
        server = new(socketPath);
        client = server.CreateClient();
        events = await EventStream.ConnectAsync(server);
        await ServerFixture.Eventually(
            async () => (await server.SystemAsync(Client)).Engine.State == EngineState.Connected,
            60
        );
    }

    void Capture(string? line) {
        if (line is not null) {
            lock (hostOutput) {
                hostOutput.AppendLine(line);
            }
        }
    }

    /// <summary>A mono WAV of sines (hz, peak dBFS), <paramref name="seconds" /> long, imported.</summary>
    async Task<AudioFile> ToneAsync(string name, double seconds, params (double Hz, double Db)[] tones) {
        var path = Path.Combine(media, name + ".wav");
        var samples = new float[(int)(seconds * Rate)];
        for (var i = 0; i < samples.Length; i++) {
            foreach (var (hz, level) in tones) {
                samples[i] += (float)(Math.Pow(10, level / 20) * Math.Sin(2 * Math.PI * hz * i / Rate));
            }
        }

        using (var writer = new WavWriter(path, 1, Rate, WavEncoding.Float32)) {
            writer.Write(samples);
        }

        return await Client.ImportAsync(path);
    }

    async Task<string> TrackAsync(string name, AudioFile file) {
        await Client.EditAsync(new AddTrackCommand(name));
        var id = (await Client.ProjectAsync()).Tracks.Last(t => t.Name == name).Id;
        await Client.EditAsync(new AddRegionCommand(id, file.Id, 0));
        return id;
    }

    /// <summary>A 32-bit float bounce of 0…<paramref name="endTick" />, read back in its layout's order.</summary>
    async Task<(float[][] Channels, Dsp.Spatial.SpeakerLayout? Layout)> BounceAsync(
        string name,
        long endTick,
        bool monitoring = false
    ) {
        var response = await Client.PostAsJsonAsync(
            "/api/v1/project/bounce",
            new BounceRequest(
                name,
                media,
                0,
                endTick,
                BitDepth: 32,
                Dither: DitherMode.None,
                Monitoring: monitoring
            ),
            ContractsJsonContext.Default.BounceRequest,
            Token
        );
        var job = (await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.Job, Token))!;
        var done = await events!.WaitAsync<JobEvent>(
            e => e.Job.Id == job.Id && e.Job.State is JobState.Done or JobState.Failed,
            120
        );
        Assert.True(done.Job.State == JobState.Done, done.Job.Message);
        var (channels, rate, layout) = BounceProcessor.ReadLayout(done.Job.Files[0]);
        Assert.Equal(Rate, rate);
        return (channels, layout);
    }

    static double Db(double value) => 20 * Math.Log10(Math.Max(value, 1e-12));

    static double Rms(ReadOnlySpan<float> samples) {
        double sum = 0;
        foreach (var s in samples) {
            sum += s * s;
        }

        return Math.Sqrt(sum / Math.Max(1, samples.Length));
    }

    /// <summary>The peak amplitude of the <paramref name="hz" /> component (Goertzel over whole cycles).</summary>
    static double Amplitude(ReadOnlySpan<float> samples, double hz) {
        var n = (int)(Math.Floor(samples.Length * hz / Rate) * Rate / hz);
        double re = 0, im = 0;
        for (var i = 0; i < n; i++) {
            var phase = 2 * Math.PI * hz * i / Rate;
            re += samples[i] * Math.Cos(phase);
            im += samples[i] * Math.Sin(phase);
        }

        return 2 * Math.Sqrt(re * re + im * im) / Math.Max(1, n);
    }

    /// <summary>The middle of a render: past the panners' first glide, before the end.</summary>
    static ReadOnlySpan<float> Middle(float[] channel) => channel.AsSpan(Rate / 2, channel.Length - Rate);

    [Fact]
    public async Task A_714_bounce_has_a_surround_panned_track_in_its_speaker_and_LFE_and_an_object_moving_overhead() {
        await StartAsync();
        await Client.CreateProjectAsync("Surround");
        await Client.EditAsync(new SetSurroundFormatCommand(ChannelFormat.Surround714));
        var tone = await ToneAsync("Tone", 8, (1000, -12), (60, -12));
        var sweep = await ToneAsync("Bee", 8, (1000, -12));
        var bed = await TrackAsync("Bed", tone);
        var bee = await TrackAsync("Bee", sweep);
        // The puck at Ls (7.1.4's side surround, M+090) as a point, the LFE send at 0 dB.
        await Client.EditAsync(
            new SetPannerCommand(bed, Angle: 90, Diversity: 1, LfeLevelDb: 0),
            new SetPannerCommand(bee, PannerMode.Object, Azimuth: 30, ObjectElevation: 0, Distance: 1),
            new SetAutomationCommand(PannerTargets.ObjectAzimuth, [new(0, 30, 0), new(2 * Bar, -135, 0)], bee),
            new SetAutomationCommand(PannerTargets.ObjectElevation, [new(0, 0, 0), new(2 * Bar, 30, 0)], bee),
            new SetStripCommand(bee, Mute: true)
        );
        var (bedMix, layout) = await BounceAsync("bed", 2 * Bar);
        Assert.Equal(Dsp.Spatial.SpeakerFormat.Surround714, layout?.Format);
        Assert.Equal(12, bedMix.Length);
        var ls = Db(Rms(Middle(bedMix[4])));
        Assert.InRange(ls, -14.5, -11.5); // two sines at −12 dBFS peak: −12 dB RMS
        for (var c = 0; c < 12; c++) {
            if (c is not (3 or 4)) {
                Assert.True(ls - Db(Rms(Middle(bedMix[c]))) >= 20, $"channel {layout!.Label(c)}");
            }
        }

        // The LFE: the 60 Hz component, the 1 kHz one filtered out.
        var lfe60 = Db(Amplitude(Middle(bedMix[3]), 60));
        var lfe1k = Db(Amplitude(Middle(bedMix[3]), 1000));
        Assert.InRange(lfe60, -13, -11);
        Assert.True(lfe60 - lfe1k > 40, $"LFE: 60 Hz {lfe60:F1} dB, 1 kHz {lfe1k:F1} dB");

        // The object from L (30°, 0°) to the top rear right (−135°, 30°) over 4 s.
        await Client.EditAsync(new SetStripCommand(bed, Mute: true), new SetStripCommand(bee, Mute: false));
        var (moving, _) = await BounceAsync("object", 2 * Bar);
        var first = moving.Select(c => Rms(c.AsSpan(0, Rate / 10))).ToArray();
        var last = moving.Select(c => Rms(c.AsSpan(4 * Rate - Rate / 10, Rate / 10))).ToArray();
        Assert.Equal(0, Array.IndexOf(first, first.Max()));
        Assert.Equal(11, Array.IndexOf(last, last.Max()));
        Assert.True(Db(first[0]) - Db(first[11]) > 20);
        Assert.True(Db(last[11]) - Db(last[0]) > 20);
    }

    [Fact]
    public async Task A_stereo_effect_on_a_714_bus_processes_every_channel_and_monitoring_down_mixes_by_BS_775_or_renders_binaurally() {
        await StartAsync();
        await Client.CreateProjectAsync("Monitoring");
        await Client.EditAsync(new SetSurroundFormatCommand(ChannelFormat.Surround714), new AddAuxCommand(5, "Bus"));
        var aux = (await Client.ProjectAsync()).Tracks.Single(t => t.Name == "Bus").Id;
        await Client.EditAsync(new SetStripFormatCommand(aux, ChannelFormat.Surround714));
        var centre = await TrackAsync("Centre", await ToneAsync("500", 6, (500, -12)));
        var side = await TrackAsync("Side", await ToneAsync("1k", 6, (1000, -12)));
        var top = await TrackAsync("Top", await ToneAsync("2k", 6, (2000, -12)));
        await Client.EditAsync(
            new SetOutputCommand(centre, "bus:5"),
            new SetOutputCommand(side, "bus:5"),
            new SetOutputCommand(top, "bus:5")
        );
        await Client.EditAsync(
            new SetPannerCommand(centre, Angle: 0, Diversity: 1),
            new SetPannerCommand(side, Angle: 90, Diversity: 1),
            new SetPannerCommand(top, Angle: 45, Diversity: 1, SurroundElevation: 30)
        );
        var (plain, _) = await BounceAsync("plain", Bar);

        // Channel EQ's output at −6 dB on the 7.1.4 bus: every channel 6 dB down, not only L and R.
        await Client.EditAsync(new AddInsertCommand("anode.channel-eq", aux));
        var insert = (await Client.ProjectAsync()).Tracks.Single(t => t.Id == aux).Strip.Inserts[0].Id;
        await Client.EditAsync(new SetInsertParameterCommand(insert, "output", -6, aux));
        var (eq, _) = await BounceAsync("eq", Bar);
        foreach (var (c, hz) in new[] { (2, 500.0), (4, 1000.0), (8, 2000.0) }) {
            var drop = Db(Amplitude(Middle(plain[c]), hz)) - Db(Amplitude(Middle(eq[c]), hz));
            Assert.InRange(drop, 5.9, 6.1);
        }

        // The down-mix to stereo: BS.775 (C at −3 dB, the side surround at −6 dB into its side; the top front at −3 dB).
        await Client.PutAsJsonAsync(
            "/api/v1/settings/monitoring",
            new MonitoringSettings(false, ChannelFormat.Stereo, []),
            ContractsJsonContext.Default.MonitoringSettings,
            Token
        );
        var (stereo, _) = await BounceAsync("downmix", Bar, true);
        Assert.Equal(2, stereo.Length);
        foreach (var (c, hz, left, right) in new[] {
                     (2, 500.0, -3.01, -3.01), (4, 1000.0, -6.02, -999), (8, 2000.0, -3.01, -999)
                 }) {
            var source = Db(Amplitude(Middle(eq[c]), hz));
            Assert.InRange(Db(Amplitude(Middle(stereo[0]), hz)) - source, left - 0.1, left + 0.1);
            var r = Db(Amplitude(Middle(stereo[1]), hz)) - source;
            if (right > -100) {
                Assert.InRange(r, right - 0.1, right + 0.1);
            } else {
                Assert.True(r < -60, $"{hz} Hz in R': {r:F1} dB");
            }
        }

        // Binaural: the side surround's source (90° left) at the left ear ≥ 6 dB louder, the right 0.5–0.8 ms later.
        await Client.EditAsync(new SetStripCommand(centre, Mute: true), new SetStripCommand(top, Mute: true));
        await Client.PutAsJsonAsync(
            "/api/v1/settings/monitoring",
            new MonitoringSettings(true, null, []),
            ContractsJsonContext.Default.MonitoringSettings,
            Token
        );
        var (ears, _) = await BounceAsync("binaural", Bar, true);
        Assert.Equal(2, ears.Length);
        var ild = Db(Rms(Middle(ears[0]))) - Db(Rms(Middle(ears[1])));
        Assert.True(ild >= 6, $"ILD {ild:F1} dB");
        var itd = Lag(Middle(ears[0]), Middle(ears[1]), Rate / 500) * 1000.0 / Rate;
        Assert.InRange(itd, 0.5, 0.8);
        // The bounce of the mix never hears the monitoring.
        var (mix, layout) = await BounceAsync("mix", Bar);
        Assert.Equal(12, mix.Length);
        Assert.Equal(Dsp.Spatial.SpeakerFormat.Surround714, layout?.Format);
    }

    /// <summary>The lag of <paramref name="b" /> behind <paramref name="a" /> (samples) by cross-correlation of a 1 kHz tone, within one period.</summary>
    static double Lag(ReadOnlySpan<float> a, ReadOnlySpan<float> b, int max) {
        var best = 0;
        var bestScore = double.MinValue;
        for (var lag = 0; lag <= max; lag++) {
            double score = 0;
            for (var i = 0; i + lag < b.Length && i < a.Length; i++) {
                score += a[i] * b[i + lag];
            }

            if (score > bestScore) {
                (best, bestScore) = (lag, score);
            }
        }

        return best;
    }

    [Fact]
    public async Task The_ADM_export_has_the_bed_as_the_master_without_objects_and_each_object_before_the_renderer() {
        await StartAsync();
        await Client.CreateProjectAsync("Adm");
        await Client.EditAsync(new SetSurroundFormatCommand(ChannelFormat.Surround714));
        var bed = await TrackAsync("Bed", await ToneAsync("Bed", 6, (500, -12)));
        var bee = await TrackAsync("Bee", await ToneAsync("Bee", 6, (1000, -12)));
        await Client.EditAsync(
            new SetStripFormatCommand(bee, ChannelFormat.Mono),
            new SetPannerCommand(bed, Angle: 135, Diversity: 1),
            new SetPannerCommand(bee, PannerMode.Object, Azimuth: -90, ObjectElevation: 0, Distance: 1),
            new SetStripCommand(TrackId: null, VolumeDb: -6) // the master's fader: on the bed and the objects
        );
        var response = await Client.PostAsJsonAsync(
            "/api/v1/project/export/adm",
            new AdmExportRequest("Mix", media, null, 0, Bar),
            ContractsJsonContext.Default.AdmExportRequest,
            Token
        );
        var job = (await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.Job, Token))!;
        var done = await events!.WaitAsync<JobEvent>(
            e => e.Job.Id == job.Id && e.Job.State is JobState.Done or JobState.Failed,
            120
        );
        Assert.True(done.Job.State == JobState.Done, done.Job.Message);
        var read = Directory.CreateTempSubdirectory("anode-adm-read-").FullName;
        try {
            var scene = AdmReader.Read(Path.Combine(media, "Mix.wav"), read);
            Assert.Equal((Rate, 2L * Rate), (scene.SampleRate, scene.Frames));
            var (bedAudio, _) = BounceProcessor.Read(Assert.Single(scene.Beds).AudioPath);
            var (objectAudio, _) = BounceProcessor.Read(Assert.Single(scene.Objects).AudioPath);
            Assert.Equal(12, bedAudio.Length);
            Assert.Single(objectAudio);
            Assert.All(bedAudio.Append(objectAudio[0]), c => Assert.Equal(2 * Rate, c.Length));
            // The bed: the bed track at Lrs (M+135, channel 6) through the master's −6 dB; no object in it.
            Assert.InRange(Db(Amplitude(Middle(bedAudio[6]), 500)), -18.2, -17.8);
            Assert.True(Db(Amplitude(Middle(bedAudio[5]), 1000)) < -80);
            // The object: its mono signal through the master's fader, before the renderer.
            Assert.InRange(Db(Amplitude(Middle(objectAudio[0]), 1000)), -18.2, -17.8);
            // And the mix bounce has the object at the right side surround (−90°): the renderer's.
            var (mix, _) = await BounceAsync("mix", Bar);
            Assert.InRange(Db(Amplitude(Middle(mix[5]), 1000)), -18.3, -17.7);
        } finally {
            Directory.Delete(read, true);
        }
    }

    [Fact]
    public async Task Listen_in_decodes_the_masters_tone_from_the_hosts_tap_through_the_framer_and_Opus() {
        await StartAsync();
        await Client.CreateProjectAsync("Listen");
        await TrackAsync("Tone", await ToneAsync("Tone", 10, (1000, -12)));
        var token = Token;
        using var socket = await server!.Server.CreateWebSocketClient()
            .ConnectAsync(new(server.Server.BaseAddress, "/api/v1/remote/listen"), token);
        var format = JsonSerializer.Deserialize(
            Encoding.UTF8.GetString(await ReceiveAsync(socket)),
            ContractsJsonContext.Default.ListenInFormat
        );
        Assert.Equal(new(48_000, 2, 960, 128_000, Rate), format);
        await Client.TransportAsync(TransportCommand.Play, 0);
        using var decoder = new OpusDecoder();
        var decoded = new List<float>();
        var pcm = new float[960 * 2];
        long firstTransport = -1;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (decoded.Count < 2 * Rate && DateTime.UtcNow < deadline) {
            var frame = await ReceiveAsync(socket);
            Assert.True(ListenIn.TryReadHeader(frame, out _, out var transport, out var flags, out var packet));
            if ((flags & ListenIn.FlagPlaying) == 0) {
                continue;
            }

            if (firstTransport < 0) {
                firstTransport = transport;
            }

            var n = decoder.Decode(packet, pcm);
            for (var i = 0; i < n; i++) {
                decoded.Add(pcm[2 * i]);
            }
        }

        await Client.TransportAsync(TransportCommand.Stop);
        Assert.True(firstTransport >= 0 && decoded.Count >= 2 * Rate, "no playing frames");
        var steady = decoded.Skip(Rate / 4).Take(Rate).ToArray();
        Assert.InRange(Db(Amplitude(steady, 1000)), -13, -11); // a mono track's −12 dBFS tone, panned centre (−3 dB + 3 dB law)
        Assert.True(Db(Amplitude(steady, 998)) < Db(Amplitude(steady, 1000)));
        var state = await Client.GetFromJsonAsync(
            "/api/v1/remote/listen/state",
            ContractsJsonContext.Default.ListenInState,
            token
        );
        Assert.True(state is { On: true, Listeners: 1, LatencyMs: > 0 and < 500 }, $"{state}");
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, token);
    }

    static async Task<byte[]> ReceiveAsync(WebSocket socket) {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var buffer = new byte[8192];
        var length = 0;
        WebSocketReceiveResult result;
        do {
            result = await socket.ReceiveAsync(new(buffer, length, buffer.Length - length), timeout.Token);
            length += result.Count;
        } while (!result.EndOfMessage);

        return buffer[..length];
    }
}
