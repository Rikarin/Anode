using Anode.Contracts;
using Anode.Ipc;
using Anode.Server.Engine;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace Anode.Server.Tests;

/// <summary>
///     Phase 2 against the in-process host (docs/03-phase-2.md): the plugin catalogue and MIDI ports, instrument tracks,
///     live MIDI and thru, MIDI recording, and instrument parameters and states across a save and reopen.
/// </summary>
public sealed class MidiApiTests {
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Plugins_and_MIDI_devices_come_from_the_engine() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await using var events = await EventStream.ConnectAsync(server);
        await WaitEngineAsync(server, client);
        IReadOnlyList<PluginInfo> plugins = [];
        await ServerFixture.Eventually(async () => (plugins = await PluginsAsync(client)).Count > 0);
        Assert.Equal([..FakeHost.Catalogue.Select(static p => p.Id)], plugins.Select(static p => p.Id));
        Assert.Equal(PluginFormat.Builtin, plugins[0].Format);
        var clap = plugins.Single(static p => p.Id == FakeHost.FakeClapId);
        Assert.Equal((PluginFormat.Clap, true), (clap.Format, clap.IsInstrument));
        Assert.Equal(["0", "1"], clap.Parameters.Select(static p => p.Id));

        var devices = await client.GetFromJsonAsync(
            "/api/v1/midi/devices",
            ContractsJsonContext.Default.MidiDevices,
            Token
        );
        Assert.Equal(["fake-in"], devices!.Inputs.Select(static p => p.Id));
        Assert.Equal(["fake-out"], devices.Outputs.Select(static p => p.Id));

        var before = events.OfType<PluginsChangedEvent>().Count();
        var rescan = await client.PostAsync("/api/v1/plugins/rescan", null, Token);
        Assert.Equal(HttpStatusCode.Accepted, rescan.StatusCode);
        await ServerFixture.Eventually(() => Task.FromResult(events.OfType<PluginsChangedEvent>().Count() > before));
        Assert.Contains(Host(server).Received, static m => m is ScanPluginsMessage);
    }

    [Fact]
    public async Task Without_an_engine_there_are_no_plugins_and_rescanning_is_refused() {
        var socket = Path.Combine(Path.GetTempPath(), $"an-{Guid.NewGuid().ToString("n")[..8]}.sock");
        await using var server = new ServerFixture(socket);
        using var client = server.CreateClient();
        Assert.Empty(await PluginsAsync(client));
        var devices = await client.GetFromJsonAsync(
            "/api/v1/midi/devices",
            ContractsJsonContext.Default.MidiDevices,
            Token
        );
        Assert.Empty(devices!.Inputs);
        await ApiCalls.AssertProblemAsync(
            await client.PostAsync("/api/v1/plugins/rescan", null, Token),
            HttpStatusCode.UnprocessableEntity
        );

        // An instrument track can still be made: the default synth, at the built-ins' defaults (if any).
        await client.CreateProjectAsync();
        await client.EditAsync(new AddTrackCommand(Kind: TrackKind.Instrument));
        Assert.Equal("anode.synth", (await client.ProjectAsync()).Tracks[0].Instrument!.PluginId);
    }

    [Fact]
    public async Task Instrument_tracks_start_at_the_catalogue_defaults_and_parameter_drags_undo_in_one_step() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await WaitEngineAsync(server, client);
        await ServerFixture.Eventually(async () => (await PluginsAsync(client)).Count > 0);
        await client.CreateProjectAsync();
        await client.EditAsync(
            new AddTrackCommand(Kind: TrackKind.Instrument),
            new AddTrackCommand(Kind: TrackKind.Instrument, PluginId: FakeHost.FakeClapId)
        );
        var tracks = (await client.ProjectAsync()).Tracks;
        var synth = FakeHost.Catalogue[0];
        Assert.Equal(synth.Id, tracks[0].Instrument!.PluginId);
        Assert.Equal(
            synth.Parameters.ToDictionary(static p => p.Id, static p => p.Default),
            tracks[0].Instrument!.Parameters
        );
        Assert.Equal(new Dictionary<string, double> { ["0"] = 0.5, ["1"] = 0 }, tracks[1].Instrument!.Parameters);

        await client.EditAsync(new SetParameterCommand(tracks[1].Id, "0", 0.6));
        await client.EditAsync(new SetParameterCommand(tracks[1].Id, "0", 0.7));
        await client.EditAsync(new SetParameterCommand(tracks[1].Id, "0", 0.8));
        var history = await client.GetFromJsonAsync(
            "/api/v1/project/history",
            ContractsJsonContext.Default.UndoHistory,
            Token
        );
        Assert.Equal(["Change Parameter", "Add Track"], history!.Undo.Select(static h => h.Label));
        var host = Host(server);
        await ServerFixture.Eventually(() =>
            Task.FromResult(
                host.Received.OfType<SetParameterMessage>().Any(static m => m is { ParameterId: "0", Value: 0.8 })
            )
        );
        await client.PostResultAsync("/api/v1/project/undo");
        Assert.Equal(0.5, (await client.ProjectAsync()).Tracks[1].Instrument!.Parameters["0"]);

        await client.EditAsync(new SetInstrumentCommand(tracks[1].Id, "anode.sampler"));
        var set = await WaitAsync<SetInstrumentMessage>(host, static m => m.Instrument?.PluginId == "anode.sampler");
        Assert.Null(set.Instrument!.State);
        var response = await client.SendCommandsAsync(null, new SetInstrumentCommand(tracks[1].Id, "com.nope"));
        await ApiCalls.AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task An_instrument_file_inside_the_project_is_kept_relative_and_sent_absolute() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await WaitEngineAsync(server, client);
        await ServerFixture.Eventually(async () => (await PluginsAsync(client)).Count > 0);
        var project = await client.CreateProjectAsync();
        await client.EditAsync(new AddTrackCommand(Kind: TrackKind.Instrument, PluginId: "anode.sampler"));
        var track = (await client.ProjectAsync()).Tracks[0].Id;
        var kits = Directory.CreateDirectory(Path.Combine(project.Path, "Kits")).FullName;
        var sfz = Path.Combine(kits, "Piano.sfz");
        await File.WriteAllTextAsync(sfz, "<region> sample=piano.wav", Token);

        await client.EditAsync(new LoadInstrumentFileCommand(track, sfz));
        Assert.Equal("Kits/Piano.sfz", (await client.ProjectAsync()).Tracks[0].Instrument!.File);
        var set = await WaitAsync<SetInstrumentMessage>(Host(server), static m => m.Instrument?.File is not null);
        Assert.Equal(Path.GetFullPath(sfz), set.Instrument!.File);

        await ApiCalls.AssertProblemAsync(
            await client.SendCommandsAsync(null, new LoadInstrumentFileCommand(track, Path.Combine(kits, "Gone.sfz"))),
            HttpStatusCode.UnprocessableEntity
        );
    }

    [Fact]
    public async Task Live_MIDI_plays_on_a_track_and_comes_back_as_activity() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await using var events = await EventStream.ConnectAsync(server);
        await WaitEngineAsync(server, client);
        await client.CreateProjectAsync();
        await client.EditAsync(new AddTrackCommand(Kind: TrackKind.Instrument), new AddTrackCommand());
        var tracks = (await client.ProjectAsync()).Tracks;

        var response = await LiveAsync(client, tracks[0].Id, new(0x90, 60, 100), new LiveMidiEvent(0x80, 60, 0));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var activity = await events.WaitAsync<MidiActivityEvent>();
        Assert.Equal(
            [(tracks[0].Id, 0x90, 60, 100), (tracks[0].Id, 0x80, 60, 0)],
            activity.Events.Select(static e => (e.TrackId, e.Status, e.Data1, e.Data2))
        );
        var live = await WaitAsync<LiveMidiMessage>(Host(server));
        Assert.Equal(2, live.Events.Count);

        Assert.Equal(HttpStatusCode.NotFound, (await LiveAsync(client, "trk_nope", new(0x90, 1, 1))).StatusCode);
        await ApiCalls.AssertProblemAsync(
            await LiveAsync(client, tracks[1].Id, new(0x90, 60, 100)),
            HttpStatusCode.UnprocessableEntity
        );
        await ApiCalls.AssertProblemAsync(
            await LiveAsync(client, tracks[0].Id, new(0xF8, 0, 0)),
            HttpStatusCode.UnprocessableEntity
        );
    }

    [Fact]
    public async Task The_thru_track_follows_the_selection() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await WaitEngineAsync(server, client);
        await client.CreateProjectAsync();
        await client.EditAsync(new AddTrackCommand(), new AddTrackCommand(Kind: TrackKind.Instrument));
        var tracks = (await client.ProjectAsync()).Tracks;
        var host = Host(server);

        Assert.Equal(HttpStatusCode.NoContent, (await ThruAsync(client, tracks[1].Id)).StatusCode);
        await ServerFixture.Eventually(() => Task.FromResult(host.Thru == 1));
        // An audio track hears no MIDI: nothing does.
        await ThruAsync(client, tracks[0].Id);
        await ServerFixture.Eventually(() => Task.FromResult(host.Thru == -1));
        await ThruAsync(client, tracks[1].Id);
        await ServerFixture.Eventually(() => Task.FromResult(host.Thru == 1));
        await ThruAsync(client, null);
        await ServerFixture.Eventually(() => Task.FromResult(host.Thru == -1));
        Assert.Equal(HttpStatusCode.NotFound, (await ThruAsync(client, "trk_nope")).StatusCode);
    }

    [Fact]
    public async Task A_recording_makes_audio_and_MIDI_regions_in_one_Record_step_and_merges_when_asked() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await using var events = await EventStream.ConnectAsync(server);
        await WaitEngineAsync(server, client);
        await client.CreateProjectAsync();
        await client.EditAsync(
            new AddTrackCommand("Keys", Kind: TrackKind.Instrument),
            new AddTrackCommand("Bass", Kind: TrackKind.Instrument),
            new AddTrackCommand("Vocal")
        );
        var tracks = (await client.ProjectAsync()).Tracks;
        await client.EditAsync([..tracks.Select(static t => new SetTrackCommand(t.Id, RecordArmed: true))]);

        // Bar 2 and a bit (4/4, 120 BPM: a bar is 2 s, 96 000 samples).
        await client.TransportAsync(TransportCommand.Record, 3840 + 480);
        await events.WaitAsync<TransportEvent>(static e => e.Transport is { State: TransportState.Recording });
        await ServerFixture.Eventually(() =>
            Task.FromResult(Host(server).State == Ipc.Telemetry.TelemetryTransportState.Recording)
        );
        await LiveAsync(client, tracks[0].Id, new(0x90, 48, 90));
        await Task.Delay(150, Token);
        await LiveAsync(client, tracks[0].Id, new(0x80, 48, 0), new LiveMidiEvent(0xB0, 64, 127));
        await events.WaitAsync<TransportEvent>(static e => e.Transport.PositionSamples > 96_000 + 12_000 + 48_000);
        await client.TransportAsync(TransportCommand.Stop);

        var recorded = await events.WaitAsync<ProjectChangedEvent>(static e => e.Delta.Ops.Any(static o =>
                o is MidiRegionUpserted
            )
        );
        var midi = recorded.Delta.Ops.OfType<MidiRegionUpserted>().Select(static o => o.Region).ToArray();
        Assert.Single(recorded.Delta.Ops.OfType<RegionUpserted>());
        Assert.Equal(2, midi.Length);
        Assert.All(midi, static r => Assert.Equal((3840L, 3840L), (r.StartTick, r.LengthTicks)));
        var keys = midi.Single(r => r.TrackId == tracks[0].Id);
        var played = Assert.Single(keys.Notes);
        Assert.Equal((48, 90, 1), (played.Pitch, played.Velocity, played.Channel));
        Assert.InRange(played.StartTick, 480, 960);
        Assert.Equal(
            (ControlKind.ControlChange, 64, 127),
            (keys.Controls[0].Kind, keys.Controls[0].Number, keys.Controls[0].Value)
        );
        // Nobody played the bass: the fake host made up a few notes.
        Assert.Equal([60, 64, 67, 72], midi.Single(r => r.TrackId == tracks[1].Id).Notes.Select(static n => n.Pitch));
        var history = await client.GetFromJsonAsync(
            "/api/v1/project/history",
            ContractsJsonContext.Default.UndoHistory,
            Token
        );
        Assert.Equal("Record", history!.Undo[0].Label);
        var undone = await client.PostResultAsync("/api/v1/project/undo");
        Assert.Equal(3, undone.Delta.Ops.Count);
        Assert.Empty((await client.ProjectAsync()).MidiRegions);
        await client.PostResultAsync("/api/v1/project/redo");

        // Merged: a second pass over the same bar adds to the region under its start.
        await client.EditAsync(
            new SetMidiRecordingCommand(true),
            new SetTrackCommand(tracks[2].Id, RecordArmed: false)
        );
        var mark = events.Events.Count;
        await client.TransportAsync(TransportCommand.Record, 3840 + 960);
        await ServerFixture.Eventually(() =>
            Task.FromResult(
                events.Events.Skip(mark)
                    .OfType<TransportEvent>()
                    .Any(static e => e.Transport is {
                            State: TransportState.Recording, PositionSamples: > 96_000 + 24_000 + 60_000
                        }
                    )
            )
        );
        await client.TransportAsync(TransportCommand.Stop);
        await ServerFixture.Eventually(async () =>
            (await client.ProjectAsync()).MidiRegions.Sum(static r => r.Notes.Count) > 5
        );
        var after = await client.ProjectAsync();
        Assert.Equal(2, after.MidiRegions.Count);
        var bass = after.MidiRegions.Single(r => r.TrackId == tracks[1].Id);
        Assert.Equal(8, bass.Notes.Count);
        Assert.Equal(bass.Notes.Count, bass.Notes.Select(static n => n.Id).Distinct().Count());
    }

    [Fact]
    public async Task Saving_and_reopening_keeps_every_instrument_parameter_and_state() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await WaitEngineAsync(server, client);
        await ServerFixture.Eventually(async () => (await PluginsAsync(client)).Count > 0);
        var project = await client.CreateProjectAsync();
        await client.EditAsync(
            new AddTrackCommand(Kind: TrackKind.Instrument, PluginId: FakeHost.FakeClapId),
            new AddTrackCommand(Kind: TrackKind.Midi)
        );
        var tracks = (await client.ProjectAsync()).Tracks;
        await client.EditAsync(
            new SetParameterCommand(tracks[0].Id, "1", -0.25),
            new SetMidiIoCommand(tracks[1].Id, new("fake-in", 2), new("fake-out", 10)),
            new AddMidiRegionCommand(tracks[0].Id, 0, 3840)
        );
        var region = (await client.ProjectAsync()).MidiRegions[0].Id;
        await client.EditAsync(
            new AddNotesCommand(region, [new(60, 100, 1, 0, 480), new(64, 100, 1, 0, 480)]),
            new QuantizeNotesCommand(region, 480)
        );
        var io = await WaitAsync<SetMidiIoMessage>(Host(server));
        Assert.Equal(new("fake-in", 2, "fake-out", 10), io.Midi);

        await client.PostAsync("/api/v1/project/save", null, Token);
        Assert.Contains(Host(server).Received, static m => m is RequestInstrumentStatesMessage);
        var stateFile = Path.Combine(project.Path, "Plugin States", tracks[0].Id + ".bin");
        Assert.Equal(
            $"fake-state:{FakeHost.FakeClapId}",
            Encoding.UTF8.GetString(await File.ReadAllBytesAsync(stateFile, Token))
        );
        await client.PostAsync("/api/v1/project/close", null, Token);

        var reopened = await client.PostAsJsonAsync(
            "/api/v1/projects/open",
            new(project.Path),
            ContractsJsonContext.Default.OpenProjectRequest,
            Token
        );
        reopened.EnsureSuccessStatusCode();
        var back = await client.ProjectAsync();
        Assert.Equal(-0.25, back.Tracks[0].Instrument!.Parameters["1"]);
        Assert.Equal(0.5, back.Tracks[0].Instrument!.Parameters["0"]);
        Assert.Equal(new("fake-out", 10), back.Tracks[1].MidiOutput);
        Assert.Equal(2, Assert.Single(back.MidiRegions).Notes.Count);

        var load = await WaitAsync<LoadModelMessage>(
            Host(server),
            static m => m.Replace && m.Tracks.Any(static t => t.Instrument?.State is not null)
        );
        var instrument = load.Tracks.Single(static t => t.Instrument is not null).Instrument!;
        Assert.Equal(-0.25, instrument.Parameters["1"]);
        Assert.Equal(
            $"fake-state:{FakeHost.FakeClapId}",
            Encoding.UTF8.GetString(Convert.FromBase64String(instrument.State!))
        );
        var engineTrack = load.Tracks.Single(static t => t.Instrument is not null).Id;
        await ServerFixture.Eventually(() => Task.FromResult(Host(server).EventsOf(engineTrack).Count == 4));
    }

    static FakeHost Host(ServerFixture server) => server.Services.GetRequiredService<EngineConnection>().InProcessHost!;

    static Task WaitEngineAsync(ServerFixture server, HttpClient client) =>
        ServerFixture.Eventually(async () => (await server.SystemAsync(client)).Engine.State == EngineState.InProcess);

    static async Task<IReadOnlyList<PluginInfo>> PluginsAsync(HttpClient client) =>
        (await client.GetFromJsonAsync(
            "/api/v1/plugins",
            ContractsJsonContext.Default.IReadOnlyListPluginInfo,
            Token
        ))!;

    static Task<HttpResponseMessage> LiveAsync(
        HttpClient client,
        string track,
        LiveMidiEvent first,
        params LiveMidiEvent[] rest
    ) =>
        client.PostAsJsonAsync(
            "/api/v1/midi/live",
            new(track, [first, ..rest]),
            ContractsJsonContext.Default.LiveMidiRequest,
            Token
        );

    static Task<HttpResponseMessage> ThruAsync(HttpClient client, string? track) =>
        client.PostAsJsonAsync("/api/v1/midi/thru", new(track), ContractsJsonContext.Default.MidiThruRequest, Token);

    static async Task<T> WaitAsync<T>(FakeHost host, Func<T, bool>? match = null) where T : IpcMessage {
        T? found = null;
        await ServerFixture.Eventually(
            () => {
                found = host.Received.OfType<T>().LastOrDefault(m => match?.Invoke(m) ?? true);
                return Task.FromResult(found is not null);
            },
            10
        );
        return found!;
    }
}
