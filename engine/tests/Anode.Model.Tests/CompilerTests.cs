using Anode.Contracts;
using Anode.Ipc.Segments;
using Anode.Model.Compile;
using Anode.Model.Editing;

namespace Anode.Model.Tests;

/// <summary>The playback-model compiler: ticks to samples, clicks, overlaps, and recompiling only what changed.</summary>
public sealed class CompilerTests {
    readonly SequentialIds ids = new();

    ProjectState state = ProjectDefaults.NewProject("prj_1", "Song", 48_000)
        .Apply(new AudioFileUpserted(new("aud_1", "Kick", "Audio Files/Kick.wav", 48_000, 1, 96_000, 24, null, 0)))
        .Apply(new AudioFileUpserted(new("aud_2", "Snare", "Audio Files/Snare.wav", 48_000, 2, 48_000, 24, null, 0)));

    [Fact]
    public void Regions_are_placed_in_samples_through_the_tempo() {
        AddTracks(1);
        Apply(new AddRegionCommand(Track(0), "aud_1", 960 * 3, 100, 50_000));
        Apply(new SetRegionCommand(Region(0), GainDb: -6.0206, FadeIn: 10, FadeOut: 20));
        var engine = EngineIds.Empty.With(state);
        var model = new ModelCompiler().Compile(state, engine);
        var entry = Assert.Single(model.Tracks[0].Playlist);
        // 120 BPM: a beat is half a second, 24 000 samples at 48 kHz.
        Assert.Equal(72_000, entry.Start);
        Assert.Equal(
            (100L, 50_000L, engine.File("aud_1"), 10, 20),
            (entry.FileOffset, entry.Length, entry.FileId,
                entry.FadeIn, entry.FadeOut)
        );
        Assert.InRange(entry.Gain, 0.4999f, 0.5001f);

        Apply(new SetTempoCommand(90));
        model = new ModelCompiler().Compile(state, engine);
        Assert.Equal(96_000, model.Tracks[0].Playlist[0].Start); // 3 beats of 2/3 s
    }

    [Fact]
    public void Clicks_fall_on_every_beat_to_the_project_end_with_downbeats_marked() {
        Apply(new SetSignatureCommand(3, 4));
        var model = new ModelCompiler().Compile(state, EngineIds.Empty.With(state));
        var clicks = model.Tracks[^1];
        Assert.Equal(TrackEntry.MetronomeTrack, clicks.TrackId);
        Assert.Empty(clicks.Playlist);
        var endTick = state.Settings.EndTick;
        Assert.Equal((int)(endTick / 960), clicks.Events.Length);
        Assert.All(clicks.Events, static e => Assert.Equal(PlaybackEventKind.Click, e.Kind));
        Assert.Equal([1, 0, 0, 1, 0, 0], clicks.Events.Take(6).Select(static e => (int)e.Data1));
        Assert.Equal([0L, 24_000, 48_000], clicks.Events.Take(3).Select(static e => e.SamplePos));
        Assert.Equal(state.TempoMap.SampleAt(endTick, 48_000), model.LengthSamples);

        // Eighth-note beats in 6/8, and clicks over a cycle past the end.
        Apply(new SetSignatureCommand(6, 8));
        Apply(new SetCycleCommand(true, endTick, endTick + 960 * 8));
        clicks = new ModelCompiler().Compile(state, EngineIds.Empty.With(state)).Tracks[^1];
        Assert.Equal((endTick + 960 * 8) / 480, clicks.Events.Length);
        Assert.Equal(12_000, clicks.Events[1].SamplePos);
    }

    [Fact]
    public void Only_the_tracks_an_edit_touches_are_recompiled() {
        AddTracks(3);
        Apply(new AddRegionCommand(Track(0), "aud_1", 0), new AddRegionCommand(Track(1), "aud_2", 0));
        var compiler = new ModelCompiler();
        var first = compiler.Compile(state, EngineIds.Empty.With(state));
        Assert.Equal(4, first.Recompiled.Count);

        // A strip or a name changes no playback data.
        Apply(new SetStripCommand(Track(1), -10), new SetTrackCommand(Track(0), "Kick"));
        var second = compiler.Compile(state, EngineIds.Empty.With(state));
        Assert.Empty(second.Recompiled);
        Assert.All(first.Tracks.Zip(second.Tracks), static p => Assert.Same(p.First, p.Second));

        var engine = EngineIds.Empty.With(state);
        Apply(new MoveRegionCommand(state.RegionsOf(Track(1)).Single().Id, 960));
        var third = compiler.Compile(state, engine);
        Assert.Equal([engine.Track(Track(1))], third.Recompiled);
        Assert.Same(second.Tracks[0], third.Tracks[0]);
        Assert.NotSame(second.Tracks[1], third.Tracks[1]);

        // The tempo moves every region and every click.
        Apply(new SetTempoCommand(100));
        Assert.Equal(4, compiler.Compile(state, engine).Recompiled.Count);

        // The signature changes only the clicks.
        Apply(new SetSignatureCommand(7, 8));
        Assert.Equal([TrackEntry.MetronomeTrack], compiler.Compile(state, engine).Recompiled);
    }

    [Fact]
    public void Engine_ids_are_stable_and_never_reused() {
        AddTracks(2);
        var first = EngineIds.Empty.With(state);
        var a = first.Track(Track(0));
        var b = first.Track(Track(1));
        Apply(new RemoveTrackCommand(Track(0)), new AddTrackCommand(Index: 0));
        var second = first.With(state);
        Assert.Equal(b, second.Track(Track(1)));
        Assert.NotEqual(a, second.Track(Track(0)));
        Assert.NotEqual(b, second.Track(Track(0)));
        Assert.Same(second, second.With(state));
        Assert.Equal(Track(1), second.TrackOf(b));
    }

    [Fact]
    public void Overlapping_regions_play_the_later_one_and_resume_the_earlier() {
        AddTracks(1);
        // A: 0…96 000 samples; B: 24 000…48 000 (one beat in, one beat long).
        Apply(
            new AddRegionCommand(Track(0), "aud_1", 0, 0, 96_000),
            new AddRegionCommand(Track(0), "aud_2", 960, 0, 24_000)
        );
        var engine = EngineIds.Empty.With(state);
        var playlist = new ModelCompiler().Compile(state, engine).Tracks[0].Playlist;
        Assert.Equal(
            [
                (0L, 0L, 24_000L, engine.File("aud_1")), (24_000, 0, 24_000, engine.File("aud_2")),
                (48_000, 48_000, 48_000, engine.File("aud_1"))
            ],
            playlist.Select(static e => (e.Start, e.FileOffset, e.Length, e.FileId))
        );
    }

    void AddTracks(int count) {
        for (var i = 0; i < count; i++) {
            Apply(new AddTrackCommand());
        }
    }

    string Track(int index) => state.Tracks[index].Id;

    string Region(int index) => state.Regions.Values.ElementAt(index).Id;

    void Apply(params EditCommand[] commands) => state = Edits.Apply(state, commands, ids).State;
}
