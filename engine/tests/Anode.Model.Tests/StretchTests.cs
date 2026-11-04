using Anode.Contracts;
using Anode.Model.Compile;
using Anode.Model.Editing;
using Anode.Model.Time;

namespace Anode.Model.Tests;

/// <summary>Time-stretching a region: it lasts longer or shorter, its file untouched, through the flex path.</summary>
public sealed class StretchTests {
    const long Bar = 3840;
    readonly SequentialIds ids = new();

    ProjectState state = ProjectDefaults.NewProject("prj_1", "Song", 48_000)
        .Apply(new AudioFileUpserted(new("aud_1", "A", "Audio Files/A.wav", 48_000, 1, 480_000, 24, null, 0)));

    void Apply(params EditCommand[] commands) => state = Edits.Apply(state, commands, ids).State;

    string Region(long start = 0, long length = 96_000) {
        if (state.Tracks.Count == 0) {
            Apply(new AddTrackCommand());
        }

        Apply(new AddRegionCommand(state.Tracks[0].Id, "aud_1", start, 0, length));
        return state.Regions.Values.Single(r => r.StartTick == start).Id;
    }

    CompiledModel Model() => new ModelCompiler().Compile(state, EngineIds.Empty.With(state));

    [Fact]
    public void A_stretched_region_lasts_that_much_longer_and_plays_through_a_warp() {
        var region = Region();
        Assert.Equal(Bar, RegionTiming.Of(state, state.Regions[region]).EndTick, 6);
        Apply(new StretchRegionCommand(region, 2));
        var timing = RegionTiming.Of(state, state.Regions[region]);
        Assert.True(timing.Warped);
        Assert.Equal(2 * Bar, timing.EndTick, 6);
        Assert.Equal(48_000, timing.FrameAt(Bar), 6);

        var entry = Assert.Single(Model().Tracks[0].Playlist);
        Assert.Equal(192_000, entry.Length);
        Assert.True(entry.WarpCount >= 2);
        var last = Model().Tracks[0].Warp![^1];
        Assert.Equal(192_000, last.Offset);
        Assert.Equal(96_000, last.FileFrame);
    }

    [Fact]
    public void The_file_and_the_region_keep_their_frames_and_undo_restores_it() {
        var region = Region();
        var before = state.Regions[region];
        var result = Edits.Apply(state, new StretchRegionCommand(region, 0.5, 960), ids);
        var stretched = result.State.Regions[region];
        Assert.Equal((before.FileOffset, before.Length), (stretched.FileOffset, stretched.Length));
        Assert.Equal((960L, 0.5), (stretched.StartTick, stretched.Stretch));
        Assert.Equal(before, result.State.Apply(result.Inverse).Regions[region]);
    }

    [Fact]
    public void Back_at_one_a_region_is_unwarped_again() {
        var region = Region();
        Apply(new StretchRegionCommand(region, 3), new StretchRegionCommand(region, 1));
        Assert.False(RegionTiming.Of(state, state.Regions[region]).Warped);
        Assert.Equal(0, Assert.Single(Model().Tracks[0].Playlist).WarpCount);
    }

    [Theory]
    [InlineData(0.24)]
    [InlineData(4.01)]
    [InlineData(0)]
    [InlineData(double.NaN)]
    public void The_stretch_stays_in_its_range(double stretch) {
        var region = Region();
        Assert.Throws<EditException>(() => Apply(new StretchRegionCommand(region, stretch)));
    }

    [Fact]
    public void Flex_markers_keep_their_place_in_the_audio() {
        var region = Region();
        Apply(new SetTrackFlexCommand(state.Tracks[0].Id, true));
        Apply(new SetFlexMarkersCommand(region, [new(48_000, 1500)]));
        Apply(new StretchRegionCommand(region, 2));
        Assert.Equal(3000, state.Regions[region].FlexMarkers[0].Tick);
        Assert.Equal(48_000, RegionTiming.Of(state, state.Regions[region]).FrameAt(3000), 6);
    }

    [Fact]
    public void Fades_are_cut_to_what_still_fits() {
        var region = Region();
        Apply(new SetRegionCommand(region, FadeIn: 60_000, FadeOut: 36_000));
        Apply(new StretchRegionCommand(region, 0.5));
        var stretched = state.Regions[region];
        Assert.Equal((48_000L, 0L), (stretched.FadeIn, stretched.FadeOut));
    }

    [Fact]
    public void Splitting_keeps_the_stretch_on_both_sides_and_the_audio_where_it_was() {
        var region = Region();
        Apply(new StretchRegionCommand(region, 2));
        Apply(new SplitRegionCommand(region, Bar));
        var parts = state.Regions.Values.OrderBy(static r => r.StartTick).ToArray();
        Assert.Equal(
            [(0L, 0L, 48_000L, 2.0), (Bar, 48_000, 48_000, 2.0)],
            parts.Select(static r => (r.StartTick, r.FileOffset, r.Length, r.Stretch))
        );
    }

    [Fact]
    public void Regions_of_different_stretches_do_not_join() {
        var region = Region();
        Apply(new SplitRegionCommand(region, Bar / 2));
        var other = state.Regions.Keys.Single(k => k != region);
        Apply(new StretchRegionCommand(other, 1.5));
        Assert.Throws<EditException>(() => Apply(new JoinRegionsCommand([region, other])));
    }

    [Fact]
    public void A_loop_repeats_at_the_stretched_length() {
        var region = Region();
        Apply(new StretchRegionCommand(region, 2), new SetRegionCommand(region, LoopTicks: 2 * Bar));
        Assert.Equal([0L, 192_000], Model().Tracks[0].Playlist.Select(static e => e.Start));
    }

    [Fact]
    public void Only_audio_stretches() {
        Apply(new AddTrackCommand(Kind: TrackKind.Instrument));
        Apply(new AddMidiRegionCommand(state.Tracks[0].Id, 0, Bar));
        var midi = state.MidiRegions.Keys.Single();
        Assert.Contains("MIDI", Assert.Throws<EditException>(() => Apply(new StretchRegionCommand(midi, 2))).Message);
    }
}
