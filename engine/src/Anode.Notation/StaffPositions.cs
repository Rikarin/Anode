using Anode.Contracts;

namespace Anode.Notation;

/// <summary>Where a pitch sits on a staff: its staff position (diatonic steps above the bottom line) and written spelling.</summary>
public sealed record StaffPlacement(int StaffPosition, ScorePitch Written);

/// <summary>
///     Staff position ↔ pitch for a clef, key and transposition (docs/10-phase-9.md, "Score edits"): what
///     <c>moveScoreNotes</c> and <c>addScoreNote</c> are converted with. Pitches in and out are concert MIDI pitches;
///     written = concert + <c>transpose</c>; the key is the concert key (a transposing staff's written key follows).
/// </summary>
public static class StaffPositions {
    /// <summary>
    ///     The concert pitch at <paramref name="staffPosition" /> on a <paramref name="clef" /> staff: the written note of
    ///     the key there (with <paramref name="accidental" /> forcing its alteration), less <paramref name="transpose" />.
    /// </summary>
    public static int PitchAt(
        Clef clef,
        int transpose,
        KeyEvent key,
        int staffPosition,
        Accidental? accidental = null
    ) =>
        throw Pending.WorkstreamA();

    /// <summary>Where <paramref name="pitch" /> is written on a <paramref name="clef" /> staff, spelled in the key.</summary>
    public static StaffPlacement PlacementOf(Clef clef, int transpose, KeyEvent key, int pitch) =>
        throw Pending.WorkstreamA();

    /// <summary>
    ///     The concert pitch <paramref name="steps" /> diatonic steps above (below when negative) <paramref name="pitch" />
    ///     in the written key, as dragging a note does (<paramref name="accidental" /> forcing the alteration).
    /// </summary>
    public static int Step(int pitch, int steps, int transpose, KeyEvent key, Accidental? accidental = null) =>
        throw Pending.WorkstreamA();
}
