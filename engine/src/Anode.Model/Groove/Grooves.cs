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
/// <remarks>
///     <para>
///         Extraction: step n's grid tick is g = startTick + n × stepTicks; its onsets are those with g − h ≤ tick &lt;
///         g + h, h = stepTicks ÷ 2 (in exact arithmetic, so each onset belongs to one step); of them the strongest wins
///         (ties: the nearest the grid, then the earlier). Its point is (tick − startTick, strength ÷ the strongest
///         winner's strength); a step without onsets, or when every winner has strength 0, is (n × stepTicks, 1).
///     </para>
///     <para>
///         Quantise: tick t's step is k = the nearest multiple of stepTicks (ties: the earlier; t &lt; 0 is step 0);
///         its template step is n = k mod N (N = the point count) in cycle c = k div N, landing at
///         L = c × lengthTicks + points[n].tick. The new tick is max(0, t + round((L − t) × strength)), rounded half
///         away from zero (never before the project start), and the velocity v becomes round(v × (1 + strength ×
///         (scale − 1))), clamped to 1…127.
///     </para>
/// </remarks>
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
    ) {
        var steps = StepCount(lengthTicks, stepTicks);
        var winners = new GrooveOnsetTick?[steps];
        foreach (var onset in onsets) {
            // 2·(tick − startTick) + stepTicks over 2·stepTicks: the step whose half-open window holds the onset.
            var doubled = 2 * (onset.Tick - startTick) + stepTicks;
            if (doubled < 0) {
                continue;
            }

            var n = doubled / (2 * stepTicks);
            if (n >= steps) {
                continue;
            }

            var grid = startTick + n * stepTicks;
            if (winners[n] is not { } best || Beats(onset, best, grid)) {
                winners[n] = onset;
            }
        }

        double strongest = 0;
        foreach (var w in winners) {
            if (w is { } onset) {
                strongest = Math.Max(strongest, onset.Strength);
            }
        }

        var points = new GroovePoint[steps];
        for (var n = 0; n < steps; n++) {
            points[n] = winners[n] is { } onset && strongest > 0
                ? new(onset.Tick - startTick, onset.Strength / strongest)
                : new(n * stepTicks, 1);
        }

        return new(id, name, lengthTicks, stepTicks, points);
    }

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
        FromOnsets(
            id,
            name,
            [..notes.Select(n => new GrooveOnsetTick(startTick + n.StartTick, n.Velocity / 127.0))],
            startTick,
            lengthTicks,
            stepTicks
        );

    /// <summary>
    ///     Where project tick <paramref name="tick" /> quantises to: its nearest grid step's template position, moved
    ///     towards by <paramref name="strength" /> (0…1), and that step's velocity scale.
    /// </summary>
    public static (long Tick, double VelocityScale) Target(GrooveTemplate groove, long tick, double strength) {
        ArgumentNullException.ThrowIfNull(groove);
        var steps = StepCount(groove.LengthTicks, groove.StepTicks);
        if (groove.Points.Count != steps) {
            throw new ArgumentException(
                $"A template of {groove.LengthTicks} ticks in steps of {groove.StepTicks} has {steps} points.",
                nameof(groove)
            );
        }

        var step = groove.StepTicks;
        var k = tick <= 0 ? 0 : (2 * tick + step - 1) / (2 * step); // ties go to the earlier step
        var n = (int)(k % steps);
        var point = groove.Points[n];
        var landing = k / steps * groove.LengthTicks + point.Tick;
        var moved = Math.Max(0, tick + (long)Math.Round((landing - tick) * strength, MidpointRounding.AwayFromZero));
        return (moved, point.VelocityScale);
    }

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
    ) {
        var result = new Note[notes.Count];
        for (var i = 0; i < notes.Count; i++) {
            var note = notes[i];
            var (tick, scale) = Target(groove, regionStartTick + note.StartTick, strength);
            var v = note.Velocity;
            if (velocity) {
                var factor = 1 + strength * (scale - 1);
                v = (int)Math.Clamp(Math.Round(v * factor, MidpointRounding.AwayFromZero), 1, 127);
            }

            result[i] = note with { StartTick = tick - regionStartTick, Velocity = v };
        }

        return result;
    }

    /// <summary>Whether <paramref name="onset" /> beats <paramref name="best" /> in the step at <paramref name="grid" />.</summary>
    static bool Beats(GrooveOnsetTick onset, GrooveOnsetTick best, long grid) {
        if (onset.Strength != best.Strength) {
            return onset.Strength > best.Strength;
        }

        var distance = Math.Abs(onset.Tick - grid);
        var bestDistance = Math.Abs(best.Tick - grid);
        return distance != bestDistance ? distance < bestDistance : onset.Tick < best.Tick;
    }

    static int StepCount(long lengthTicks, long stepTicks) {
        if (stepTicks <= 0 || lengthTicks <= 0 || lengthTicks % stepTicks != 0) {
            throw new ArgumentException(
                $"A groove's length ({lengthTicks}) must be a positive multiple of its step ({stepTicks})."
            );
        }

        return (int)(lengthTicks / stepTicks);
    }
}
