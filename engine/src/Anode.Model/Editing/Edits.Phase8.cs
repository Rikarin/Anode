using Anode.Contracts;

namespace Anode.Model.Editing;

/// <summary>
///     Phase 8 (docs/09-phase-8.md) placeholders: the model refuses the new commands until the server workstream (B)
///     implements them and removes this file.
/// </summary>
public static partial class Edits {
    /// <summary>The Phase 8 commands; null for every other command.</summary>
    static List<DeltaOp>? Phase8Ops(EditCommand command) =>
        command switch {
            SetSyncSettingsCommand or AddStemsCommand => throw Phase8Pending(command.GetType().Name),
            _ => null
        };

    static EditException Phase8Pending(string what) => new($"{what} is not implemented yet (Phase 8).");
}
