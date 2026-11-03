using Anode.Contracts;
using Anode.Ipc;
using Anode.Ipc.Telemetry;
using Anode.Model.Editing;

namespace Anode.Model.Compile;

/// <summary>
///     The Environment flattened for the host (docs/adr/0015-environment.md; <see cref="EngineEnvironment" />): its
///     objects in an order in which every cable goes forward (the project's order where cables allow), with no layers
///     and no contract ids; cables as routes by index; track ids as engine ids (a track input or control target to a
///     track the engine has no track for drives nothing); a control target's address as the automation target it
///     drives (none when the strip no longer has it); the transformer's rules by their contract names; monitors and
///     faders on telemetry slots in order (the first <see cref="TelemetryLayout.MaxMonitors" /> and
///     <see cref="TelemetryLayout.MaxFaders" />; the rest report nothing). An empty Environment is null: the default
///     input path.
/// </summary>
public static class EnvironmentCompiler {
    public static EngineEnvironment? Compile(ProjectState state, EngineIds ids) {
        var environment = state.Environment;
        if (environment.Objects.Count == 0) {
            return null;
        }

        var order = Order(environment);
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < order.Count; i++) {
            index[order[i].Id] = i;
        }

        var (monitors, faders) = (0, 0);
        var objects = new EngineEnvironmentObject[order.Count];
        for (var i = 0; i < order.Count; i++) {
            var o = order[i];
            var slot = o.Kind switch {
                EnvironmentObjectKind.Monitor => monitors < TelemetryLayout.MaxMonitors ? monitors++ : -1,
                EnvironmentObjectKind.Fader => faders < TelemetryLayout.MaxFaders ? faders++ : -1,
                _ => -1
            };
            objects[i] = new(
                i,
                (EngineEnvironmentKind)o.Kind,
                new Dictionary<string, double>(o.Parameters, StringComparer.Ordinal),
                [
                    ..environment.Cables.Where(c => c.FromObjectId == o.Id && index.ContainsKey(c.ToObjectId))
                        .OrderBy(static c => c.Outlet)
                        .ThenBy(c => index[c.ToObjectId])
                        .ThenBy(static c => c.Inlet)
                        .Select(c => new EngineEnvironmentRoute(c.Outlet, index[c.ToObjectId], c.Inlet))
                ],
                o.Port,
                o.Kind == EnvironmentObjectKind.TrackInput ? EngineTrack(state, ids, o.TrackId) : -1,
                o.Kind == EnvironmentObjectKind.ControlTarget ? Control(state, ids, o) : null,
                o.Conditions is { } conditions
                    ? [..conditions.Select(static c => new EngineTransformRule(Name(c.Field), Name(c.Op), c.A, c.B))]
                    : null,
                o.Operations is { } operations
                    ? [..operations.Select(static p => new EngineTransformRule(Name(p.Field), Name(p.Op), p.A, p.B))]
                    : null,
                o.Taps is { } taps ? [..taps.Select(static t => new EngineDelayTap(t.DelayTicks, t.Transpose, t.Velocity))] : null,
                o.Chord is { } chord ? [..chord] : null,
                slot
            );
        }

        return new(objects);
    }

    /// <summary>
    ///     The objects in an order in which every cable goes from an earlier to a later one: the project's order, each
    ///     object as early as its inputs allow (Kahn's algorithm, ties in the project's order). The edits keep the graph
    ///     free of cycles; an object on one (a damaged document) is left out.
    /// </summary>
    public static IReadOnlyList<EnvironmentObject> Order(MidiEnvironment environment) {
        var objects = environment.Objects;
        var position = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < objects.Count; i++) {
            position.TryAdd(objects[i].Id, i);
        }

        var incoming = new int[objects.Count];
        var outgoing = new List<int>[objects.Count];
        for (var i = 0; i < objects.Count; i++) {
            outgoing[i] = [];
        }

        foreach (var cable in environment.Cables) {
            if (position.TryGetValue(cable.FromObjectId, out var from) && position.TryGetValue(cable.ToObjectId, out var to)) {
                outgoing[from].Add(to);
                incoming[to]++;
            }
        }

        var ready = new SortedSet<int>(Enumerable.Range(0, objects.Count).Where(i => incoming[i] == 0));
        var ordered = new List<EnvironmentObject>(objects.Count);
        while (ready.Count > 0) {
            var next = ready.Min;
            ready.Remove(next);
            ordered.Add(objects[next]);
            foreach (var to in outgoing[next]) {
                if (--incoming[to] == 0) {
                    ready.Add(to);
                }
            }
        }

        return ordered;
    }

    /// <summary>The engine track a track input plays, or −1 (none, or one that takes no MIDI).</summary>
    static int EngineTrack(ProjectState state, EngineIds ids, string? trackId) =>
        trackId is not null && state.FindTrack(trackId) is { } track && Stacks.IsPlayable(track) && Edits.HoldsMidi(track)
            ? ids.Track(track.Id)
            : -1;

    /// <summary>What a control target drives: its address on its strip (null: the master), when the strip has it.</summary>
    static EngineAutomationTarget? Control(ProjectState state, EngineIds ids, EnvironmentObject o) {
        if (o.Address is not { } address || !Edits.TargetExists(state, o.TrackId, address)) {
            return null;
        }

        if (o.TrackId is null) {
            return AutomationEvents.Describe(-1, AutomationEvents.MasterTrack, address);
        }

        return state.FindTrack(o.TrackId) is { } track && Stacks.IsPlayable(track)
            ? AutomationEvents.Describe(-1, ids.Track(track.Id), address)
            : null;
    }

    /// <summary>An enum value's contract (JSON) name: its name with a lower-case first letter.</summary>
    static string Name<T>(T value) where T : struct, Enum {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}
