using Anode.Contracts;
using Anode.Model;
using Anode.Model.Editing;
using Anode.Notation;

namespace Anode.Server.Projects;

/// <summary>
///     The notation engine as the server uses it (docs/10-phase-9.md, workstream A's <c>Anode.Notation</c>): the score
///     view and its MEI for <c>GET /project/score</c> and the MusicXML export, and the factory staff styles for
///     <c>GET /library/staff-styles</c>. A seam so the server's tests run with a small double.
/// </summary>
public interface INotation {
    /// <summary>The factory staff styles in library order.</summary>
    IReadOnlyList<StaffStyle> FactoryStyles { get; }

    /// <summary>
    ///     The view of <paramref name="state" /> (its stored score included); <see cref="ArgumentException" /> for an
    ///     unknown set or track.
    /// </summary>
    ScoreView Render(ProjectState state, ScoreRequest request);

    /// <summary>The view as MEI 5 text.</summary>
    string Mei(ScoreView view);
}

/// <summary>
///     <see cref="INotation" /> over <c>Anode.Notation</c>: <see cref="NotationEngine.Render" /> with the project's stored
///     score, <see cref="MeiWriter.Write" /> and <see cref="FactoryStaffStyles.All" />.
/// </summary>
public sealed class EngineNotation : INotation {
    public IReadOnlyList<StaffStyle> FactoryStyles => FactoryStaffStyles.All;

    public ScoreView Render(ProjectState state, ScoreRequest request) =>
        NotationEngine.Render(state, state.Score, request);

    public string Mei(ScoreView view) => MeiWriter.Write(view);
}

/// <summary>
///     The score edits' conversion (<c>moveScoreNotes</c>, <c>addScoreNote</c>) over <c>Anode.Notation</c>'s factory
///     styles and staff positions (<see cref="FactoryStaffStyles.Find" />, <see cref="StaffPositions.PitchAt" />,
///     <see cref="StaffPositions.Step" />): what the session gives the model's edits (<see cref="IScoreNotation" />).
/// </summary>
public sealed class EngineScoreNotation : IScoreNotation {
    public StaffStyle? FactoryStyle(string id) => FactoryStaffStyles.Find(id);

    public int PitchAt(Clef clef, int transpose, KeyEvent key, int staffPosition, Accidental? accidental) =>
        StaffPositions.PitchAt(clef, transpose, key, staffPosition, accidental);

    public int Step(int pitch, int steps, int transpose, KeyEvent key, Accidental? accidental) =>
        StaffPositions.Step(pitch, steps, transpose, key, accidental);
}
