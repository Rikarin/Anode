using Anode.Contracts;

namespace Anode.Model.Editing;

/// <summary>
///     Phase 9 (docs/10-phase-9.md) placeholders: the model refuses the new commands, and region notation in a Live
///     Loops cell's content, until the server workstream (B) implements them and removes this file.
/// </summary>
public static partial class Edits {
    /// <summary>The Phase 9 commands; null for every other command.</summary>
    static List<DeltaOp>? Phase9Ops(EditCommand command) =>
        command switch {
            SetStaffStyleCommand
                or DeleteStaffStyleCommand
                or AssignStaffStyleCommand
                or SetRegionNotationCommand
                or AddScoreSymbolsCommand
                or EditScoreSymbolsCommand
                or DeleteScoreSymbolsCommand
                or SetLyricsCommand
                or AddChordSymbolsCommand
                or EditChordSymbolsCommand
                or DeleteChordSymbolsCommand
                or SetScoreSetCommand
                or DeleteScoreSetCommand
                or SetScoreLayoutCommand
                or MoveScoreNotesCommand
                or AddScoreNoteCommand
                or ImportTracksCommand => throw Phase9Pending(command.GetType().Name),
            _ => null
        };

    /// <summary>
    ///     Whether <paramref name="command" /> is an existing command that uses a Phase 9 field: a MIDI cell's region with
    ///     notation settings other than the defaults.
    /// </summary>
    static bool UsesPhase9Fields(EditCommand command) =>
        command is SetCellCommand { Content.Midi.Notation: { } notation } && notation != RegionNotation.Default;

    static EditException Phase9Pending(string what) => new($"{what} is not implemented yet (Phase 9).");
}
