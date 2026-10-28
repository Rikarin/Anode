using Anode.Contracts;
using Anode.Model.Compile;
using Anode.Model.Editing;

namespace Anode.Model.Tests;

/// <summary>Take folders and comps, region glue and copy, and what recordings make (docs/04-phase-3.md).</summary>
public sealed class TakeEditTests {
    const long Beat = 960;
    const long BeatFrames = 24_000; // 120 BPM at 48 kHz
    readonly SequentialIds ids = new();

    ProjectState state = ProjectDefaults.NewProject("prj_1", "Song", 48_000)
        .Apply(new AudioFileUpserted(new("aud_1", "Take 1", "Audio Files/1.wav", 48_000, 1, 480_000, 24)))
        .Apply(new AudioFileUpserted(new("aud_2", "Take 2", "Audio Files/2.wav", 48_000, 1, 480_000, 24)))
        .Apply(new AudioFileUpserted(new("aud_3", "Take 3", "Audio Files/3.wav", 48_000, 1, 480_000, 24)));

    [Fact]
    public void Packing_regions_makes_a_folder_with_a_take_each_and_the_comp_on_the_last() {
        var folder = PackThree();
        Assert.StartsWith("tkf_", folder.Id, StringComparison.Ordinal);
        Assert.Empty(state.Regions);
        Assert.Equal(3, folder.Takes.Count);
        Assert.All(folder.Takes, static t => Assert.StartsWith("tak_", t.Id, StringComparison.Ordinal));
        Assert.Equal((0L, 4 * Beat), (folder.StartTick, folder.LengthTicks));
        // The last by start: the region a beat in.
        Assert.Equal([new CompSegment(Beat, 4 * Beat, folder.Takes[2].Id)], folder.Comp);
        Assert.Equal(ProjectDefaults.CompCrossfade(48_000), folder.Crossfade);
    }

    [Fact]
    public void A_comp_lies_in_its_folder_in_order_and_names_its_takes() {
        var folder = PackThree();
        var (a, b) = (folder.Takes[0].Id, folder.Takes[1].Id);
        Apply(new SetCompCommand(folder.Id, [new(0, Beat, a), new(Beat, 4 * Beat, b)]));
        Assert.Equal(2, state.TakeFolders[folder.Id].Comp.Count);
        Assert.Throws<EditException>(() => Apply(new SetCompCommand(folder.Id, [new(0, 5 * Beat, a)])));
        Assert.Throws<EditException>(() => Apply(new SetCompCommand(folder.Id, [new(Beat, 2 * Beat, a), new(0, Beat, b)])));
        Assert.Throws<EditException>(() => Apply(new SetCompCommand(folder.Id, [new(0, Beat, "tak_none")])));
        Apply(new SetCompCommand(folder.Id, []));
        Assert.Empty(state.TakeFolders[folder.Id].Comp);
        Apply(new SelectTakeCommand(folder.Id, b));
        Assert.Equal([new CompSegment(0, 4 * Beat, b)], state.TakeFolders[folder.Id].Comp);
    }

    [Fact]
    public void Deleting_a_take_drops_its_comp_segments_but_never_the_last_take() {
        var folder = PackThree();
        var (a, b, c) = (folder.Takes[0].Id, folder.Takes[1].Id, folder.Takes[2].Id);
        Apply(new SetCompCommand(folder.Id, [new(0, Beat, a), new(Beat, 4 * Beat, b)]));
        Apply(new DeleteTakeCommand(folder.Id, a));
        Assert.Equal([new CompSegment(Beat, 4 * Beat, b)], state.TakeFolders[folder.Id].Comp);
        Apply(new DeleteTakeCommand(folder.Id, b));
        Assert.Throws<EditException>(() => Apply(new DeleteTakeCommand(folder.Id, c)));
    }

    [Fact]
    public void A_take_folder_is_renamed_muted_and_its_takes_renamed() {
        var folder = PackThree();
        Apply(new SetTakeFolderCommand(folder.Id, "Vox", 480, true, folder.Takes[0].Id, "Best"));
        var changed = state.TakeFolders[folder.Id];
        Assert.Equal(("Vox", 480L, true, "Best"), (changed.Name, changed.Crossfade, changed.Muted, changed.Takes[0].Name));
        Assert.Throws<EditException>(() => Apply(new SetTakeFolderCommand(folder.Id, TakeName: "No take")));
        Assert.Throws<EditException>(() => Apply(new SetTakeFolderCommand(folder.Id, Crossfade: -1)));
    }

    [Fact]
    public void A_comp_crossfades_centred_on_its_boundaries() {
        var folder = PackThree();
        var (a, b) = (folder.Takes[0].Id, folder.Takes[1].Id);
        Apply(
            new SetTakeFolderCommand(folder.Id, Crossfade: 480),
            new SetCompCommand(folder.Id, [new(0, 2 * Beat, a), new(2 * Beat, 4 * Beat, b)])
        );
        var pieces = Comps.Pieces(state, state.TakeFolders[folder.Id]);
        Assert.Equal(2, pieces.Count);
        // The first plays on 240 frames past the boundary, the second starts 240 before it; both fade over 480.
        Assert.Equal((0L, 2 * BeatFrames + 240, 0L, 480L), (pieces[0].Start, pieces[0].Length, pieces[0].FadeIn, pieces[0].FadeOut));
        Assert.Equal((2 * BeatFrames - 240, 2 * BeatFrames - 240, 480L, 0L), (pieces[1].Start, pieces[1].FileOffset, pieces[1].FadeIn, pieces[1].FadeOut));
        Assert.Equal(2 * BeatFrames + 240, pieces[1].Length);
    }

    [Fact]
    public void Flattening_leaves_regions_that_play_the_comp() {
        var folder = PackThree();
        var (a, b) = (folder.Takes[0].Id, folder.Takes[1].Id);
        Apply(
            new SetTakeFolderCommand(folder.Id, Crossfade: 480),
            new SetCompCommand(folder.Id, [new(0, 2 * Beat, a), new(2 * Beat, 4 * Beat, b)])
        );
        var engine = EngineIds.Empty.With(state);
        var before = new ModelCompiler().Compile(state, engine).Tracks[0].Playlist;
        Apply(new FlattenTakeFolderCommand(folder.Id));
        Assert.Empty(state.TakeFolders);
        Assert.Equal(2, state.Regions.Count);
        var after = new ModelCompiler().Compile(state, engine).Tracks[0].Playlist;
        // The same pieces (a region starts on a tick: within one, 25 frames, of the comp's), still crossfading.
        Assert.Equal(before.Length, after.Length);
        Assert.All(
            before.Zip(after),
            static p => {
                var (comp, region) = p;
                var shift = region.Start - comp.Start;
                Assert.InRange(shift, 0, 25);
                Assert.Equal((comp.FileId, comp.FileOffset + shift, comp.Length - shift), (region.FileId, region.FileOffset, region.Length));
                Assert.Equal(comp.FadeOut, region.FadeOut);
                Assert.Equal(Math.Max(0, comp.FadeIn - shift), region.FadeIn);
            }
        );
        Assert.True(after[0].Start + after[0].Length - after[1].Start <= Math.Min(after[0].FadeOut, after[1].FadeIn));
    }

    [Fact]
    public void Take_folders_move_copy_and_delete_like_regions_but_do_not_split() {
        var folder = PackThree();
        Apply(new MoveRegionCommand(folder.Id, 4 * Beat));
        var moved = state.TakeFolders[folder.Id];
        Assert.Equal(4 * Beat, moved.StartTick);
        Assert.All(moved.Takes, static t => Assert.True(t.StartTick >= 4 * Beat));
        Assert.Equal(5 * Beat, moved.Comp[0].StartTick);
        Assert.Throws<EditException>(() => Apply(new SplitRegionCommand(folder.Id, 5 * Beat)));
        Apply(new CopyRegionsCommand([folder.Id], 8 * Beat));
        Assert.Equal(2, state.TakeFolders.Count);
        var copy = state.TakeFolders.Values.Single(f => f.Id != folder.Id);
        Assert.Equal(12 * Beat, copy.StartTick);
        Assert.Empty(copy.Takes.Select(static t => t.Id).Intersect(moved.Takes.Select(static t => t.Id)));
        Assert.Equal(copy.Takes[2].Id, copy.Comp[0].TakeId);
        Apply(new DeleteRegionsCommand([folder.Id, copy.Id]));
        Assert.Empty(state.TakeFolders);
    }

    [Fact]
    public void Copied_MIDI_regions_get_new_note_ids() {
        Apply(new AddTrackCommand(Kind: TrackKind.Midi));
        Apply(new AddMidiRegionCommand(state.Tracks[0].Id, 0, 4 * Beat));
        var region = state.MidiRegions.Keys.Single();
        Apply(new AddNotesCommand(region, [new(60, 100, 1, 0, 480), new(62, 100, 1, 480, 480)]));
        Apply(new CopyRegionsCommand([region], 4 * Beat));
        var copy = state.MidiRegions.Values.Single(r => r.Id != region);
        Assert.Equal(4 * Beat, copy.StartTick);
        Assert.Equal([3L, 4L], copy.Notes.Select(static n => n.Id));
        Assert.Throws<EditException>(() => Apply(new CopyRegionsCommand([region], -Beat)));
    }

    [Fact]
    public void Gluing_merges_MIDI_regions_and_contiguous_audio_regions() {
        Apply(new AddTrackCommand(Kind: TrackKind.Midi));
        var track = state.Tracks[0].Id;
        Apply(new AddMidiRegionCommand(track, 0, Beat), new AddMidiRegionCommand(track, 2 * Beat, Beat));
        var regions = state.MidiRegions.Values.OrderBy(static r => r.StartTick).ToArray();
        Apply(new AddNotesCommand(regions[1].Id, [new(64, 90, 1, 0, 240)]));
        Apply(new JoinRegionsCommand([regions[1].Id, regions[0].Id]));
        var joined = Assert.Single(state.MidiRegions.Values);
        Assert.Equal((regions[0].Id, 3 * Beat), (joined.Id, joined.LengthTicks));
        Assert.Equal(2 * Beat, Assert.Single(joined.Notes).StartTick);

        Apply(new AddTrackCommand());
        var audio = state.Tracks[1].Id;
        Apply(new AddRegionCommand(audio, "aud_1", 0, 0, 4 * BeatFrames));
        var whole = state.Regions.Keys.Single();
        Apply(new SplitRegionCommand(whole, Beat));
        Apply(new JoinRegionsCommand([..state.Regions.Keys]));
        var glued = Assert.Single(state.Regions.Values);
        Assert.Equal((0L, 4 * BeatFrames), (glued.FileOffset, glued.Length));

        Apply(new AddRegionCommand(audio, "aud_2", 8 * Beat));
        Assert.Throws<EditException>(() => Apply(new JoinRegionsCommand([..state.Regions.Keys])));
    }

    [Fact]
    public void Region_mute_and_fade_curves_are_region_settings() {
        Apply(new AddTrackCommand());
        Apply(new AddRegionCommand(state.Tracks[0].Id, "aud_1", 0));
        var region = state.Regions.Keys.Single();
        Apply(new SetRegionCommand(region, FadeIn: 100, FadeInCurve: -0.5, FadeOutCurve: 1, Muted: true));
        Assert.Equal((-0.5, 1.0, true), (state.Regions[region].FadeInCurve, state.Regions[region].FadeOutCurve, state.Regions[region].Muted));
        Assert.Throws<EditException>(() => Apply(new SetRegionCommand(region, FadeInCurve: 1.5)));
    }

    [Fact]
    public void A_cycle_recording_becomes_a_take_folder_with_the_comp_on_the_last_pass() {
        Apply(new AddTrackCommand("Vox"));
        var track = state.Tracks[0];
        TakePass[] passes = [new(0, 0, 4 * BeatFrames), new(0, 4 * BeatFrames, 4 * BeatFrames), new(0, 8 * BeatFrames, 2 * BeatFrames)];
        Record(track, passes);
        var folder = Assert.Single(state.TakeFolders.Values);
        Assert.Equal(["Take 1", "Take 2", "Take 3"], folder.Takes.Select(static t => t.Name));
        Assert.Equal([0L, 4 * BeatFrames, 8 * BeatFrames], folder.Takes.Select(static t => t.FileOffset));
        Assert.Equal((0L, 4 * Beat), (folder.StartTick, folder.LengthTicks));
        Assert.Equal([new CompSegment(0, 2 * Beat, folder.Takes[2].Id)], folder.Comp);
        Assert.Empty(state.Regions);
    }

    [Fact]
    public void In_regions_mode_each_pass_is_a_region_with_the_last_on_top() {
        Apply(new AddTrackCommand("Vox"), new SetRecordingCommand(CycleRecordMode.Regions));
        var track = state.Tracks[0];
        Record(track, [new(0, 0, 4 * BeatFrames), new(0, 4 * BeatFrames, 4 * BeatFrames)]);
        Assert.Empty(state.TakeFolders);
        var regions = state.RegionsOf(track.Id).ToArray();
        Assert.Equal(2, regions.Length);
        // At an equal start, the later in order wins: that is the last pass.
        Assert.Equal(4 * BeatFrames, regions[^1].FileOffset);
    }

    [Fact]
    public void Replace_mode_clears_what_the_recording_covers() {
        Apply(new AddTrackCommand("Vox"));
        var track = state.Tracks[0];
        Apply(new AddRegionCommand(track.Id, "aud_2", 0, 0, 8 * BeatFrames), new AddRegionCommand(track.Id, "aud_3", 2 * Beat, 0, BeatFrames));
        Apply(new SetRecordingCommand(Replace: true));
        Record(track, [new(BeatFrames, 0, 4 * BeatFrames)]);
        var regions = state.RegionsOf(track.Id).ToArray();
        // The long region is cut around the take (left: beat 0–1, right: beat 5–8); the short one inside it is gone.
        Assert.Equal(
            [("aud_2", 0L, 0L, BeatFrames), ("aud_1", Beat, 0L, 4 * BeatFrames), ("aud_2", 5 * Beat, 5 * BeatFrames, 3 * BeatFrames)],
            regions.Select(static r => (r.FileId, r.StartTick, r.FileOffset, r.Length))
        );
    }

    [Fact]
    public void Autopunch_keeps_only_the_punch_range() {
        Apply(new SetRecordingCommand(Autopunch: true, PunchInTick: Beat, PunchOutTick: 3 * Beat));
        var punched = Recordings.Punched(state, [new(0, 0, 4 * BeatFrames), new(0, 4 * BeatFrames, 4 * BeatFrames)]);
        Assert.Equal([new TakePass(BeatFrames, BeatFrames, 2 * BeatFrames), new TakePass(BeatFrames, 5 * BeatFrames, 2 * BeatFrames)], punched);
    }

    [Fact]
    public void A_recording_stays_where_it_was_played_to_the_sample() {
        Apply(new AddTrackCommand("Vox"));
        Record(state.Tracks[0], [new(1000, 0, BeatFrames)]);
        var region = Assert.Single(state.Regions.Values);
        Assert.Equal(1000, state.TempoMap.SampleAt(region.StartTick, 48_000) - region.FileOffset);
    }

    void Record(Track track, IReadOnlyList<TakePass> passes) =>
        state = state.Apply(Recordings.Audio(state, track, state.Files["aud_1"], passes, false, ids));

    /// <summary>Three regions on one track, a beat apart and four beats long at most; packed.</summary>
    TakeFolder PackThree() {
        Apply(new AddTrackCommand("Vox"));
        var track = state.Tracks[0].Id;
        Apply(
            new AddRegionCommand(track, "aud_1", 0, 0, 4 * BeatFrames),
            new AddRegionCommand(track, "aud_2", Beat, 0, 3 * BeatFrames),
            new AddRegionCommand(track, "aud_3", 0, 0, 4 * BeatFrames)
        );
        var regions = state.Regions.Keys.ToArray();
        Apply(new PackTakeFolderCommand(regions));
        return state.TakeFolders.Values.Single();
    }

    void Apply(params EditCommand[] commands) => state = Edits.Apply(state, commands, ids).State;
}
