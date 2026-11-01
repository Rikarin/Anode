using Anode.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Anode.Server.Api;

/// <summary>
///     Phase 9 (docs/10-phase-9.md): the rendered score (the notation engine's view and its MEI) and the factory staff
///     styles. The score is a view: edits go through commands, and deltas say when to refetch it.
/// </summary>
static class ScoreApi {
    public static void Map(RouteGroupBuilder api) {
        var project = api.MapGroup("/project").WithTags("Score").AddEndpointFilter<ProblemFilter>();
        project.MapGet(
                "/score",
                // view is its contract name (linear, page), which query binding does not parse.
                static Results<Ok<ScoreDocument>, NotFound, ProblemHttpResult>(
                    string? set,
                    string? part,
                    string? view
                ) =>
                    throw Pending.Phase9("GetScore")
            )
            .WithName("GetScore")
            .WithSummary(
                "The rendered score and its MEI: score set `set` (left out: every MIDI track), or track `part`'s part "
                + "alone, in the `view` linear (the default) or page (a ScoreViewMode); 422 for an unknown set, track "
                + "or view. Cached per project revision; the same project gives the same answer."
            );

        var library = api.MapGroup("/library").WithTags("Score").AddEndpointFilter<ProblemFilter>();
        library.MapGet(
                "/staff-styles",
                static Ok<IReadOnlyList<StaffStyle>>() => throw Pending.Phase9("ListStaffStyles")
            )
            .WithName("ListStaffStyles")
            .WithSummary(
                "The factory staff styles (StaffStyleIds), in library order; a project's own are in its score."
            );
    }
}
