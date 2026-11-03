using Anode.Contracts;
using Anode.Dsp.Mastering;
using Anode.Model.Audio;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;

namespace Anode.Server.Tests;

/// <summary>
///     Phase 11 at integration (docs/12-phase-11.md, "Integration" step 3): anode-host in its own process, the Server with
///     its real registrations: Session Player regions through the compiler into the host (D's generator, a kit from the
///     test pack), Pitch Source, the Environment on live MIDI, the Mastering Assistant on a bounce, and the Down Mixer and
///     Binaural Post-Processing inserts.
/// </summary>
public sealed class Phase11TwoProcessTests : IAsyncDisposable {
    const int Rate = 48_000;
    const int Bar = 3840; // 120 bpm: a bar is 2 s

    static CancellationToken Token => TestContext.Current.CancellationToken;

    readonly StringBuilder hostOutput = new();
    readonly string media = Directory.CreateTempSubdirectory("anode-phase11-two-").FullName;
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

    async Task StartAsync(string name) {
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
        await Client.CreateProjectAsync(name);
    }

    void Capture(string? line) {
        if (line is not null) {
            lock (hostOutput) {
                hostOutput.AppendLine(line);
            }
        }
    }

    /// <summary>A mono WAV from a sample function, imported.</summary>
    async Task<AudioFile> FileAsync(string name, double seconds, Func<int, float> sample) {
        var path = Path.Combine(media, name + ".wav");
        var samples = new float[(int)(seconds * Rate)];
        for (var i = 0; i < samples.Length; i++) {
            samples[i] = sample(i);
        }

        using (var writer = new WavWriter(path, 1, Rate, WavEncoding.Float32)) {
            writer.Write(samples);
        }

        return await Client.ImportAsync(path);
    }

    static Func<int, float> Sine(double hz, double db) =>
        i => (float)(Math.Pow(10, db / 20) * Math.Sin(2 * Math.PI * hz * i / Rate));

    async Task<string> TrackAsync(string name, AudioFile file, int bus = 0, double? angle = null) {
        await Client.EditAsync(new AddTrackCommand(name));
        var id = (await Client.ProjectAsync()).Tracks.Last(t => t.Name == name).Id;
        await Client.EditAsync(new AddRegionCommand(id, file.Id, 0));
        if (bus > 0) {
            await Client.EditAsync(
                new SetOutputCommand(id, $"bus:{bus}"),
                new SetPannerCommand(id, PannerMode.Surround, angle, 1)
            );
        }

        return id;
    }

    async Task<(string Track, string Region)> InstrumentAsync(string name, string pluginId, int bars) {
        await Client.EditAsync(new AddTrackCommand(name, Kind: TrackKind.Instrument, PluginId: pluginId));
        var track = (await Client.ProjectAsync()).Tracks.Last(t => t.Name == name).Id;
        await Client.EditAsync(new AddMidiRegionCommand(track, 0, bars * Bar));
        return (track, (await Client.ProjectAsync()).MidiRegions.First(r => r.TrackId == track).Id);
    }

    /// <summary>A 32-bit float bounce of 0…<paramref name="endTick" /> (a stem when a track is named), read back.</summary>
    async Task<float[][]> BounceAsync(string name, long endTick, string? track = null) {
        var response = await Client.PostAsJsonAsync(
            "/api/v1/project/bounce",
            new(
                name,
                media,
                0,
                endTick,
                track is null ? BounceSource.Mix : BounceSource.Stems,
                track is null ? null : [track],
                BitDepth: 32,
                Dither: DitherMode.None
            ),
            ContractsJsonContext.Default.BounceRequest,
            Token
        );
        var job = (await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.Job, Token))!;
        await DoneAsync(job);
        var finished = await Client.GetFromJsonAsync($"/api/v1/jobs/{job.Id}", ContractsJsonContext.Default.Job, Token);
        var (channels, rate, _) = BounceProcessor.ReadLayout(finished!.Files[0]);
        Assert.Equal(Rate, rate);
        return channels;
    }

    async Task DoneAsync(Job job) {
        var done = await events!.WaitAsync<JobEvent>(
            e => e.Job.Id == job.Id && e.Job.State is JobState.Done or JobState.Failed,
            120
        );
        Assert.True(done.Job.State == JobState.Done, done.Job.Message);
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

    /// <summary>The cents between the strongest component within a fifth of <paramref name="note" /> and that note.</summary>
    static double CentsOff(ReadOnlySpan<float> samples, int note) {
        var target = 440 * Math.Pow(2, (note - 69) / 12.0);
        var best = target;
        var strongest = 0.0;
        for (var cents = -300.0; cents <= 300; cents += 0.5) {
            var hz = target * Math.Pow(2, cents / 1200);
            var amplitude = Amplitude(samples, hz);
            if (amplitude > strongest) {
                strongest = amplitude;
                best = hz;
            }
        }

        return 1200 * Math.Log2(best / target);
    }

    static PitchName N(PitchStep step) => new(step, 0);

    [Fact]
    public async Task A_Drummer_s_generated_notes_play_through_its_kit_each_piece_on_its_own_strip() {
        var pack = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/content/TestPack.anodepack")
        );
        Assert.SkipUnless(File.Exists(pack), "No test pack: run node scripts/build-content.mjs --test.");
        await StartAsync("Drummer");
        (await Client.PostAsJsonAsync(
            "/api/v1/library/packs",
            new(pack),
            ContractsJsonContext.Default.InstallPackRequest,
            Token
        )).EnsureSuccessStatusCode();
        await Client.EditAsync(new AddSessionPlayerTrackCommand(SessionPlayerKind.Drummer, "drums-rock"));
        var project = await Client.ProjectAsync();
        var drummer = project.Tracks.Single(static t => t.Kind == TrackKind.Drummer);
        var region = project.SessionPlayerRegions.Single();
        var notes = (await Client.GetFromJsonAsync(
            $"/api/v1/project/regions/{region.Id}/session-player",
            ContractsJsonContext.Default.SessionPlayerNotes,
            Token
        ))!.Notes;
        Assert.NotEmpty(notes);
        var kick = drummer.DrumMachine!.Pads.Single(static p => p.Note == DrumKitNotes.Kick);
        Assert.Equal("Kick", project.Tracks.Single(t => t.Id == kick.TrackId).Name);
        var stem = (await BounceAsync("kick", region.LengthTicks, kick.TrackId))[0];
        var ticksPerSample = 960.0 / (Rate / 2.0);
        var kicks = notes.Where(static n => n.Pitch == DrumKitNotes.Kick).ToList();
        Assert.NotEmpty(kicks);
        foreach (var hit in kicks) {
            var at = (int)(hit.StartTick / ticksPerSample);
            Assert.True(Db(Rms(stem.AsSpan(at, 2400))) > -40, $"no kick at tick {hit.StartTick}");
        }

        var mix = (await BounceAsync("drums", region.LengthTicks))[0];
        Assert.True(Db(Rms(mix)) > Db(Rms(stem)), "the other pieces are in the mix");
    }

    [Fact]
    public async Task Pitch_Source_moves_MIDI_and_audio_by_each_chord_s_root_interval() {
        await StartAsync("Pitch Source");
        var c = new RegionKey(N(PitchStep.C), KeyMode.Major);
        await Client.EditAsync(
            new AddChordsCommand(
                [
                    new(0, N(PitchStep.C), ChordQualities.Major), new(Bar, N(PitchStep.A), ChordQualities.Minor),
                    new(2 * Bar, N(PitchStep.F), ChordQualities.Major),
                    new(3 * Bar, N(PitchStep.G), ChordQualities.Major)
                ]
            )
        );
        int[] intervals = [0, -3, 5, -5];
        var (track, region) = await InstrumentAsync("Bass", "anode.synth", 4);
        await Client.EditAsync(
            new AddNotesCommand(region, [..intervals.Select(static (_, bar) => new NewNote(48, 100, 1, bar * Bar, Bar))]),
            new SetRegionKeyCommand([region], c),
            new SetPitchSourceCommand([region], PitchSourceMode.Root)
        );
        var midi = (await BounceAsync("midi", 4 * Bar, track))[0];
        for (var bar = 0; bar < 4; bar++) {
            var cents = CentsOff(midi.AsSpan(2 * Rate * bar + Rate / 4, Rate), 48 + intervals[bar]);
            Assert.True(Math.Abs(cents) < 10, $"MIDI bar {bar + 1}: {cents:F1} cents");
        }

        // Chords mode: C stays on Am's C, moves to F's C, and down to G's B.
        await Client.EditAsync(new SetPitchSourceCommand([region], PitchSourceMode.Chords));
        var tones = (await BounceAsync("chords", 4 * Bar, track))[0];
        var nearest = new[] { 48, 48, 48, 47 };
        for (var bar = 0; bar < 4; bar++) {
            var cents = CentsOff(tones.AsSpan(2 * Rate * bar + Rate / 4, Rate), nearest[bar]);
            Assert.True(Math.Abs(cents) < 10, $"Chords bar {bar + 1}: {cents:F1} cents");
        }

        var file = await FileAsync("C3", 8, Sine(130.8128, -12));
        var audio = await TrackAsync("Audio", file);
        var audioRegion = (await Client.ProjectAsync()).Regions.Single(r => r.TrackId == audio).Id;
        await Client.EditAsync(
            new SetRegionKeyCommand([audioRegion], c),
            new SetPitchSourceCommand([audioRegion], PitchSourceMode.Root)
        );
        var followed = (await BounceAsync("audio", 4 * Bar, audio))[0];
        for (var bar = 0; bar < 4; bar++) {
            var cents = CentsOff(followed.AsSpan(2 * Rate * bar + Rate / 4, 3 * Rate / 2), 48 + intervals[bar]);
            Assert.True(Math.Abs(cents) < 5, $"audio bar {bar + 1}: {cents:F1} cents");
        }
    }

    [Fact]
    public async Task The_Environment_transforms_arpeggiates_and_records_live_MIDI_and_a_fader_moves_the_strip() {
        await StartAsync("Environment");
        var (track, _) = await InstrumentAsync("Keys", "anode.synth", 1);
        await Client.EditAsync(
            new AddEnvironmentObjectCommand(EnvironmentObjectKind.PhysicalInput, 0, 0),
            new AddEnvironmentObjectCommand(
                EnvironmentObjectKind.Transformer,
                100,
                0,
                Conditions: [new(TransformField.Status, TransformConditionOp.Inside, 128, 159)],
                Operations: [new(TransformField.Pitch, TransformOperationKind.Add, 12)]
            ),
            new AddEnvironmentObjectCommand(EnvironmentObjectKind.Arpeggiator, 200, 0),
            new AddEnvironmentObjectCommand(EnvironmentObjectKind.TrackInput, 300, 0, TrackId: track),
            new AddEnvironmentObjectCommand(EnvironmentObjectKind.Monitor, 200, 100),
            new AddEnvironmentObjectCommand(EnvironmentObjectKind.Fader, 0, 200, Parameters: new Dictionary<string, double> {
                    ["message"] = 0, ["number"] = 7
                }
            ),
            new AddEnvironmentObjectCommand(EnvironmentObjectKind.ControlTarget, 100, 200, TrackId: track, Address: "volume")
        );
        var environment = (await Client.ProjectAsync()).Environment;
        string O(EnvironmentObjectKind kind) => environment.Objects.Single(o => o.Kind == kind).Id;
        await Client.EditAsync(
            new ConnectEnvironmentCommand(
                O(EnvironmentObjectKind.PhysicalInput),
                0,
                O(EnvironmentObjectKind.Transformer),
                0
            ),
            new ConnectEnvironmentCommand(O(EnvironmentObjectKind.Transformer), 0, O(EnvironmentObjectKind.Arpeggiator), 0),
            new ConnectEnvironmentCommand(O(EnvironmentObjectKind.Arpeggiator), 0, O(EnvironmentObjectKind.TrackInput), 0),
            new ConnectEnvironmentCommand(O(EnvironmentObjectKind.Transformer), 0, O(EnvironmentObjectKind.Monitor), 0),
            new ConnectEnvironmentCommand(O(EnvironmentObjectKind.Fader), 0, O(EnvironmentObjectKind.ControlTarget), 0),
            new SetTrackCommand(track, RecordArmed: true)
        );
        await Client.TransportAsync(TransportCommand.Locate, 4 * Bar);
        await Client.TransportAsync(TransportCommand.Record);
        async Task LiveAsync(int status, int velocity) {
            (await Client.PostAsJsonAsync(
                "/api/v1/midi/live",
                new(track, [..((int[])[60, 64, 67]).Select(p => new LiveMidiEvent(status, p, velocity))]),
                ContractsJsonContext.Default.LiveMidiRequest,
                Token
            )).EnsureSuccessStatusCode();
        }

        await LiveAsync(0x90, 100);
        var monitored = await events!.WaitAsync<EnvironmentMonitorEvent>(
            e => e.ObjectId == O(EnvironmentObjectKind.Monitor) && e.Events.Count >= 3,
            10
        );
        Assert.Equal([72, 76, 79], monitored.Events.Select(static e => e.Data1).Take(3).Order());
        await Task.Delay(1500, Token);
        await LiveAsync(0x80, 0);
        await Client.TransportAsync(TransportCommand.Stop);
        await ServerFixture.Eventually(async () =>
            (await Client.ProjectAsync()).MidiRegions.Any(r => r.TrackId == track && r.StartTick >= 3 * Bar)
        );
        var recorded = (await Client.ProjectAsync()).MidiRegions
            .Where(r => r.TrackId == track && r.StartTick >= 3 * Bar)
            .SelectMany(static r => r.Notes.Select(n => (n.Pitch, Tick: r.StartTick + n.StartTick)))
            .ToList();
        Assert.Equal([72, 76, 79], recorded.Select(static n => n.Pitch).Distinct().Order());
        Assert.All(recorded, static n => Assert.Equal(0, n.Tick % 240));

        // A fader moved on screen: CC 7 to the track's volume, in the host and then in the model.
        (await Client.PostAsJsonAsync(
            $"/api/v1/project/environment/objects/{O(EnvironmentObjectKind.Fader)}/input",
            new([new(0xB0, 7, 76)]),
            ContractsJsonContext.Default.EnvironmentInputRequest,
            Token
        )).EnsureSuccessStatusCode();
        await ServerFixture.Eventually(async () =>
            Math.Abs((await Client.ProjectAsync()).Tracks.Single(t => t.Id == track).Strip.VolumeDb + 6) < 0.5
        );
    }

    [Theory]
    [InlineData(MasteringCharacter.Clean)]
    [InlineData(MasteringCharacter.Valve)]
    public async Task The_Mastering_Assistant_brings_a_bounce_to_its_target_and_ceiling(MasteringCharacter character) {
        await StartAsync("Mastering");
        var noise = new Random(7);
        var file = await FileAsync(
            "Mix",
            12,
            i => {
                var beat = i % (Rate / 2) < Rate / 8 ? 0.4 : 0.08;
                return (float)(beat * Math.Sin(2 * Math.PI * 110 * i / Rate)
                    + 0.05 * Math.Sin(2 * Math.PI * 880 * i / Rate)
                    + beat * 0.2 * (noise.NextDouble() - 0.5));
            }
        );
        await TrackAsync("Mix", file);
        var response = await Client.PostAsJsonAsync(
            "/api/v1/project/mastering/analyse",
            new(0, 6 * Bar),
            ContractsJsonContext.Default.MasteringAnalysisRequest,
            Token
        );
        await DoneAsync((await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.Job, Token))!);
        await Client.EditAsync(new SetMasteringCommand(Character: character));
        var master = await BounceAsync($"master {character}", 6 * Bar);
        var measured = MasteringAnalyzer.Analyse(master[0], master[1], Rate);
        Assert.InRange(measured.IntegratedLufs, -14.5, -13.5);
        Assert.True(measured.TruePeakDbtp <= -1, $"{measured.TruePeakDbtp:F2} dBTP");
    }

    [Fact]
    public async Task The_Down_Mixer_and_Binaural_inserts_change_their_bus_s_format() {
        await StartAsync("Surround Buses");
        await Client.EditAsync(new AddAuxCommand(1, "Down Mix"), new AddAuxCommand(2, "Binaural"));
        var tracks = (await Client.ProjectAsync()).Tracks;
        var (downMix, binaural) = (tracks.Single(static t => t.Name == "Down Mix").Id,
            tracks.Single(static t => t.Name == "Binaural").Id);
        await Client.EditAsync(
            new SetStripFormatCommand(downMix, ChannelFormat.Surround51),
            new AddInsertCommand("anode.down-mixer", downMix),
            new SetStripFormatCommand(binaural, ChannelFormat.Surround714),
            new AddInsertCommand("anode.binaural", binaural)
        );
        await TrackAsync("Centre", await FileAsync("Centre", 4, Sine(500, -12)), 1, 0);
        await TrackAsync("Surround", await FileAsync("Surround", 4, Sine(1000, -12)), 1, 110);
        var noise = new Random(11);
        await TrackAsync(
            "Noise",
            await FileAsync("Noise", 4, _ => (float)(0.25 * (2 * noise.NextDouble() - 1))),
            2,
            90
        );
        var stereo = await BounceAsync("downmix", Bar, downMix);
        Assert.Equal(2, stereo.Length);
        var left = stereo[0].AsSpan(Rate / 2, Rate);
        var right = stereo[1].AsSpan(Rate / 2, Rate);
        Assert.Equal(-15.01, Db(Amplitude(left, 500)), 0.1);
        Assert.Equal(-15.01, Db(Amplitude(right, 500)), 0.1);
        Assert.Equal(-15.01, Db(Amplitude(left, 1000)), 0.1);
        Assert.True(Db(Amplitude(right, 1000)) < -70);

        var ears = await BounceAsync("binaural", Bar, binaural);
        Assert.Equal(2, ears.Length);
        var ild = Db(Rms(ears[0].AsSpan(Rate / 2, Rate))) - Db(Rms(ears[1].AsSpan(Rate / 2, Rate)));
        Assert.True(ild >= 6, $"ILD {ild:F1} dB");
        var lag = 0;
        var strongest = double.MinValue;
        for (var l = 0; l < 72; l++) {
            double sum = 0;
            for (var i = Rate / 2; i < Rate; i++) {
                sum += ears[0][i] * ears[1][i + l];
            }

            if (sum > strongest) {
                (strongest, lag) = (sum, l);
            }
        }

        var itd = lag * 1000.0 / Rate;
        Assert.InRange(itd, 0.5, 0.8);
    }
}
