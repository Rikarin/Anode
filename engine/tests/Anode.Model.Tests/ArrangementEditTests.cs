using Anode.Contracts;
using Anode.Model.Compile;
using Anode.Model.Editing;

namespace Anode.Model.Tests;

/// <summary>Global tracks, markers, arrangement sections, stacks and notes (docs/04-phase-3.md).</summary>
public sealed class ArrangementEditTests {
    const long Bar = 3840;
    readonly SequentialIds ids = new();

    ProjectState state = ProjectDefaults.NewProject("prj_1", "Song", 48_000)
        .Apply(new AudioFileUpserted(new("aud_1", "Kick", "Audio Files/Kick.wav", 48_000, 1, 96_000, 24, null, 0)));

    [Fact]
    public void The_tempo_and_signature_settings_are_always_the_first_events() {
        Apply(new SetTempoEventsCommand([new(0, 90, TempoCurve.Ramp), new(Bar, 140, TempoCurve.Step)]));
        Assert.Equal(90, state.Settings.Tempo);
        Apply(new SetTempoCommand(100));
        Assert.Equal(
            [new TempoEvent(0, 100, TempoCurve.Ramp), new TempoEvent(Bar, 140, TempoCurve.Step)],
            state.Settings.TempoEvents
        );
        Assert.Equal(100, state.TempoMap.BpmAt(0));
        Assert.Equal(120, state.TempoMap.BpmAt(Bar / 2.0), 9);

        Apply(new SetSignaturesCommand([new(0, 3, 4), new(2880 * 2, 7, 8)]));
        Assert.Equal(new(3, 4), state.Settings.Signature);
        // Changing the first signature keeps the later change at its bar (bar 2): two bars of 4/4 now.
        Apply(new SetSignatureCommand(4, 4));
        Assert.Equal([new SignatureEvent(0, 4, 4), new SignatureEvent(2 * Bar, 7, 8)], state.Settings.Signatures);
    }

    [Theory]
    [InlineData(1, 120)] // not at tick 0
    [InlineData(0, 2)] // too slow
    [InlineData(0, 2000)] // too fast
    public void A_tempo_track_starts_at_tick_zero_with_tempos_in_range(long first, double bpm) =>
        Assert.Throws<EditException>(() => Apply(new SetTempoEventsCommand([new(first, bpm, TempoCurve.Step)])));

    [Fact]
    public void Tempo_events_are_in_order_and_signature_changes_on_bar_lines() {
        Assert.Throws<EditException>(() =>
            Apply(new SetTempoEventsCommand([new(0, 120, TempoCurve.Step), new(0, 100, TempoCurve.Step)]))
        );
        Assert.Throws<EditException>(() => Apply(new SetSignaturesCommand([new(0, 4, 4), new(Bar + 960, 3, 4)])));
        Assert.Throws<EditException>(() => Apply(new SetSignaturesCommand([new(0, 4, 3)])));
        Assert.Throws<EditException>(() => Apply(new SetKeysCommand([new(0, 12, KeyMode.Major)])));
        Apply(new SetKeysCommand([new(0, 9, KeyMode.Minor), new(8 * Bar, 0, KeyMode.Major)]));
        Assert.Equal(2, state.Settings.Keys.Count);
    }

    [Fact]
    public void Bars_and_beats_follow_the_signature_track() {
        Apply(new SetSignaturesCommand([new(0, 4, 4), new(2 * Bar, 6, 8)]));
        var bars = state.SignatureMap;
        Assert.Equal(new(2, 0, 0), bars.Position(2 * Bar));
        Assert.Equal(new(2, 3, 0), bars.Position(2 * Bar + 3 * 480));
        Assert.Equal(new(3, 0, 10), bars.Position(2 * Bar + 2880 + 10));
        Assert.Equal(2 * Bar + 2880, bars.BarStart(3));
        Assert.Equal(2 * Bar + 2880, bars.TickOf(new(3, 0, 0)));
        Assert.True(bars.IsBarLine(2 * Bar + 2880));
        Assert.Equal(2 * Bar, bars.BarLineAtOrBefore(2 * Bar + 100));
        Assert.Equal(2 * Bar + 2880, bars.BarLineAtOrAfter(2 * Bar + 100));
        var beats = bars.Beats(2 * Bar + 2880).ToArray();
        Assert.Equal(8 + 6, beats.Length);
        Assert.Equal([true, false, false, false, true], beats.Take(5).Select(static b => b.Downbeat));
        Assert.Equal((2 * Bar + 480, false), beats[9]);
    }

    [Fact]
    public void Markers_are_added_changed_and_deleted() {
        Apply(new AddMarkerCommand("Intro", 0, Bar, "#ff0000"));
        var marker = Assert.Single(state.Markers.Values);
        Assert.StartsWith("mrk_", marker.Id, StringComparison.Ordinal);
        Apply(new SetMarkerCommand(marker.Id, Tick: Bar, Text: "Count in"));
        Assert.Equal(
            (Bar, "Count in", "Intro"),
            (state.Markers[marker.Id].Tick, state.Markers[marker.Id].Text, state.Markers[marker.Id].Name)
        );
        Assert.Throws<EditException>(() => Apply(new SetMarkerCommand("mrk_none", "X")));
        Assert.Throws<EditException>(() => Apply(new AddMarkerCommand("Bad", -1)));
        Apply(new DeleteMarkersCommand([marker.Id]));
        Assert.Empty(state.Markers);
    }

    [Fact]
    public void Sections_never_overlap() {
        Apply(new AddSectionCommand("Verse", 0, 4 * Bar));
        Assert.Throws<EditException>(() => Apply(new AddSectionCommand("Chorus", 2 * Bar, 4 * Bar)));
        Apply(new AddSectionCommand("Chorus", 4 * Bar, 4 * Bar));
        var verse = Section("Verse");
        Assert.Throws<EditException>(() => Apply(new SetSectionCommand(verse.Id, LengthTicks: 5 * Bar)));
        Apply(new SetSectionCommand(verse.Id, "Verse 1", 2 * Bar, "#123456"));
        Assert.Equal(
            ("Verse 1", 2 * Bar, "#123456"),
            (state.Sections[verse.Id].Name, state.Sections[verse.Id].LengthTicks, state.Sections[verse.Id].Color)
        );
    }

    [Fact]
    public void Moving_a_section_takes_what_starts_in_it_and_closes_the_gap() {
        // Intro 0–1, Verse 1–3, Chorus 3–5 (bars); a region and a marker in each.
        SongWithSections();
        var chorus = Section("Chorus");
        Apply(new MoveSectionCommand(chorus.Id, Bar)); // to the start of the Verse
        Assert.Equal(
            (0L, Bar, 3 * Bar),
            (Section("Intro").StartTick, Section("Chorus").StartTick, Section("Verse").StartTick)
        );
        Assert.Equal(
            ["Intro", "Chorus", "Verse"],
            state.Regions.Values.OrderBy(static r => r.StartTick).Select(static r => r.Name)
        );
        Assert.Equal(
            [0L, Bar, 3 * Bar],
            state.Markers.Values.OrderBy(static m => m.Tick).Select(static m => m.Tick)
        );
        Assert.Equal("Chorus", state.Markers.Values.Single(static m => m.Tick == Bar).Name);

        // And back to the end of the last section.
        Apply(new MoveSectionCommand(chorus.Id, 5 * Bar));
        Assert.Equal(
            (0L, Bar, 3 * Bar),
            (Section("Intro").StartTick, Section("Verse").StartTick, Section("Chorus").StartTick)
        );

        Assert.Throws<EditException>(() => Apply(new MoveSectionCommand(chorus.Id, Bar / 2)));
    }

    [Fact]
    public void Copying_a_section_inserts_a_copy_and_moves_later_material() {
        SongWithSections();
        var verse = Section("Verse");
        Apply(new MoveSectionCommand(verse.Id, 3 * Bar, true));
        Assert.Equal(4, state.Sections.Count);
        Assert.Equal(
            [("Intro", 0L), ("Verse", Bar), ("Verse", 3 * Bar), ("Chorus", 5 * Bar)],
            state.Sections.Values.OrderBy(static s => s.StartTick).Select(static s => (s.Name, s.StartTick))
        );
        Assert.Equal(4, state.Regions.Count);
        Assert.Equal(2, state.Regions.Values.Count(static r => r.Name == "Verse"));
    }

    [Fact]
    public void Deleting_a_section_with_its_content_closes_the_gap() {
        SongWithSections();
        Apply(new DeleteSectionsCommand([Section("Verse").Id], true));
        Assert.Equal(
            [("Intro", 0L), ("Chorus", Bar)],
            state.Sections.Values.OrderBy(static s => s.StartTick).Select(static s => (s.Name, s.StartTick))
        );
        Assert.Equal(
            ["Intro", "Chorus"],
            state.Regions.Values.OrderBy(static r => r.StartTick).Select(static r => r.Name)
        );

        Apply(new DeleteSectionsCommand([Section("Chorus").Id]));
        Assert.Equal(2, state.Regions.Count); // without content: only the section goes
    }

    [Fact]
    public void A_stack_holds_its_members_after_it_and_cannot_nest() {
        Apply(new AddTrackCommand("A"), new AddTrackCommand("B"), new AddTrackCommand("C"), new AddTrackCommand("D"));
        Apply(new CreateStackCommand([Track("D"), Track("B")], "Drums"));
        Assert.Equal(["A", "Drums", "B", "D", "C"], state.Tracks.Select(static t => t.Name));
        var stack = Track("Drums");
        Assert.Equal(TrackKind.Stack, state.FindTrack(stack)!.Kind);
        Assert.All(state.MembersOf(stack), m => Assert.Equal(stack, m.ParentId));
        Assert.Throws<EditException>(() => Apply(new CreateStackCommand([stack])));
        Assert.Throws<EditException>(() => Apply(new SetTrackParentCommand(Track("A"), Track("C"))));
        Assert.Throws<EditException>(() => Apply(new SetTrackCommand(stack, RecordArmed: true)));

        Apply(new SetTrackParentCommand(Track("C"), stack));
        Assert.Equal(["A", "Drums", "B", "D", "C"], state.Tracks.Select(static t => t.Name));
        Apply(new SetTrackParentCommand(Track("B"), null));
        Assert.Equal(["A", "Drums", "D", "C", "B"], state.Tracks.Select(static t => t.Name));
        Assert.Null(state.FindTrack(Track("B"))!.ParentId);

        Apply(new RemoveStackCommand(stack));
        Assert.Equal(["A", "D", "C", "B"], state.Tracks.Select(static t => t.Name));
        Assert.All(state.Tracks, static t => Assert.Null(t.ParentId));
    }

    [Fact]
    public void A_new_track_asked_for_among_a_stacks_members_goes_after_them() {
        Apply(new AddTrackCommand("A"), new AddTrackCommand("B"), new AddTrackCommand("C"));
        Apply(new CreateStackCommand([Track("A"), Track("B")], "Stack"));
        // Right after the stack (a new track below a selected Drum Machine Designer), and between two members.
        Apply(new AddTrackCommand("After stack", 1));
        Apply(new AddTrackCommand("Between", 2));
        Assert.Equal(["Stack", "A", "B", "Between", "After stack", "C"], state.Tracks.Select(static t => t.Name));
        Assert.Null(state.FindTrack(Track("Between"))!.ParentId);
        // After the last member (outside the stack already) and at the end stay where they were asked for.
        Apply(new AddTrackCommand("Next", 3), new AddTrackCommand("End"));
        Assert.Equal(
            ["Stack", "A", "B", "Next", "Between", "After stack", "C", "End"],
            state.Tracks.Select(static t => t.Name)
        );
    }

    [Fact]
    public void A_stacks_strip_offsets_its_members_volume_and_its_mute_and_solo_apply_to_them() {
        Apply(new AddTrackCommand("A"), new AddTrackCommand("B"));
        Apply(new CreateStackCommand([Track("A")], "Stack"));
        Apply(
            new SetStripCommand(Track("A"), -6, 0.5),
            new SetStripCommand(Track("Stack"), -3, Mute: true, Solo: true)
        );
        var a = state.FindTrack(Track("A"))!;
        Assert.Equal(
            (-9d, 0.5, true, true),
            (Stacks.EffectiveStrip(state, a).VolumeDb, Stacks.EffectiveStrip(state, a).Pan,
                Stacks.EffectiveStrip(state, a).Mute, Stacks.EffectiveStrip(state, a).Solo)
        );
        Assert.Equal(state.FindTrack(Track("B"))!.Strip, Stacks.EffectiveStrip(state, state.FindTrack(Track("B"))!));
        Apply(new SetStripCommand(Track("Stack"), Units.SilenceDb));
        Assert.Equal(Units.SilenceDb, Stacks.EffectiveStrip(state, state.FindTrack(Track("A"))!).VolumeDb);
    }

    [Fact]
    public void Removing_a_stack_track_removes_its_members_too() {
        Apply(new AddTrackCommand("A"), new AddTrackCommand("B"));
        Apply(new CreateStackCommand([Track("A"), Track("B")], "Stack"));
        Apply(new AddRegionCommand(Track("A"), "aud_1", 0));
        var before = RandomProjects.Json(state);
        var result = Edits.Apply(state, new RemoveTrackCommand(Track("Stack")), ids);
        Assert.Empty(result.State.Tracks);
        Assert.Empty(result.State.Regions);
        Assert.Equal(before, RandomProjects.Json(result.State.Apply(result.Inverse)));
    }

    [Fact]
    public void Notes_and_recording_settings_are_project_settings() {
        Apply(new SetProjectNotesCommand("Bridge: try a key change"));
        Assert.Equal("Bridge: try a key change", state.Notes);
        Apply(new AddTrackCommand("A"));
        Apply(new SetTrackCommand(Track("A"), Collapsed: true, Notes: "Room mic"));
        Assert.Equal((true, "Room mic"), (state.Tracks[0].Collapsed, state.Tracks[0].Notes));
        Apply(new SetRecordingCommand(CycleRecordMode.Regions, true, true, Bar, 2 * Bar));
        Assert.Equal(
            new(CycleRecordMode.Regions, true, true, Bar, 2 * Bar),
            state.Settings.Recording
        );
        Assert.Throws<EditException>(() => Apply(new SetRecordingCommand(PunchOutTick: Bar)));
    }

    /// <summary>Intro 0–1, Verse 1–3, Chorus 3–5 (bars) on an audio track, each with a region and a marker named alike.</summary>
    void SongWithSections() {
        Apply(new AddTrackCommand());
        foreach (var (name, start, bars) in new[] { ("Intro", 0L, 1L), ("Verse", Bar, 2L), ("Chorus", 3 * Bar, 2L) }) {
            Apply(new AddSectionCommand(name, start, bars * Bar), new AddMarkerCommand(name, start));
            Apply(new AddRegionCommand(state.Tracks[0].Id, "aud_1", start, 0, 1000));
            var region = state.Regions.Values.Single(r => r.StartTick == start);
            Apply(new SetRegionCommand(region.Id, name));
        }
    }

    ArrangementMarker Section(string name) => state.Sections.Values.Single(s => s.Name == name);

    string Track(string name) => state.Tracks.Single(t => t.Name == name).Id;

    void Apply(params EditCommand[] commands) => state = Edits.Apply(state, commands, ids).State;
}
