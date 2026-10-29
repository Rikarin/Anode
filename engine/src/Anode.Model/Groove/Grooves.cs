using Anode.Contracts;

namespace Anode.Model.Groove;

/// <summary>An onset a groove template is made from: its project tick and strength 0…1 (a note's velocity ÷ 127).</summary>
public readonly record struct GrooveOnsetTick(long Tick, double Strength);

/// <summary>
///     Groove templates (docs/08-phase-7.md, "Grooves"): extraction (the strongest onset per grid step) and quantise to
///     a template (a note moves to the template position of its grid step, by the strength; the template repeats every
///     <c>LengthTicks</c> from the project start). Pure and deterministic; checked against the shared
///     <c>GrooveVectors.json</c>.
/// </summary>
public static class Grooves {
    /// <summary>
    ///     A template of <paramref name="lengthTicks" /> with steps of <paramref name="stepTicks" /> from
    ///     <paramref name="onsets" /> (project ticks) from <paramref name="startTick" /> on: per step, the strongest onset
    ///     within half a step of it (none: the grid position at scale 1); velocity scales relative to the strongest.
    /// </summary>
    public static GrooveTemplate FromOnsets(
        string id,
        string name,
        IReadOnlyList<GrooveOnsetTick> onsets,
        long startTick,
        long lengthTicks,
        long stepTicks
    ) =>
        throw Pending.WorkstreamE();

    /// <summary>
    ///     A template from a MIDI region's notes (positions relative to the region's start <paramref name="startTick" />,
    ///     as the region holds them): <see cref="FromOnsets" /> with each note's start and velocity ÷ 127.
    /// </summary>
    public static GrooveTemplate FromNotes(
        string id,
        string name,
        IReadOnlyList<Note> notes,
        long startTick,
        long lengthTicks,
        long stepTicks
    ) =>
        throw Pending.WorkstreamE();

    /// <summary>
    ///     Where project tick <paramref name="tick" /> quantises to: its nearest grid step's template position, moved
    ///     towards by <paramref name="strength" /> (0…1), and that step's velocity scale.
    /// </summary>
    public static (long Tick, double VelocityScale) Target(GrooveTemplate groove, long tick, double strength) =>
        throw Pending.WorkstreamE();

    /// <summary>
    ///     Quantises a region's notes (positions relative to <paramref name="regionStartTick" />) to
    ///     <paramref name="groove" /> by <paramref name="strength" />; with <paramref name="velocity" />, their
    ///     velocities follow the template's scales by the same strength (clamped to 1…127). The notes in the order given.
    /// </summary>
    public static IReadOnlyList<Note> Quantize(
        IReadOnlyList<Note> notes,
        long regionStartTick,
        GrooveTemplate groove,
        double strength,
        bool velocity
    ) =>
        throw Pending.WorkstreamE();
}
