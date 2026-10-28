using Anode.Contracts;
using Anode.Ipc;
using Anode.Ipc.Segments;
using Anode.Model;
using Anode.Server.Engine;
using System.Net.Http.Json;

namespace Anode.Server.Tests;

/// <summary>
///     What the server sends a host over the socket (docs/02-phase-1.md, "Model swaps"), against
///     <see cref="FakeHost" /> listening as anode-host does: the project whole on connect, then only what changed, and
///     everything again after the host restarts.
/// </summary>
public sealed class HostLinkTests : IAsyncDisposable {
    readonly string socketPath = Path.Combine(Path.GetTempPath(), $"an-{Guid.NewGuid().ToString("n")[..8]}.sock");
    readonly string media = Directory.CreateTempSubdirectory("anode-link-").FullName;
    FakeHost? host;

    public async ValueTask DisposeAsync() {
        if (host is not null) {
            await host.DisposeAsync();
        }

        Directory.Delete(media, true);
    }

    [Fact]
    public async Task The_host_gets_the_project_whole_then_only_what_changes() {
        StartHost();
        await using var server = new ServerFixture(socketPath);
        using var client = server.CreateClient();
        await WaitConnectedAsync(server, client);

        await client.CreateProjectAsync();
        var opened = await WaitForAsync<LoadModelMessage>(static m => m.Replace);
        Assert.Empty(opened.Tracks);
        await WaitForAsync<SetCycleMessage>();
        await WaitForAsync<SetMetronomeMessage>();
        await WaitForAsync<SetStripMessage>(static m => m.TrackId == -1);

        var file = await client.ImportAsync(TestWav.Write(media, "Kick.wav", frames: 48_000));
        var withFile = await WaitForAsync<LoadModelMessage>(static m => m.Files.Count > 0);
        Assert.False(withFile.Replace);
        Assert.EndsWith("Kick.wav", Assert.Single(withFile.Files).Path, StringComparison.Ordinal);

        await client.EditAsync(new AddTrackCommand("A"), new AddTrackCommand("B", Stereo: true));
        var tracks = (await client.ProjectAsync()).Tracks;
        await client.EditAsync(new AddRegionCommand(tracks[0].Id, file.Id, 960));
        LoadModelMessage[] Patches() => [..Received<LoadModelMessage>().SkipWhile(m => m != withFile).Skip(1)];
        await ServerFixture.Eventually(() => Task.FromResult(Patches().Length == 3));
        var patches = Patches();
        Assert.Equal(3, host!.Model.Tracks.Count);
        Assert.All(patches, static m => Assert.False(m.Replace));
        Assert.Equal([0, 1, 0], patches.Select(static m => Assert.Single(m.Tracks).Id));
        Assert.True(patches[1].Tracks[0].Stereo);

        await client.EditAsync(new SetStripCommand(tracks[1].Id, -6, -1));
        var strip = await WaitForAsync<SetStripMessage>(static m => m.TrackId == 1);
        Assert.Equal((Units.DbToGain(-6), -1f), (strip.Strip.Gain, strip.Strip.Pan));

        await client.EditAsync(new SetTrackCommand(tracks[0].Id, RecordArmed: true, Input: new(3, true)));
        var io = await WaitForAsync<SetTrackIoMessage>();
        Assert.Equal((0, 2, true, true), (io.TrackId, io.InputChannel, io.Stereo, io.RecordArmed));

        await client.EditAsync(new SetCycleCommand(true, 960 * 4, 960 * 8));
        var cycle = await WaitForAsync<SetCycleMessage>(static m => m.Enabled);
        Assert.Equal((96_000L, 192_000L), (cycle.StartSample, cycle.EndSample));
        await client.EditAsync(new SetMetronomeCommand(true, LevelDb: 0));
        Assert.Equal(1f, (await WaitForAsync<SetMetronomeMessage>(static m => m.Enabled)).Gain);
        await client.EditAsync(new SetMetronomeCommand(CountIn: true));
        Assert.True((await WaitForAsync<SetMetronomeMessage>(static m => m.CountIn)).Enabled);

        var before = Received<LoadModelMessage>().Count(static m => m.Replace);
        await client.EditAsync(new SetTempoCommand(60));
        await ServerFixture.Eventually(() =>
            Task.FromResult(Received<LoadModelMessage>().Count(static m => m.Replace) == before + 1)
        );

        await client.EditAsync(new RemoveTrackCommand(tracks[0].Id));
        var removed = await WaitForAsync<LoadModelMessage>(static m => m is { Replace: true, Tracks.Count: 1 });
        Assert.Equal(1, removed.Tracks[0].Id);
        await ServerFixture.Eventually(() =>
            Task.FromResult(host!.Model.Tracks.SequenceEqual([TrackEntry.MetronomeTrack, 1]))
        );

        await client.PostAsync("/api/v1/project/close", null, TestContext.Current.CancellationToken);
        await WaitForAsync<TransportMessage>(static m => m.Action == TransportAction.Stop);
        await ServerFixture.Eventually(() =>
            Task.FromResult(host!.Model.Tracks.SequenceEqual([TrackEntry.MetronomeTrack]))
        );
    }

    [Fact]
    public async Task A_restarted_host_gets_everything_again() {
        StartHost();
        await using var server = new ServerFixture(socketPath);
        using var client = server.CreateClient();
        await WaitConnectedAsync(server, client);
        await client.CreateProjectAsync();
        await client.EditAsync(new AddTrackCommand(), new SetStripCommand(VolumeDb: -12));
        var track = (await client.ProjectAsync()).Tracks[0];
        await client.EditAsync(
            new SetStripCommand(track.Id, Mute: true),
            new SetTrackCommand(track.Id, RecordArmed: true)
        );
        await WaitForAsync<SetStripMessage>(static m => m.TrackId == 0);

        await host!.DisposeAsync();
        host = null;
        await ServerFixture.Eventually(async () =>
            (await server.SystemAsync(client)).Engine.State == EngineState.Disconnected
        );
        StartHost();
        await WaitConnectedAsync(server, client);

        var load = await WaitForAsync<LoadModelMessage>(static m => m.Replace);
        var engineTrack = Assert.Single(load.Tracks);
        Assert.Equal((0, true, true), (engineTrack.Id, engineTrack.Strip.Mute, engineTrack.RecordArmed));
        Assert.Equal(Units.DbToGain(-12), load.Master.Gain);
        Assert.EndsWith("Audio Files", load.RecordDirectory, StringComparison.Ordinal);
        Assert.Equal(
            Units.DbToGain(-12),
            (await WaitForAsync<SetStripMessage>(static m => m.TrackId == -1)).Strip.Gain
        );
        await WaitForAsync<SetCycleMessage>();
        await WaitForAsync<SetMetronomeMessage>();
        Assert.Equal([TrackEntry.MetronomeTrack, 0], host!.Model.Tracks);

        // Transport through the new link, in order after the sync.
        await client.TransportAsync(TransportCommand.Play, 0);
        await WaitForAsync<TransportMessage>(static m => m.Action == TransportAction.Play);
    }

    [Fact]
    public async Task Instruments_parameters_MIDI_IO_thru_and_live_MIDI_reach_the_host() {
        StartHost();
        await using var server = new ServerFixture(socketPath);
        using var client = server.CreateClient();
        await using var events = await EventStream.ConnectAsync(server);
        await WaitConnectedAsync(server, client);
        // The catalogue and the ports come after the hello (whether this socket saw their events or not).
        await ServerFixture.Eventually(async () =>
            (await client.GetFromJsonAsync(
                "/api/v1/plugins",
                ContractsJsonContext.Default.IReadOnlyListPluginInfo,
                TestContext.Current.CancellationToken
            ))!.Count
            == FakeHost.Catalogue.Count
        );
        await client.CreateProjectAsync();
        await WaitForAsync<LoadModelMessage>(static m => m.Replace);

        // A new instrument track rides on its patch with its instrument and MIDI settings.
        await client.EditAsync(
            new AddTrackCommand(Kind: TrackKind.Instrument),
            new AddTrackCommand(Kind: TrackKind.Midi)
        );
        var tracks = (await client.ProjectAsync()).Tracks;
        var patch = await WaitForAsync<LoadModelMessage>(static m => m.Tracks.Any(static t => t.Instrument is not null)
        );
        var engineTrack = patch.Tracks.Single(static t => t.Instrument is not null);
        Assert.Equal(FakeHost.Catalogue[0].Id, engineTrack.Instrument!.PluginId);
        Assert.Equal(FakeHost.Catalogue[0].Parameters.Count, engineTrack.Instrument.Parameters.Count);
        Assert.Equal(new(null, 0, null, 0), engineTrack.Midi);
        var inst = engineTrack.Id;
        var ext = (await WaitForAsync<LoadModelMessage>(static m => m.Tracks.Any(static t => t is {
                    Instrument: null, Midi: not null
                }
            )
        )).Tracks[0].Id;

        var parameter = FakeHost.Catalogue[0].Parameters[0];
        await client.EditAsync(new SetParameterCommand(tracks[0].Id, parameter.Id, parameter.Max));
        Assert.Equal(
            parameter.Max,
            (await WaitForAsync<SetParameterMessage>(m => m.ParameterId == parameter.Id)).Value
        );
        await client.EditAsync(new SetInstrumentCommand(tracks[0].Id, FakeHost.FakeClapId));
        Assert.Equal(
            inst,
            (await WaitForAsync<SetInstrumentMessage>(static m => m.Instrument?.PluginId == FakeHost.FakeClapId))
            .TrackId
        );
        await client.EditAsync(new SetMidiIoCommand(tracks[1].Id, Output: new("fake-out", 4)));
        var io = await WaitForAsync<SetMidiIoMessage>();
        Assert.Equal((ext, "fake-out", 4), (io.TrackId, io.Midi.OutputDevice, io.Midi.OutputChannel));

        // Notes reach the host compiled: a region with one note is a note-on and a note-off.
        await client.EditAsync(new AddMidiRegionCommand(tracks[0].Id, 960, 3840));
        var region = (await client.ProjectAsync()).MidiRegions[0].Id;
        await client.EditAsync(new AddNotesCommand(region, [new(60, 100, 1, 0, 960)]));
        await ServerFixture.Eventually(() =>
            Task.FromResult(
                host!.EventsOf(inst)
                    .Select(static e => (e.SamplePos, e.Kind))
                    .SequenceEqual(
                        [
                            (24_000L, PlaybackEventKind.NoteOn), (48_000L, PlaybackEventKind.NoteOff)
                        ]
                    )
            )
        );

        await client.PostAsJsonAsync(
            "/api/v1/midi/thru",
            new(tracks[0].Id),
            ContractsJsonContext.Default.MidiThruRequest,
            TestContext.Current.CancellationToken
        );
        await ServerFixture.Eventually(() => Task.FromResult(host!.Thru == inst));
        await client.PostAsJsonAsync(
            "/api/v1/midi/live",
            new(tracks[0].Id, [new(0x90, 64, 80)]),
            ContractsJsonContext.Default.LiveMidiRequest,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(inst, (await WaitForAsync<LiveMidiMessage>()).TrackId);
        var activity = await events.WaitAsync<MidiActivityEvent>();
        Assert.Equal(tracks[0].Id, Assert.Single(activity.Events).TrackId);

        // Save asks for the states; a restarted host gets them back with the instrument, and the thru track again.
        await client.PostAsync("/api/v1/project/save", null, TestContext.Current.CancellationToken);
        await WaitForAsync<RequestInstrumentStatesMessage>();
        await host!.DisposeAsync();
        host = null;
        await ServerFixture.Eventually(async () =>
            (await server.SystemAsync(client)).Engine.State == EngineState.Disconnected
        );
        StartHost();
        await WaitConnectedAsync(server, client);
        var load = await WaitForAsync<LoadModelMessage>(static m => m.Replace);
        var restored = load.Tracks.Single(t => t.Id == inst).Instrument!;
        Assert.Equal(FakeHost.FakeClapId, restored.PluginId);
        Assert.Equal(
            $"fake-state:{FakeHost.FakeClapId}",
            System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(restored.State!))
        );
        await ServerFixture.Eventually(() => Task.FromResult(host!.Thru == inst));
    }

    [Fact]
    public async Task Punch_tempo_tracks_stacks_comps_preview_and_flashback_reach_the_host() {
        StartHost();
        await using var server = new ServerFixture(socketPath);
        using var client = server.CreateClient();
        await using var events = await EventStream.ConnectAsync(server);
        await WaitConnectedAsync(server, client);
        await client.CreateProjectAsync();
        await WaitForAsync<LoadModelMessage>(static m => m.Replace);
        // The punch range travels in samples with every project.
        Assert.Equal(new SetPunchMessage(false, 0, 4 * 96_000), await WaitForAsync<SetPunchMessage>());

        await client.EditAsync(new SetRecordingCommand(Autopunch: true, PunchInTick: 960, PunchOutTick: 1920));
        Assert.Equal(new SetPunchMessage(true, 24_000, 48_000), await WaitForAsync<SetPunchMessage>(static m => m.Enabled));

        // A tempo track change: the whole model again, and the punch range moves with the tempo.
        var wholes = Received<LoadModelMessage>().Count(static m => m.Replace);
        await client.EditAsync(new SetTempoEventsCommand([new(0, 60, TempoCurve.Step), new(3840, 120, TempoCurve.Ramp)]));
        await ServerFixture.Eventually(() => Task.FromResult(Received<LoadModelMessage>().Count(static m => m.Replace) > wholes));
        Assert.Equal(new SetPunchMessage(true, 48_000, 96_000), await WaitForAsync<SetPunchMessage>(static m => m.InSample == 48_000));
        Assert.Equal(48_000, host!.EventsOf(TrackEntry.MetronomeTrack)[1].SamplePos); // a beat at 60 BPM

        // A stack has no engine track; its strip reaches its members.
        await client.EditAsync(new AddTrackCommand("A"), new AddTrackCommand("B"));
        var tracks = (await client.ProjectAsync()).Tracks;
        await WaitForAsync<LoadModelMessage>(static m => m.Tracks.Any(static t => t.Id == 1));
        await client.EditAsync(new CreateStackCommand([tracks[0].Id, tracks[1].Id], "Stack"));
        var stack = (await client.ProjectAsync()).Tracks[0];
        Assert.Equal(TrackKind.Stack, stack.Kind);
        await client.EditAsync(new SetStripCommand(tracks[0].Id, -6), new SetStripCommand(stack.Id, -3, Mute: true));
        var a = await WaitForAsync<SetStripMessage>(static m => m is { TrackId: 0, Strip.Mute: true });
        Assert.Equal(Units.DbToGain(-9), a.Strip.Gain);
        var b = await WaitForAsync<SetStripMessage>(static m => m is { TrackId: 1, Strip.Mute: true });
        Assert.Equal(Units.DbToGain(-3), b.Strip.Gain);
        Assert.Equal([TrackEntry.MetronomeTrack, 0, 1], host.Model.Tracks);

        // A comp reaches the host as crossfading playlist entries.
        var file = await client.ImportAsync(TestWav.Write(media, "Take.wav", frames: 192_000));
        await client.EditAsync(new AddRegionCommand(tracks[0].Id, file.Id, 0), new AddRegionCommand(tracks[0].Id, file.Id, 0));
        var project = await client.ProjectAsync();
        await client.EditAsync(new PackTakeFolderCommand([..project.Regions.Select(static r => r.Id)]));
        var folder = (await client.ProjectAsync()).TakeFolders[0];
        await client.EditAsync(
            new SetCompCommand(folder.Id, [new(0, 960, folder.Takes[0].Id), new(960, 1920, folder.Takes[1].Id)])
        );
        await ServerFixture.Eventually(() => Task.FromResult(host.Model.Tracks.Contains(0) && PlaylistOf(0).Length == 2));
        var playlist = PlaylistOf(0);
        Assert.True(playlist[0].Start + playlist[0].Length > playlist[1].Start); // they overlap ...
        Assert.Equal(playlist[0].FadeOut, playlist[1].FadeIn); // ... as a crossfade

        // Preview and Flashback Capture.
        var wav = TestWav.Write(media, "Preview.wav");
        await client.PostAsJsonAsync("/api/v1/files/preview", new PreviewRequest(wav), ContractsJsonContext.Default.PreviewRequest, TestContext.Current.CancellationToken);
        Assert.Equal(wav, (await WaitForAsync<PreviewFileMessage>()).Path);
        await client.EditAsync(new SetTrackCommand(tracks[1].Id, RecordArmed: true));
        await client.TransportAsync(TransportCommand.Play, 0);
        await events.WaitAsync<TransportEvent>(static e => e.Transport is { State: TransportState.Playing, PositionSamples: > 12_000 });
        await client.PostAsync("/api/v1/transport/capture", null, TestContext.Current.CancellationToken);
        await WaitForAsync<FlashbackCaptureMessage>();
        var captured = await events.WaitAsync<ProjectChangedEvent>(static e => e.Delta.Ops.Any(static o => o is RegionUpserted r && r.Region.StartTick == 0 && r.Region.Name == "B"));
        Assert.Contains(captured.Delta.Ops, static o => o is AudioFileUpserted);
    }

    AudioPlaylistEntry[] PlaylistOf(int track) => host!.PlaylistOf(track);

    void StartHost() {
        host = new(EngineConnection.Version);
        host.Listen(socketPath);
    }

    static Task WaitConnectedAsync(ServerFixture server, HttpClient client) =>
        ServerFixture.Eventually(async () => (await server.SystemAsync(client)).Engine.State
            == EngineState.Connected
        );

    IReadOnlyList<T> Received<T>() where T : IpcMessage => [..host!.Received.OfType<T>()];

    async Task<T> WaitForAsync<T>(Func<T, bool>? match = null) where T : IpcMessage {
        T? found = null;
        await ServerFixture.Eventually(
            () => {
                found = host!.Received.OfType<T>().LastOrDefault(m => match?.Invoke(m) ?? true);
                return Task.FromResult(found is not null);
            },
            10
        );
        return found!;
    }
}
