using Anode.Contracts;
using Anode.Model;

namespace Anode.Notation;

/// <summary>
///     What to render: score set <c>SetId</c> (null: every track that holds MIDI, in track order), or the part of track
///     <c>PartTrackId</c> alone (written at transposed pitch), in the linear or page view
///     (<c>GET /project/score?set=&amp;part=&amp;view=</c>).
/// </summary>
public sealed record ScoreRequest(
    string? SetId = null,
    string? PartTrackId = null,
    ScoreViewMode Mode = ScoreViewMode.Linear);

/// <summary>
///     MIDI to notation, as Logic's score does it (docs/10-phase-9.md, workstream A). The engine decides what is
///     musically fixed (which notes, durations, ties, tuplets, voices, spelling, measures); Verovio engraves
///     (docs/adr/0011-notation-rendering.md).
/// </summary>
public static class NotationEngine {
    /// <summary>
    ///     The score view of <paramref name="state" /> for <paramref name="request" />: each track's MIDI regions under its
    ///     staff style (<see cref="FactoryStaffStyles.Resolve" />) and their <see cref="RegionNotation" />, measured by the
    ///     project's signatures, spelled by its keys, with its tempo marks and markers as rehearsal marks, and the stored
    ///     symbols, lyrics and chord symbols of <paramref name="score" /> (the project's <c>score</c>; passed in until the
    ///     model stores it). Deterministic: the same input gives the same view. Throws <see cref="ArgumentException" />
    ///     for a set or track that does not exist.
    /// </summary>
    public static ScoreView Render(ProjectState state, ProjectScore score, ScoreRequest request) =>
        throw Pending.WorkstreamA();
}
