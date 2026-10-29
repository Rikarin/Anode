using Anode.Contracts;
using Anode.Model.Time;

namespace Anode.Model.Transform;

/// <summary>
///     What applying a Transform set did: every event given, in the order given (the matching ones changed, unless the
///     mode is <c>selectOnly</c>), and the ids of the events that matched its conditions.
/// </summary>
public sealed record TransformResult(
    IReadOnlyList<Note> Notes,
    IReadOnlyList<ControlEvent> Controls,
    IReadOnlyList<long> SelectedNoteIds,
    IReadOnlyList<long> SelectedControlIds
);

/// <summary>
///     The MIDI Transform engine (docs/08-phase-7.md, "Rules the contracts imply"): conditions ANDed; operations in the
///     order position, length, channel, pitch, velocity; random operations seeded by the set's seed and the event's id;
///     results clamped to each field's range (a note's length at least 1 tick, its position at least 0). Pure and
///     deterministic; the editor's TypeScript port matches it on the shared <c>TransformVectors.json</c>.
/// </summary>
public static class TransformEngine {
    /// <summary>
    ///     Applies <paramref name="set" /> to <paramref name="notes" /> and <paramref name="controls" />, whose positions
    ///     (<c>startTick</c>, <c>tick</c>) are project ticks here: the caller adds each region's start before and
    ///     subtracts it after, so position conditions, subposition and crescendos span regions.
    ///     <paramref name="signatures" /> places bar lines for <c>subposition</c>.
    /// </summary>
    public static TransformResult Apply(
        IReadOnlyList<Note> notes,
        IReadOnlyList<ControlEvent> controls,
        TransformSet set,
        SignatureMap signatures
    ) =>
        throw Pending.WorkstreamE();

    /// <summary>
    ///     The factory presets, as data, in menu order; their ids are <see cref="TransformPresetIds" />, with seed 0
    ///     (the editor's Transform window draws a new seed when it applies a random one).
    /// </summary>
    public static IReadOnlyList<TransformSet> Presets() => throw Pending.WorkstreamE();
}

/// <summary>The factory Transform presets' ids (<c>factory.</c> plus the preset); workstream E may add more.</summary>
public static class TransformPresetIds {
    public const string Humanize = "factory.humanize";
    public const string Quantize = "factory.quantize";
    public const string Crescendo = "factory.crescendo";
    public const string FixedVelocity = "factory.fixedVelocity";
    public const string Transpose = "factory.transpose";
    public const string DoubleSpeed = "factory.doubleSpeed";
    public const string HalfSpeed = "factory.halfSpeed";
    public const string ReversePosition = "factory.reversePosition";
    public const string ReversePitch = "factory.reversePitch";
    public const string LengthLimits = "factory.lengthLimits";
}
