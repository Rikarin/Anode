namespace Anode.Contracts;

/// <summary>
///     One grid step of a groove template: where its onset lands (<c>tick</c>, from the template's start) and how its
///     velocity scales (1: unchanged; the strongest onset of the template is 1).
/// </summary>
public sealed record GroovePoint(long Tick, double VelocityScale);

/// <summary>
///     A groove template (docs/08-phase-7.md, "Grooves"): one point per grid step of <c>stepTicks</c> over
///     <c>lengthTicks</c> (1–4 bars), so <c>points</c> holds lengthTicks ÷ stepTicks points in step order, point n
///     being step n's onset (near n × stepTicks). Made from a MIDI region's notes or an audio region's transients: the
///     strongest onset per grid step (a step without one keeps its grid position at scale 1). Quantising to it moves a
///     note to the template position of its grid step (the template repeating every <c>lengthTicks</c> from the
///     project start), by the quantise's strength. Ids start with <c>grv_</c>; templates live in the project
///     (<c>Project.grooves</c>) and in the user library (<c>/library/grooves</c>).
/// </summary>
public sealed record GrooveTemplate(
    string Id,
    string Name,
    long LengthTicks,
    long StepTicks,
    IReadOnlyList<GroovePoint> Points
);
