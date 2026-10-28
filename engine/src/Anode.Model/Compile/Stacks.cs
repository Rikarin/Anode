using Anode.Contracts;

namespace Anode.Model.Compile;

/// <summary>
///     What a folder stack does to its members (docs/04-phase-3.md, "Stacks"): the engine has no track for the stack,
///     so each member gets the effective strip — its volume plus the stack's (in dB), its own pan, muted when either
///     is, soloed when either is.
/// </summary>
public static class Stacks {
    public static ChannelStrip EffectiveStrip(ProjectState state, Track track) {
        if (track.ParentId is null || state.FindTrack(track.ParentId) is not { Kind: TrackKind.Stack } stack) {
            return track.Strip;
        }

        var (own, outer) = (track.Strip, stack.Strip);
        var volume = own.VolumeDb <= Units.SilenceDb || outer.VolumeDb <= Units.SilenceDb
            ? Units.SilenceDb
            : Math.Max(Units.SilenceDb, own.VolumeDb + outer.VolumeDb);
        return own with { VolumeDb = volume, Mute = own.Mute || outer.Mute, Solo = own.Solo || outer.Solo };
    }

    /// <summary>Whether the engine has a track for <paramref name="track" /> (every kind but a stack).</summary>
    public static bool IsPlayable(Track track) => track.Kind != TrackKind.Stack;
}
