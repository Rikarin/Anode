using Anode.Contracts;

namespace Anode.Model.Compile;

/// <summary>
///     What a folder stack and a VCA do to their members (docs/04-phase-3.md, "Stacks"; docs/05-phase-4.md, "VCA and
///     groups"): the engine has no track for either, so each member gets the effective strip — its volume plus the
///     stack's and the VCA's (in dB), its own pan, muted when any of them is, soloed when any of them is.
/// </summary>
public static class Stacks {
    public static ChannelStrip EffectiveStrip(ProjectState state, Track track) {
        var own = track.Strip;
        var offsets = Offsets(state, track);
        if (offsets.Count == 0) {
            return own;
        }

        var volume = own.VolumeDb;
        var mute = own.Mute;
        var solo = own.Solo;
        foreach (var outer in offsets) {
            volume = Add(volume, outer.VolumeDb);
            mute |= outer.Mute;
            solo |= outer.Solo;
        }

        return own with { VolumeDb = volume, Mute = mute, Solo = solo };
    }

    /// <summary>The strips that offset <paramref name="track" />'s: its stack's, then its VCA's.</summary>
    public static IReadOnlyList<ChannelStrip> Offsets(ProjectState state, Track track) {
        var offsets = new List<ChannelStrip>(2);
        if (track.ParentId is not null && state.FindTrack(track.ParentId) is { Kind: TrackKind.Stack } stack) {
            offsets.Add(stack.Strip);
        }

        if (track.Strip.VcaId is not null && state.FindTrack(track.Strip.VcaId) is { Kind: TrackKind.Vca } vca) {
            offsets.Add(vca.Strip);
        }

        return offsets;
    }

    /// <summary>Two volumes added in dB, −∞ (<see cref="Units.SilenceDb" />) absorbing.</summary>
    public static double Add(double a, double b) =>
        a <= Units.SilenceDb || b <= Units.SilenceDb ? Units.SilenceDb : Math.Max(Units.SilenceDb, a + b);

    /// <summary>
    ///     Whether the engine has a track for <paramref name="track" />: every kind but a stack and a VCA, and a Drum
    ///     Machine Designer (a stack that takes live MIDI and routes it to its pads by note; it plays nothing itself).
    /// </summary>
    public static bool IsPlayable(Track track) =>
        track.Kind is not (TrackKind.Stack or TrackKind.Vca) || track.DrumMachine is not null;
}
