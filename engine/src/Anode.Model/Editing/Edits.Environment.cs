using Anode.Contracts;

namespace Anode.Model.Editing;

/// <summary>
///     Phase 11's Environment (docs/12-phase-11.md, docs/adr/0015-environment.md): layers, objects and cables, every
///     change one <c>environmentChanged</c> of the whole document. The document stays valid: objects of known kinds on
///     layers that exist, each with only the settings its kind uses, in their ranges; at most one physical input without
///     a port and one sequencer input; cables between outlets and inlets their objects' kinds have, each once, and no
///     cycles.
/// </summary>
public static partial class Edits {
    public const int MaxEnvironmentLayers = 64;
    public const int MaxEnvironmentObjects = 1024;
    public const int MaxEnvironmentCables = 4096;
    public const int MaxDelayTaps = 16;
    public const long MaxDelayTicks = 3840;
    public const int MaxTransformerRules = 16;
    public const int MaxChordNotes = 16;
    public const double MaxCanvas = 1_000_000;

    static DeltaOp[]? EnvironmentOps(ProjectState state, EditCommand command, IIdSource ids) =>
        command switch {
            AddEnvironmentLayerCommand c => AddEnvironmentLayer(state, c, ids),
            SetEnvironmentLayerCommand c => SetEnvironmentLayer(state, c),
            RemoveEnvironmentLayerCommand c => RemoveEnvironmentLayer(state, c),
            AddEnvironmentObjectCommand c => AddEnvironmentObject(state, c, ids),
            SetEnvironmentObjectCommand c => SetEnvironmentObject(state, c),
            RemoveEnvironmentObjectsCommand c => RemoveEnvironmentObjects(state, c),
            ConnectEnvironmentCommand c => ConnectEnvironment(state, c, ids),
            DisconnectEnvironmentCommand c => DisconnectEnvironment(state, c),
            _ => null
        };

    static DeltaOp[] AddEnvironmentLayer(ProjectState state, AddEnvironmentLayerCommand c, IIdSource ids) {
        var environment = state.Environment;
        if (environment.Layers.Count >= MaxEnvironmentLayers) {
            throw new EditException($"The Environment holds at most {MaxEnvironmentLayers} layers.");
        }

        var layer = new EnvironmentLayer(
            ids.Next(IdPrefix.EnvironmentLayer),
            c.Name is null ? NextLayerName(environment) : Name(c.Name, "layer name")
        );
        return [new EnvironmentChanged(environment with { Layers = [..environment.Layers, layer] })];
    }

    static string NextLayerName(MidiEnvironment environment) {
        for (var n = environment.Layers.Count + 1;; n++) {
            var name = $"Layer {n}";
            if (environment.Layers.All(l => l.Name != name)) {
                return name;
            }
        }
    }

    static DeltaOp[] SetEnvironmentLayer(ProjectState state, SetEnvironmentLayerCommand c) {
        var environment = state.Environment;
        var layer = LayerOf(environment, c.LayerId);
        var changed = layer with { Name = c.Name is null ? layer.Name : Name(c.Name, "layer name") };
        var layers = environment.Layers.Where(l => l.Id != layer.Id).ToList();
        var index = c.Index ?? environment.Layers.ToList().FindIndex(l => l.Id == layer.Id);
        if (index < 0 || index > layers.Count) {
            throw new EditException($"Layer index {index} is outside 0…{layers.Count}.");
        }

        layers.Insert(index, changed);
        return [new EnvironmentChanged(environment with { Layers = layers })];
    }

    static DeltaOp[] RemoveEnvironmentLayer(ProjectState state, RemoveEnvironmentLayerCommand c) {
        var environment = state.Environment;
        var layer = LayerOf(environment, c.LayerId);
        var gone = environment.Objects.Where(o => o.LayerId == layer.Id).Select(static o => o.Id).ToHashSet();
        return [
            new EnvironmentChanged(
                Without(environment, gone) with { Layers = [..environment.Layers.Where(l => l.Id != layer.Id)] }
            )
        ];
    }

    static DeltaOp[] AddEnvironmentObject(ProjectState state, AddEnvironmentObjectCommand c, IIdSource ids) {
        var environment = state.Environment;
        if (!Enum.IsDefined(c.Kind)) {
            throw new EditException($"Unknown Environment object kind {c.Kind}.");
        }

        if (environment.Objects.Count >= MaxEnvironmentObjects) {
            throw new EditException($"The Environment holds at most {MaxEnvironmentObjects} objects.");
        }

        string layerId;
        if (c.LayerId is not null) {
            layerId = LayerOf(environment, c.LayerId).Id;
        } else if (environment.Layers.Count > 0) {
            layerId = environment.Layers[0].Id;
        } else {
            var first = new EnvironmentLayer(ids.Next(IdPrefix.EnvironmentLayer), "Layer 1");
            environment = environment with { Layers = [first] };
            layerId = first.Id;
        }

        var info = EnvironmentObjectKinds.Of(c.Kind);
        var added = CheckEnvironmentObject(
            state,
            environment,
            new(
                ids.Next(IdPrefix.EnvironmentObject),
                layerId,
                c.Kind,
                c.Name is null ? info.Name : Name(c.Name, "object name"),
                c.X,
                c.Y,
                WithDefaults(info, c.Parameters),
                Blank(c.Port),
                Blank(c.TrackId),
                Blank(c.Address),
                c.Conditions,
                c.Operations,
                c.Taps,
                c.Chord is { Count: 0 } ? null : c.Chord
            )
        );
        return [new EnvironmentChanged(environment with { Objects = [..environment.Objects, added] })];
    }

    static DeltaOp[] SetEnvironmentObject(ProjectState state, SetEnvironmentObjectCommand c) {
        var environment = state.Environment;
        var old = ObjectOf(environment, c.ObjectId);
        var parameters = old.Parameters.ToDictionary(StringComparer.Ordinal);
        foreach (var (id, value) in c.Parameters ?? new Dictionary<string, double>()) {
            parameters[id] = value;
        }

        var changed = old with {
            Name = c.Name is null ? old.Name : Name(c.Name, "object name"),
            LayerId = c.LayerId is null ? old.LayerId : LayerOf(environment, c.LayerId).Id,
            X = c.X ?? old.X,
            Y = c.Y ?? old.Y,
            Parameters = parameters,
            Port = c.Port is null ? old.Port : Blank(c.Port),
            TrackId = c.TrackId is null ? old.TrackId : Blank(c.TrackId),
            Address = c.Address is null ? old.Address : Blank(c.Address),
            Conditions = c.Conditions ?? old.Conditions,
            Operations = c.Operations ?? old.Operations,
            Taps = c.Taps ?? old.Taps,
            Chord = c.Chord is null ? old.Chord : c.Chord.Count == 0 ? null : c.Chord
        };
        var others = environment with { Objects = [..environment.Objects.Where(o => o.Id != old.Id)] };
        changed = CheckEnvironmentObject(state, others, changed);
        return [
            new EnvironmentChanged(
                environment with { Objects = [..environment.Objects.Select(o => o.Id == old.Id ? changed : o)] }
            )
        ];
    }

    static DeltaOp[] RemoveEnvironmentObjects(ProjectState state, RemoveEnvironmentObjectsCommand c) {
        if (c.ObjectIds is not { Count: > 0 }) {
            throw new EditException("Name at least one Environment object.");
        }

        var environment = state.Environment;
        var gone = c.ObjectIds.Select(id => ObjectOf(environment, id).Id).ToHashSet(StringComparer.Ordinal);
        return [new EnvironmentChanged(Without(environment, gone))];
    }

    static DeltaOp[] ConnectEnvironment(ProjectState state, ConnectEnvironmentCommand c, IIdSource ids) {
        var environment = state.Environment;
        var cable = new EnvironmentCable(
            ids.Next(IdPrefix.EnvironmentCable),
            ObjectOf(environment, c.FromObjectId).Id,
            c.Outlet,
            ObjectOf(environment, c.ToObjectId).Id,
            c.Inlet
        );
        if (environment.Cables.Count >= MaxEnvironmentCables) {
            throw new EditException($"The Environment holds at most {MaxEnvironmentCables} cables.");
        }

        CheckCable(environment, cable);
        return [new EnvironmentChanged(environment with { Cables = [..environment.Cables, cable] })];
    }

    static DeltaOp[] DisconnectEnvironment(ProjectState state, DisconnectEnvironmentCommand c) {
        if (c.CableIds is not { Count: > 0 }) {
            throw new EditException("Name at least one cable.");
        }

        var environment = state.Environment;
        var gone = c.CableIds.ToHashSet(StringComparer.Ordinal);
        foreach (var id in gone) {
            if (environment.Cables.All(cable => cable.Id != id)) {
                throw new EditException($"No cable '{id}'.");
            }
        }

        return [
            new EnvironmentChanged(environment with { Cables = [..environment.Cables.Where(x => !gone.Contains(x.Id))] })
        ];
    }

    /// <summary>
    ///     A cable that may join <paramref name="environment" />: from an outlet and to an inlet its objects' kinds have,
    ///     between two objects, not there yet, and closing no cycle.
    /// </summary>
    static void CheckCable(MidiEnvironment environment, EnvironmentCable cable) {
        var from = ObjectOf(environment, cable.FromObjectId);
        var to = ObjectOf(environment, cable.ToObjectId);
        var outlets = EnvironmentObjectKinds.Of(from.Kind).Outlets;
        var inlets = EnvironmentObjectKinds.Of(to.Kind).Inlets;
        if (cable.Outlet < 0 || cable.Outlet >= outlets.Count) {
            throw new EditException($"A {from.Name} object has outlets 0…{outlets.Count - 1}, not {cable.Outlet}.");
        }

        if (cable.Inlet < 0 || cable.Inlet >= inlets.Count) {
            throw new EditException($"A {to.Name} object has inlets 0…{inlets.Count - 1}, not {cable.Inlet}.");
        }

        if (environment.Cables.Any(x => x.FromObjectId == cable.FromObjectId
                && x.Outlet == cable.Outlet
                && x.ToObjectId == cable.ToObjectId
                && x.Inlet == cable.Inlet
            )) {
            throw new EditException("That cable is there already.");
        }

        if (from.Id == to.Id || Reaches(environment, to.Id, from.Id)) {
            throw new EditException("That cable would close a loop.");
        }
    }

    /// <summary>Whether a path of cables leads from object <paramref name="from" /> to object <paramref name="to" />.</summary>
    static bool Reaches(MidiEnvironment environment, string from, string to) {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>([from]);
        while (stack.TryPop(out var at)) {
            if (at == to) {
                return true;
            }

            if (!seen.Add(at)) {
                continue;
            }

            foreach (var cable in environment.Cables) {
                if (cable.FromObjectId == at) {
                    stack.Push(cable.ToObjectId);
                }
            }
        }

        return false;
    }

    /// <summary>
    ///     An object as the Environment keeps it (<paramref name="environment" /> holding the others): a known kind on a
    ///     layer there is, a name and a place on the canvas, only the settings its kind uses, each in its range.
    /// </summary>
    static EnvironmentObject CheckEnvironmentObject(
        ProjectState state,
        MidiEnvironment environment,
        EnvironmentObject o
    ) {
        if (!Enum.IsDefined(o.Kind)) {
            throw new EditException($"Unknown Environment object kind {o.Kind}.");
        }

        _ = LayerOf(environment, o.LayerId);
        _ = Name(o.Name ?? string.Empty, "object name");
        if (!double.IsFinite(o.X) || !double.IsFinite(o.Y) || Math.Abs(o.X) > MaxCanvas || Math.Abs(o.Y) > MaxCanvas) {
            throw new EditException($"An object's place is within ±{MaxCanvas} on its canvas.");
        }

        var kind = o.Kind;
        Uses(o.Port is not null, kind is EnvironmentObjectKind.PhysicalInput or EnvironmentObjectKind.MidiPort, "port");
        Uses(
            o.TrackId is not null,
            kind is EnvironmentObjectKind.TrackInput or EnvironmentObjectKind.ControlTarget,
            "track"
        );
        Uses(o.Address is not null, kind == EnvironmentObjectKind.ControlTarget, "control address");
        Uses(o.Conditions is not null || o.Operations is not null, kind == EnvironmentObjectKind.Transformer, "rules");
        Uses(o.Taps is not null, kind == EnvironmentObjectKind.DelayLine, "taps");
        Uses(o.Chord is not null, kind == EnvironmentObjectKind.ChordMemorizer, "chord");
        if (o.Port is { Length: > MaxNameLength }) {
            throw new EditException($"A port's name has at most {MaxNameLength} characters.");
        }

        CheckSingle(environment, o);
        CheckParameters(o);
        if (o.TrackId is { } trackId) {
            var track = TrackOf(state, trackId);
            if (kind == EnvironmentObjectKind.TrackInput && !HoldsMidi(track)) {
                throw new EditException($"Track '{track.Name}' takes no MIDI: a track input plays a MIDI track.");
            }
        }

        if (o.Address is { } address && !TargetExists(state, o.TrackId, address)) {
            throw new EditException($"'{address}' is not a control of {(o.TrackId is null ? "the master" : "that track")}.");
        }

        CheckRules(o);
        if (o.Taps is { } taps
            && (taps.Count > MaxDelayTaps
                || taps.Any(static t => t is null
                    || t.DelayTicks is < 1 or > MaxDelayTicks
                    || t.Transpose is < -127 or > 127
                    || t.Velocity is < -127 or > 127
                ))) {
            throw new EditException(
                $"A delay line has at most {MaxDelayTaps} taps of 1…{MaxDelayTicks} ticks, transposed and offset by −127…127."
            );
        }

        if (o.Chord is { } chord
            && (chord.Count > MaxChordNotes || chord.Any(static n => n is < -127 or > 127) || chord.Distinct().Count() != chord.Count)) {
            throw new EditException($"A chord memoriser's chord is at most {MaxChordNotes} distinct intervals of −127…127.");
        }

        return o with {
            Parameters = PluginCatalog.Sorted(o.Parameters),
            Conditions = o.Conditions is null ? null : [..o.Conditions],
            Operations = o.Operations is null ? null : [..o.Operations],
            Taps = o.Taps is null ? null : [..o.Taps],
            Chord = o.Chord is null ? null : [..o.Chord]
        };

        void Uses(bool set, bool used, string what) {
            if (set && !used) {
                throw new EditException($"A {EnvironmentObjectKinds.Of(kind).Name} object has no {what}.");
            }
        }
    }

    /// <summary>At most one physical input without a port, and one sequencer input.</summary>
    static void CheckSingle(MidiEnvironment others, EnvironmentObject o) {
        if (o is { Kind: EnvironmentObjectKind.PhysicalInput, Port: null }
            && others.Objects.Any(x => x is { Kind: EnvironmentObjectKind.PhysicalInput, Port: null } && x.Id != o.Id)) {
            throw new EditException("The Environment has one physical input of every port; give this one a port.");
        }

        if (o.Kind == EnvironmentObjectKind.SequencerInput
            && others.Objects.Any(x => x.Kind == EnvironmentObjectKind.SequencerInput && x.Id != o.Id)) {
            throw new EditException("The Environment has one sequencer input.");
        }
    }

    /// <summary>
    ///     An object's numeric settings: its kind's, each in its range (the arpeggiator's and chord memoriser's are their
    ///     MIDI FX's, whose ranges the host clamps: any finite values).
    /// </summary>
    static void CheckParameters(EnvironmentObject o) {
        if (o.Parameters is null) {
            throw new EditException("An object has parameters (none: empty).");
        }

        var info = EnvironmentObjectKinds.Of(o.Kind);
        var midiEffect = o.Kind is EnvironmentObjectKind.Arpeggiator or EnvironmentObjectKind.ChordMemorizer;
        foreach (var (id, value) in o.Parameters) {
            if (!double.IsFinite(value) || id is not { Length: > 0 and <= MaxParameterIdLength }) {
                throw new EditException("An object's parameters have ids and finite values.");
            }

            if (midiEffect) {
                continue;
            }

            var parameter = info.Parameters.FirstOrDefault(p => p.Id == id)
                ?? throw new EditException($"A {info.Name} object has no parameter '{id}'.");
            if (value < parameter.Min || value > parameter.Max) {
                throw new EditException($"{parameter.Name} is {parameter.Min}…{parameter.Max}, not {value}.");
            }
        }

        if (o.Kind == EnvironmentObjectKind.Fader
            && o.Parameters.GetValueOrDefault("min", 0) > o.Parameters.GetValueOrDefault("max", 127)) {
            throw new EditException("A fader's minimum is at most its maximum.");
        }
    }

    /// <summary>
    ///     A transformer's rules: Phase 7's conditions on status, channel, pitch and velocity, and its operations on
    ///     channel, pitch and velocity (not the time-based ones, nor the map: the Environment has none).
    /// </summary>
    static void CheckRules(EnvironmentObject o) {
        TransformField[] fields = [
            TransformField.Status, TransformField.Channel, TransformField.Pitch, TransformField.Velocity
        ];
        foreach (var condition in o.Conditions ?? []) {
            if (condition is null
                || !fields.Contains(condition.Field)
                || !Enum.IsDefined(condition.Op)
                || !double.IsFinite(condition.A)
                || !double.IsFinite(condition.B)) {
                throw new EditException("A transformer's conditions are on status, channel, pitch or velocity.");
            }
        }

        foreach (var operation in o.Operations ?? []) {
            if (operation is null
                || operation.Field is not (TransformField.Channel or TransformField.Pitch or TransformField.Velocity)
                || !Enum.IsDefined(operation.Op)
                || operation.Op is TransformOperationKind.Crescendo
                    or TransformOperationKind.RelativeCrescendo
                    or TransformOperationKind.UseMap
                || !double.IsFinite(operation.A)
                || !double.IsFinite(operation.B)) {
                throw new EditException(
                    "A transformer's operations are on channel, pitch or velocity, and none is time-based or a map."
                );
            }
        }

        if ((o.Conditions?.Count ?? 0) > MaxTransformerRules
            || (o.Operations?.Count ?? 0) > MaxTransformerRules
            || (o.Operations ?? []).DistinctBy(static p => p.Field).Count() != (o.Operations?.Count ?? 0)) {
            throw new EditException(
                $"A transformer has at most {MaxTransformerRules} conditions and operations, one operation per field."
            );
        }
    }

    /// <summary>A kind's own parameters at their defaults, overridden by <paramref name="given" />.</summary>
    static Dictionary<string, double> WithDefaults(EnvironmentObjectKindInfo info, IReadOnlyDictionary<string, double>? given) {
        var parameters = info.Parameters.ToDictionary(static p => p.Id, static p => p.Default, StringComparer.Ordinal);
        foreach (var (id, value) in given ?? new Dictionary<string, double>()) {
            parameters[id] = value;
        }

        return parameters;
    }

    static string? Blank(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>The Environment without the objects <paramref name="gone" /> and every cable to or from them.</summary>
    static MidiEnvironment Without(MidiEnvironment environment, HashSet<string> gone) =>
        environment with {
            Objects = [..environment.Objects.Where(o => !gone.Contains(o.Id))],
            Cables = [
                ..environment.Cables.Where(c => !gone.Contains(c.FromObjectId) && !gone.Contains(c.ToObjectId))
            ]
        };

    /// <summary>
    ///     The Environment without references to track <paramref name="trackId" /> (a track input plays no track; a
    ///     control target drives nothing), or null when it has none.
    /// </summary>
    static MidiEnvironment? UnlinkedEnvironment(MidiEnvironment environment, string trackId) =>
        environment.Objects.Any(o => o.TrackId == trackId)
            ? environment with {
                Objects = [
                    ..environment.Objects.Select(o => o.TrackId != trackId
                            ? o
                            : o with { TrackId = null, Address = null }
                    )
                ]
            }
            : null;

    /// <summary>
    ///     The Environment as a package reads it: the objects and cables that pass the checks of the commands that make
    ///     them against the project read so far, in order (the first of each id); layers named and once each.
    /// </summary>
    public static MidiEnvironment StoredEnvironment(ProjectState state, MidiEnvironment? stored) {
        if (stored is null) {
            return MidiEnvironment.Empty;
        }

        var layers = (stored.Layers ?? [])
            .Where(static l => l?.Id is not null && l.Name is { Length: > 0 and <= MaxNameLength })
            .DistinctBy(static l => l.Id)
            .Take(MaxEnvironmentLayers)
            .ToList();
        var environment = MidiEnvironment.Empty with { Layers = layers };
        foreach (var o in stored.Objects ?? []) {
            if (o?.Id is null
                || environment.Objects.Count >= MaxEnvironmentObjects
                || environment.Objects.Any(x => x.Id == o.Id)) {
                continue;
            }

            try {
                environment = environment with {
                    Objects = [..environment.Objects, CheckEnvironmentObject(state, environment, o)]
                };
            } catch (EditException) {
                // an object the project cannot have
            }
        }

        foreach (var cable in stored.Cables ?? []) {
            if (cable?.Id is null
                || environment.Cables.Count >= MaxEnvironmentCables
                || environment.Cables.Any(x => x.Id == cable.Id)) {
                continue;
            }

            try {
                CheckCable(environment, cable);
                environment = environment with { Cables = [..environment.Cables, cable] };
            } catch (EditException) {
                // a cable to nothing, or one that would close a loop
            }
        }

        return environment;
    }

    static EnvironmentLayer LayerOf(MidiEnvironment environment, string? id) =>
        environment.Layers.FirstOrDefault(l => l.Id == id) ?? throw new EditException($"No Environment layer '{id}'.");

    static EnvironmentObject ObjectOf(MidiEnvironment environment, string? id) =>
        environment.Objects.FirstOrDefault(o => o.Id == id) ?? throw new EditException($"No Environment object '{id}'.");
}
