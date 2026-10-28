using Anode.Contracts;
using Anode.Ipc.Segments;
using Anode.Model.Compile;
using Anode.Model.Editing;
using CsCheck;

namespace Anode.Model.Tests;

/// <summary>
///     The compiler's Phase 3 rules (docs/04-phase-3.md): tempo ramps, clicks on the signature track, fade curves,
///     crossfades, comps, mute and stacks.
/// </summary>
public sealed class ArrangementCompilerTests {
    const long Bar = 3840;
    readonly SequentialIds ids = new();

    ProjectState state = ProjectDefaults.NewProject("prj_1", "Song", 48_000)
        .Apply(new AudioFileUpserted(new("aud_1", "A", "Audio Files/A.wav", 48_000, 1, 480_000, 24, null)))
        .Apply(new AudioFileUpserted(new("aud_2", "B", "Audio Files/B.wav", 48_000, 1, 480_000, 24, null)));

    [Fact]
    public void Regions_and_clicks_follow_a_tempo_ramp() {
        Apply(new AddTrackCommand());
        Apply(new AddRegionCommand(state.Tracks[0].Id, "aud_1", Bar, 0, 1000));
        Apply(new SetTempoEventsCommand([new(0, 60, TempoCurve.Ramp), new(Bar, 120, TempoCurve.Step)]));
        var model = Compile();
        // The ramp's bar lasts 4·ln 2 s.
        Assert.Equal((long)Math.Round(4 * Math.Log(2) * 48_000), model.Tracks[0].Playlist[0].Start);
        var clicks = model.Tracks[^1].Events;
        Assert.Equal(0, clicks[0].SamplePos);
        // Each beat of the ramp is shorter than the one before it.
        Assert.True(clicks[1].SamplePos - clicks[0].SamplePos > clicks[2].SamplePos - clicks[1].SamplePos);
        // After it, a beat at 120 is 24 000 samples.
        Assert.Equal(24_000, clicks[5].SamplePos - clicks[4].SamplePos);
    }

    [Fact]
    public void Clicks_follow_the_signature_track() {
        Apply(new SetSignaturesCommand([new(0, 4, 4), new(Bar, 3, 4), new(Bar + 2880, 6, 8)]));
        var clicks = Compile().Tracks[^1].Events;
        Assert.Equal(
            [1, 0, 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 1],
            clicks.Take(14).Select(static e => (int)e.Data1)
        );
        // The 6/8 bar clicks on eighths: 12 000 samples apart at 120 BPM.
        Assert.Equal(12_000, clicks[8].SamplePos - clicks[7].SamplePos);
    }

    [Fact]
    public void Fade_curves_go_into_the_entries() {
        Apply(new AddTrackCommand());
        Apply(new AddRegionCommand(state.Tracks[0].Id, "aud_1", 0, 0, 10_000));
        Apply(
            new SetRegionCommand(
                state.Regions.Keys.Single(),
                FadeIn: 100,
                FadeOut: 200,
                FadeInCurve: 0.5,
                FadeOutCurve: -1
            )
        );
        var entry = Assert.Single(Compile().Tracks[0].Playlist);
        Assert.Equal((100, 200, 0.5f, -1f), (entry.FadeIn, entry.FadeOut, entry.FadeInCurve, entry.FadeOutCurve));
    }

    [Fact]
    public void Regions_overlapping_inside_their_fades_crossfade_and_others_follow_the_later_one() {
        Apply(new AddTrackCommand());
        var track = state.Tracks[0].Id;
        // A: 0…48 000, B from 47 000 (sample) – an overlap of 1 000 frames.
        Apply(
            new AddRegionCommand(track, "aud_1", 0, 0, 48_000),
            new AddRegionCommand(track, "aud_2", 1880, 0, 48_000)
        );
        var (a, b) = (state.RegionsOf(track).First().Id, state.RegionsOf(track).Last().Id);
        Assert.Equal(47_000, state.TempoMap.SampleAt(1880, 48_000));

        // No fades: the later region wins, the earlier is cut where it starts.
        var cut = Compile().Tracks[0].Playlist;
        Assert.Equal([(0L, 47_000L), (47_000L, 48_000L)], cut.Select(static e => (e.Start, e.Length)));

        // Fades over the whole overlap: both play, crossfading.
        Apply(new SetRegionCommand(a, FadeOut: 1000), new SetRegionCommand(b, FadeIn: 1000));
        var crossfade = Compile().Tracks[0].Playlist;
        Assert.Equal([(0L, 48_000L), (47_000L, 48_000L)], crossfade.Select(static e => (e.Start, e.Length)));
        AssertHostAccepts(crossfade);

        // A fade shorter than the overlap is not a crossfade.
        Apply(new SetRegionCommand(b, FadeIn: 999));
        Assert.Equal(47_000, Compile().Tracks[0].Playlist[0].Length);
    }

    [Fact]
    public void Muted_regions_and_folders_play_nothing() {
        Apply(new AddTrackCommand(), new AddTrackCommand(Kind: TrackKind.Midi));
        var (audio, midi) = (state.Tracks[0].Id, state.Tracks[1].Id);
        Apply(new AddRegionCommand(audio, "aud_1", 0, 0, 1000), new AddMidiRegionCommand(midi, 0, Bar));
        var midiRegion = state.MidiRegions.Keys.Single();
        Apply(new AddNotesCommand(midiRegion, [new(60, 100, 1, 0, 480)]));
        Assert.NotEmpty(Compile().Tracks[1].Events);
        Apply(
            new SetRegionCommand(state.Regions.Keys.Single(), Muted: true),
            new SetMidiRegionCommand(midiRegion, Muted: true)
        );
        var model = Compile();
        Assert.Empty(model.Tracks[0].Playlist);
        Assert.Empty(model.Tracks[1].Events);

        Apply(new SetRegionCommand(state.Regions.Keys.Single(), Muted: false));
        Apply(new PackTakeFolderCommand([state.Regions.Keys.Single()]));
        Assert.Single(Compile().Tracks[0].Playlist);
        Apply(new SetTakeFolderCommand(state.TakeFolders.Keys.Single(), Muted: true));
        Assert.Empty(Compile().Tracks[0].Playlist);
    }

    [Fact]
    public void A_comp_plays_its_segments_crossfaded_and_an_empty_comp_nothing() {
        Apply(new AddTrackCommand());
        var track = state.Tracks[0].Id;
        Apply(new AddRegionCommand(track, "aud_1", 0, 0, 96_000), new AddRegionCommand(track, "aud_2", 0, 0, 96_000));
        Apply(new PackTakeFolderCommand([..state.Regions.Keys]));
        var folder = state.TakeFolders.Values.Single();
        Apply(
            new SetCompCommand(
                folder.Id,
                [
                    new(0, 960, folder.Takes[0].Id), new(960, 1920, folder.Takes[1].Id),
                    new(1920, Bar, folder.Takes[0].Id)
                ]
            )
        );
        var engine = EngineIds.Empty.With(state);
        var playlist = new ModelCompiler().Compile(state, engine).Tracks[0].Playlist;
        Assert.Equal(3, playlist.Length);
        Assert.Equal(
            [
                engine.File(folder.Takes[0].FileId), engine.File(folder.Takes[1].FileId),
                engine.File(folder.Takes[0].FileId)
            ],
            playlist.Select(static e => e.FileId)
        );
        var crossfade = (int)folder.Crossfade;
        Assert.Equal([0, crossfade, crossfade], playlist.Select(static e => e.FadeIn));
        Assert.Equal([crossfade, crossfade, 0], playlist.Select(static e => e.FadeOut));
        Assert.All(playlist, static e => Assert.Equal((0f, 0f), (e.FadeInCurve, e.FadeOutCurve)));
        // Centred on the boundary at 24 000.
        Assert.Equal(24_000 - crossfade / 2, playlist[1].Start);
        Assert.Equal(24_000 + (crossfade - crossfade / 2), playlist[0].Start + playlist[0].Length);
        AssertHostAccepts(playlist);

        Apply(new SetCompCommand(folder.Id, []));
        Assert.Empty(Compile().Tracks[0].Playlist);
    }

    [Fact]
    public void A_stack_has_no_engine_track() {
        Apply(new AddTrackCommand("A"), new AddTrackCommand("B"));
        Apply(new CreateStackCommand([state.Tracks[0].Id, state.Tracks[1].Id]));
        var engine = EngineIds.Empty.With(state);
        var model = new ModelCompiler().Compile(state, engine);
        Assert.Equal(3, model.Tracks.Count); // A, B and the clicks
        Assert.Throws<KeyNotFoundException>(() => engine.Track(state.Tracks[0].Id));
    }

    [Fact]
    public void A_tempo_track_change_recompiles_every_track_and_a_signature_change_only_the_clicks() {
        Apply(new AddTrackCommand(), new AddTrackCommand());
        var compiler = new ModelCompiler();
        var engine = EngineIds.Empty.With(state);
        compiler.Compile(state, engine);
        Apply(new SetTempoEventsCommand([new(0, 120, TempoCurve.Step), new(Bar, 90, TempoCurve.Step)]));
        Assert.Equal(3, compiler.Compile(state, engine).Recompiled.Count);
        Apply(new SetSignaturesCommand([new(0, 4, 4), new(Bar, 5, 4)]));
        Assert.Equal([TrackEntry.MetronomeTrack], compiler.Compile(state, engine).Recompiled);
    }

    [Fact]
    public void Every_compiled_playlist_is_one_the_host_accepts() =>
        Gen.Int.Sample(
            static seed => {
                var (state, ids, random) = RandomProjects.Build(seed, 40);
                // Fades on every region, so overlaps are often crossfades.
                foreach (var region in state.Regions.Values.ToArray()) {
                    var fade = random.NextInt64(0, region.Length / 2 + 1);
                    state = Edits.Apply(state, new SetRegionCommand(region.Id, FadeIn: fade, FadeOut: fade), ids).State;
                }

                var model = new ModelCompiler().Compile(state, EngineIds.Empty.With(state));
                foreach (var track in model.Tracks) {
                    AssertHostAccepts(track.Playlist);
                }
            },
            iter: 200
        );

    /// <summary>What <c>MappedSegment.Verify</c> checks of a playlist: sorted, overlapping only as crossfades.</summary>
    static void AssertHostAccepts(AudioPlaylistEntry[] playlist) {
        for (var e = 1; e < playlist.Length; e++) {
            var previous = playlist[e - 1];
            var current = playlist[e];
            var overlap = previous.Start + previous.Length - current.Start;
            var threeAtOnce = e >= 2 && playlist[e - 2].Start + playlist[e - 2].Length > current.Start;
            Assert.True(current.Start >= previous.Start, "sorted");
            Assert.False(
                overlap > 0 && (overlap > previous.FadeOut || overlap > current.FadeIn || threeAtOnce),
                "crossfade"
            );
        }

        Assert.All(playlist, static e => Assert.True(e.FadeIn + e.FadeOut <= e.Length && e.Length > 0));
    }

    CompiledModel Compile() => new ModelCompiler().Compile(state, EngineIds.Empty.With(state));

    void Apply(params EditCommand[] commands) => state = Edits.Apply(state, commands, ids).State;
}
