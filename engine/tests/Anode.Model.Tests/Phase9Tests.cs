using Anode.Contracts;
using Anode.Model.Editing;

namespace Anode.Model.Tests;

/// <summary>
///     The Phase 9 contracts (docs/10-phase-9.md) before workstream B implements them: the model refuses the new
///     commands and region notation in a cell's content, and new regions take the default notation. Also the score's
///     element ids, which the editor mirrors.
/// </summary>
public sealed class Phase9Tests {
    [Fact]
    public void The_model_refuses_the_Phase_9_commands_until_workstream_B() {
        var (state, ids, random) = RandomProjects.Build(7, 40);
        var refused = 0;
        foreach (var type in RandomProjects.CommandTypes.Where(static t => RandomProjects.PendingPhase9.Contains(t.Name)
                 )) {
            if (RandomProjects.Command(type, state, random) is not { } command) {
                continue;
            }

            var e = Assert.Throws<EditException>(() => Edits.Apply(
                    state,
                    command,
                    ids,
                    RandomProjects.Plugins,
                    RandomProjects.Patches,
                    RandomProjects.Analysis
                )
            );
            Assert.Contains("Phase 9", e.Message, StringComparison.Ordinal);
            refused++;
        }

        Assert.True(refused >= 10, $"Only {refused} Phase 9 commands could be made.");
    }

    [Fact]
    public void New_regions_take_the_default_notation_and_a_cell_with_other_notation_is_refused() {
        var ids = new SequentialIds();
        var state = Edits.Apply(
                ProjectDefaults.NewProject("prj_1", "Song", 48_000),
                new AddTrackCommand(Kind: TrackKind.Instrument),
                ids,
                RandomProjects.Plugins
            )
            .State;
        var track = state.Tracks[0].Id;
        state = Edits.Apply(state, new AddMidiRegionCommand(track, 0, 3840), ids).State;
        var region = Assert.Single(state.MidiRegions.Values);
        Assert.Equal(RegionNotation.Default, region.Notation);
        Assert.Null(state.Tracks[0].StaffStyleId);
        state = Edits.Apply(state, new AddSceneCommand(), ids).State;
        var scene = state.LiveLoops.Scenes[0].Id;
        var midi = region with { Notation = RegionNotation.Default with { MaxDots = 2 } };
        var e = Assert.Throws<EditException>(() =>
            Edits.Apply(state, new SetCellCommand(track, scene, new(Midi: midi)), ids)
        );
        Assert.Contains("Phase 9", e.Message, StringComparison.Ordinal);
        state = Edits.Apply(state, new SetCellCommand(track, scene, new(Midi: region with { Notation = null! })), ids)
            .State;
        Assert.Equal(RegionNotation.Default, state.LiveLoops.Cells[0].Content.Midi!.Notation);
    }

    [Fact]
    public void Score_element_ids_map_back_to_notes_and_symbols() {
        Assert.Equal("n42", ScoreIds.Note(42));
        Assert.Equal("n42-3", ScoreIds.Note(42, 3));
        Assert.Equal("c7-2", ScoreIds.Chord(7, 2));
        Assert.Equal("ssym_abc", ScoreIds.Symbol("sym_abc"));
        Assert.Equal("rtrk_1-4-0-1-2", ScoreIds.Rest("trk_1", 4, 0, 1, 2));
        Assert.Equal(42, ScoreIds.NoteIdOf("n42"));
        Assert.Equal(42, ScoreIds.NoteIdOf("n42-3"));
        Assert.Null(ScoreIds.NoteIdOf("c42"));
        Assert.Null(ScoreIds.NoteIdOf("n"));
        Assert.Null(ScoreIds.NoteIdOf("nx"));
        Assert.Null(ScoreIds.NoteIdOf("n042"));
        Assert.Null(ScoreIds.NoteIdOf("n42-"));
        Assert.Null(ScoreIds.NoteIdOf("n42-x"));
        Assert.Equal("sym_abc", ScoreIds.SymbolIdOf("ssym_abc"));
        Assert.Null(ScoreIds.SymbolIdOf("s"));
        Assert.Null(ScoreIds.SymbolIdOf("n42"));
    }
}
