using Anode.Contracts;

namespace Anode.Model.Editing;

/// <summary>
///     What the score edits (<c>moveScoreNotes</c>, <c>addScoreNote</c>; docs/10-phase-9.md) ask of the notation engine,
///     which the model cannot reference: the factory staff styles and staff position ↔ pitch. The server's source is
///     <c>Anode.Notation</c> (<c>FactoryStaffStyles</c>, <c>StaffPositions</c>); tests use a small double. Pitches in and
///     out are concert MIDI pitches; written = concert + <c>transpose</c>; the key is the concert key.
/// </summary>
public interface IScoreNotation {
    /// <summary>The factory staff style <paramref name="id" /> (<see cref="StaffStyleIds" />), or null.</summary>
    StaffStyle? FactoryStyle(string id);

    /// <summary>
    ///     The concert pitch at <paramref name="staffPosition" /> (diatonic steps above the bottom line) on a
    ///     <paramref name="clef" /> staff: the written note of the key there, <paramref name="accidental" /> forcing its
    ///     alteration, less <paramref name="transpose" />.
    /// </summary>
    int PitchAt(Clef clef, int transpose, KeyEvent key, int staffPosition, Accidental? accidental);

    /// <summary>
    ///     The concert pitch <paramref name="steps" /> diatonic steps above (below when negative)
    ///     <paramref name="pitch" /> in the written key, <paramref name="accidental" /> forcing the alteration.
    /// </summary>
    int Step(int pitch, int steps, int transpose, KeyEvent key, Accidental? accidental);
}

/// <summary>No notation engine: the score edits are refused (a model used without the server's engine).</summary>
public sealed class NoScoreNotation : IScoreNotation {
    public static NoScoreNotation Instance { get; } = new();

    public StaffStyle? FactoryStyle(string id) => null;

    public int PitchAt(Clef clef, int transpose, KeyEvent key, int staffPosition, Accidental? accidental) =>
        throw new EditException("Score edits need the notation engine.");

    public int Step(int pitch, int steps, int transpose, KeyEvent key, Accidental? accidental) =>
        throw new EditException("Score edits need the notation engine.");
}
