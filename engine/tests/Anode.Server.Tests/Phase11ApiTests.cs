using Anode.Contracts;
using Anode.Ipc;
using Anode.Ipc.Segments;
using Anode.Ipc.Telemetry;
using Anode.Model.Compile;
using Anode.Model.Editing;
using Anode.Server.Engine;
using Anode.Server.Projects;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;

namespace Anode.Server.Tests;

/// <summary>
///     Phase 11's server (docs/12-phase-11.md, workstream B) over REST, against the in-process host: the chord track and
///     Chord ID, Session Player regions' notes (generated, cached by their input's hash and regenerated when what they
///     depend on changes), Regenerate and Convert, the Mastering Assistant's settings and analysis (the master rendered
///     before it), the Environment in the host, live MIDI into its objects and its telemetry as events. Workstream D's
///     generator and Chord ID and workstream E's analysis and chain designer are doubles (Phase11Doubles.cs).
/// </summary>
public sealed class Phase11ApiTests : IAsyncDisposable {
    static CancellationToken Token => TestContext.Current.CancellationToken;

    readonly FakeSessionPlayers players = new();
    readonly FakeChordRecognition chords = new();
    readonly FakeMasteringAnalysis analysis = new();
    readonly FakeMasteringDesigner designer = new();
    ServerFixture? server;
    HttpClient? client;

    public async ValueTask DisposeAsync() {
        client?.Dispose();
        if (server is not null) {
            await server.DisposeAsync();
        }
    }

    HttpClient Client => client!;

    ServerFixture Server => server!;

    FakeHost Host => Server.Services.GetRequiredService<EngineConnection>().InProcessHost!;

    async Task StartAsync(ISessionPlayerLibrary? library = null) {
        server = new(
            configure: s => {
                s.AddSingleton(library ?? players);
                s.AddSingleton<IChordRecognition>(chords);
                s.AddSingleton<IMasteringAnalysis>(analysis);
                s.AddSingleton<IMasteringDesigner>(designer);
            }
        );
        client = server.CreateClient();
        await ServerFixture.Eventually(async () =>
            (await server.SystemAsync(Client)).Engine.State == EngineState.InProcess
        );
        await Client.CreateProjectAsync();
    }

    async Task<Job> DoneAsync(string path, HttpContent? content = null) {
        var response = await Client.PostAsync(path, content, Token);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Token));
        var job = (await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.Job, Token))!;
        Job? now = null;
        await ServerFixture.Eventually(async () => {
                now = await Client.GetFromJsonAsync($"/api/v1/jobs/{job.Id}", ContractsJsonContext.Default.Job, Token);
                return now!.State is JobState.Done or JobState.Failed or JobState.Cancelled;
            }
        );
        Assert.True(now!.State == JobState.Done, now.Message);
        return now;
    }

    async Task<SessionPlayerNotes> NotesAsync(string regionId) =>
        (await Client.GetFromJsonAsync(
            $"/api/v1/project/regions/{regionId}/session-player",
            ContractsJsonContext.Default.SessionPlayerNotes,
            Token
        ))!;

    static PitchName N(PitchStep step) => new(step, 0);

    [Fact]
    public async Task The_style_catalogue_comes_from_the_library_and_without_one_the_players_are_unavailable() {
        await StartAsync(new MissingSessionPlayers());
        var response = await Client.GetAsync("/api/v1/library/session-players", Token);
        Assert.Contains(
            "not available",
            await ApiCalls.AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable)
        );
        var refused = await Client.SendCommandsAsync(null, new AddSessionPlayerTrackCommand(SessionPlayerKind.Drummer));
        Assert.Contains("style", await ApiCalls.AssertProblemAsync(refused, HttpStatusCode.UnprocessableEntity));
    }

    [Fact]
    public async Task A_region_s_notes_are_generated_once_and_again_when_what_it_depends_on_changes() {
        await StartAsync();
        var catalog = await Client.GetFromJsonAsync(
            "/api/v1/library/session-players",
            ContractsJsonContext.Default.SessionPlayerCatalog,
            Token
        );
        Assert.Equal("fake-1", catalog!.Version);
        await Client.EditAsync(
            new AddSectionCommand("Verse", 0, 7680),
            new AddSectionCommand("Chorus", 7680, 7680),
            new AddChordsCommand(
                [new(0, N(PitchStep.C), ChordQualities.Major), new(7680, N(PitchStep.F), ChordQualities.Major)]
            ),
            new AddSessionPlayerTrackCommand(SessionPlayerKind.Bass, "fake.bass")
        );
        var project = await Client.ProjectAsync();
        var (verse, chorus) = (project.SessionPlayerRegions[0].StartTick == 0
            ? project.SessionPlayerRegions[0]
            : project.SessionPlayerRegions[1],
            project.SessionPlayerRegions[0].StartTick == 0
                ? project.SessionPlayerRegions[1]
                : project.SessionPlayerRegions[0]);
        var first = await NotesAsync(chorus.Id);
        Assert.All(first.Notes.Where(static n => n.Pitch != 60), static n => Assert.Equal(36 + 5, n.Pitch));
        // The host plays them on the track.
        var engineId = Host.Tracks.Values.Single().Id;
        await ServerFixture.Eventually(() => Task.FromResult(
                Host.EventsOf(engineId).Any(static e => e.Kind == PlaybackEventKind.NoteOn && e.Data1 == 41)
            )
        );
        var generated = players.Generated;
        Assert.Equal(first.Hash, (await NotesAsync(chorus.Id)).Hash);
        Assert.Equal(generated, players.Generated);

        // A chord in the chorus regenerates it; one in the verse leaves it.
        await Client.EditAsync(new AddChordsCommand([new(1920, N(PitchStep.A), ChordQualities.Minor)]));
        Assert.Equal(first.Hash, (await NotesAsync(chorus.Id)).Hash);
        Assert.NotEqual(first.Hash, (await NotesAsync(verse.Id)).Hash);
        await Client.EditAsync(new AddChordsCommand([new(11_520, N(PitchStep.G), ChordQualities.Major)]));
        var changed = await NotesAsync(chorus.Id);
        Assert.NotEqual(first.Hash, changed.Hash);
        Assert.Contains(changed.Notes, static n => n.Pitch == 36 + 7);
        // A section's rename (its kind) in range regenerates; a parameter change too, the same seed.
        await Client.EditAsync(
            new SetSectionCommand(project.Arrangement.Single(static s => s.Name == "Chorus").Id, "Bridge")
        );
        Assert.NotEqual(changed.Hash, (await NotesAsync(chorus.Id)).Hash);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await Client.GetAsync("/api/v1/project/regions/spr_none/session-player", Token)).StatusCode
        );
    }

    [Fact]
    public async Task A_Drummer_follows_its_rhythm_source_s_onsets() {
        await StartAsync();
        await Client.EditAsync(
            new AddTrackCommand(Kind: TrackKind.Instrument),
            new AddSessionPlayerTrackCommand(SessionPlayerKind.Drummer, Regions: false)
        );
        var project = await Client.ProjectAsync();
        var (bass, drummer) = (project.Tracks[0].Id, project.Tracks.Single(static t => t.Kind == TrackKind.Drummer).Id);
        await Client.EditAsync(
            new AddMidiRegionCommand(bass, 0, 3840),
            new AddSessionPlayerRegionCommand(drummer, 0, 3840, Seed: 2)
        );
        project = await Client.ProjectAsync();
        var midi = project.MidiRegions.Single().Id;
        var region = project.SessionPlayerRegions.Single();
        await Client.EditAsync(
            new AddNotesCommand(midi, [new(40, 100, 1, 0, 240), new(40, 100, 1, 1440, 240)]),
            new SetSessionPlayerRegionCommand(region.Id, Drums: Edits.DefaultDrums with { RhythmSourceTrackId = bass })
        );
        var notes = await NotesAsync(region.Id);
        Assert.Equal([0L, 1440], notes.Notes.Select(static n => n.StartTick));
        // A note of the source over the region regenerates it.
        await Client.EditAsync(new AddNotesCommand(midi, [new(43, 100, 1, 2880, 240)]));
        Assert.Equal([0L, 1440, 2880], (await NotesAsync(region.Id)).Notes.Select(static n => n.StartTick));
        // Its kit is multi-output: the kick plays on its own pad's track.
        var kick = project.Tracks.Single(static t => t.Kind == TrackKind.Drummer).DrumMachine!.Pads
            .Single(static p => p.Note == DrumKitNotes.Kick)
            .TrackId;
        var kickTrack = Host.Tracks.Values.Single(t =>
                Server.Services.GetRequiredService<ProjectSession>().View!.Ids.Track(kick) == t.Id
            )
            .Id;
        await ServerFixture.Eventually(() => Task.FromResult(
                Host.EventsOf(kickTrack).Count(static e => e.Kind == PlaybackEventKind.NoteOn) == 1
            )
        );
    }

    [Fact]
    public async Task Regenerate_draws_a_new_seed_and_Convert_freezes_the_notes_into_a_region() {
        await StartAsync();
        await Client.EditAsync(new AddSessionPlayerTrackCommand(SessionPlayerKind.Drummer));
        var region = (await Client.ProjectAsync()).SessionPlayerRegions.Single();
        var response = await Client.PostAsJsonAsync(
            $"/api/v1/project/regions/{region.Id}/session-player/regenerate",
            new(5),
            ContractsJsonContext.Default.RegenerateRequest,
            Token
        );
        response.EnsureSuccessStatusCode();
        Assert.Equal(5, (await Client.ProjectAsync()).SessionPlayerRegions.Single().Seed);
        var notes = await NotesAsync(region.Id);
        Assert.Contains(notes.Notes, static n => n.Pitch == DrumKitNotes.ClosedHiHat);

        await DoneAsync(
            $"/api/v1/project/regions/{region.Id}/session-player/convert",
            JsonContent.Create(
                new(SessionPlayerConvertTarget.Pattern),
                ContractsJsonContext.Default.SessionPlayerConvertRequest
            )
        );
        var project = await Client.ProjectAsync();
        Assert.Empty(project.SessionPlayerRegions);
        // Eight bars of 16ths: two pattern regions of four bars (rows hold 64 steps at most).
        Assert.Equal(
            [0L, 15_360],
            project.PatternRegions.OrderBy(static r => r.StartTick).Select(static r => r.StartTick)
        );
        var pattern = project.PatternRegions.Single(static r => r.StartTick == 0);
        Assert.Equal((15_360L, 64), (pattern.LengthTicks, pattern.Pattern.Rows[0].Length));
        Assert.Equal(
            notes.Notes.Select(static n => n.Pitch).Distinct().Order(),
            pattern.Pattern.Rows.Select(static r => r.Note)
        );
        await Client.PostResultAsync("/api/v1/project/undo");
        Assert.Single((await Client.ProjectAsync()).SessionPlayerRegions);
        await DoneAsync(
            $"/api/v1/project/regions/{region.Id}/session-player/convert",
            JsonContent.Create(
                new(),
                ContractsJsonContext.Default.SessionPlayerConvertRequest
            )
        );
        var midi = (await Client.ProjectAsync()).MidiRegions.Single();
        Assert.Equal(
            notes.Notes.Select(static n => (n.Pitch, n.StartTick)),
            midi.Notes.Select(static n => (n.Pitch, n.StartTick))
        );
        var bass = await Client.SendCommandsAsync(null, new AddSessionPlayerTrackCommand(SessionPlayerKind.Bass));
        bass.EnsureSuccessStatusCode();
        var bassRegion = (await Client.ProjectAsync()).SessionPlayerRegions.Single();
        var refused = await Client.PostAsJsonAsync(
            $"/api/v1/project/regions/{bassRegion.Id}/session-player/convert",
            new(SessionPlayerConvertTarget.Pattern),
            ContractsJsonContext.Default.SessionPlayerConvertRequest,
            Token
        );
        await ApiCalls.AssertProblemAsync(refused, HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Chord_ID_writes_the_chord_track_and_keys_in_one_undo_step() {
        await StartAsync();
        await Client.EditAsync(new AddTrackCommand(Kind: TrackKind.Instrument));
        var track = (await Client.ProjectAsync()).Tracks[0].Id;
        await Client.EditAsync(new AddMidiRegionCommand(track, 0, 7680));
        var region = (await Client.ProjectAsync()).MidiRegions.Single().Id;
        // C major for a bar, B flat major for a bar: spelled with flats in the notes' key (D minor).
        await Client.EditAsync(
            new AddNotesCommand(
                region,
                [
                    new(48, 100, 1, 0, 3840), new(52, 100, 1, 0, 3840), new(46, 100, 1, 3840, 3840),
                    new(50, 100, 1, 3840, 3840)
                ]
            )
        );
        await DoneAsync(
            "/api/v1/project/chords/detect",
            JsonContent.Create(new ChordIdRequest([region]), ContractsJsonContext.Default.ChordIdRequest)
        );
        var chordTrack = (await Client.GetFromJsonAsync(
            "/api/v1/project/chords",
            ContractsJsonContext.Default.ChordTrack,
            Token
        ))!;
        Assert.Equal(
            [(0L, N(PitchStep.C), ChordQualities.Major), (3840L, new PitchName(PitchStep.B, -1), ChordQualities.Major)],
            chordTrack.Chords.Select(static c => (c.Tick, c.Root, c.Quality))
        );
        // A beat each, the range the region's.
        Assert.Equal((960L, 9), (chords.Heard[0].Resolution, chords.Heard[0].Beats.Count));
        Assert.Equal(
            new(N(PitchStep.D), KeyMode.Minor),
            (await Client.ProjectAsync()).MidiRegions.Single().Key
        );
        var history = (await Client.GetFromJsonAsync(
            "/api/v1/project/history",
            ContractsJsonContext.Default.UndoHistory,
            Token
        ))!;
        Assert.Equal("Chord ID", history.Undo[0].Label);
        await Client.PostResultAsync("/api/v1/project/undo");
        var undone = await Client.ProjectAsync();
        Assert.Empty(undone.ChordTrack.Chords);
        Assert.Null(undone.MidiRegions.Single().Key);
        var missing = await Client.PostAsJsonAsync(
            "/api/v1/project/chords/detect",
            new ChordIdRequest(["mid_none"]),
            ContractsJsonContext.Default.ChordIdRequest,
            Token
        );
        await ApiCalls.AssertProblemAsync(missing, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_analysis_renders_the_master_before_the_assistant_and_its_chain_reaches_the_host() {
        await StartAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync("/api/v1/project/mastering", Token)).StatusCode);
        var none = await Client.PostAsJsonAsync(
            "/api/v1/project/mastering/analyse",
            new(Add: false),
            ContractsJsonContext.Default.MasteringAnalysisRequest,
            Token
        );
        await ApiCalls.AssertProblemAsync(none, HttpStatusCode.NotFound);
        await Client.EditAsync(new AddTrackCommand(Kind: TrackKind.Instrument));
        // Without an assistant: one is added last, in the analysis' undo step.
        await DoneAsync(
            "/api/v1/project/mastering/analyse",
            JsonContent.Create(
                new(0, 7680),
                ContractsJsonContext.Default.MasteringAnalysisRequest
            )
        );
        var state = (await Client.GetFromJsonAsync(
            "/api/v1/project/mastering",
            ContractsJsonContext.Default.MasteringState,
            Token
        ))!;
        Assert.Equal(MasteringSettings.Default, state.Settings);
        Assert.Equal((0L, 7680L), (state.Analysis!.StartTick, state.Analysis.EndTick));
        Assert.Equal(4 * 48_000, analysis.Samples);
        await ServerFixture.Eventually(() =>
            Task.FromResult(Host.Master.Inserts?.SingleOrDefault()?.Mastering is { CompressorRatio: 2 })
        );
        // Settings are its parameters; the character designs the chain again.
        var put = await Client.PutAsJsonAsync(
            "/api/v1/project/mastering",
            MasteringSettings.Default with { Character = MasteringCharacter.Punch, TargetLufs = -11 },
            ContractsJsonContext.Default.MasteringSettings,
            Token
        );
        put.EnsureSuccessStatusCode();
        await ServerFixture.Eventually(() =>
            Task.FromResult(Host.Master.Inserts?.SingleOrDefault()?.Mastering is { CompressorRatio: 4 })
        );
        Assert.Equal(-11, Host.Master.Inserts![0].Parameters[MasteringParameters.TargetLufs]);

        // A second analysis renders with the assistant bypassed, at unity, and restores the master after.
        await Client.EditAsync(new SetStripCommand(null, -6));
        await DoneAsync(
            "/api/v1/project/mastering/analyse",
            JsonContent.Create(new(), ContractsJsonContext.Default.MasteringAnalysisRequest)
        );
        var messages = Host.Received.ToArray();
        var render = Array.FindLastIndex(messages, static m => m is RenderMessage);
        var swapped = (SetChannelMessage)messages[..render].Last(static m => m is SetChannelMessage { TrackId: -1 });
        Assert.Equal((1f, true), (swapped.Strip.Gain, swapped.Strip.Inserts![0].Bypassed));
        await ServerFixture.Eventually(() =>
            Task.FromResult(
                Host.Received.Skip(render)
                    .OfType<SetChannelMessage>()
                    .Any(static m => m.TrackId == -1 && !m.Strip.Inserts![0].Bypassed)
            )
        );
        var history = (await Client.GetFromJsonAsync(
            "/api/v1/project/history",
            ContractsJsonContext.Default.UndoHistory,
            Token
        ))!;
        Assert.Equal("Analyse Mastering", history.Undo[0].Label);
    }

    [Fact]
    public async Task The_Environment_goes_to_the_host_and_takes_live_MIDI_into_its_objects() {
        await StartAsync();
        Assert.Null(Host.Environment);
        await Client.EditAsync(new AddTrackCommand(Kind: TrackKind.Instrument));
        var track = (await Client.ProjectAsync()).Tracks[0].Id;
        await Client.EditAsync(
            new AddEnvironmentObjectCommand(EnvironmentObjectKind.Keyboard, 0, 0),
            new AddEnvironmentObjectCommand(
                EnvironmentObjectKind.Transformer,
                100,
                0,
                Operations: [new(TransformField.Pitch, TransformOperationKind.Add, 12)]
            ),
            new AddEnvironmentObjectCommand(EnvironmentObjectKind.Monitor, 200, 0),
            new AddEnvironmentObjectCommand(EnvironmentObjectKind.TrackInput, 300, 0, TrackId: track)
        );
        var environment = (await Client.GetFromJsonAsync(
            "/api/v1/project/environment",
            ContractsJsonContext.Default.MidiEnvironment,
            Token
        ))!;
        var o = environment.Objects.Select(static x => x.Id).ToArray();
        await Client.EditAsync(
            new ConnectEnvironmentCommand(o[0], 0, o[1], 0),
            new ConnectEnvironmentCommand(o[1], 0, o[2], 0),
            new ConnectEnvironmentCommand(o[2], 0, o[3], 0)
        );
        await ServerFixture.Eventually(() =>
            Task.FromResult(Host.Environment is { Objects.Count: 4 } e && e.Objects[2].Routes.Count == 1)
        );
        Assert.Equal(EngineEnvironmentKind.Transformer, Host.Environment!.Objects[1].Kind);
        var cycle = await Client.SendCommandsAsync(null, new ConnectEnvironmentCommand(o[3], 0, o[0], 0));
        await ApiCalls.AssertProblemAsync(cycle, HttpStatusCode.UnprocessableEntity);

        var input = await Client.PostAsJsonAsync(
            $"/api/v1/project/environment/objects/{o[0]}/input",
            new EnvironmentInputRequest([new(0x90, 60, 100)]),
            ContractsJsonContext.Default.EnvironmentInputRequest,
            Token
        );
        Assert.Equal(HttpStatusCode.NoContent, input.StatusCode);
        await ServerFixture.Eventually(() => Task.FromResult(Host.EnvironmentMidi.Count == 1));
        Assert.Equal((0, 60), (Host.EnvironmentMidi[0].ObjectIndex, Host.EnvironmentMidi[0].Events[0].Data1));
        var monitor = await Client.PostAsJsonAsync(
            $"/api/v1/project/environment/objects/{o[2]}/input",
            new EnvironmentInputRequest([new(0x90, 60, 100)]),
            ContractsJsonContext.Default.EnvironmentInputRequest,
            Token
        );
        await ApiCalls.AssertProblemAsync(monitor, HttpStatusCode.NotFound);

        // The monitor's events and a fader's value come from the telemetry as events.
        await using var events = await EventStream.ConnectAsync(Server);
        var entry = new EnvironmentMonitorEntry { Object = 3, Count = 2 };
        unsafe {
            entry.Events[0] = 480;
            entry.Events[1] = 0x90 | (72 << 8) | (100 << 16);
            entry.Events[2] = 960;
            entry.Events[3] = 0x80 | (72 << 8);
        }

        Host.SetPhase11Telemetry([entry], [], default);
        var seen = await events.WaitAsync<EnvironmentMonitorEvent>();
        Assert.Equal(o[2], seen.ObjectId);
        Assert.Equal([new MonitoredEvent(0x90, 72, 100, 480), new(0x80, 72, 0, 960)], seen.Events);
    }
}
