using Anode.Contracts;
using System.Collections.Immutable;

namespace Anode.Model.Editing;

/// <summary>
///     Smart Controls (docs/07-phase-6.md): a track's layout of knobs, switches and an XY pad, each control mapped to any
///     number of the track's parameters (<see cref="SmartMath" />). A control change applies its mappings' parameters;
///     automatic mapping follows the plugins' macro hints; Compare swaps saved and edited values.
/// </summary>
public static partial class Edits {
    public const int MaxSmartMappings = 32;
    public const int MaxGraphPoints = 16;
    public const string ArpeggiatorId = "anode.arpeggiator";
    const string SmartControlPrefix = "smartControl:";

    /// <summary>The control ids of a layout, in order: knobs, the two switches, the pad's two axes.</summary>
    public static IReadOnlyList<string> ControlIds(SmartControlsLayout layout) => [
        ..Enumerable.Range(1, layout == SmartControlsLayout.Knobs12 ? 12 : 8).Select(static n => $"knob{n}"),
        "switch1",
        "switch2",
        "padX",
        "padY"
    ];

    /// <summary>The kind of a control by its id.</summary>
    public static SmartControlKind SmartControlKindOf(string controlId) => KindOf(controlId);

    static SmartControlKind KindOf(string controlId) =>
        controlId.StartsWith("knob", StringComparison.Ordinal)
            ? SmartControlKind.Knob
            : controlId.StartsWith("switch", StringComparison.Ordinal)
                ? SmartControlKind.Switch
                : SmartControlKind.Pad;

    /// <summary>A track's Smart Controls, or the ones it starts with (8 knobs, unnamed, at 0, unmapped).</summary>
    public static SmartControls SmartControlsOf(Track track) =>
        track.SmartControls
        ?? new(
            SmartControlsLayout.Knobs8,
            [
                ..ControlIds(SmartControlsLayout.Knobs8)
                    .Select(static id => new SmartControl(id, string.Empty, KindOf(id), 0, null, []))
            ],
            ArpeggiatorState(track),
            false
        );

    /// <summary>The arpeggiator button: null on a track without MIDI FX; on when its first slot is an unbypassed one.</summary>
    static bool? ArpeggiatorState(Track track) =>
        IsMidiKind(track.Kind)
            ? track.Strip.MidiEffects is [{ PluginId: ArpeggiatorId, Bypassed: false }, ..]
            : null;

    /// <summary>A track with its Smart Controls' arpeggiator button following its MIDI FX (when they are set up).</summary>
    static Track SyncArpeggiator(Track track) =>
        track.SmartControls is { } smart && smart.Arpeggiator != ArpeggiatorState(track)
            ? track with { SmartControls = smart with { Arpeggiator = ArpeggiatorState(track) } }
            : track;

    static Track SmartTrackOf(ProjectState state, string? id) {
        var track = TrackOf(state, id ?? string.Empty);
        return HasAudioStrip(track.Kind)
            ? track
            : throw new EditException($"A {Kind(track.Kind)} track has no Smart Controls.");
    }

    static List<DeltaOp> SetSmartControls(
        ProjectState state,
        SetSmartControlsCommand c,
        IIdSource ids,
        PluginCatalog plugins
    ) {
        var track = SmartTrackOf(state, c.TrackId);
        var smart = SmartControlsOf(track);
        var ops = new List<DeltaOp>();
        if (c.Layout is { } layout) {
            if (!Enum.IsDefined(layout)) {
                throw new EditException($"Unknown Smart Controls layout {layout}.");
            }

            var kept = smart.Controls.ToDictionary(static x => x.Id, StringComparer.Ordinal);
            var controlIds = ControlIds(layout);
            smart = smart with {
                Layout = layout,
                Controls = [
                    ..controlIds.Select(id => kept.GetValueOrDefault(id)
                        ?? new SmartControl(id, string.Empty, KindOf(id), 0, null, [])
                    )
                ]
            };
            // The lanes of controls the layout drops go with them.
            ops.AddRange(
                LanesFor(state, track.Id, SmartControlPrefix)
                    .Where(l => !controlIds.Contains(l.Target[SmartControlPrefix.Length..]))
                    .Select(static l => new AutomationLaneRemoved(l.Id))
            );
        }

        var changed = track with { SmartControls = smart };
        if (c.Arpeggiator is { } on) {
            if (!IsMidiKind(track.Kind)) {
                throw new EditException($"Track '{track.Name}' has no MIDI FX: it has no arpeggiator.");
            }

            CheckNotFrozen(track);
            var slots = track.Strip.MidiEffects;
            if (slots is [{ PluginId: ArpeggiatorId } first, ..]) {
                slots = [first with { Bypassed = !on }, ..slots.Skip(1)];
            } else if (on) {
                if (slots.Count >= MaxInserts) {
                    throw new EditException($"A channel strip holds at most {MaxInserts} MIDI FX.");
                }

                var plugin = plugins.Find(ArpeggiatorId);
                slots = [
                    new Insert(
                        ids.Next(IdPrefix.Insert),
                        ArpeggiatorId,
                        plugin?.Name ?? "Arpeggiator",
                        false,
                        plugin is null ? PluginCatalog.Sorted(null) : PluginCatalog.Defaults(plugin),
                        null
                    ),
                    ..slots
                ];
            }

            changed = changed with { Strip = changed.Strip with { MidiEffects = slots } };
        }

        ops.Add(new TrackUpserted(SyncArpeggiator(changed), state.IndexOfTrack(track.Id)));
        return ops;
    }

    static List<DeltaOp> SetSmartControl(ProjectState state, SetSmartControlCommand c, PluginCatalog plugins) {
        var track = SmartTrackOf(state, c.TrackId);
        var smart = SmartControlsOf(track);
        var control = ControlOf(smart, c.ControlId);
        var changed = control;
        if (c.Name is not null) {
            changed = changed with { Name = Text(c.Name, "control name", MaxNameLength) };
        }

        if (c.Value is { } value) {
            if (!(value is >= 0 and <= 1)) {
                throw new EditException($"A Smart Control's value is 0…1, not {value}.");
            }

            if (control.Kind == SmartControlKind.Switch) {
                value = value >= 0.5 ? 1 : 0;
            }

            // The saved side of Compare keeps the value the project had before the first change.
            changed = changed with {
                Value = value, SavedValue = smart.Comparing ? control.SavedValue : control.SavedValue ?? control.Value
            };
        }

        var withControl = track with { SmartControls = smart with { Controls = Replace(smart.Controls, changed) } };
        if (c.Value is not null) {
            withControl = Applied(withControl, changed, plugins);
        }

        return [new TrackUpserted(withControl, state.IndexOfTrack(track.Id))];
    }

    static List<DeltaOp> SetSmartMappings(ProjectState state, SetSmartMappingsCommand c, PluginCatalog plugins) {
        var track = SmartTrackOf(state, c.TrackId);
        var smart = SmartControlsOf(track);
        var control = ControlOf(smart, c.ControlId);
        if (c.Mappings is not { Count: <= MaxSmartMappings } mappings) {
            throw new EditException($"A Smart Control has at most {MaxSmartMappings} mappings.");
        }

        var checkedMappings = new List<SmartMapping>(mappings.Count);
        foreach (var mapping in mappings) {
            if (mapping?.Target is null) {
                throw new EditException("A mapping names its target.");
            }

            if (mapping.Target.StartsWith(SmartControlPrefix, StringComparison.Ordinal) || mapping.Target == "mute") {
                throw new EditException($"'{mapping.Target}' is not a parameter a Smart Control maps.");
            }

            var range = RangeOf(state, track, mapping.Target, plugins);
            if (!double.IsFinite(mapping.Min)
                || !double.IsFinite(mapping.Max)
                || mapping.Min < range.Min
                || mapping.Max > range.Max
                || mapping.Min > range.Max
                || mapping.Max < range.Min) {
                throw new EditException(
                    $"A mapping of '{mapping.Target}' ranges within {range.Min}…{range.Max}, not {mapping.Min}…{mapping.Max}."
                );
            }

            checkedMappings.Add(mapping with { Graph = CheckGraph(mapping.Graph) });
        }

        var changed = control with { Mappings = checkedMappings };
        return [
            new TrackUpserted(
                track with { SmartControls = smart with { Controls = Replace(smart.Controls, changed) } },
                state.IndexOfTrack(track.Id)
            )
        ];
    }

    /// <summary>A mapping graph: 2…16 points sorted by x, from x 0 to x 1, every y 0…1 (null: the default line).</summary>
    static IReadOnlyList<GraphPoint> CheckGraph(IReadOnlyList<GraphPoint>? graph) {
        if (graph is null || graph.Count == 0) {
            return SmartMath.Line;
        }

        if (graph.Count is < 2 or > MaxGraphPoints) {
            throw new EditException($"A mapping graph has 2…{MaxGraphPoints} points.");
        }

        for (var i = 0; i < graph.Count; i++) {
            var p = graph[i] ?? throw new EditException("A graph point is null.");
            if (!(p.X is >= 0 and <= 1) || !(p.Y is >= 0 and <= 1)) {
                throw new EditException("A graph point's x and y are 0…1.");
            }

            if (i > 0 && p.X < graph[i - 1].X) {
                throw new EditException("A graph's points are sorted by x.");
            }
        }

        return graph[0].X == 0 && graph[^1].X == 1
            ? [..graph]
            : throw new EditException("A graph starts at x 0 and ends at x 1.");
    }

    static List<DeltaOp> AutoMapSmartControls(
        ProjectState state,
        AutoMapSmartControlsCommand c,
        PluginCatalog plugins
    ) {
        var track = SmartTrackOf(state, c.TrackId);
        var smart = SmartControlsOf(track);
        var hints = MacroHints(track, plugins).ToList();
        var knobs = smart.Controls.Where(static x => x.Kind == SmartControlKind.Knob).ToArray();
        var controls = smart.Controls.ToList();
        for (var k = 0; k < knobs.Length; k++) {
            var knob = knobs[k];
            SmartControl mapped;
            if (k < hints.Count) {
                var (target, info, current) = hints[k];
                var span = info.Max - info.Min;
                var value = span > 0 ? Math.Clamp((current - info.Min) / span, 0, 1) : 0;
                mapped = knob with {
                    Name = info.Name.Length > MaxNameLength ? info.Name[..MaxNameLength] : info.Name,
                    Value = value,
                    SavedValue = null,
                    Mappings = [new(target, info.Min, info.Max, false, SmartMath.Line)]
                };
            } else {
                mapped = knob with { Name = string.Empty, Mappings = [] };
            }

            controls[controls.FindIndex(x => x.Id == knob.Id)] = mapped;
        }

        return [
            new TrackUpserted(
                track with { SmartControls = smart with { Controls = controls } },
                state.IndexOfTrack(track.Id)
            )
        ];
    }

    /// <summary>
    ///     The parameters a track's plugins hint as macros, in order: the instrument's by ascending hint, then each
    ///     insert's (in slot order) by ascending hint; with their lane targets and current values.
    /// </summary>
    public static IEnumerable<(string Target, ParameterInfo Info, double Value)> MacroHints(
        Track track,
        PluginCatalog plugins
    ) {
        if (track.Instrument is { } slot && plugins.Find(slot.PluginId) is { } instrument) {
            foreach (var p in Hinted(instrument)) {
                yield return ($"instrument:{p.Id}", p, slot.Parameters.GetValueOrDefault(p.Id, p.Default));
            }
        }

        foreach (var insert in track.Strip.Inserts) {
            foreach (var p in plugins.Find(insert.PluginId) is { } plugin ? Hinted(plugin) : []) {
                yield return ($"insert:{insert.Id}:{p.Id}", p, insert.Parameters.GetValueOrDefault(p.Id, p.Default));
            }
        }
    }

    /// <summary>A plugin's macro-hinted parameters by ascending hint (never the host's tempo).</summary>
    static IEnumerable<ParameterInfo> Hinted(PluginInfo plugin) =>
        plugin.Parameters.Where(static p => p.Macro is not null && p.Id != HostTempoParameter)
            .OrderBy(static p => p.Macro);

    static List<DeltaOp> CompareSmartControls(
        ProjectState state,
        CompareSmartControlsCommand c,
        PluginCatalog plugins
    ) {
        var track = SmartTrackOf(state, c.TrackId);
        var smart = SmartControlsOf(track);
        if (smart.Comparing == c.On) {
            return [];
        }

        var changed = track;
        var controls = new List<SmartControl>(smart.Controls.Count);
        var swapped = new List<SmartControl>();
        foreach (var control in smart.Controls) {
            if (control.SavedValue is { } other) {
                var swap = control with { Value = other, SavedValue = control.Value };
                controls.Add(swap);
                swapped.Add(swap);
            } else {
                controls.Add(control);
            }
        }

        changed = changed with { SmartControls = smart with { Controls = controls, Comparing = c.On } };
        foreach (var control in swapped) {
            changed = Applied(changed, control, plugins);
        }

        return [new TrackUpserted(changed, state.IndexOfTrack(track.Id))];
    }

    static SmartControl ControlOf(SmartControls smart, string? id) =>
        smart.Controls.FirstOrDefault(x => x.Id == id)
        ?? throw new EditException($"The Smart Controls have no control '{id}'.");

    static IReadOnlyList<SmartControl> Replace(IReadOnlyList<SmartControl> controls, SmartControl control) =>
        [..controls.Select(x => x.Id == control.Id ? control : x)];

    /// <summary>A track with every parameter <paramref name="control" /> maps set from its value (missing ones skipped).</summary>
    public static Track Applied(Track track, SmartControl control, PluginCatalog plugins) {
        foreach (var mapping in control.Mappings) {
            track = WithParameter(track, mapping.Target, SmartMath.Map(mapping, control.Value), plugins);
        }

        return track;
    }

    /// <summary>
    ///     A track with lane target <paramref name="target" /> set to <paramref name="value" /> (clamped to its range,
    ///     rounded to its steps); the track itself when the target is not there.
    /// </summary>
    public static Track WithParameter(Track track, string target, double value, PluginCatalog plugins) {
        var strip = track.Strip;
        switch (target.Split(':', 3)) {
            case ["volume"]:
                return track with { Strip = strip with { VolumeDb = Math.Clamp(value, Units.SilenceDb, MaxVolumeDb) } };
            case ["pan"]:
                return track with { Strip = strip with { Pan = Math.Clamp(value, -1, 1) } };
            case ["send", var id] when strip.Sends.Any(s => s.Id == id):
                var level = Math.Clamp(value, Units.SilenceDb, MaxVolumeDb);
                return track with {
                    Strip = strip with {
                        Sends = [..strip.Sends.Select(s => s.Id == id ? s with { LevelDb = level } : s)]
                    }
                };
            case ["instrument", var parameter] when track.Instrument is { } slot:
                return track with {
                    Instrument = slot with {
                        Parameters = PluginCatalog.Sorted(slot.Parameters)
                            .SetItem(parameter, Snapped(plugins, slot.PluginId, parameter, value))
                    }
                };
            case ["insert", var id, var parameter] when strip.Inserts.Any(i => i.Id == id):
                return track with {
                    Strip = strip with { Inserts = WithSlotParameter(strip.Inserts, id, parameter, value, plugins) }
                };
            case ["midiEffect", var id, var parameter] when strip.MidiEffects.Any(i => i.Id == id):
                return track with {
                    Strip = strip with {
                        MidiEffects = WithSlotParameter(strip.MidiEffects, id, parameter, value, plugins)
                    }
                };
            default:
                return track;
        }
    }

    static IReadOnlyList<Insert> WithSlotParameter(
        IReadOnlyList<Insert> slots,
        string id,
        string parameter,
        double value,
        PluginCatalog plugins
    ) =>
        [
            ..slots.Select(i => i.Id == id
                    ? i with {
                        Parameters = PluginCatalog.Sorted(i.Parameters)
                            .SetItem(parameter, Snapped(plugins, i.PluginId, parameter, value))
                    }
                    : i
            )
        ];

    /// <summary>A value clamped to a parameter's range and rounded to its steps (as it is when it is not catalogued).</summary>
    static double Snapped(PluginCatalog plugins, string pluginId, string parameter, double value) {
        if (plugins.Find(pluginId)?.Parameters.FirstOrDefault(p => p.Id == parameter) is not { } info) {
            return value;
        }

        value = Math.Clamp(value, info.Min, info.Max);
        if (info.Steps > 0 && info.Max > info.Min) {
            var step = (info.Max - info.Min) / info.Steps;
            value = Math.Clamp(info.Min + Math.Round((value - info.Min) / step) * step, info.Min, info.Max);
        }

        return value;
    }

    static string Text(string text, string what, int max) =>
        text.Length > max ? throw new EditException($"A {what} has at most {max} characters.") : text.Trim();
}

/// <summary>
///     Smart Control maths (docs/07-phase-6.md): a control's value (0…1) through a mapping's graph (piecewise linear),
///     inverted when it says so, then scaled into the mapping's <c>min … max</c>.
/// </summary>
public static class SmartMath {
    /// <summary>The default graph: the line from (0, 0) to (1, 1).</summary>
    public static IReadOnlyList<GraphPoint> Line { get; } = [new(0, 0), new(1, 1)];

    /// <summary>The graph's output at <paramref name="x" /> (clamped to 0…1); a vertical step takes the later point.</summary>
    public static double Graph(IReadOnlyList<GraphPoint> graph, double x) {
        if (graph.Count == 0) {
            return Math.Clamp(x, 0, 1);
        }

        x = Math.Clamp(x, 0, 1);
        if (x <= graph[0].X) {
            return graph[0].Y;
        }

        for (var i = 1; i < graph.Count; i++) {
            var (a, b) = (graph[i - 1], graph[i]);
            if (x < b.X || (x == b.X && i == graph.Count - 1)) {
                return b.X == a.X ? b.Y : a.Y + (b.Y - a.Y) * (x - a.X) / (b.X - a.X);
            }
        }

        return graph[^1].Y;
    }

    /// <summary>The parameter value a mapping gives a control value.</summary>
    public static double Map(SmartMapping mapping, double value) {
        var y = Graph(mapping.Graph, value);
        if (mapping.Invert) {
            y = 1 - y;
        }

        return mapping.Min + (mapping.Max - mapping.Min) * y;
    }
}
