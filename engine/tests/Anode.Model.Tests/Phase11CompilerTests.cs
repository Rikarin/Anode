using Anode.Contracts;
using Anode.Ipc;
using Anode.Ipc.Segments;
using Anode.Model.Compile;
using Anode.Model.Editing;

namespace Anode.Model.Tests;

/// <summary>
///     Phase 11's compiler (docs/12-phase-11.md): Pitch Source over chord changes (MIDI by root and to chord tones, audio
///     by a transposition per chord segment, crossfaded), Session Player regions as MIDI of their generated notes (a
///     Drummer's through its pads) and their recompilation, the Environment flattened for the host, and a Mastering
///     Assistant's chain in its insert.
/// </summary>
public sealed class Phase11CompilerTests {
    readonly SequentialIds ids = new();
    ProjectState state = ProjectDefaults.NewProject("prj_1", "Song", 48_000);

    static PitchName N(PitchStep step, int alter = 0) => new(step, alter);

    void Apply(params EditCommand[] commands) =>
        state = Edits.Apply(
                state,
                commands,
                ids,
                RandomProjects.Plugins,
                RandomProjects.Patches,
                styles: RandomProjects.Styles
            )
            .State;

    /// <summary>C, Am, F and G, a bar each.</summary>
    void Chords() =>
        Apply(
            new AddChordsCommand(
                [
                    new(0, N(PitchStep.C), ChordQualities.Major), new(3840, N(PitchStep.A), ChordQualities.Minor),
                    new(7680, N(PitchStep.F), ChordQualities.Major), new(11_520, N(PitchStep.G), ChordQualities.Major)
                ]
            )
        );

    static int[] NoteOns(TrackModel track) => [
        ..track.Events.Where(static e => e.Kind == PlaybackEventKind.NoteOn).Select(static e => (int)e.Data1)
    ];

    TrackModel TrackOf(CompiledModel model, string trackId) {
        var engineId = EngineIds.Empty.With(state).Track(trackId);
        return model.Tracks.Single(t => t.TrackId == engineId);
    }

    [Theory]
    [InlineData(PitchSourceMode.Off, new[] { 52, 52, 52, 52 })]
    [InlineData(PitchSourceMode.Root, new[] { 52, 49, 57, 47 })]
    [InlineData(PitchSourceMode.Chords, new[] { 52, 48, 57, 47 })]
    public void A_MIDI_region_follows_each_bar_s_chord(PitchSourceMode mode, int[] expected) {
        Apply(new AddTrackCommand(Kind: TrackKind.Instrument));
        var track = state.Tracks[0].Id;
        Apply(new AddMidiRegionCommand(track, 0, 15_360));
        var region = state.MidiRegions.Keys.Single();
        Apply(
            new AddNotesCommand(
                region,
                [..Enumerable.Range(0, 4).Select(static i => new NewNote(52, 100, 1, i * 3840L, 960))]
            ),
            new SetPitchSourceCommand([region], mode)
        );
        Chords();
        Assert.Equal(
            expected,
            NoteOns(TrackOf(new ModelCompiler().Compile(state, EngineIds.Empty.With(state)), track))
        );
    }

    [Fact]
    public void A_region_in_another_key_transposes_from_its_own_tonic_and_follows_chord_track_changes() {
        Apply(new AddTrackCommand(Kind: TrackKind.Instrument));
        var track = state.Tracks[0].Id;
        Apply(new AddMidiRegionCommand(track, 0, 3840));
        var region = state.MidiRegions.Keys.Single();
        Apply(
            new AddNotesCommand(region, [new(45, 100, 1, 0, 960)]),
            new SetPitchSourceCommand([region], PitchSourceMode.Root),
            new SetRegionKeyCommand([region], new(N(PitchStep.A), KeyMode.Minor)),
            new AddChordsCommand([new(0, N(PitchStep.D), ChordQualities.Minor)])
        );
        var compiler = new ModelCompiler();
        // A to D: up a fourth (+5).
        Assert.Equal([50], NoteOns(TrackOf(compiler.Compile(state, EngineIds.Empty.With(state)), track)));
        // Another chord recompiles the track; a change elsewhere does not.
        Apply(new EditChordsCommand([state.ChordTrack.Chords[0] with { Root = N(PitchStep.E) }]));
        var model = compiler.Compile(state, EngineIds.Empty.With(state));
        Assert.Equal([40], NoteOns(TrackOf(model, track)));
        Assert.Contains(EngineIds.Empty.With(state).Track(track), model.Recompiled);
        Apply(new SetMetronomeCommand(LevelDb: -12));
        Assert.DoesNotContain(
            EngineIds.Empty.With(state).Track(track),
            compiler.Compile(state, EngineIds.Empty.With(state)).Recompiled
        );
    }

    [Fact]
    public void An_audio_region_plays_a_transposition_per_chord_segment_crossfaded() {
        Apply(new AddTrackCommand(Kind: TrackKind.Audio));
        state = state.Apply(
            new AudioFileUpserted(new("aud_1", "Bass", "Audio Files/Bass.wav", 48_000, 1, 480_000, 24, null, 0))
        );
        var track = state.Tracks[0].Id;
        Apply(new AddRegionCommand(track, "aud_1", 0, Length: 4 * 96_000));
        Chords();
        var unfollowed = TrackOf(new ModelCompiler().Compile(state, EngineIds.Empty.With(state)), track);
        Assert.Single(unfollowed.Playlist);
        Apply(new SetPitchSourceCommand([state.Regions.Keys.Single()], PitchSourceMode.Root));
        var model = TrackOf(new ModelCompiler().Compile(state, EngineIds.Empty.With(state)), track);
        // A bar is 96 000 samples at 120 BPM; C, Am (−3), F (+5), G (−5).
        Assert.Equal([0L, 96_000, 192_000, 288_000], model.Playlist.Select(static e => e.Start));
        var fade = (long)Math.Round(PitchFollow.CrossfadeSeconds * 48_000);
        int[] cents = [0, -300, 500, -500];
        for (var i = 0; i < model.Playlist.Length; i++) {
            var entry = model.Playlist[i];
            Assert.True(entry.WarpCount >= 2);
            Assert.Equal(cents[i], entry.PitchCount == 0 ? 0 : model.Pitch![entry.PitchStart].Cents);
            if (i + 1 < model.Playlist.Length) {
                Assert.Equal(model.Playlist[i + 1].Start + fade, entry.Start + entry.Length);
                Assert.Equal(((int)fade, (int)fade), (entry.FadeOut, model.Playlist[i + 1].FadeIn));
            }
        }

        ArrangementCompilerTests.AssertWarpsValid(model);
    }

    [Fact]
    public void Session_Player_regions_play_their_generated_notes_and_recompile_only_when_the_notes_change() {
        Apply(new AddSessionPlayerTrackCommand(SessionPlayerKind.Bass, Regions: false));
        var track = state.Tracks[0].Id;
        Apply(new AddSessionPlayerRegionCommand(track, 3840, 3840));
        var region = state.SessionPlayerRegions.Keys.Single();
        Note[] notes = [new(1, 40, 100, 1, 0, 480, 0), new(2, 43, 90, 1, 960, 480, 0), new(3, 45, 90, 1, 3900, 480, 0)];
        var compiler = new ModelCompiler();
        Assert.Empty(NoteOns(TrackOf(compiler.Compile(state, EngineIds.Empty.With(state)), track)));
        var played = new Dictionary<string, IReadOnlyList<Note>> { [region] = notes };
        var model = compiler.Compile(state, EngineIds.Empty.With(state), null, played);
        // A note past the region's end does not play.
        Assert.Equal([40, 43], NoteOns(TrackOf(model, track)));
        var engineId = EngineIds.Empty.With(state).Track(track);
        Assert.Contains(engineId, model.Recompiled);
        Assert.DoesNotContain(engineId, compiler.Compile(state, EngineIds.Empty.With(state), null, played).Recompiled);
        var again = new Dictionary<string, IReadOnlyList<Note>> { [region] = [notes[0]] };
        Assert.Equal([40], NoteOns(TrackOf(compiler.Compile(state, EngineIds.Empty.With(state), null, again), track)));
        Apply(new SetSessionPlayerRegionCommand(region, Muted: true));
        Assert.Empty(NoteOns(TrackOf(compiler.Compile(state, EngineIds.Empty.With(state), null, again), track)));
    }

    [Fact]
    public void A_Drummer_s_notes_play_on_its_pads() {
        Apply(new AddSessionPlayerTrackCommand(SessionPlayerKind.Drummer, "drums.rock"));
        var drummer = state.Tracks[0];
        var region = state.SessionPlayerRegions.Keys.Single();
        Note[] notes =
            [new(1, DrumKitNotes.Kick, 100, 10, 0, 120, 0), new(2, DrumKitNotes.Snare, 100, 10, 960, 120, 0)];
        var model = new ModelCompiler().Compile(
            state,
            EngineIds.Empty.With(state),
            null,
            new Dictionary<string, IReadOnlyList<Note>> { [region] = notes }
        );
        Assert.Empty(NoteOns(TrackOf(model, drummer.Id)));
        var kick = drummer.DrumMachine!.Pads.Single(static p => p.Note == DrumKitNotes.Kick).TrackId;
        var snare = drummer.DrumMachine.Pads.Single(static p => p.Note == DrumKitNotes.Snare).TrackId;
        Assert.Equal([DrumKitNotes.Kick], NoteOns(TrackOf(model, kick)));
        Assert.Equal([DrumKitNotes.Snare], NoteOns(TrackOf(model, snare)));
    }

    [Fact]
    public void The_Environment_compiles_flat_with_every_route_going_forward() {
        Assert.Null(EnvironmentCompiler.Compile(state, EngineIds.Empty.With(state)));
        Apply(new AddTrackCommand(Kind: TrackKind.Instrument), new AddTrackCommand(Kind: TrackKind.Audio));
        var (instrument, audio) = (state.Tracks[0].Id, state.Tracks[1].Id);
        Apply(
            new AddEnvironmentObjectCommand(EnvironmentObjectKind.TrackInput, 300, 0, TrackId: instrument),
            new AddEnvironmentObjectCommand(
                EnvironmentObjectKind.Transformer,
                100,
                0,
                Conditions: [new(TransformField.Status, TransformConditionOp.Equal, 144)],
                Operations: [new(TransformField.Pitch, TransformOperationKind.Add, 12)]
            ),
            new AddEnvironmentObjectCommand(EnvironmentObjectKind.Monitor, 200, 0),
            new AddEnvironmentObjectCommand(EnvironmentObjectKind.PhysicalInput, 0, 0),
            new AddEnvironmentObjectCommand(EnvironmentObjectKind.Fader, 0, 100),
            new AddEnvironmentObjectCommand(
                EnvironmentObjectKind.ControlTarget,
                100,
                100,
                TrackId: audio,
                Address: "volume"
            ),
            new AddEnvironmentLayerCommand("Second"),
            new AddEnvironmentObjectCommand(EnvironmentObjectKind.Monitor, 0, 0)
        );
        var o = state.Environment.Objects.Select(static x => x.Id).ToArray();
        Apply(
            new ConnectEnvironmentCommand(o[3], 0, o[1], 0),
            new ConnectEnvironmentCommand(o[1], 0, o[2], 0),
            new ConnectEnvironmentCommand(o[1], 1, o[6], 0),
            new ConnectEnvironmentCommand(o[2], 0, o[0], 0),
            new ConnectEnvironmentCommand(o[4], 0, o[5], 0)
        );
        var engineIds = EngineIds.Empty.With(state);
        var compiled = EnvironmentCompiler.Compile(state, engineIds)!;
        Assert.Equal(
            [
                EngineEnvironmentKind.PhysicalInput, EngineEnvironmentKind.Transformer, EngineEnvironmentKind.Monitor,
                EngineEnvironmentKind.TrackInput, EngineEnvironmentKind.Fader, EngineEnvironmentKind.ControlTarget,
                EngineEnvironmentKind.Monitor
            ],
            compiled.Objects.Select(static x => x.Kind)
        );
        Assert.All(compiled.Objects, static x => Assert.All(x.Routes, r => Assert.True(r.To > x.Index)));
        Assert.Equal([new EngineEnvironmentRoute(0, 2, 0), new(1, 6, 0)], compiled.Objects[1].Routes);
        Assert.Equal(new("pitch", "add", 12, 0), compiled.Objects[1].Operations!.Single());
        Assert.Equal(new("status", "equal", 144, 0), compiled.Objects[1].Conditions!.Single());
        Assert.Equal(engineIds.Track(instrument), compiled.Objects[3].TrackId);
        Assert.Equal(
            (engineIds.Track(audio), "volume"),
            (compiled.Objects[5].Control!.TrackId, compiled.Objects[5].Control!.Kind)
        );
        // Monitors and faders on telemetry slots of their own, in order.
        Assert.Equal([-1, -1, 0, -1, 0, -1, 1], compiled.Objects.Select(static x => x.TelemetrySlot));
        Assert.Equal(7, compiled.Objects[4].Parameters["number"]);
    }

    [Fact]
    public void An_analysed_Mastering_Assistant_carries_its_chain_and_a_new_chain_is_a_structural_change() {
        var (added, assistant) = Edits.AddMasteringAssistant(state, ids);
        state = state.Apply(added);
        var designer = new CountingDesigner();
        var chains = new MasteringChains(designer);
        var engineIds = EngineIds.Empty.With(state);
        Assert.Null(Strips.Engine(state, null, engineIds, chainOf: chains.Of).Inserts![0].Mastering);
        Apply(new SetMasteringAnalysisCommand(assistant.Id, RandomProjects.MasteringAnalysis(new(3))));
        var analysed = Strips.Engine(state, null, engineIds, chainOf: chains.Of);
        Assert.Equal(-2f, analysed.Inserts![0].Mastering!.ChainGainDb);
        Assert.Equal(1, designer.Calls);
        // Designed once per analysis and character.
        _ = Strips.Engine(state, null, engineIds, chainOf: chains.Of);
        Assert.Equal(1, designer.Calls);
        Apply(new SetMasteringCommand(Character: MasteringCharacter.Punch));
        var punched = Strips.Engine(state, null, engineIds, chainOf: chains.Of);
        Assert.Equal(
            (2, (float)MasteringCharacter.Punch),
            (designer.Calls, punched.Inserts![0].Mastering!.CompressorRatio)
        );
        Assert.True(Strips.StructureDiffers(analysed, punched));
        // A designer that fails gives no chain.
        Assert.Null(new MasteringChains(new FailingDesigner()).Of(Edits.MasteringAssistantOf(state)!));
    }

    sealed class CountingDesigner : IMasteringDesigner {
        public int Calls { get; private set; }

        public EngineMasteringChain Design(MasteringAnalysis analysis, MasteringCharacter character) {
            Calls++;
            return new([..Enumerable.Repeat(0f, 30)], -18, (float)character, (float)analysis.IntegratedLufs, -2);
        }
    }

    sealed class FailingDesigner : IMasteringDesigner {
        public EngineMasteringChain Design(MasteringAnalysis analysis, MasteringCharacter character) =>
            throw new InvalidOperationException("The designer failed.");
    }
}
