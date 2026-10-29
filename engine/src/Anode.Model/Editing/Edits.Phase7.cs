using Anode.Contracts;

namespace Anode.Model.Editing;

/// <summary>
///     Phase 7 (docs/08-phase-7.md) placeholders: the model refuses the new commands, and the new fields of existing
///     commands, until the server workstream (B) implements them and removes this file.
/// </summary>
public static partial class Edits {
    /// <summary>The Phase 7 commands; null for every other command.</summary>
    static List<DeltaOp>? Phase7Ops(EditCommand command) =>
        command switch {
            SetControlsCommand
                or TransformCommand
                or MakeGrooveCommand
                or SetGrooveCommand
                or DeleteGrooveCommand
                or SetGrooveTrackCommand
                or SetMatchGrooveCommand
                or SetArticulationSetCommand
                or DeleteArticulationSetCommand
                or AssignArticulationSetCommand
                or SetLaneSetCommand
                or DeleteLaneSetCommand
                or StepNotesCommand
                or StepTieCommand
                or SetFileVersionCommand
                or SetScriptCommand => throw Phase7Pending(command.GetType().Name),
            _ => null
        };

    /// <summary>
    ///     Whether <paramref name="command" /> is an existing command that uses a Phase 7 field: a groove for quantise, an
    ///     articulation for notes.
    /// </summary>
    static bool UsesPhase7Fields(EditCommand command) =>
        command switch {
            QuantizeNotesCommand c => c.GrooveId is not null || c.GrooveVelocity is not null,
            QuantizeAudioCommand c => c.GrooveId is not null,
            SetMidiRegionCommand c => c.GrooveId is not null,
            AddNotesCommand c => c.Notes.Any(static n => n.ArticulationId is not null and not 0),
            EditNotesCommand c => c.Changes.Any(static n => n.ArticulationId is not null),
            _ => false
        };

    static EditException Phase7Pending(string what) => new($"{what} is not implemented yet (Phase 7).");
}
