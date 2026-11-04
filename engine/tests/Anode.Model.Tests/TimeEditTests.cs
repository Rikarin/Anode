using Anode.Contracts;
using Anode.Model.Editing;

namespace Anode.Model.Tests;

/// <summary>Insert Time, Cut Time and ripple delete, across every track and the global tracks.</summary>
public sealed class TimeEditTests {
    const long Bar = 3840;
    readonly SequentialIds ids = new();

    ProjectState state = ProjectDefaults.NewProject("prj_1", "Song", 48_000)
        .Apply(new AudioFileUpserted(new("aud_1", "A", "Audio Files/A.wav", 48_000, 1, 960_000, 24, null, 0)));

    void Apply(params EditCommand[] commands) => state = Edits.Apply(state, commands, ids).State;

    string Audio() {
        Apply(new AddTrackCommand());
        return state.Tracks[^1].Id;
    }

    string Instrument() {
        Apply(new AddTrackCommand(Kind: TrackKind.Instrument));
        return state.Tracks[^1].Id;
    }

    /// <summary>A two-bar audio region (96 000 frames a bar at 120 BPM).</summary>
    string Region(string track, long start, long length = 192_000) {
        Apply(new AddRegionCommand(track, "aud_1", start, 0, length));
        return state.Regions.Values.Single(r => r.TrackId == track && r.StartTick == start).Id;
    }

    (long Start, long Offset, long Length)[] Spans() =>
        [..state.Regions.Values.OrderBy(static r => r.StartTick).Select(static r => (r.StartTick, r.FileOffset, r.Length))];

    [Fact]
    public void Inserting_splits_what_crosses_and_moves_what_follows_on_every_track() {
        var a = Audio();
        var b = Audio();
        var i = Instrument();
        Region(a, 0);
        Region(b, 4 * Bar);
        Apply(new AddMidiRegionCommand(i, 0, 2 * Bar));
        var midi = state.MidiRegions.Keys.Single();
        Apply(new AddNotesCommand(midi, [new(60, 100, 1, 0, 480), new(62, 100, 1, 2 * 960 + Bar, 480)]));

        Apply(new InsertTimeCommand(Bar, Bar));

        Assert.Equal([(0L, 0L, 96_000L), (2 * Bar, 96_000, 96_000), (5 * Bar, 0, 192_000)], Spans());
        var regions = state.MidiRegions.Values.OrderBy(static r => r.StartTick).ToArray();
        Assert.Equal([(0L, Bar), (2 * Bar, Bar)], regions.Select(static r => (r.StartTick, r.LengthTicks)));
        Assert.Equal(2 * 960, regions[1].Notes.Single().StartTick);
    }

    [Fact]
    public void Cutting_removes_what_is_inside_keeps_the_rest_and_closes_the_gap() {
        var a = Audio();
        Region(a, 0, 4 * 96_000);
        Region(a, 6 * Bar);
        Apply(new CutTimeCommand(Bar, 3 * Bar));
        Assert.Equal([(0L, 0L, 96_000L), (Bar, 3 * 96_000, 96_000), (4 * Bar, 0, 192_000)], Spans());
    }

    [Fact]
    public void Markers_sections_cycle_punch_and_the_end_follow() {
        Apply(
            new AddMarkerCommand("After", Bar * 2),
            new AddMarkerCommand("Before", 0),
            new AddMarkerCommand("Inside", Bar + 100, Bar),
            new AddSectionCommand("A", 0, 2 * Bar),
            new AddSectionCommand("B", 2 * Bar, 2 * Bar),
            new SetCycleCommand(true, 2 * Bar, 4 * Bar),
            new SetRecordingCommand(PunchInTick: 2 * Bar, PunchOutTick: 3 * Bar)
        );
        var end = state.Settings.EndTick;

        Apply(new InsertTimeCommand(Bar, Bar));
        Assert.Equal([0L, Bar * 2 + 100, 3 * Bar], state.Markers.Values.Select(static m => m.Tick).Order());
        var sections = state.Sections.Values.OrderBy(static s => s.StartTick).ToArray();
        Assert.Equal([(0L, 3 * Bar), (3 * Bar, 2 * Bar)], sections.Select(static s => (s.StartTick, s.LengthTicks)));
        Assert.Equal((3 * Bar, 5 * Bar), (state.Settings.Cycle.StartTick, state.Settings.Cycle.EndTick));
        Assert.Equal(
            (3 * Bar, 4 * Bar),
            (state.Settings.Recording.PunchInTick, state.Settings.Recording.PunchOutTick)
        );
        Assert.Equal(end + Bar, state.Settings.EndTick);

        Apply(new CutTimeCommand(Bar, 2 * Bar));
        Assert.Equal([0L, Bar + 100, 2 * Bar], state.Markers.Values.Select(static m => m.Tick).Order());
        Assert.Equal(end, state.Settings.EndTick);

        Apply(new CutTimeCommand(Bar, 3 * Bar));
        Assert.Equal(0, state.Markers.Values.Single().Tick);
        sections = [..state.Sections.Values.OrderBy(static s => s.StartTick)];
        Assert.Equal([(0L, Bar), (Bar, Bar)], sections.Select(static s => (s.StartTick, s.LengthTicks)));
    }

    [Fact]
    public void The_tempo_key_and_chord_tracks_follow() {
        Apply(
            new SetTempoEventsCommand([new(0, 120, TempoCurve.Step), new(2 * Bar, 90, TempoCurve.Step)]),
            new SetKeysCommand([new(0, 0, KeyMode.Major), new(2 * Bar, 7, KeyMode.Minor)]),
            new AddChordsCommand([new(0, new(PitchStep.C, 0), "", null), new(2 * Bar, new(PitchStep.G, 0), "", null)])
        );
        Apply(new InsertTimeCommand(Bar, Bar));
        Assert.Equal([0L, 3 * Bar], state.Settings.TempoEvents!.Select(static e => e.Tick));
        Assert.Equal([0L, 3 * Bar], state.Settings.Keys!.Select(static e => e.Tick));
        Assert.Equal([0L, 3 * Bar], state.ChordTrack.Chords.Select(static c => c.Tick));

        // Cutting over a change: what was in force at the cut's end holds from its start.
        Apply(new CutTimeCommand(2 * Bar, 4 * Bar));
        Assert.Equal([(0L, 120.0), (2 * Bar, 90)], state.Settings.TempoEvents!.Select(static e => (e.Tick, e.Bpm)));
        Assert.Equal([0L, 2 * Bar], state.Settings.Keys!.Select(static e => e.Tick));
        Assert.Equal([0L, 2 * Bar], state.ChordTrack.Chords.Select(static c => c.Tick));
    }

    [Fact]
    public void A_ramp_holds_its_tempo_through_an_insertion() {
        Apply(new SetTempoEventsCommand([new(0, 60, TempoCurve.Ramp), new(2 * Bar, 120, TempoCurve.Step)]));
        var bpm = state.TempoMap.BpmAt(Bar);
        Apply(new InsertTimeCommand(Bar, Bar));
        var events = state.Settings.TempoEvents!;
        Assert.Equal([0L, Bar, 2 * Bar, 3 * Bar], events.Select(static e => e.Tick));
        Assert.Equal(bpm, events[1].Bpm, 9);
        Assert.Equal(TempoCurve.Step, events[1].Curve);
        Assert.Equal(TempoCurve.Ramp, events[2].Curve);
        Assert.Equal(bpm, state.TempoMap.BpmAt(Bar + 960), 9);
    }

    [Fact]
    public void A_signature_change_stays_on_a_bar_line() {
        Apply(new SetSignaturesCommand([new(0, 4, 4), new(2 * Bar, 3, 4)]));
        Apply(new InsertTimeCommand(100, 960));
        Assert.Equal([0L, 2 * Bar], state.Settings.Signatures!.Select(static e => e.Tick));
        Apply(new InsertTimeCommand(0, Bar));
        Assert.Equal([0L, 3 * Bar], state.Settings.Signatures!.Select(static e => e.Tick));
    }

    [Fact]
    public void Automation_holds_through_an_insertion_and_jumps_over_a_cut() {
        var a = Audio();
        Apply(new SetAutomationCommand("volume", [new(0, -24, 0), new(2 * Bar, 0, 0)], a));
        var lane = state.LaneOf(a, "volume")!;
        Apply(new InsertTimeCommand(Bar, Bar));
        lane = state.Automation[lane.Id];
        Assert.Equal(
            [(0L, -24.0), (Bar, -12), (2 * Bar, -12), (3 * Bar, 0)],
            lane.Points.Select(static p => (p.Tick, p.Value))
        );

        Apply(new CutTimeCommand(Bar / 2, 2 * Bar + Bar / 2));
        lane = state.Automation[lane.Id];
        Assert.Equal(
            [(0L, -24.0), (Bar / 2, -18), (Bar / 2, -6), (Bar, 0)],
            lane.Points.Select(static p => (p.Tick, p.Value))
        );
    }

    [Fact]
    public void Looped_regions_become_regions_where_time_crosses_them() {
        var a = Audio();
        var region = Region(a, 0, 96_000);
        Apply(new SetRegionCommand(region, LoopTicks: 2 * Bar));
        Apply(new InsertTimeCommand(Bar + Bar / 2, Bar));
        Assert.All(state.Regions.Values, static r => Assert.Equal(0, r.LoopTicks));
        Assert.Equal(
            [(0L, 0L, 96_000L), (Bar, 0, 48_000), (2 * Bar + Bar / 2, 48_000, 48_000), (3 * Bar, 0, 96_000)],
            Spans()
        );
    }

    [Fact]
    public void A_looped_region_after_the_point_moves_with_its_loop() {
        var a = Audio();
        var region = Region(a, Bar, 96_000);
        Apply(new SetRegionCommand(region, LoopTicks: Bar));
        Apply(new InsertTimeCommand(0, Bar));
        Assert.Equal((2 * Bar, Bar), (state.Regions[region].StartTick, state.Regions[region].LoopTicks));
    }

    [Fact]
    public void Pattern_regions_grow_and_shrink_rather_than_split() {
        var i = Instrument();
        Apply(new AddPatternRegionCommand(i, 0, 2 * Bar));
        var pattern = state.PatternRegions.Keys.Single();
        Apply(new InsertTimeCommand(Bar, Bar));
        Assert.Equal(3 * Bar, state.PatternRegions[pattern].LengthTicks);
        Apply(new CutTimeCommand(Bar, 2 * Bar));
        Assert.Equal(2 * Bar, state.PatternRegions[pattern].LengthTicks);
        Apply(new CutTimeCommand(0, 2 * Bar));
        Assert.Empty(state.PatternRegions);
    }

    [Fact]
    public void Take_folders_split_with_their_takes_and_comp() {
        var a = Audio();
        Region(a, 0, 192_000);
        Apply(new PackTakeFolderCommand([state.Regions.Keys.Single()]));
        Apply(new InsertTimeCommand(Bar, Bar));
        var folders = state.TakeFolders.Values.OrderBy(static f => f.StartTick).ToArray();
        Assert.Equal([(0L, Bar), (2 * Bar, Bar)], folders.Select(static f => (f.StartTick, f.LengthTicks)));
        Assert.Equal(96_000, folders[0].Takes[0].Length);
        Assert.Equal((96_000L, 96_000L), (folders[1].Takes[0].FileOffset, folders[1].Takes[0].Length));
        Assert.NotEqual(folders[0].Takes[0].Id, folders[1].Takes[0].Id);
        Assert.Equal(folders[1].Takes[0].Id, Assert.Single(folders[1].Comp).TakeId);
    }

    [Fact]
    public void Ripple_delete_closes_the_gap_leaving_what_crosses_it_on_other_tracks() {
        var a = Audio();
        var b = Audio();
        var gone = Region(a, Bar, 96_000);
        Region(a, 3 * Bar);
        var other = Region(b, 0, 3 * 96_000);
        var later = Region(b, 4 * Bar);
        Apply(new AddMarkerCommand("Later", 5 * Bar));
        var cycle = state.Settings.Cycle;

        Apply(new RippleDeleteCommand([gone]));

        Assert.DoesNotContain(gone, state.Regions.Keys);
        Assert.Equal((0L, 3 * 96_000L), (state.Regions[other].StartTick, state.Regions[other].Length));
        Assert.Equal(3 * Bar, state.Regions[later].StartTick);
        Assert.Contains(state.Regions.Values, static r => r.StartTick == 2 * Bar);
        Assert.Equal(4 * Bar, state.Markers.Values.Single().Tick);
        Assert.Equal(cycle.EndTick - Bar, state.Settings.Cycle.EndTick);
    }

    [Fact]
    public void Ripple_delete_closes_each_span_once_and_merges_touching_ones() {
        var a = Audio();
        var one = Region(a, 0, 96_000);
        var two = Region(a, Bar, 96_000);
        var three = Region(a, 4 * Bar, 96_000);
        var last = Region(a, 6 * Bar, 96_000);
        Apply(new RippleDeleteCommand([three, one, two]));
        Assert.Equal(3 * Bar, state.Regions[last].StartTick);
    }

    [Fact]
    public void Refuses_what_makes_no_sense_and_undoes_in_one_step() {
        Assert.Throws<EditException>(() => Apply(new InsertTimeCommand(0, 0)));
        Assert.Throws<EditException>(() => Apply(new CutTimeCommand(Bar, Bar)));
        Assert.Throws<EditException>(() => Apply(new RippleDeleteCommand(["rgn_x"])));
        var a = Audio();
        Region(a, 0);
        var before = RandomProjects.Json(state);
        var result = Edits.Apply(state, new CutTimeCommand(Bar / 2, Bar), ids);
        Assert.Equal(before, RandomProjects.Json(result.State.Apply(result.Inverse)));
    }
}
