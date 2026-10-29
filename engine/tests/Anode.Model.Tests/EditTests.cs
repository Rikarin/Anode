using Anode.Contracts;
using Anode.Model.Editing;
using Anode.Model.History;

namespace Anode.Model.Tests;

/// <summary>What each edit does, what it refuses, and the undo history.</summary>
public sealed class EditTests {
    readonly SequentialIds ids = new();

    ProjectState state = ProjectDefaults.NewProject("prj_1", "Song", 48_000)
        .Apply(new AudioFileUpserted(new("aud_1", "Kick", "Audio Files/Kick.wav", 48_000, 1, 96_000, 24, null, 0)));

    [Fact]
    public void Adding_tracks_names_and_colours_them_in_order() {
        Apply(new AddTrackCommand(), new AddTrackCommand(Stereo: true), new AddTrackCommand("Lead", 0));
        Assert.Equal(["Lead", "Audio 1", "Audio 2"], state.Tracks.Select(static t => t.Name));
        Assert.True(state.Tracks[2].Input.Stereo);
        Assert.Equal(ProjectDefaults.Colors[1], state.Tracks[2].Color);
    }

    [Fact]
    public void Removing_a_track_removes_its_regions_and_undo_puts_both_back() {
        Apply(new AddTrackCommand(), new AddTrackCommand());
        var track = state.Tracks[0].Id;
        Apply(new AddRegionCommand(track, "aud_1", 0));
        var before = RandomProjects.Json(state);
        var result = Edits.Apply(state, new RemoveTrackCommand(track), ids);
        Assert.Empty(result.State.Regions);
        Assert.Equal([typeof(RegionRemoved), typeof(TrackRemoved)], result.Forward.Select(static o => o.GetType()));
        Assert.Equal(before, RandomProjects.Json(result.State.Apply(result.Inverse)));
    }

    [Fact]
    public void A_region_plays_the_whole_file_unless_told_otherwise() {
        Apply(new AddTrackCommand());
        Apply(new AddRegionCommand(state.Tracks[0].Id, "aud_1", 1920));
        var region = Assert.Single(state.Regions.Values);
        Assert.Equal((1920L, 0L, 96_000L, "Kick"), (region.StartTick, region.FileOffset, region.Length, region.Name));
    }

    [Fact]
    public void Splitting_makes_two_regions_that_play_the_same_frames() {
        Apply(new AddTrackCommand());
        Apply(new AddRegionCommand(state.Tracks[0].Id, "aud_1", 960));
        var id = state.Regions.Keys.Single();
        Apply(new SetRegionCommand(id, FadeIn: 100, FadeOut: 200));
        // 120 BPM at 48 kHz: one beat (960 ticks) is 24 000 samples.
        Apply(new SplitRegionCommand(id, 1920));
        var parts = state.Regions.Values.OrderBy(static r => r.StartTick).ToArray();
        Assert.Equal(2, parts.Length);
        Assert.Equal(
            (960L, 0L, 24_000L, 100L, 0L),
            (parts[0].StartTick, parts[0].FileOffset, parts[0].Length,
                parts[0].FadeIn, parts[0].FadeOut)
        );
        Assert.Equal(
            (1920L, 24_000L, 72_000L, 0L, 200L),
            (parts[1].StartTick, parts[1].FileOffset, parts[1].Length,
                parts[1].FadeIn, parts[1].FadeOut)
        );
    }

    [Theory]
    [InlineData(960)] // the region's start
    [InlineData(500)] // before it
    [InlineData(960 + 4 * 960)] // its end: 96 000 samples are four beats
    [InlineData(100_000)] // after it
    public void Splitting_outside_the_region_is_refused(long at) {
        Apply(new AddTrackCommand());
        Apply(new AddRegionCommand(state.Tracks[0].Id, "aud_1", 960));
        Assert.Throws<EditException>(() => Apply(new SplitRegionCommand(state.Regions.Keys.Single(), at)));
    }

    public static TheoryData<EditCommand> Refused() => [
        new RemoveTrackCommand("trk_nope"),
        new MoveTrackCommand("trk_nope", 0),
        new SetStripCommand(VolumeDb: 12),
        new SetStripCommand(VolumeDb: double.NaN),
        new SetStripCommand(Pan: 1.5),
        new AddRegionCommand("trk_nope", "aud_1", 0),
        new SetTempoCommand(2),
        new SetSignatureCommand(4, 3),
        new SetSignatureCommand(0, 4),
        new SetCycleCommand(true, 1920, 960),
        new SetMetronomeCommand(LevelDb: 20),
        new RenameProjectCommand("  "),
        new DeleteRegionsCommand([]),
        new AddTrackCommand(Index: 5)
    ];

    [Theory]
    [MemberData(nameof(Refused))]
    public void Edits_that_do_not_apply_are_refused(EditCommand command) =>
        Assert.Throws<EditException>(() => Apply(command));

    [Fact]
    public void Regions_must_stay_inside_their_file() {
        Apply(new AddTrackCommand());
        var track = state.Tracks[0].Id;
        Assert.Throws<EditException>(() => Apply(new AddRegionCommand(track, "aud_1", 0, 90_000, 10_000)));
        Assert.Throws<EditException>(() => Apply(new AddRegionCommand(track, "aud_1", 0, 0, 0)));
        Assert.Throws<EditException>(() => Apply(new AddRegionCommand(track, "aud_1", -1)));
        Apply(new AddRegionCommand(track, "aud_1", 0, 1000, 1000));
        var id = state.Regions.Keys.Single();
        Assert.Throws<EditException>(() => Apply(new SetRegionCommand(id, FadeIn: 600, FadeOut: 600)));
        Assert.Throws<EditException>(() => Apply(new ResizeRegionCommand(id, 0, 95_000, 2_000)));
    }

    [Fact]
    public void A_batch_that_fails_part_way_changes_nothing() {
        var before = state;
        var e = Assert.Throws<EditException>(() =>
            Edits.Apply(state, [new AddTrackCommand(), new RemoveTrackCommand("trk_nope")], ids)
        );
        Assert.StartsWith("Command 2", e.Message, StringComparison.Ordinal);
        Assert.Same(before, state);
    }

    [Fact]
    public void Strip_drags_on_one_control_within_a_second_are_one_undo_step() {
        var time = new ManualTime();
        var history = new EditHistory(time);
        Apply(new AddTrackCommand());
        var track = state.Tracks[0].Id;
        var original = state;
        for (var i = 0; i < 5; i++) {
            Record(history, new CommandBatch([new SetStripCommand(track, -i)]));
            time.Advance(TimeSpan.FromMilliseconds(400));
        }

        Assert.Single(history.History().Undo);
        state = state.Apply(history.PeekUndo()!.Inverse);
        Assert.Equal(RandomProjects.Json(original), RandomProjects.Json(state));

        // Another control, the same control after a pause, and any other edit start steps of their own.
        Record(history, new CommandBatch([new SetStripCommand(track, Pan: 0.5)]));
        Record(history, new CommandBatch([new SetStripCommand(track, -3)]));
        time.Advance(TimeSpan.FromSeconds(2));
        Record(history, new CommandBatch([new SetStripCommand(track, -4)]));
        Record(history, new CommandBatch([new SetTrackCommand(track, "Bass")]));
        Assert.Equal(
            [
                "Change Track", "Change Channel Strip", "Change Channel Strip", "Change Channel Strip",
                "Change Channel Strip"
            ],
            history.History().Undo.Select(static h => h.Label)
        );
    }

    [Fact]
    public void Undo_and_redo_move_steps_between_the_stacks() {
        var history = new EditHistory(TimeProvider.System);
        Record(history, new CommandBatch([new AddTrackCommand()], "First"));
        Record(history, new CommandBatch([new AddTrackCommand()], "Second"));
        history.Undone(3);
        Assert.Equal(["First"], history.History().Undo.Select(static h => h.Label));
        Assert.Equal([new HistoryEntry("Second", 3)], history.History().Redo);
        history.Redone(4);
        Assert.Equal(["Second", "First"], history.History().Undo.Select(static h => h.Label));
        Record(history, new CommandBatch([new AddTrackCommand()]));
        history.Undone(6);
        Record(history, new CommandBatch([new AddTrackCommand()]));
        Assert.Empty(history.History().Redo);
    }

    void Record(EditHistory history, CommandBatch batch) {
        var result = Edits.Apply(state, batch.Commands, ids);
        state = result.State;
        history.Push(UndoLabels.For(batch), 0, new(result.Forward, result.Inverse), UndoLabels.CoalesceKey(batch));
    }

    void Apply(params EditCommand[] commands) {
        foreach (var command in commands) {
            state = Edits.Apply(state, command, ids).State;
        }
    }
}

/// <summary>A clock the test moves.</summary>
sealed class ManualTime : TimeProvider {
    DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => now;

    public void Advance(TimeSpan by) => now += by;
}
