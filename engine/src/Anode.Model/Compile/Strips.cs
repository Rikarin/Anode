using Anode.Contracts;
using Anode.Ipc;
using Anode.Model.Editing;

namespace Anode.Model.Compile;

/// <summary>
///     Channel strips in engine units (docs/05-phase-4.md, "Graph"; <see cref="EngineStrip" />): the effective strip's
///     linear gain, pan, mute and solo (a stack's and a VCA's folded in, <see cref="Stacks" />), the inserts in order
///     with their sidechains as engine track ids or buses, the sends at linear gain, and the output (0 the master, N bus
///     N, −N device output pair N). A VCA's solo solos its members.
/// </summary>
public static class Strips {
    /// <summary>
    ///     A track's engine strip (null: the master's); <paramref name="stateOf" /> gives an insert's saved opaque state
    ///     (base64) when the host is to build it from scratch.
    /// </summary>
    public static EngineStrip Engine(
        ProjectState state,
        Track? track,
        EngineIds ids,
        Func<Insert, string?>? stateOf = null
    ) {
        var strip = track is null ? state.Master : Stacks.EffectiveStrip(state, track);
        var full = track is null || Edits.HasAudioStrip(track.Kind);
        return new(
            Units.DbToGain(strip.VolumeDb),
            (float)strip.Pan,
            strip.Mute,
            strip.Solo,
            full ? [..strip.Inserts.Select(i => Insert(state, ids, i, stateOf?.Invoke(i)))] : [],
            full && track is not null
                ? [
                    ..strip.Sends.Select(static s => new EngineSend(
                            s.Id,
                            s.Bus,
                            Units.DbToGain(s.LevelDb),
                            (float)s.Pan,
                            s.PreFader
                        )
                    )
                ]
                : [],
            track is null ? 0 : Output(strip.Output)
        );
    }

    /// <summary>An output as the engine numbers it: 0 the master, N bus N, −N device output pair N.</summary>
    public static int Output(string output) =>
        Routing.BusOf(output) is { } bus ? bus : Routing.PairOf(output) is { } pair ? -pair : 0;

    static EngineInsert Insert(ProjectState state, EngineIds ids, Insert insert, string? saved) {
        var (track, bus) = insert.Sidechain switch {
            null => (-1, 0),
            var s when Routing.BusOf(s) is { } b => (-1, b),
            var s when state.FindTrack(s) is { } t && Stacks.IsPlayable(t) => (ids.Track(t.Id), 0),
            _ => (-1, 0)
        };
        return new(insert.Id, insert.PluginId, insert.Bypassed, insert.Parameters, saved, track, bus);
    }

    /// <summary>Whether two engine strips differ in structure (inserts, sends, output), not just levels.</summary>
    public static bool StructureDiffers(EngineStrip a, EngineStrip b) =>
        a.Output != b.Output
        || !Same(
            a.Inserts,
            b.Inserts,
            static (x, y) => x.Id == y.Id
                && x.PluginId == y.PluginId
                && x.Bypassed == y.Bypassed
                && x.SidechainTrack == y.SidechainTrack
                && x.SidechainBus == y.SidechainBus
        )
        || !Same(a.Sends, b.Sends, static (x, y) => x.Id == y.Id && x.Bus == y.Bus && x.PreFader == y.PreFader);

    static bool Same<T>(IReadOnlyList<T>? a, IReadOnlyList<T>? b, Func<T, T, bool> equal) {
        a ??= [];
        b ??= [];
        if (a.Count != b.Count) {
            return false;
        }

        for (var i = 0; i < a.Count; i++) {
            if (!equal(a[i], b[i])) {
                return false;
            }
        }

        return true;
    }
}
