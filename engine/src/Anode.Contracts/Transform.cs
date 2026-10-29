using System.Text.Json.Serialization;

namespace Anode.Contracts;

/// <summary>
///     What a Transform condition tests or an operation changes (Logic's MIDI Transform window; docs/08-phase-7.md).
///     For a note: its position (project ticks), length (ticks), channel (1…16), pitch (0…127) and velocity (1…127).
///     For a controller event, as in Logic: <c>pitch</c> is its first data byte (a control change's controller, a poly
///     pressure's key; 0 otherwise) and <c>velocity</c> its value (a pitch bend's −8192…8191); its length is 0.
///     <c>status</c> is the event's MIDI status (the status byte's high nibble as a number: 144 note, 160 poly
///     pressure, 176 control change, 192 program change, 208 channel pressure, 224 pitch bend) and
///     <c>subposition</c> its position within its bar (ticks from the bar line); both are conditions only.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<TransformField>))]
public enum TransformField {
    [JsonStringEnumMemberName("position")]
    Position,

    [JsonStringEnumMemberName("length")]
    Length,

    [JsonStringEnumMemberName("channel")]
    Channel,

    [JsonStringEnumMemberName("pitch")]
    Pitch,

    [JsonStringEnumMemberName("velocity")]
    Velocity,

    [JsonStringEnumMemberName("status")]
    Status,

    [JsonStringEnumMemberName("subposition")]
    Subposition
}

/// <summary>How a condition compares its field with <c>a</c> (and <c>b</c>); <c>all</c> matches every event.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TransformConditionOp>))]
public enum TransformConditionOp {
    [JsonStringEnumMemberName("all")]
    All,

    [JsonStringEnumMemberName("equal")]
    Equal,

    [JsonStringEnumMemberName("unequal")]
    Unequal,

    [JsonStringEnumMemberName("lessEqual")]
    LessEqual,

    [JsonStringEnumMemberName("greaterEqual")]
    GreaterEqual,

    /// <summary><c>a</c> ≤ field ≤ <c>b</c>.</summary>
    [JsonStringEnumMemberName("inside")]
    Inside,

    /// <summary>field &lt; <c>a</c> or field &gt; <c>b</c>.</summary>
    [JsonStringEnumMemberName("outside")]
    Outside
}

/// <summary>
///     A condition on one field of an event. A set's conditions are ANDed; a field without a condition (or with
///     <c>all</c>) matches everything. <c>b</c> is used by <c>inside</c> and <c>outside</c> only.
/// </summary>
public sealed record TransformCondition(TransformField Field, TransformConditionOp Op, double A, double B = 0);

/// <summary>
///     What an operation does to its field (value x, result clamped to the field's range and rounded to an integer).
///     <c>thru</c> leaves it; <c>fix</c> sets a; <c>add</c>/<c>subtract</c> x ± a; <c>min</c> max(x, a) (raises to at
///     least a); <c>max</c> min(x, a) (lowers to at most a); <c>flip</c> mirrors x around a (2a − x); <c>multiply</c>/
///     <c>divide</c> x × a, x ÷ a; <c>scale</c> x × a + b; <c>range</c> clamps to a…b; <c>random</c> a uniform value in
///     a…b; <c>plusMinusRandom</c> x plus a uniform value in −a…a; <c>quantize</c> to the nearest multiple of a;
///     <c>exponent</c> curves x over its range by exponent a (the field's range normalised to 0…1, raised to a, scaled
///     back); <c>crescendo</c> runs linearly from a at the first selected event's position to b at the last's;
///     <c>relativeCrescendo</c> adds that ramp to x; <c>useMap</c> looks x (0…127) up in the set's <c>map</c>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<TransformOperationKind>))]
public enum TransformOperationKind {
    [JsonStringEnumMemberName("thru")]
    Thru,

    [JsonStringEnumMemberName("fix")]
    Fix,

    [JsonStringEnumMemberName("add")]
    Add,

    [JsonStringEnumMemberName("subtract")]
    Subtract,

    [JsonStringEnumMemberName("min")]
    Min,

    [JsonStringEnumMemberName("max")]
    Max,

    [JsonStringEnumMemberName("flip")]
    Flip,

    [JsonStringEnumMemberName("multiply")]
    Multiply,

    [JsonStringEnumMemberName("divide")]
    Divide,

    [JsonStringEnumMemberName("scale")]
    Scale,

    [JsonStringEnumMemberName("range")]
    Range,

    [JsonStringEnumMemberName("random")]
    Random,

    [JsonStringEnumMemberName("plusMinusRandom")]
    PlusMinusRandom,

    [JsonStringEnumMemberName("quantize")]
    Quantize,

    [JsonStringEnumMemberName("exponent")]
    Exponent,

    [JsonStringEnumMemberName("crescendo")]
    Crescendo,

    [JsonStringEnumMemberName("relativeCrescendo")]
    RelativeCrescendo,

    [JsonStringEnumMemberName("useMap")]
    UseMap
}

/// <summary>
///     An operation on one field (<c>position</c>, <c>length</c>, <c>channel</c>, <c>pitch</c> or <c>velocity</c>; not
///     <c>status</c> or <c>subposition</c>). A set's operations apply to each selected event in field order: position,
///     length, channel, pitch, velocity, whatever their order in the list; at most one per field.
/// </summary>
public sealed record TransformOperation(TransformField Field, TransformOperationKind Op, double A = 0, double B = 0);

/// <summary>
///     What applying a set does: <c>selectOnly</c> selects the matching events in the editor and changes nothing (the
///     editor runs it itself; the <c>transform</c> command refuses it); <c>operateOnly</c> changes the matching events;
///     <c>selectAndOperate</c> changes them and leaves them selected.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<TransformMode>))]
public enum TransformMode {
    [JsonStringEnumMemberName("selectOnly")]
    SelectOnly,

    [JsonStringEnumMemberName("operateOnly")]
    OperateOnly,

    [JsonStringEnumMemberName("selectAndOperate")]
    SelectAndOperate
}

/// <summary>
///     A MIDI Transform set: conditions (ANDed), operations, the 128-step <c>map</c> that <c>useMap</c> reads (128
///     values 0…127; null: the identity), the mode, and the seed of its random operations: each event draws from a
///     generator seeded by <c>seed</c> and the event's id, so applying a set is deterministic. Factory presets are
///     sets whose ids start with <c>factory.</c> (<c>factory.humanize</c>, <c>factory.quantize</c> …;
///     <c>TransformEngine.Presets</c>); the user's are saved in the library (<c>/library/transform-sets</c>).
/// </summary>
public sealed record TransformSet(
    string Id,
    string Name,
    IReadOnlyList<TransformCondition> Conditions,
    IReadOnlyList<TransformOperation> Operations,
    IReadOnlyList<int>? Map,
    TransformMode Mode,
    long Seed
);
