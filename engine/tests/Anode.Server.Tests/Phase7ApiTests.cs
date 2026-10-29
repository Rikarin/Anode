using Anode.Contracts;
using Anode.Dsp.Editing;
using Anode.Ipc;
using Anode.Server.Engine;
using Anode.Server.Projects;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;

namespace Anode.Server.Tests;

/// <summary>
///     Phase 7 through the API (docs/08-phase-7.md): the user library of grooves, Transform sets and articulation sets;
///     step input from the keyboard window and the MIDI keyboard; the Audio File Editor's jobs, versions, undo,
///     region shifts, samples, loop search and preview; Scripter's scripts, run-time parameters and logs; grooves
///     from audio; and input switches while recording. Audio edits come from <see cref="TestEditor" />.
/// </summary>
public sealed class Phase7ApiTests {
    static CancellationToken Token => TestContext.Current.CancellationToken;

    readonly string media = Directory.CreateTempSubdirectory("anode-phase7-").FullName;

    [Fact]
    public async Task The_library_keeps_grooves_Transform_sets_and_articulation_sets() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        Assert.Empty(
            await GetAsync(client, "library/grooves", ContractsJsonContext.Default.IReadOnlyListGrooveTemplate)
        );

        var groove = new GrooveTemplate("grv_swing", " Swing ", 960, 480, [new(0, 1), new(600, 0.5)]);
        var saved = await PostAsync(client, "library/grooves", groove, ContractsJsonContext.Default.GrooveTemplate);
        Assert.Equal("Swing", saved.Name);
        Assert.Equal(
            ["grv_swing"],
            (await GetAsync(client, "library/grooves", ContractsJsonContext.Default.IReadOnlyListGrooveTemplate))
            .Select(static g => g.Id)
        );
        await ApiCalls.AssertProblemAsync(
            await client.PostAsJsonAsync(
                "/api/v1/library/grooves",
                groove with { Points = [] },
                ContractsJsonContext.Default.GrooveTemplate,
                Token
            ),
            HttpStatusCode.UnprocessableEntity
        );
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await client.DeleteAsync("/api/v1/library/grooves/grv_swing", Token)).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.DeleteAsync("/api/v1/library/grooves/grv_swing", Token)).StatusCode
        );

        // Transform sets: the factory presets first, then the user's; a preset is never saved over or deleted.
        var presets = await GetAsync(
            client,
            "library/transform-sets",
            ContractsJsonContext.Default.IReadOnlyListTransformSet
        );
        Assert.NotEmpty(presets);
        Assert.All(presets, static p => Assert.StartsWith("factory.", p.Id));
        var mine = new TransformSet(
            "trf_mine",
            "Mine",
            [],
            [new(TransformField.Velocity, TransformOperationKind.Fix, 100)],
            null,
            TransformMode.OperateOnly,
            0
        );
        await PostAsync(client, "library/transform-sets", mine, ContractsJsonContext.Default.TransformSet);
        Assert.Equal(
            "trf_mine",
            (await GetAsync(
                client,
                "library/transform-sets",
                ContractsJsonContext.Default.IReadOnlyListTransformSet
            ))[^1].Id
        );
        await ApiCalls.AssertProblemAsync(
            await client.PostAsJsonAsync(
                "/api/v1/library/transform-sets",
                mine with { Id = presets[0].Id },
                ContractsJsonContext.Default.TransformSet,
                Token
            ),
            HttpStatusCode.UnprocessableEntity
        );
        await ApiCalls.AssertProblemAsync(
            await client.DeleteAsync($"/api/v1/library/transform-sets/{presets[0].Id}", Token),
            HttpStatusCode.UnprocessableEntity
        );
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await client.DeleteAsync("/api/v1/library/transform-sets/trf_mine", Token)).StatusCode
        );

        var set = new ArticulationSet(
            "ars_strings",
            "Strings",
            [new(1, "Legato", null)],
            [new(1, ArticulationSwitchKind.Note, 24, null)],
            [new(1, ArticulationOutputKind.Note, 24, 100)]
        );
        await PostAsync(client, "library/articulation-sets", set, ContractsJsonContext.Default.ArticulationSet);
        Assert.Single(
            await GetAsync(
                client,
                "library/articulation-sets",
                ContractsJsonContext.Default.IReadOnlyListArticulationSet
            )
        );
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await client.DeleteAsync("/api/v1/library/articulation-sets/ars_strings", Token)).StatusCode
        );
    }

    [Fact]
    public async Task Step_input_enters_notes_a_chord_a_rest_and_a_tie_and_reports_its_state() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await using var events = await EventStream.ConnectAsync(server);
        await client.CreateProjectAsync();
        await client.EditAsync(new AddTrackCommand(Kind: TrackKind.Instrument));
        var track = (await client.ProjectAsync()).Tracks[0].Id;
        var state = await StepInputAsync(client);
        Assert.Equal(ProjectSession.DefaultStepInput, state);

        state = await SetStepInputAsync(client, new(true, TrackId: track, PositionTick: 3840, Velocity: 90));
        Assert.True(state.On);
        await events.WaitAsync<StepInputEvent>(static e => e.State.On);

        // Two quarter-note steps, then a chord, a rest, an eighth with a tie.
        await SetStepInputAsync(client, new(NoteValueTicks: 960));
        await client.EditAsync(new StepNotesCommand([60]));
        await client.EditAsync(new StepNotesCommand([62], 70));
        await SetStepInputAsync(client, new(Chord: true));
        await client.EditAsync(new StepNotesCommand([64]));
        await client.EditAsync(new StepNotesCommand([67]));
        state = await SetStepInputAsync(client, new(Chord: false));
        Assert.Equal(3840 + 3 * 960, state.PositionTick);
        state = await PostStateAsync(client, "project/step-input/rest");
        Assert.Equal(3840 + 4 * 960, state.PositionTick);
        await SetStepInputAsync(client, new(NoteValueTicks: 480));
        await client.EditAsync(new StepNotesCommand([72]));
        await client.EditAsync(new StepTieCommand());
        state = await StepInputAsync(client);
        Assert.Equal(3840 + 4 * 960 + 2 * 480, state.PositionTick);

        var region = Assert.Single((await client.ProjectAsync()).MidiRegions);
        Assert.Equal((track, 3840L), (region.TrackId, region.StartTick));
        Assert.Equal(region.Id, state.RegionId);
        Assert.Equal(
            [(60, 90, 0L, 960L), (62, 70, 960, 960), (64, 90, 1920, 960), (67, 90, 1920, 960), (72, 90, 3840, 960)],
            region.Notes.Select(static n => (n.Pitch, n.Velocity, n.StartTick, n.LengthTicks))
        );
        // A dotted triplet step is a note value × 1.5 × 2/3.
        state = await SetStepInputAsync(client, new(Dotted: true, Triplet: true));
        await client.EditAsync(new StepNotesCommand([48]));
        Assert.Equal(
            480,
            (await client.ProjectAsync()).MidiRegions[0].Notes.Single(static n => n.Pitch == 48).LengthTicks
        );
        await events.WaitAsync<StepInputEvent>(e => e.State.PositionTick == state.PositionTick + 480);

        await ApiCalls.AssertProblemAsync(
            await client.PutAsJsonAsync(
                "/api/v1/project/step-input",
                new(Velocity: 0),
                ContractsJsonContext.Default.StepInputRequest,
                Token
            ),
            HttpStatusCode.UnprocessableEntity
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.PutAsJsonAsync(
                "/api/v1/project/step-input",
                new(RegionId: "mid_none"),
                ContractsJsonContext.Default.StepInputRequest,
                Token
            )).StatusCode
        );
    }

    [Fact]
    public async Task MIDI_step_input_is_forwarded_by_the_host_and_entered_as_steps() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await WaitEngineAsync(server, client);
        await client.CreateProjectAsync();
        await client.EditAsync(new AddTrackCommand(Kind: TrackKind.Instrument));
        var track = (await client.ProjectAsync()).Tracks[0].Id;
        var host = Host(server);

        // Forwarded only while step input and MIDI input are both on.
        await SetStepInputAsync(client, new(MidiInput: true, TrackId: track));
        Assert.False(host.StepInputForward);
        await SetStepInputAsync(client, new(true));
        await ServerFixture.Eventually(() => Task.FromResult(host.StepInputForward));
        await host.StepAsync([60, 64, 67], 80);
        await host.StepAsync([72], 110);
        await ServerFixture.Eventually(async () =>
            (await client.ProjectAsync()).MidiRegions is [{ Notes.Count: 4 }]
        );
        var notes = (await client.ProjectAsync()).MidiRegions[0].Notes;
        Assert.Equal(
            [(60, 80, 0L), (64, 80, 0), (67, 80, 0), (72, 110, 240)],
            notes.Select(static n => (n.Pitch, n.Velocity, n.StartTick))
        );
        await SetStepInputAsync(client, new(false));
        await ServerFixture.Eventually(() => Task.FromResult(!host.StepInputForward));
        await host.StepAsync([50], 100);
        await Task.Delay(100, Token);
        Assert.Equal(4, (await client.ProjectAsync()).MidiRegions[0].Notes.Count);
    }

    [Fact]
    public async Task An_audio_edit_writes_a_new_version_that_undo_points_back_from_and_saving_prunes() {
        await using var server = new ServerFixture(editor: new TestEditor());
        using var client = server.CreateClient();
        await using var events = await EventStream.ConnectAsync(server);
        var project = await client.CreateProjectAsync();
        var file = await client.ImportAsync(TestWav.Write(media, "Tone.wav", frames: 48_000));
        var before = await SamplesAsync(client, file.Id, 1000, 4, 1);
        Assert.NotEqual(0, before.Samples![0][0]);
        var tone = (await SamplesAsync(client, file.Id, 0, 1000, 1)).Samples![0];

        // Silence a part, then gain: two versions, each its own undo step.
        await EditFileAsync(client, events, file.Id, new(AudioEditKind.Silence, 1000, 2000));
        var edited = (await client.ProjectAsync()).AudioFiles[0];
        Assert.Equal((1, "Audio Files/Tone.v1.wav"), (edited.Version, edited.Path));
        Assert.True(File.Exists(Path.Combine(project.Path, "Audio Files", "Tone.v1.wav")));
        Assert.All((await SamplesAsync(client, file.Id, 1000, 1000, 1)).Samples![0], static x => Assert.Equal(0, x));
        await EditFileAsync(client, events, file.Id, new(AudioEditKind.Gain, 0, 1000, 6.0206));
        Assert.Equal(2, (await client.ProjectAsync()).AudioFiles[0].Version);
        Assert.Equal(2 * tone[500], (await SamplesAsync(client, file.Id, 500, 1, 1)).Samples![0][0], 0.001);
        var history = await client.GetFromJsonAsync(
            "/api/v1/project/history",
            ContractsJsonContext.Default.UndoHistory,
            Token
        );
        Assert.Equal(["Change Gain", "Silence"], history!.Undo.Take(2).Select(static s => s.Label));
        // Each version has its own peaks.
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.GetAsync($"/api/v1/project/files/{file.Id}/peaks", Token)).StatusCode
        );

        // Undo the silence: the file points at the original again, and its samples are back.
        await client.PostResultAsync("/api/v1/project/undo");
        await client.PostResultAsync("/api/v1/project/undo");
        Assert.Equal(
            (0, "Audio Files/Tone.wav"),
            ((await client.ProjectAsync()).AudioFiles[0].Version, (await client.ProjectAsync()).AudioFiles[0].Path)
        );
        Assert.Equal(before.Samples[0], (await SamplesAsync(client, file.Id, 1000, 4, 1)).Samples![0]);

        // Saved while redo still reaches both versions: kept. A new edit drops redo; the next save prunes.
        await SaveAsync(client);
        Assert.True(File.Exists(Path.Combine(project.Path, "Audio Files", "Tone.v2.wav")));
        await EditFileAsync(client, events, file.Id, new(AudioEditKind.Invert, 0, 10));
        Assert.Equal(
            (3, "Audio Files/Tone.v3.wav"),
            ((await client.ProjectAsync()).AudioFiles[0].Version, (await client.ProjectAsync()).AudioFiles[0].Path)
        );
        await SaveAsync(client);
        Assert.False(File.Exists(Path.Combine(project.Path, "Audio Files", "Tone.v1.wav")));
        Assert.False(File.Exists(Path.Combine(project.Path, "Audio Files", "Tone.v2.wav")));
        Assert.True(File.Exists(Path.Combine(project.Path, "Audio Files", "Tone.v3.wav")));
        Assert.True(File.Exists(Path.Combine(project.Path, "Audio Files", "Tone.wav")));

        await ApiCalls.AssertProblemAsync(
            await PostEditAsync(client, file.Id, new(AudioEditKind.Silence, 100, 99_999)),
            HttpStatusCode.UnprocessableEntity
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await PostEditAsync(client, "aud_none", new(AudioEditKind.Silence, 0, 1))).StatusCode
        );
    }

    [Fact]
    public async Task A_trim_shifts_and_clips_the_regions_of_the_file_or_is_refused_when_one_would_vanish() {
        await using var server = new ServerFixture(editor: new TestEditor());
        using var client = server.CreateClient();
        await using var events = await EventStream.ConnectAsync(server);
        await client.CreateProjectAsync();
        await client.EditAsync(new SetSmartTempoCommand(AnalyzeImports: false), new AddTrackCommand());
        var file = await client.ImportAsync(TestWav.Write(media, "Tone.wav", frames: 96_000));
        var track = (await client.ProjectAsync()).Tracks[0].Id;
        await client.EditAsync(new AddRegionCommand(track, file.Id, 3840, 10_000, 20_000));

        await ApiCalls.AssertProblemAsync(
            await PostEditAsync(client, file.Id, new(AudioEditKind.Trim, 40_000, 50_000)),
            HttpStatusCode.UnprocessableEntity
        );
        await EditFileAsync(client, events, file.Id, new(AudioEditKind.Trim, 15_000, 50_000));
        var project = await client.ProjectAsync();
        Assert.Equal(35_000, project.AudioFiles[0].Frames);
        var region = project.Regions[0];
        Assert.Equal((4040L, 0L, 15_000L), (region.StartTick, region.FileOffset, region.Length));
        await client.PostResultAsync("/api/v1/project/undo");
        project = await client.ProjectAsync();
        Assert.Equal(
            (96_000L, 3840L, 10_000L),
            (project.AudioFiles[0].Frames, project.Regions[0].StartTick, project.Regions[0].FileOffset)
        );
    }

    [Fact]
    public async Task Samples_come_raw_or_as_min_and_max_and_loop_search_and_preview_work_on_the_current_version() {
        await using var server = new ServerFixture(editor: new TestEditor());
        using var client = server.CreateClient();
        await WaitEngineAsync(server, client);
        await client.CreateProjectAsync();
        var file = await client.ImportAsync(TestWav.Write(media, "Tone.wav", frames: 10_000, channels: 2));
        var raw = await SamplesAsync(client, file.Id, 0, 100, 1);
        Assert.Equal((2, 100, 1), (raw.Channels, raw.Samples![0].Count, raw.Bucket));
        Assert.Null(raw.Min);
        var buckets = await SamplesAsync(client, file.Id, 0, 1000, 64);
        Assert.Equal(16, buckets.Max![1].Count);
        Assert.Equal(raw.Samples[0].Take(64).Max(), buckets.Max[0][0]);
        Assert.Equal(raw.Samples[0].Take(64).Min(), buckets.Min![0][0]);
        var end = await SamplesAsync(client, file.Id, 9_990, 100, 1);
        Assert.Equal(10, end.Frames);
        await ApiCalls.AssertProblemAsync(
            await client.GetAsync($"/api/v1/project/files/{file.Id}/samples?start=0&frames=1000000&bucket=1", Token),
            HttpStatusCode.UnprocessableEntity
        );

        var loops = await client.PostAsJsonAsync(
            $"/api/v1/project/files/{file.Id}/loop-search",
            new(0, 10_000, 3),
            ContractsJsonContext.Default.LoopSearchRequest,
            Token
        );
        var found = (await loops.Content.ReadFromJsonAsync(
            ContractsJsonContext.Default.IReadOnlyListLoopSearchResult,
            Token
        ))!;
        Assert.InRange(found.Count, 1, 3);
        Assert.All(
            found,
            static l => Assert.True(l.StartFrame >= 0 && l.EndFrame <= 10_000 && l.EndFrame > l.StartFrame)
        );
        await ApiCalls.AssertProblemAsync(
            await client.PostAsJsonAsync(
                $"/api/v1/project/files/{file.Id}/loop-search",
                new(0, 10_000, 50),
                ContractsJsonContext.Default.LoopSearchRequest,
                Token
            ),
            HttpStatusCode.UnprocessableEntity
        );

        var preview = await client.PostAsJsonAsync(
            $"/api/v1/project/files/{file.Id}/preview",
            new(100, 5000, true),
            ContractsJsonContext.Default.PreviewRangeRequest,
            Token
        );
        Assert.Equal(HttpStatusCode.NoContent, preview.StatusCode);
        var host = Host(server);
        await ServerFixture.Eventually(() =>
            Task.FromResult(host.PreviewRange is { StartFrame: 100, EndFrame: 5000, Loop: true })
        );
        Assert.EndsWith("Tone.wav", host.PreviewRange!.Path);
    }

    [Fact]
    public async Task A_script_reaches_the_host_and_its_parameters_and_trace_come_back() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await using var events = await EventStream.ConnectAsync(server);
        await WaitEngineAsync(server, client);
        await client.CreateProjectAsync();
        await client.EditAsync(new AddTrackCommand(Kind: TrackKind.Instrument));
        var track = (await client.ProjectAsync()).Tracks[0].Id;
        await client.EditAsync(new AddInsertCommand("anode.scripter", track, Chain: InsertChain.MidiEffects));
        var slot = (await client.ProjectAsync()).Tracks[0].Strip.MidiEffects[0].Id;
        const string Script = """
                              var PluginParameters = [
                                { name: "Interval", type: "lin", minValue: -24, maxValue: 24, defaultValue: 12, numberOfSteps: 48 },
                                { name: "Mode", type: "menu", valueStrings: ["Up", "Down"] }
                              ];
                              function HandleMIDI(event) { event.send(); }
                              """;
        await client.EditAsync(new SetScriptCommand(track, slot, Script));
        var host = Host(server);
        await ServerFixture.Eventually(() => Task.FromResult(host.Scripts.Values.Contains(Script)));
        var parameters = await events.WaitAsync<SlotParametersEvent>();
        Assert.Equal((track, slot), (parameters.TrackId, parameters.SlotId));
        Assert.Equal(["Interval", "Mode"], parameters.Parameters.Select(static p => p.Id));
        await ServerFixture.Eventually(async () =>
            (await client.ProjectAsync()).Tracks[0].Strip.MidiEffects[0].RuntimeParameters is { Count: 2 }
        );
        var stored = (await client.ProjectAsync()).Tracks[0].Strip.MidiEffects[0];
        Assert.Equal((Script, 12.0, 0.0), (stored.Source, stored.Parameters["Interval"], stored.Parameters["Mode"]));

        // Automatable and mappable by id.
        await client.EditAsync(
            new SetAutomationCommand($"midiEffect:{slot}:Interval", [new(0, 5, 0), new(3840, -5, 0)], track)
        );
        Assert.Single((await client.ProjectAsync()).Automation);
        await client.EditAsync(new SetInsertParameterCommand(slot, "Interval", 7, track, InsertChain.MidiEffects));
        Assert.Equal(7, (await client.ProjectAsync()).Tracks[0].Strip.MidiEffects[0].Parameters["Interval"]);

        // Trace output comes back as scriptLog; undoing the script tells the host the previous one.
        var engineTrack = host.Received.OfType<SetScriptMessage>().First().TrackId;
        await host.TraceAsync(engineTrack, slot, ["hello", "world"], 3);
        var log = await events.WaitAsync<ScriptLogEvent>();
        Assert.Equal((track, slot, 3), (log.TrackId, log.SlotId, log.Dropped));
        Assert.Equal(["hello", "world"], log.Lines);
        await client.EditAsync(new SetScriptCommand(track, slot, "function HandleMIDI(e) { e.send(); }"));
        await ServerFixture.Eventually(() =>
            Task.FromResult(host.Scripts.Values.Contains("function HandleMIDI(e) { e.send(); }"))
        );
        await client.PostResultAsync("/api/v1/project/undo");
        await ServerFixture.Eventually(() =>
            Task.FromResult(host.Received.OfType<SetScriptMessage>().LastOrDefault()?.Source == Script)
        );
    }

    [Fact]
    public async Task A_groove_made_from_audio_takes_the_file_onsets_where_the_region_plays_them() {
        await using var server = new ServerFixture(editor: new TestEditor());
        using var client = server.CreateClient();
        await client.CreateProjectAsync();
        await client.EditAsync(new SetSmartTempoCommand(AnalyzeImports: false), new AddTrackCommand());
        var file = await client.ImportAsync(TestWav.Write(media, "Drums.wav", frames: 96_000));
        var track = (await client.ProjectAsync()).Tracks[0].Id;
        await client.EditAsync(new AddRegionCommand(track, file.Id, 0));
        var region = (await client.ProjectAsync()).Regions[0].Id;
        await client.EditAsync(new MakeGrooveCommand(region, "Drums", 1, 480));
        var groove = Assert.Single((await client.ProjectAsync()).Grooves);
        // Onsets every 12 000 frames (a 1/8 at 120 BPM), every second one 3000 frames (120 ticks) late.
        Assert.Equal(
            [0L, 600, 960, 1560, 1920, 2520, 2880, 3480],
            groove.Points.Select(static p => p.Tick)
        );
    }

    [Fact]
    public async Task Input_switches_select_articulations_while_recording_and_are_not_recorded() {
        await using var server = new ServerFixture();
        using var client = server.CreateClient();
        await using var events = await EventStream.ConnectAsync(server);
        await WaitEngineAsync(server, client);
        await client.CreateProjectAsync();
        await client.EditAsync(new AddTrackCommand("Strings", Kind: TrackKind.Instrument));
        var track = (await client.ProjectAsync()).Tracks[0].Id;
        await client.EditAsync(
            new SetArticulationSetCommand(
                new(
                    "ars_strings",
                    "Strings",
                    [new(1, "Legato", null), new(2, "Pizzicato", null)],
                    [
                        new(1, ArticulationSwitchKind.Note, 24, null),
                        new(2, ArticulationSwitchKind.ControlChange, 32, 2)
                    ],
                    []
                )
            ),
            new AssignArticulationSetCommand(track, "ars_strings"),
            new SetTrackCommand(track, RecordArmed: true)
        );
        await client.TransportAsync(TransportCommand.Record, 0);
        await events.WaitAsync<TransportEvent>(static e => e.Transport is { State: TransportState.Recording });
        await ServerFixture.Eventually(() =>
            Task.FromResult(Host(server).State == Ipc.Telemetry.TelemetryTransportState.Recording)
        );
        await LiveAsync(client, track, new(0x90, 24, 100), new(0x90, 60, 90));
        await Task.Delay(50, Token);
        await LiveAsync(client, track, new(0x80, 60, 0), new(0x80, 24, 0), new(0xB0, 32, 2), new(0x90, 62, 80));
        await Task.Delay(50, Token);
        await LiveAsync(client, track, new(0x80, 62, 0), new(0xB0, 7, 100));
        await Task.Delay(100, Token);
        await client.TransportAsync(TransportCommand.Stop);
        await ServerFixture.Eventually(async () => (await client.ProjectAsync()).MidiRegions.Count == 1);
        var region = (await client.ProjectAsync()).MidiRegions[0];
        Assert.Equal([(60, 1), (62, 2)], region.Notes.Select(static n => (n.Pitch, n.ArticulationId)));
        Assert.Equal([7], region.Controls.Select(static e => e.Number));
    }

    /// <summary>
    ///     The Audio File Editor's algorithms for tests: <c>Anode.Dsp.Editing</c>'s where workstream E implemented them,
    ///     else plain ones by the documented rules (loop search: a range's halves; onsets: every 12 000 frames, every
    ///     second one 3000 late, the first of each pair the strongest).
    /// </summary>
    sealed class TestEditor : IAudioEditor {
        readonly DspAudioEditor real = new();

        public (float[][] Channels, int SampleRate) Apply(
            float[][] channels,
            int sampleRate,
            AudioEditOperation operation
        ) {
            try {
                return real.Apply(channels, sampleRate, operation);
            } catch (NotImplementedException) {
                var (start, end) = ((int)operation.StartFrame, (int)operation.EndFrame);
                switch (operation.Kind) {
                    case AudioEditKind.Trim:
                        return ([..channels.Select(c => c[start..end])], sampleRate);
                    case AudioEditKind.Silence:
                        foreach (var c in channels) {
                            Array.Clear(c, start, end - start);
                        }

                        break;
                    case AudioEditKind.Gain:
                        var gain = (float)Math.Pow(10, (operation.Value ?? 0) / 20);
                        foreach (var c in channels) {
                            for (var i = start; i < end; i++) {
                                c[i] *= gain;
                            }
                        }

                        break;
                    case AudioEditKind.Invert:
                        foreach (var c in channels) {
                            for (var i = start; i < end; i++) {
                                c[i] = -c[i];
                            }
                        }

                        break;
                    case AudioEditKind.Reverse:
                        foreach (var c in channels) {
                            Array.Reverse(c, start, end - start);
                        }

                        break;
                    default:
                        throw new ArgumentException($"The test editor does not {operation.Kind}.");
                }

                return (channels, sampleRate);
            }
        }

        public IReadOnlyList<LoopCandidate> Loops(
            float[] mono,
            int sampleRate,
            long start,
            long end,
            int count,
            long minFrames
        ) {
            try {
                return real.Loops(mono, sampleRate, start, end, count, minFrames);
            } catch (NotImplementedException) {
                return [
                    ..new[] {
                        new LoopCandidate(start, end, 0.9), new LoopCandidate(start, start + (end - start) / 2, 0.5)
                    }.Take(count)
                ];
            }
        }

        public IReadOnlyList<GrooveOnset> Onsets(float[] mono, int sampleRate) {
            try {
                return real.Onsets(mono, sampleRate);
            } catch (NotImplementedException) {
                return [
                    ..Enumerable.Range(0, mono.Length / 12_000)
                        .Select(static i => new GrooveOnset(i * 12_000L + i % 2 * 3000, i % 2 == 0 ? 1 : 0.5))
                ];
            }
        }
    }

    static async Task EditFileAsync(
        HttpClient client,
        EventStream events,
        string fileId,
        AudioEditOperation operation
    ) {
        var response = await PostEditAsync(client, fileId, operation);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var job = (await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.Job, Token))!;
        Assert.Equal(JobKind.AudioEdit, job.Kind);
        var done = await events.WaitAsync<JobEvent>(e =>
            e.Job.Id == job.Id && e.Job.State is JobState.Done or JobState.Failed
        );
        Assert.Equal(JobState.Done, done.Job.State);
    }

    static async Task SaveAsync(HttpClient client) =>
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/v1/project/save", null, Token)).StatusCode);

    static Task<HttpResponseMessage> PostEditAsync(HttpClient client, string fileId, AudioEditOperation operation) =>
        client.PostAsJsonAsync(
            $"/api/v1/project/files/{fileId}/edit",
            operation,
            ContractsJsonContext.Default.AudioEditOperation,
            Token
        );

    static async Task<SampleRange> SamplesAsync(
        HttpClient client,
        string fileId,
        long start,
        long frames,
        int bucket
    ) =>
        (await client.GetFromJsonAsync(
            $"/api/v1/project/files/{fileId}/samples?start={start}&frames={frames}&bucket={bucket}",
            ContractsJsonContext.Default.SampleRange,
            Token
        ))!;

    static async Task<StepInputState> StepInputAsync(HttpClient client) =>
        (await client.GetFromJsonAsync(
            "/api/v1/project/step-input",
            ContractsJsonContext.Default.StepInputState,
            Token
        ))!;

    static async Task<StepInputState> SetStepInputAsync(HttpClient client, StepInputRequest request) {
        var response = await client.PutAsJsonAsync(
            "/api/v1/project/step-input",
            request,
            ContractsJsonContext.Default.StepInputRequest,
            Token
        );
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.StepInputState, Token))!;
    }

    static async Task<StepInputState> PostStateAsync(HttpClient client, string path) {
        var response = await client.PostAsync($"/api/v1/{path}", null, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.StepInputState, Token))!;
    }

    static async Task<IReadOnlyList<T>> GetAsync<T>(
        HttpClient client,
        string path,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<IReadOnlyList<T>> type
    ) =>
        (await client.GetFromJsonAsync($"/api/v1/{path}", type, Token))!;

    static async Task<T> PostAsync<T>(
        HttpClient client,
        string path,
        T body,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type
    ) {
        var response = await client.PostAsJsonAsync($"/api/v1/{path}", body, type, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync(type, Token))!;
    }

    static Task<HttpResponseMessage> LiveAsync(HttpClient client, string track, params LiveMidiEvent[] events) =>
        client.PostAsJsonAsync(
            "/api/v1/midi/live",
            new(track, events),
            ContractsJsonContext.Default.LiveMidiRequest,
            Token
        );

    static FakeHost Host(ServerFixture server) => server.Services.GetRequiredService<EngineConnection>().InProcessHost!;

    static Task WaitEngineAsync(ServerFixture server, HttpClient client) =>
        ServerFixture.Eventually(async () => (await server.SystemAsync(client)).Engine.State == EngineState.InProcess);
}
