using Anode.Contracts;
using Anode.Ipc.Segments;
using Anode.Model.Compile;
using Anode.Model.Editing;
using Anode.Model.Time;

namespace Anode.Model.Tests;

/// <summary>
///     Phase 5 edits (docs/06-phase-5.md): flex, quantise audio, Smart Tempo, beat mapping, Strip Silence, track
///     alternatives, Flex Pitch to MIDI, and how the compiler plays them.
/// </summary>
public sealed class FlexEditTests {
    const int Rate = 48_000;

    /// <summary>A 100 bpm beat in frames.</summary>
    const long Beat = 28_800;

    readonly SequentialIds ids = new();
    readonly Analysis analysis = new();

    ProjectState state = ProjectDefaults.NewProject("prj_1", "Song", Rate)
        .Apply(new AudioFileUpserted(new("aud_1", "Loop", "Audio Files/Loop.wav", Rate, 1, 16 * Beat, 24, null)));

    string Track => state.Tracks[0].Id;
    AudioRegion Region => state.Regions.Values.Single();

    void Apply(params EditCommand[] commands) =>
        state = Edits.Apply(state, commands, ids, RandomProjects.Plugins, null, analysis).State;

    void Refused(EditCommand command, string message) =>
        Assert.Contains(
            message,
            Assert.Throws<EditException>(() => Edits.Apply(state, command, ids, null, null, analysis)).Message,
            StringComparison.Ordinal
        );

    void LoopOnTrack(long startTick = 0) {
        Apply(new AddTrackCommand());
        Apply(new AddRegionCommand(Track, "aud_1", startTick));
    }

    [Fact]
    public void Following_the_tempo_analyses_the_file_and_stretches_the_region_to_the_project_tempo() {
        LoopOnTrack();
        Apply(new SetRegionFlexCommand(Region.Id, FlexFollow.On));
        Assert.Equal(100, state.Files["aud_1"].Tempo!.Bpm);
        // 16 beats of 100 bpm at 120 bpm: 16 quarter notes, 8 s.
        Assert.Equal(16 * 960, Edits.EndTick(state, Region));
        Assert.Equal(8 * Rate, Edits.EndSample(state, Region));
    }

    [Fact]
    public void A_transpose_needs_flex_or_follow() {
        LoopOnTrack();
        Refused(new SetRegionFlexCommand(Region.Id, Transpose: 2), "need Flex or Follow");
        Apply(new SetTrackFlexCommand(Track, true, FlexMode.Polyphonic));
        Apply(new SetRegionFlexCommand(Region.Id, Transpose: 2, FineTune: -10));
        Assert.Equal((2, -10), (Region.Transpose, Region.FineTune));
        Refused(new SetRegionFlexCommand(Region.Id, Transpose: 40), "transposes");
    }

    [Fact]
    public void Flex_markers_need_flex_and_lie_inside_the_region_in_order() {
        LoopOnTrack();
        Refused(new SetFlexMarkersCommand(Region.Id, [new(Beat, 900)]), "flex on");
        Apply(new SetTrackFlexCommand(Track, true));
        Refused(new SetFlexMarkersCommand(Region.Id, [new(0, 900)]), "strictly inside");
        Refused(new SetFlexMarkersCommand(Region.Id, [new(Beat, 900), new(2 * Beat, 800)]), "increase");
        Apply(new SetFlexMarkersCommand(Region.Id, [new(2 * Beat, 2_000), new(Beat, 900)]));
        Assert.Equal([new FlexMarker(Beat, 900), new FlexMarker(2 * Beat, 2_000)], Region.FlexMarkers);
        // In time at 120 bpm, frame 28 800 is tick 1152; the marker plays it at 900.
        Assert.Equal(900, RegionTiming.Of(state, Region).TickAt(Beat), 9);
    }

    [Fact]
    public void Quantising_audio_puts_markers_on_the_transients_moved_to_the_grid() {
        // A follow-on loop: 100 bpm beats are quarter notes; the transients are humanised 1/16s.
        LoopOnTrack();
        Apply(new SetRegionFlexCommand(Region.Id, FlexFollow.On));
        Apply(new QuantizeAudioCommand([Region.Id], 240));
        Assert.True(state.Tracks[0].FlexOn);
        var timing = RegionTiming.Of(state, Region);
        foreach (var transient in analysis.Transients.Where(f => f > 0 && f < 16 * Beat)) {
            var tick = timing.TickAt(transient);
            Assert.Equal(0, Math.Round(tick) % 240);
            Assert.InRange(tick, transient / 30.0 - 120, transient / 30.0 + 120);
        }

        Assert.NotEmpty(Region.FlexMarkers);
        Refused(new QuantizeAudioCommand([Region.Id], 0), "grid");
    }

    [Fact]
    public void Half_strength_moves_halfway() {
        LoopOnTrack();
        Apply(new SetRegionFlexCommand(Region.Id, FlexFollow.On));
        Apply(new QuantizeAudioCommand([Region.Id], 240, 0.5));
        var timing = RegionTiming.Of(state, Region);
        var humanised = analysis.Transients[1]; // 7200 + 300 frames: tick 250
        Assert.Equal(245, timing.TickAt(humanised), 6);
    }

    [Fact]
    public void Mapping_a_beat_sets_the_tempo_before_it_and_keeps_earlier_mappings() {
        // Bar 2 (tick 3840) at 120 bpm is sample 96 000; map it to 100 000, then bar 3 to 200 000.
        Apply(new MapBeatsCommand([new(3_840, 100_000), new(7_680, 200_000)]));
        var map = state.TempoMap;
        Assert.Equal(100_000, map.SampleAt(3_840, Rate));
        Assert.Equal(200_000, map.SampleAt(7_680, Rate));
        Assert.Equal(3_840 * 60.0 / (960 * (100_000.0 / Rate)), state.Settings.TempoEvents[0].Bpm, 9);
        Assert.Equal(state.Settings.Tempo, state.Settings.TempoEvents[0].Bpm);

        // A later mapping before an earlier one keeps it: the tempo after it changes so bar 3 stays.
        Apply(new MapBeatsCommand([new(1_920, 40_000)]));
        Assert.Equal(40_000, state.TempoMap.SampleAt(1_920, Rate));
        Assert.Equal(100_000, state.TempoMap.SampleAt(3_840, Rate));
        Assert.Equal(200_000, state.TempoMap.SampleAt(7_680, Rate));
        Refused(new MapBeatsCommand([new(960, 50_000)]), "cannot map");
    }

    [Fact]
    public void Mapping_after_a_ramp_keeps_the_ramp() {
        Apply(new SetTempoEventsCommand([new(0, 60, TempoCurve.Ramp, 0.3), new(3_840, 120, TempoCurve.Step)]));
        var before = state.TempoMap.SecondsAt(3_840);
        Apply(new MapBeatsCommand([new(7_680, (long)Math.Round((before + 1.5) * Rate))]));
        Assert.Equal(before, state.TempoMap.SecondsAt(3_840), 9);
        Assert.Equal(Math.Round((before + 1.5) * Rate), state.TempoMap.SampleAt(7_680, Rate), 0);
    }

    [Fact]
    public void Adapting_the_tempo_sets_it_from_the_file_and_plays_the_region_unwarped() {
        LoopOnTrack(1_920);
        Apply(new SetRegionFlexCommand(Region.Id, FlexFollow.On));
        Apply(new AdaptTempoCommand(Region.Id));
        Assert.Equal(FlexFollow.Off, Region.Follow);
        var events = state.Settings.TempoEvents;
        Assert.Equal([0L, 1_920, 1_920 + 16 * 960], events.Select(static e => e.Tick));
        Assert.Equal([120.0, 100, 120], events.Select(static e => e.Bpm));
        // Every beat of the file lands on a quarter note.
        var timing = RegionTiming.Of(state, Region);
        Assert.Equal(1_920 + 5 * 960, timing.TickAt(5 * Beat), 6);
    }

    [Fact]
    public void A_variable_file_adapts_one_tempo_per_beat() {
        // Beats 24 000 then 32 000 frames apart: 120 then 90 bpm.
        state = state.Apply(
            new AudioFileUpserted(
                state.Files["aud_1"] with { Tempo = new(100, true, [0, 24_000, 56_000, 88_000], [0]) }
            )
        );
        LoopOnTrack();
        Apply(new AdaptTempoCommand(Region.Id));
        Assert.Equal([120.0, 90, 90], state.Settings.TempoEvents.Take(3).Select(static e => e.Bpm));
        Assert.Equal(88_000, state.TempoMap.SampleAt(3 * 960, Rate));
    }

    [Fact]
    public void The_first_audio_in_adapt_mode_sets_the_project_tempo() {
        Apply(new SetSmartTempoCommand(ProjectTempoMode.Adapt));
        state = state.Apply(new AudioFileUpserted(state.Files["aud_1"] with { Tempo = analysis.Rhythm(state.Files["aud_1"])!.Tempo }));
        LoopOnTrack();
        Assert.Equal(100, state.Settings.Tempo);
        Assert.Equal(FlexFollow.Off, Region.Follow);
        // The second does not.
        Apply(new SetTempoCommand(120), new AddRegionCommand(Track, "aud_1", 30_000));
        Assert.Equal(120, state.Settings.Tempo);
    }

    [Fact]
    public void Auto_adapts_only_without_MIDI_and_with_one_tempo() {
        Apply(new SetSmartTempoCommand(ProjectTempoMode.Auto, FlexFollow.On));
        state = state.Apply(new AudioFileUpserted(state.Files["aud_1"] with { Tempo = analysis.Rhythm(state.Files["aud_1"])!.Tempo }));
        Apply(new AddTrackCommand(Kind: TrackKind.Instrument), new AddMidiRegionCommand("trk_000001", 0, 960));
        Apply(new AddTrackCommand());
        Apply(new AddRegionCommand(state.Tracks[1].Id, "aud_1", 0));
        Assert.Equal(120, state.Settings.Tempo);
        Assert.Equal(FlexFollow.On, state.Regions.Values.Single().Follow); // the default follow
    }

    [Fact]
    public void Strip_silence_makes_a_region_per_sound() {
        LoopOnTrack(960);
        var id = Region.Id;
        Apply(new StripSilenceCommand([id]));
        var parts = state.Regions.Values.OrderBy(static r => r.StartTick).ToArray();
        // The fake's sound: frames 0…20 000, 30 000…50 000, … of a 460 800-frame file: 16 stretches.
        Assert.Equal(16, parts.Length);
        Assert.Equal(id, parts[0].Id);
        Assert.Equal((960L, 0L, 20_000L), (parts[0].StartTick, parts[0].FileOffset, parts[0].Length));
        // The second starts on the first tick at or after where frame 30 000 played (sample 54 000 = tick 2160).
        Assert.Equal((2_160L, 30_000L, 20_000L), (parts[1].StartTick, parts[1].FileOffset, parts[1].Length));
        Assert.All(parts, p => Assert.Equal(RegionTiming.Of(state, p).StartSample, 24_000 + p.FileOffset));
    }

    [Fact]
    public void Track_alternatives_swap_the_content() {
        LoopOnTrack();
        var kept = Region.Id;
        Apply(new NewTrackAlternativeCommand(Track, Duplicate: true));
        var track = state.Tracks[0];
        Assert.Equal(["A", "B"], track.Alternatives.Select(static a => a.Name));
        Assert.Equal(track.Alternatives[1].Id, track.AlternativeId);
        var copy = Assert.Single(state.Regions.Values);
        Assert.NotEqual(kept, copy.Id);
        Apply(new DeleteRegionsCommand([copy.Id]));
        Assert.Empty(state.Regions);

        Apply(new SelectTrackAlternativeCommand(Track, track.Alternatives[0].Id));
        Assert.Equal(kept, Region.Id);
        Assert.Empty(state.Tracks[0].Alternatives[0].Regions); // the active one's content is live
        Apply(new SelectTrackAlternativeCommand(Track, track.Alternatives[1].Id));
        Assert.Empty(state.Regions);
        Assert.Equal(kept, Assert.Single(state.Tracks[0].Alternatives[0].Regions).Id);

        Apply(new SetTrackAlternativeCommand(Track, track.Alternatives[1].Id, "Edit"));
        Apply(new DeleteTrackAlternativeCommand(Track, track.Alternatives[1].Id));
        Assert.Equal(["A"], state.Tracks[0].Alternatives.Select(static a => a.Name));
        Assert.Equal(kept, Region.Id);
        Refused(new DeleteTrackAlternativeCommand(Track, track.Alternatives[0].Id), "last alternative");
    }

    [Fact]
    public void Flex_pitch_to_MIDI_makes_the_notes_where_they_play() {
        LoopOnTrack(960);
        Apply(new SetRegionFlexCommand(Region.Id, FlexFollow.On));
        Apply(
            new SetPitchNotesCommand(
                Region.Id,
                [new(0, Beat, 6_010, 6_000, 1, 1, 0, 0), new(2 * Beat, 3 * Beat, 6_390, 6_400, 1, 0.5, 0, -3)]
            )
        );
        Apply(new FlexPitchToMidiCommand(Region.Id));
        Assert.Equal(TrackKind.Instrument, state.Tracks[1].Kind);
        var midi = state.MidiRegions.Values.Single();
        Assert.Equal((960L, 16 * 960L), (midi.StartTick, midi.LengthTicks));
        Assert.Equal(
            [(60, 0L, 960L, 100), (64, 1_920L, 960L, 94)],
            midi.Notes.Select(static n => (n.Pitch, n.StartTick, n.LengthTicks, n.Velocity))
        );
    }

    [Fact]
    public void A_warped_region_compiles_to_warp_points_and_its_track_algorithm() {
        LoopOnTrack();
        Apply(new SetTrackFlexCommand(Track, true, FlexMode.Slicing));
        Apply(new SetRegionFlexCommand(Region.Id, FlexFollow.On, Transpose: 2));
        Apply(new SetFlexMarkersCommand(Region.Id, [new(Beat, 900)]));
        var model = new ModelCompiler().Compile(state, EngineIds.Empty.With(state)).Tracks[0];
        var entry = Assert.Single(model.Playlist);
        Assert.Equal(FlexAlgorithm.Slicing, entry.Algorithm);
        Assert.Equal(8 * Rate, entry.Length);
        var warp = model.Warp![entry.WarpStart..(entry.WarpStart + entry.WarpCount)];
        Assert.Equal((0L, 0.0), (warp[0].Offset, warp[0].FileFrame));
        Assert.Equal((entry.Length, 16.0 * Beat), (warp[^1].Offset, warp[^1].FileFrame));
        // The marker: frame 28 800 at tick 900, sample 22 500.
        Assert.Contains(warp, static p => p.Offset == 22_500 && Math.Abs(p.FileFrame - Beat) < 1e-6);
        // One point per quarter note at least.
        Assert.True(entry.WarpCount >= 17);
        var pitch = model.Pitch![entry.PitchStart..(entry.PitchStart + entry.PitchCount)];
        Assert.All(pitch, static p => Assert.Equal(200, p.Cents));
        ArrangementCompilerTests.AssertWarpsValid(model);
    }

    [Fact]
    public void Pitch_notes_scale_drift_and_vibrato_of_the_analysed_curve() {
        LoopOnTrack();
        Apply(new SetTrackFlexCommand(Track, true, FlexMode.Pitch));
        // The analysed curve: 6000 cents with a ±20-cent vibrato; symmetric about the note's middle, so its trend is flat; the note corrected to 6100 with the vibrato halved.
        Apply(new SetPitchNotesCommand(Region.Id, [new(0, 48_000, 6_000, 6_100, 1, 0.5, 50, -2)]));
        var analyses = new Dictionary<string, FileAnalysis> { ["aud_1"] = analysis.WithVibrato(state.Files["aud_1"]) };
        var model = new ModelCompiler().Compile(state, EngineIds.Empty.With(state), analyses).Tracks[0];
        var entry = model.Playlist.Single();
        var pitch = model.Pitch![entry.PitchStart..(entry.PitchStart + entry.PitchCount)];
        foreach (var p in pitch.Where(static p => p.FileFrame < 48_000)) {
            var vibrato = Math.Round(p.FileFrame / 240) >= 200
                ? 0
                : 20 * Math.Cos(2 * Math.PI * (Math.Round(p.FileFrame / 240) * 240 - 23_880) / 8_000);
            Assert.InRange(p.Cents, 100 - 0.5 * vibrato - 0.5, 100 - 0.5 * vibrato + 0.5);
            Assert.Equal((50f, -2f), (p.FormantCents, p.GainDb));
        }

        Assert.Equal(0, pitch[^1].Cents);
    }

    [Fact]
    public void Varispeed_scales_positions_and_warps_every_region() {
        LoopOnTrack();
        Apply(new AddTrackCommand(Kind: TrackKind.Instrument));
        Apply(new AddMidiRegionCommand(state.Tracks[1].Id, 960, 960));
        var region = state.MidiRegions.Keys.Single();
        Apply(new AddNotesCommand(region, [new(60, 100, 1, 0, 480)]));
        Apply(new SetVarispeedCommand(true, VarispeedMode.SpeedAndPitchMidi, 0.9));
        var model = new ModelCompiler().Compile(state, EngineIds.Empty.With(state));
        var entry = model.Tracks[0].Playlist.Single();
        Assert.Equal(FlexAlgorithm.Speed, entry.Algorithm);
        Assert.Equal(Math.Round(16 * Beat / 0.9), entry.Length);
        var on = model.Tracks[1].Events.Single(static e => e.Kind == PlaybackEventKind.NoteOn);
        Assert.Equal(Math.Round(24_000 / 0.9), on.SamplePos);
        Assert.Equal(58, on.Data1); // round(12·log2 0.9) = −2

        Apply(new SetVarispeedCommand(Mode: VarispeedMode.SpeedOnly));
        model = new ModelCompiler().Compile(state, EngineIds.Empty.With(state));
        Assert.Equal(FlexAlgorithm.Polyphonic, model.Tracks[0].Playlist.Single().Algorithm);
        Assert.Equal(60, model.Tracks[1].Events.Single(static e => e.Kind == PlaybackEventKind.NoteOn).Data1);
        Assert.Equal(1 / 0.9, state.PlaybackMap.SecondsAt(1_920), 12);
        Refused(new SetVarispeedCommand(Speed: 2), "Varispeed");
    }

    [Fact]
    public void Splitting_a_following_region_splits_where_the_tick_plays() {
        LoopOnTrack();
        Apply(new SetRegionFlexCommand(Region.Id, FlexFollow.On));
        Apply(new SplitRegionCommand(Region.Id, 4 * 960));
        var parts = state.Regions.Values.OrderBy(static r => r.StartTick).ToArray();
        Assert.Equal((4 * Beat, 4 * Beat), (parts[0].Length, parts[1].FileOffset));
        Assert.Equal(16 * 960, Edits.EndTick(state, parts[1]));
        // And they join back.
        Apply(new JoinRegionsCommand([parts[0].Id, parts[1].Id]));
        Assert.Equal(16 * Beat, Region.Length);
    }

    [Fact]
    public void Moving_a_following_region_keeps_its_length_in_ticks_across_a_tempo_change() {
        LoopOnTrack();
        Apply(new SetRegionFlexCommand(Region.Id, FlexFollow.On));
        Apply(new SetTempoEventsCommand([new(0, 120, TempoCurve.Step), new(3_840, 60, TempoCurve.Step)]));
        Apply(new MoveRegionCommand(Region.Id, 7_680));
        Assert.Equal(7_680 + 16 * 960, Edits.EndTick(state, Region));
    }

    /// <summary>
    ///     A deterministic analysis: 100 bpm beats (downbeats every fourth); transients on every 1/16 (7200 frames),
    ///     every second one 300 frames late; sound for 20 000 frames of every 30 000.
    /// </summary>
    sealed class Analysis : IAnalysisSource {
        public long[] Transients { get; } = [..Enumerable.Range(0, 64).Select(static i => i * 7_200L + (i % 2) * 300)];

        public FileAnalysis Rhythm(AudioFile file) {
            long[] beats = [..Enumerable.Range(0, (int)(file.Frames / Beat)).Select(static i => i * Beat)];
            return new(
                file.Id,
                240,
                Transients,
                new(100, false, beats, [..beats.Where(static (_, i) => i % 4 == 0)]),
                [],
                [],
                false
            );
        }

        /// <summary>Pitch at 6000 cents with a 6 Hz-ish (8000-frame) ±20-cent vibrato over the first second.</summary>
        public FileAnalysis WithVibrato(AudioFile file) =>
            Rhythm(file) with {
                Pitch = [
                    ..Enumerable.Range(0, 200)
                        .Select(static k => (float?)(6_000 + 20 * Math.Cos(2 * Math.PI * (k * 240 - 23_880) / 8_000.0)))
                ]
            };

        public IReadOnlyList<(long Start, long End)> Silence(
            AudioFile file,
            double thresholdDb,
            double minTimeMs,
            double preAttackMs,
            double postReleaseMs
        ) =>
            [
                ..Enumerable.Range(0, (int)(file.Frames / 30_000) + 1)
                    .Select(static i => (i * 30_000L, i * 30_000L + 20_000))
                    .Where(x => x.Item1 < file.Frames)
                    .Select(x => (x.Item1, Math.Min(x.Item2, file.Frames)))
            ];
    }
}
