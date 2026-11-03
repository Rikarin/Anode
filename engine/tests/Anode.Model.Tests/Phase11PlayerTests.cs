using Anode.Contracts;
using Anode.Model.Editing;

namespace Anode.Model.Tests;

/// <summary>
///     Phase 11's Session Players in the model (docs/12-phase-11.md): Drummer, Bass Player and Keyboard Player tracks,
///     their regions' defaults from the section kinds, presets and parameters, Regenerate, Convert, and their regions
///     on the timeline. The styles are <see cref="RandomProjects.Styles" />, a stand-in for workstream D's library.
/// </summary>
public sealed class Phase11PlayerTests {
    readonly SequentialIds ids = new();
    ProjectState state = ProjectDefaults.NewProject("prj_1", "Song", 48_000);

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

    EditException Refused(EditCommand command) =>
        Assert.Throws<EditException>(() =>
            Edits.Apply(state, command, ids, RandomProjects.Plugins, RandomProjects.Patches, styles: RandomProjects.Styles)
        );

    static SessionPlayerStyle StyleOf(string id) => RandomProjects.Styles.Styles.Single(s => s.Id == id);

    [Fact]
    public void A_new_player_gets_a_region_per_section_with_its_kind_s_defaults_and_a_seed_of_its_own() {
        Apply(
            new AddSectionCommand("Intro", 0, 7680),
            new AddSectionCommand("Chorus 1", 7680, 15_360),
            new AddSectionCommand("Middle", 23_040, 7680),
            new AddSessionPlayerTrackCommand(SessionPlayerKind.Bass, "bass.pop")
        );
        var track = Assert.Single(state.Tracks);
        Assert.Equal((TrackKind.BassPlayer, "Bass Player 1"), (track.Kind, track.Name));
        Assert.Equal(PluginCatalog.DefaultInstrumentId, track.Instrument!.PluginId);
        var regions = state.SessionPlayerRegionsOf(track.Id).ToArray();
        Assert.Equal([0L, 7680, 23_040], regions.Select(static r => r.StartTick));
        Assert.Equal([7680L, 15_360, 7680], regions.Select(static r => r.LengthTicks));
        Assert.Equal(["Intro", "Chorus 1", "Middle"], regions.Select(static r => r.Name));
        var style = StyleOf("bass.pop");
        SectionKind[] kinds = [SectionKind.Intro, SectionKind.Chorus, SectionKind.Bridge];
        for (var i = 0; i < kinds.Length; i++) {
            Assert.Equal(style.Sections.Single(s => s.Kind == kinds[i]).Parameters.Pad, regions[i].Parameters.Pad);
            Assert.NotNull(regions[i].Parameters.Bass);
            Assert.Null(regions[i].Parameters.Drums);
        }

        Assert.Equal(3, regions.Select(static r => r.Seed).Distinct().Count());
        // Without sections: one region of 8 bars from the start.
        var other = Edits.Apply(
            ProjectDefaults.NewProject("prj_2", "Song", 48_000),
            new AddSessionPlayerTrackCommand(SessionPlayerKind.Keyboard),
            ids,
            styles: RandomProjects.Styles
        );
        var single = Assert.Single(other.State.SessionPlayerRegions.Values);
        Assert.Equal((0L, 8 * 3840L, "keys.ballad"), (single.StartTick, single.LengthTicks, single.Parameters.StyleId));
    }

    [Fact]
    public void A_Drummer_is_a_Drum_Machine_Designer_whose_pads_are_its_kit_s() {
        Apply(new AddSessionPlayerTrackCommand(SessionPlayerKind.Drummer, "drums.rock", Regions: false));
        var drummer = state.Tracks[0];
        Assert.Equal(TrackKind.Drummer, drummer.Kind);
        // The style's kit patch: three pads, each a member instrument track (multi-output).
        Assert.Equal([36, 38, 42], drummer.DrumMachine!.Pads.Select(static p => p.Note));
        Assert.All(state.MembersOf(drummer.Id), static t => Assert.Equal(TrackKind.Instrument, t.Kind));
        Assert.True(Edits.HoldsMidi(drummer));
        // A kit the library does not have: one Drum Synth pad on each note the Drummer plays.
        var kitless = Edits.Apply(
            ProjectDefaults.NewProject("prj_2", "Song", 48_000),
            new AddSessionPlayerTrackCommand(SessionPlayerKind.Drummer, Regions: false),
            ids,
            RandomProjects.Plugins,
            new RandomProjects.TestPatches([]),
            styles: RandomProjects.Styles
        );
        var notes = Enum.GetValues<DrumPiece>().SelectMany(DrumKitNotes.Of).Order();
        Assert.Equal(notes, kitless.State.Tracks[0].DrumMachine!.Pads.Select(static p => p.Note));
        Assert.All(
            kitless.State.Tracks.Skip(1),
            static t => Assert.Equal(Edits.DrumSynthId, t.Instrument!.PluginId)
        );
        Refused(new AddSessionPlayerTrackCommand(SessionPlayerKind.Drummer, KitPatchId: "pat_none"));
        Refused(new AddSessionPlayerTrackCommand(SessionPlayerKind.Drummer, PluginId: "anode.synth"));
        Refused(new AddSessionPlayerTrackCommand(SessionPlayerKind.Bass, "drums.rock"));
        Assert.Contains("addSessionPlayerTrack", Refused(new AddTrackCommand(Kind: TrackKind.Drummer)).Message);
        // No library: no players.
        Assert.Throws<EditException>(() =>
            Edits.Apply(state, new AddSessionPlayerTrackCommand(SessionPlayerKind.Bass), ids, RandomProjects.Plugins)
        );
    }

    [Fact]
    public void A_preset_applies_first_and_any_other_parameter_clears_it_while_the_seed_stays() {
        Apply(new AddSessionPlayerTrackCommand(SessionPlayerKind.Drummer, "drums.rock", "drums.rock.soft"));
        var region = state.SessionPlayerRegions.Values.Single();
        Assert.Equal("drums.rock.soft", region.Parameters.PresetId);
        Apply(new SetSessionPlayerRegionCommand(region.Id, PresetId: "drums.rock.busy", Swing: 0.4));
        var changed = state.SessionPlayerRegions[region.Id];
        Assert.Equal((null, 0.9, 0.4), (changed.Parameters.PresetId, changed.Parameters.Pad.Complexity, changed.Parameters.Swing));
        Apply(new SetSessionPlayerRegionCommand(region.Id, PresetId: "drums.rock.busy", Name: "Groove"));
        Assert.Equal(("drums.rock.busy", "Groove"), (state.SessionPlayerRegions[region.Id].Parameters.PresetId, state.SessionPlayerRegions[region.Id].Name));
        // A new style brings its first preset.
        Apply(new SetSessionPlayerRegionCommand(region.Id, StyleId: "drums.funk"));
        Assert.Equal(("drums.funk", "drums.funk.soft"), (state.SessionPlayerRegions[region.Id].Parameters.StyleId, state.SessionPlayerRegions[region.Id].Parameters.PresetId));
        Apply(new SetSessionPlayerRegionCommand(region.Id, Complexity: 0.1, Intensity: 0.2));
        var moved = state.SessionPlayerRegions[region.Id];
        Assert.Equal((new SessionPlayerPad(0.1, 0.2), null, region.Seed), (moved.Parameters.Pad, moved.Parameters.PresetId, moved.Seed));
        Refused(new SetSessionPlayerRegionCommand(region.Id, Complexity: 1.5));
        Refused(new SetSessionPlayerRegionCommand(region.Id, Feel: -2));
        Refused(new SetSessionPlayerRegionCommand(region.Id, Bass: Edits.DefaultBass));
        Refused(new SetSessionPlayerRegionCommand(region.Id, StyleId: "bass.pop"));
        Refused(new SetSessionPlayerRegionCommand(region.Id, Drums: Edits.DefaultDrums with { Pieces = Edits.DefaultDrums.Pieces.Skip(1).ToArray() }));
        Refused(new SetSessionPlayerRegionCommand(region.Id, Drums: Edits.DefaultDrums with { RhythmSourceTrackId = state.Tracks[0].Id }));
    }

    [Fact]
    public void Bass_and_keys_switches_keep_their_ranges_and_rhythms() {
        Apply(
            new AddSessionPlayerTrackCommand(SessionPlayerKind.Bass, Regions: false),
            new AddSessionPlayerTrackCommand(SessionPlayerKind.Keyboard, Regions: false)
        );
        Apply(new AddSessionPlayerRegionCommand(state.Tracks[0].Id, 0), new AddSessionPlayerRegionCommand(state.Tracks[1].Id, 0));
        var (bass, keys) = (state.SessionPlayerRegionsOf(state.Tracks[0].Id).Single(), state.SessionPlayerRegionsOf(state.Tracks[1].Id).Single());
        Refused(new SetSessionPlayerRegionCommand(bass.Id, Bass: new(30, 40, false, false)));
        Refused(new SetSessionPlayerRegionCommand(bass.Id, Bass: new(20, 60, false, false)));
        Apply(new SetSessionPlayerRegionCommand(bass.Id, Bass: new(24, 36, true, true)));
        Refused(new SetSessionPlayerRegionCommand(keys.Id, Keys: new(KeysVoicing.Shell, 48, 84, KeysLeftHand.Octaves, "none")));
        Apply(new SetSessionPlayerRegionCommand(keys.Id, Keys: new(KeysVoicing.Shell, 48, 84, KeysLeftHand.Octaves, "whole")));
        Assert.Equal("whole", state.SessionPlayerRegions[keys.Id].Parameters.Keys!.RhythmId);
    }

    [Fact]
    public void A_region_s_defaults_come_from_the_section_it_starts_in_and_the_track_s_style() {
        Apply(
            new AddSectionCommand("Verse", 0, 15_360),
            new AddSectionCommand("Outro", 15_360, 7680),
            new AddSessionPlayerTrackCommand(SessionPlayerKind.Keyboard, "keys.synth", Regions: false)
        );
        var track = state.Tracks[0].Id;
        Apply(new AddSessionPlayerRegionCommand(track, 3840));
        var first = state.SessionPlayerRegions.Values.Single();
        // To the section's end; the player's first style without a region before it.
        Assert.Equal((3840L, 15_360 - 3840L, "keys.ballad"), (first.StartTick, first.LengthTicks, first.Parameters.StyleId));
        Apply(new SetSessionPlayerRegionCommand(first.Id, StyleId: "keys.synth"), new AddSessionPlayerRegionCommand(track, 15_360, Seed: 42));
        var second = state.SessionPlayerRegionsOf(track).Last();
        Assert.Equal(("keys.synth", 42L, "Outro"), (second.Parameters.StyleId, second.Seed, second.Name));
        Assert.Equal(StyleOf("keys.synth").Sections.Single(static s => s.Kind == SectionKind.Outro).Parameters.Pad, second.Parameters.Pad);
        Refused(new AddSessionPlayerRegionCommand(track, 0, Parameters: StyleOf("bass.pop").Presets[0].Parameters));
    }

    [Fact]
    public void Regenerate_draws_new_seeds_and_Convert_replaces_regions_by_their_notes() {
        Apply(new AddSessionPlayerTrackCommand(SessionPlayerKind.Drummer, Regions: false));
        var drummer = state.Tracks[0].Id;
        Apply(new AddSessionPlayerRegionCommand(drummer, 0, 3840, Name: "Groove", Seed: 1), new AddSessionPlayerRegionCommand(drummer, 3840, 3840, Seed: 1));
        var (a, b) = (state.SessionPlayerRegionsOf(drummer).First(), state.SessionPlayerRegionsOf(drummer).Last());
        Apply(new RegenerateSessionPlayerCommand([a.Id, b.Id]));
        Assert.NotEqual(1, state.SessionPlayerRegions[a.Id].Seed);
        Assert.NotEqual(state.SessionPlayerRegions[a.Id].Seed, state.SessionPlayerRegions[b.Id].Seed);
        Apply(new RegenerateSessionPlayerCommand([a.Id], 7));
        Assert.Equal(7, state.SessionPlayerRegions[a.Id].Seed);

        Apply(new SetSessionPlayerRegionCommand(a.Id, Muted: true));
        var pattern = new Pattern([], 0, 3);
        var before = state;
        var result = Edits.Apply(
            state,
            new ConvertSessionPlayerRegionsCommand(
                [
                    new(a.Id, SessionPlayerConvertTarget.Midi, [new(36, 100, 1, 0, 240), new(42, 80, 1, 480, 120)], null),
                    new(b.Id, SessionPlayerConvertTarget.Pattern, [], pattern)
                ]
            ),
            ids
        );
        state = result.State;
        Assert.Empty(state.SessionPlayerRegions);
        var midi = state.MidiRegions.Values.Single();
        Assert.Equal((drummer, "Groove", 0L, 3840L, true), (midi.TrackId, midi.Name, midi.StartTick, midi.LengthTicks, midi.Muted));
        Assert.Equal([36, 42], midi.Notes.Select(static n => n.Pitch));
        var converted = state.PatternRegions.Values.Single();
        Assert.Equal((3840L, 3L, drummer), (converted.StartTick, converted.Pattern.Seed, converted.TrackId));
        Assert.Equal(RandomProjects.Json(before), RandomProjects.Json(state.Apply(result.Inverse)));
    }

    [Fact]
    public void Only_a_Drummer_s_regions_convert_to_a_pattern() {
        Apply(new AddSessionPlayerTrackCommand(SessionPlayerKind.Bass));
        var region = state.SessionPlayerRegions.Values.Single();
        Assert.Contains(
            "MIDI only",
            Refused(new ConvertSessionPlayerRegionsCommand([new(region.Id, SessionPlayerConvertTarget.Pattern, [], new([], 0, 1))])).Message
        );
    }

    [Fact]
    public void Session_Player_regions_move_copy_and_delete_as_regions_and_go_with_their_track() {
        Apply(
            new AddSectionCommand("Verse", 0, 7680),
            new AddSectionCommand("Chorus", 7680, 7680),
            new AddTrackCommand(Kind: TrackKind.Instrument),
            new AddSessionPlayerTrackCommand(SessionPlayerKind.Drummer, Regions: false),
            new AddSessionPlayerTrackCommand(SessionPlayerKind.Drummer, Regions: false)
        );
        var (instrument, one) = (state.Tracks[0].Id, state.Tracks.First(static t => t.Kind == TrackKind.Drummer).Id);
        var two = state.Tracks.Last(static t => t.Kind == TrackKind.Drummer).Id;
        Apply(new AddSessionPlayerRegionCommand(one, 0, 7680));
        var region = state.SessionPlayerRegions.Values.Single();
        Apply(new MoveRegionCommand(region.Id, 960, two));
        Assert.Equal((960L, two), (state.SessionPlayerRegions[region.Id].StartTick, state.SessionPlayerRegions[region.Id].TrackId));
        Refused(new MoveRegionCommand(region.Id, 0, instrument));
        Apply(new CopyRegionsCommand([region.Id], 7680));
        var copy = state.SessionPlayerRegions.Values.Single(r => r.Id != region.Id);
        Assert.Equal((8640L, state.SessionPlayerRegions[region.Id].Seed), (copy.StartTick, copy.Seed));
        Refused(new CopyRegionsCommand([region.Id], 0, instrument));
        // A section moves what starts in it.
        Apply(new MoveSectionCommand(state.Sections.Values.Single(static s => s.Name == "Chorus").Id, 0));
        Assert.Equal(960L, state.SessionPlayerRegions[copy.Id].StartTick);
        Apply(
            new SetSessionPlayerRegionCommand(region.Id, Drums: Edits.DefaultDrums with { RhythmSourceTrackId = instrument }),
            new DeleteRegionsCommand([copy.Id])
        );
        Assert.Single(state.SessionPlayerRegions);
        // The rhythm source's track goes: the region follows nothing; its own track goes: it goes.
        Apply(new RemoveTrackCommand(instrument));
        Assert.Null(state.SessionPlayerRegions[region.Id].Parameters.Drums!.RhythmSourceTrackId);
        Apply(new RemoveTrackCommand(two));
        Assert.Empty(state.SessionPlayerRegions);
    }
}
