using System.Text.Json.Serialization;

namespace Anode.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<SmartControlsLayout>))]
public enum SmartControlsLayout {
    /// <summary>Knobs <c>knob1</c>…<c>knob8</c>, two switches and the pad.</summary>
    [JsonStringEnumMemberName("knobs8")]
    Knobs8,

    /// <summary>Knobs <c>knob1</c>…<c>knob12</c>, two switches and the pad.</summary>
    [JsonStringEnumMemberName("knobs12")]
    Knobs12
}

[JsonConverter(typeof(JsonStringEnumConverter<SmartControlKind>))]
public enum SmartControlKind {
    [JsonStringEnumMemberName("knob")]
    Knob,

    /// <summary>A switch: its value is 0 or 1.</summary>
    [JsonStringEnumMemberName("switch")]
    Switch,

    /// <summary>An axis of the XY pad (<c>padX</c> or <c>padY</c>).</summary>
    [JsonStringEnumMemberName("pad")]
    Pad
}

/// <summary>A point of a mapping graph: input <c>x</c> (0…1) gives output <c>y</c> (0…1).</summary>
public sealed record GraphPoint(double X, double Y);

/// <summary>
///     A Smart Control's mapping to one parameter. <c>target</c> addresses it as automation lanes do (<c>volume</c>,
///     <c>pan</c>, <c>send:{sendId}</c>, <c>instrument:{parameterId}</c>, <c>insert:{insertId}:{parameterId}</c>,
///     <c>midiEffect:{slotId}:{parameterId}</c>; see <see cref="AutomationLane" />). The control's value goes through
///     <c>graph</c> (piecewise linear, 2–16 points sorted by x, the first at x 0 and the last at x 1; the default is
///     the line (0, 0)–(1, 1)), is inverted (1 − y) when <c>invert</c>, then scaled to <c>min</c> … <c>max</c> in the
///     parameter's unit.
/// </summary>
public sealed record SmartMapping(string Target, double Min, double Max, bool Invert, IReadOnlyList<GraphPoint> Graph);

/// <summary>
///     A Smart Control: its id (<c>knob1</c>…<c>knob12</c>, <c>switch1</c>, <c>switch2</c>, <c>padX</c>, <c>padY</c>),
///     name, kind, value (0…1) and mappings (any number). <c>savedValue</c> is the other side of Compare: while not
///     comparing, the value the project was last saved with (null: unchanged since); while comparing, the edited value
///     that Compare will restore.
/// </summary>
public sealed record SmartControl(
    string Id,
    string Name,
    SmartControlKind Kind,
    double Value,
    double? SavedValue,
    IReadOnlyList<SmartMapping> Mappings
);

/// <summary>
///     A track's Smart Controls (docs/07-phase-6.md): the layout and its controls in order (knobs, then
///     <c>switch1</c>, <c>switch2</c>, then <c>padX</c>, <c>padY</c>: every control of the layout); the arpeggiator
///     button (null on a track without MIDI FX; on, the track's first MIDI FX slot is an unbypassed
///     <c>anode.arpeggiator</c>); and whether Compare shows the saved values.
/// </summary>
/// <remarks>
///     A control change applies every mapping's parameter change, coalesced into one undo step as <c>setParameter</c>
///     is. Automation of a control (lane target <c>smartControl:{controlId}</c>) compiles to automation of every mapped
///     parameter, sampled as curves are. Automatic mapping takes the plugins' macro hints in order (the instrument's,
///     then each insert's), up to the layout's knobs. Compare swaps every control's value and saved value (applying the
///     parameters) without an undo step.
/// </remarks>
public sealed record SmartControls(
    SmartControlsLayout Layout,
    IReadOnlyList<SmartControl> Controls,
    bool? Arpeggiator,
    bool Comparing
);
