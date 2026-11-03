using Anode.Contracts;

namespace Anode.Model.Editing;

/// <summary>
///     Phase 10 (docs/11-phase-10.md) placeholders: the model refuses the new commands, scene markers, and imported
///     tracks with formats, panners or automation, until the server workstream (B) implements them and removes this
///     file. Panner automation targets (<c>surround:…</c>, <c>object:…</c>) are refused as unknown targets until then.
/// </summary>
public static partial class Edits {
    /// <summary>The Phase 10 commands; null for every other command.</summary>
    static List<DeltaOp>? Phase10Ops(EditCommand command) =>
        command switch {
            SetSurroundFormatCommand
                or SetStripFormatCommand
                or SetPannerCommand
                or SetMovieCommand
                or SetMoviePositionCommand
                or RemoveMovieCommand
                or AddSceneMarkersCommand => throw Phase10Pending(command.GetType().Name),
            _ => null
        };

    /// <summary>
    ///     Whether <paramref name="command" /> is an existing command that uses a Phase 10 field: a marker's kind other than
    ///     <c>marker</c>, or an imported track's input format, panner or automation.
    /// </summary>
    static bool UsesPhase10Fields(EditCommand command) =>
        command switch {
            AddMarkerCommand { Kind: { } kind } => kind != MarkerKind.Marker,
            SetMarkerCommand { Kind: { } kind } => kind != MarkerKind.Marker,
            ImportTracksCommand { Content.Tracks: { } tracks } => tracks.Any(static t =>
                t is not null && (t.InputFormat is not null || t.Panner is not null || t.Automation is not null)
            ),
            _ => false
        };

    static EditException Phase10Pending(string what) => new($"{what} is not implemented yet (Phase 10).");
}
