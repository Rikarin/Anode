using Anode.Contracts;
using Anode.Model.Editing;
using Anode.Model.History;

namespace Anode.Model.Tests;

/// <summary>
///     <c>pasteRegions</c>: regions off the editor's clipboard, whose originals a cut has deleted, are checked as new and
///     get ids of their own (docs/04-phase-3.md, "Regions").
/// </summary>
public sealed class PasteRegionsTests {
    const long Beat = 960;
    const long BeatFrames = 24_000; // 120 BPM at 48 kHz
    readonly SequentialIds ids = new();

    ProjectState state = ProjectDefaults.NewProject("prj_1", "Song", 48_000)
        .Apply(new AudioFileUpserted(new("aud_1", "Take 1", "Audio Files/1.wav", 48_000, 1, 480_000, 24, null, 0)));

    void Apply(params EditCommand[] commands) =>
        state = Edits.Apply(state, commands, ids, styles: RandomProjects.Styles).State;

    void Refused(params EditCommand[] commands) => Assert.Throws<EditException>(() => Apply(commands));

    string Audio(string name = "Vox") {
        Apply(new AddTrackCommand(name));
        return state.Tracks[^1].Id;
    }

    string Instrument(string name = "Keys") {
        Apply(new AddTrackCommand(name, Kind: TrackKind.Instrument));
        return state.Tracks[^1].Id;
    }

    [Fact]
    public void Audio_regions_come_back_after_a_cut_on_another_track_with_new_ids() {
        var (from, to) = (Audio(), Audio("Gtr"));
        Apply(new AddRegionCommand(from, "aud_1", Beat, 100, 2 * BeatFrames));
        var region = state.Regions.Values.Single() with { GainDb = -3, FadeIn = 10 };
        Apply(new DeleteRegionsCommand([region.Id]));
        Assert.Empty(state.Regions);

        Apply(new PasteRegionsCommand([region with { TrackId = to, StartTick = 8 * Beat }]));
        var pasted = state.Regions.Values.Single();
        Assert.NotEqual(region.Id, pasted.Id);
        Assert.Equal(
            (to, 8 * Beat, 100L, 2 * BeatFrames, -3.0, 10L),
            (pasted.TrackId, pasted.StartTick, pasted.FileOffset, pasted.Length, pasted.GainDb, pasted.FadeIn)
        );
    }

    [Fact]
    public void A_copy_keeps_its_flex_and_tuning_unlike_an_import() {
        var track = Audio();
        Apply(new SetTrackFlexCommand(track, true));
        Apply(new AddRegionCommand(track, "aud_1", 0, 0, 4 * BeatFrames));
        var region = state.Regions.Values.Single();
        Apply(
            new SetRegionFlexCommand(region.Id, Transpose: 2, FineTune: -10),
            new SetFlexMarkersCommand(region.Id, [new(BeatFrames, 2 * Beat)])
        );
        var source = state.Regions[region.Id];
        Apply(new PasteRegionsCommand([source with { StartTick = 16 * Beat }]));
        var pasted = state.Regions.Values.Single(r => r.Id != region.Id);
        Assert.Equal((2, -10), (pasted.Transpose, pasted.FineTune));
        Assert.Equal(source.FlexMarkers, pasted.FlexMarkers);
    }

    [Fact]
    public void A_region_is_refused_when_its_track_cannot_hold_it_or_its_file_is_not_the_projects() {
        var (audio, keys) = (Audio(), Instrument());
        Apply(new AddRegionCommand(audio, "aud_1", 0, 0, BeatFrames));
        Apply(new AddMidiRegionCommand(keys, 0, Beat));
        var region = state.Regions.Values.Single();
        var midi = state.MidiRegions.Values.Single();

        Refused(new PasteRegionsCommand([region with { TrackId = keys }]));
        Refused(new PasteRegionsCommand(MidiRegions: [midi with { TrackId = audio }]));
        Refused(new PasteRegionsCommand([region with { TrackId = "trk_none" }]));
        Refused(new PasteRegionsCommand([region with { FileId = "aud_other" }]));
        Refused(new PasteRegionsCommand([region with { Length = 480_001 }]));
        Refused(new PasteRegionsCommand([region with { StartTick = -1 }]));
        Refused(new PasteRegionsCommand([region with { Follow = (FlexFollow)99 }]));
        Refused(new PasteRegionsCommand([region with { Follow = FlexFollow.On }])); // the file has no beats
        Refused(new PasteRegionsCommand([region with { Transpose = Edits.MaxRegionTranspose + 1 }]));
        Refused(new PasteRegionsCommand([region with { FineTune = Edits.MaxFineTune + 1 }]));
        Refused(new PasteRegionsCommand([region with { FlexMarkers = [new(0, Beat)] }]));
        Refused(new PasteRegionsCommand([region, region]));
        Refused(new PasteRegionsCommand());
    }

    [Fact]
    public void MIDI_regions_get_new_note_and_controller_ids() {
        var keys = Instrument();
        Apply(new AddMidiRegionCommand(keys, 0, 4 * Beat));
        var id = state.MidiRegions.Keys.Single();
        Apply(new AddNotesCommand(id, [new(60, 100, 1, 0, 480), new(62, 100, 1, 480, 480)]));
        var region = state.MidiRegions[id];
        Apply(new PasteRegionsCommand(MidiRegions: [region with { StartTick = 4 * Beat }]));
        var pasted = state.MidiRegions.Values.Single(r => r.Id != id);
        Assert.Equal(region.Notes.Select(static n => n.Pitch), pasted.Notes.Select(static n => n.Pitch));
        Assert.Empty(pasted.Notes.Select(static n => n.Id).Intersect(region.Notes.Select(static n => n.Id)));
        Refused(new PasteRegionsCommand(MidiRegions: [region with { Notes = [region.Notes[0] with { Pitch = 128 }] }]));
        var loud = region.Playback with { Velocity = 128 };
        Refused(new PasteRegionsCommand(MidiRegions: [region with { Playback = loud }]));
    }

    [Fact]
    public void Take_folders_keep_their_comp_on_new_take_ids() {
        var track = Audio();
        Apply(
            new AddRegionCommand(track, "aud_1", 0, 0, 2 * BeatFrames),
            new AddRegionCommand(track, "aud_1", 0, 0, 2 * BeatFrames)
        );
        Apply(new PackTakeFolderCommand([..state.Regions.Keys]));
        var folder = state.TakeFolders.Values.Single();
        Apply(new DeleteRegionsCommand([folder.Id]));

        var shifted = folder with {
            StartTick = folder.StartTick + 4 * Beat,
            Takes = [..folder.Takes.Select(static t => t with { StartTick = t.StartTick + 4 * Beat })],
            Comp = [
                ..folder.Comp.Select(static s => s with {
                        StartTick = s.StartTick + 4 * Beat, EndTick = s.EndTick + 4 * Beat
                    }
                )
            ]
        };
        Apply(new PasteRegionsCommand(TakeFolders: [shifted]));
        var pasted = state.TakeFolders.Values.Single();
        Assert.NotEqual(folder.Id, pasted.Id);
        Assert.Equal(4 * Beat, pasted.StartTick);
        Assert.Empty(pasted.Takes.Select(static t => t.Id).Intersect(folder.Takes.Select(static t => t.Id)));
        Assert.Contains(pasted.Takes, t => t.Id == pasted.Comp[0].TakeId);

        Refused(new PasteRegionsCommand(TakeFolders: [folder with { Takes = [] }]));
        Refused(new PasteRegionsCommand(TakeFolders: [folder with { Crossfade = -1 }]));
        Refused(new PasteRegionsCommand(TakeFolders: [folder with { Takes = [folder.Takes[0], folder.Takes[0]] }]));
        Refused(new PasteRegionsCommand(TakeFolders: [folder with { Comp = [new(0, Beat, "tak_none")] }]));
        Refused(new PasteRegionsCommand(TakeFolders: [folder with { TrackId = Instrument() }]));
    }

    [Fact]
    public void Pattern_regions_go_on_tracks_that_hold_MIDI() {
        var (keys, audio) = (Instrument(), Audio());
        Apply(new AddPatternRegionCommand(keys, 0));
        var region = state.PatternRegions.Values.Single();
        Apply(new PasteRegionsCommand(PatternRegions: [region with { StartTick = 8 * Beat }]));
        Assert.Equal(2, state.PatternRegions.Count);
        Assert.Equal(region.Pattern.Rows, state.PatternRegions.Values.Single(r => r.Id != region.Id).Pattern.Rows);
        Refused(new PasteRegionsCommand(PatternRegions: [region with { TrackId = audio }]));
        Refused(new PasteRegionsCommand(PatternRegions: [region with { LengthTicks = 0 }]));
    }

    [Fact]
    public void Session_Player_regions_keep_their_seed_on_a_track_of_their_player() {
        Apply(new AddSessionPlayerTrackCommand(SessionPlayerKind.Drummer, "drums.rock", Regions: false));
        var drums = state.Tracks.Single(static t => t.Kind == TrackKind.Drummer).Id;
        Apply(new AddSessionPlayerRegionCommand(drums, 0));
        var region = state.SessionPlayerRegions.Values.Single();
        Apply(new PasteRegionsCommand(SessionPlayerRegions: [region with { StartTick = 64 * Beat }]));
        var pasted = state.SessionPlayerRegions.Values.Single(r => r.Id != region.Id);
        Assert.Equal((region.Seed, region.Parameters.StyleId), (pasted.Seed, pasted.Parameters.StyleId));
        Assert.Equal(region.Parameters.Pad, pasted.Parameters.Pad);
        Refused(new PasteRegionsCommand(SessionPlayerRegions: [region with { TrackId = Instrument() }]));
    }

    [Fact]
    public void Paste_and_its_automation_are_one_undo_step() {
        var track = Audio();
        Apply(new AddRegionCommand(track, "aud_1", 0, 0, BeatFrames));
        var region = state.Regions.Values.Single();
        var before = state;
        var history = new EditHistory(TimeProvider.System);
        var batch = new CommandBatch(
            [
                new PasteRegionsCommand([region with { StartTick = 8 * Beat }]),
                new SetAutomationCommand("volume", [new(8 * Beat, -6, 0), new(9 * Beat, 0, 0)], track)
            ],
            "Paste Region"
        );
        var result = Edits.Apply(state, batch.Commands, ids);
        state = result.State;
        history.Push(UndoLabels.For(batch), 0, new(result.Forward, result.Inverse), UndoLabels.CoalesceKey(batch));
        Assert.Equal(2, state.Regions.Count);
        Assert.Single(state.Automation);

        Assert.Equal("Paste Region", Assert.Single(history.History().Undo).Label);
        state = state.Apply(history.PeekUndo()!.Inverse);
        Assert.Equal(before.Regions.Keys, state.Regions.Keys);
        Assert.Empty(state.Automation);
    }
}
