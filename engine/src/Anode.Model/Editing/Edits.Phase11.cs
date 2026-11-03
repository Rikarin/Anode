using Anode.Contracts;

namespace Anode.Model.Editing;

/// <summary>
///     Phase 11 (docs/12-phase-11.md) placeholders: the model refuses the new commands, and new tracks and imported
///     tracks of the Session Player kinds (<c>drummer</c>, <c>bassPlayer</c>, <c>keyboardPlayer</c>), until the server
///     workstream (B) implements them and removes this file. Projects have an empty chord track and Environment and no
///     Session Player regions until then, and every region's Pitch Source is off.
/// </summary>
public static partial class Edits {
    /// <summary>The Phase 11 commands; null for every other command.</summary>
    static List<DeltaOp>? Phase11Ops(EditCommand command) =>
        command switch {
            AddChordsCommand
                or EditChordsCommand
                or DeleteChordsCommand
                or SetScalesCommand
                or ApplyChordIdCommand
                or SetPitchSourceCommand
                or SetRegionKeyCommand
                or AddSessionPlayerTrackCommand
                or AddSessionPlayerRegionCommand
                or SetSessionPlayerRegionCommand
                or RegenerateSessionPlayerCommand
                or ConvertSessionPlayerRegionsCommand
                or SetMasteringCommand
                or SetMasteringAnalysisCommand
                or AddEnvironmentLayerCommand
                or SetEnvironmentLayerCommand
                or RemoveEnvironmentLayerCommand
                or AddEnvironmentObjectCommand
                or SetEnvironmentObjectCommand
                or RemoveEnvironmentObjectsCommand
                or ConnectEnvironmentCommand
                or DisconnectEnvironmentCommand => throw Phase11Pending(command.GetType().Name),
            _ => null
        };

    /// <summary>
    ///     Whether <paramref name="command" /> is an existing command that uses a Phase 11 field: a new or imported track of
    ///     a Session Player kind, or imported regions with a Pitch Source or a key.
    /// </summary>
    static bool UsesPhase11Fields(EditCommand command) =>
        command switch {
            AddTrackCommand { Kind: { } kind } => IsSessionPlayerKind(kind),
            ImportTracksCommand { Content.Tracks: { } tracks } => tracks.Any(static t =>
                t is not null
                && (IsSessionPlayerKind(t.Kind)
                    || (t.MidiRegions ?? []).Any(static r => r is not null
                        && (r.PitchSource != PitchSourceMode.Off || r.Key is not null)
                    )
                    || (t.Regions ?? []).Any(static r => r is not null
                        && (r.PitchSource != PitchSourceMode.Off || r.Key is not null)
                    ))
            ),
            _ => false
        };

    static bool IsSessionPlayerKind(TrackKind kind) =>
        kind is TrackKind.Drummer or TrackKind.BassPlayer or TrackKind.KeyboardPlayer;

    static EditException Phase11Pending(string what) => new($"{what} is not implemented yet (Phase 11).");
}
