using Anode.Contracts;
using Anode.Server.Engine;
using Anode.Server.Library;
using Anode.Server.Projects;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Anode.Server.Api;

/// <summary>Project and track templates (docs/04-phase-3.md, "Templates").</summary>
static class TemplatesApi {
    public static void Map(RouteGroupBuilder api) {
        var library = api.MapGroup("/library").WithTags("Library").AddEndpointFilter<ProblemFilter>();
        library.MapGet(
                "/project-templates",
                static Ok<IReadOnlyList<ProjectTemplate>>(TemplateLibrary templates) =>
                    TypedResults.Ok(templates.ProjectTemplates())
            )
            .WithName("ListProjectTemplates")
            .WithSummary("The factory's project templates, then the user's: what `template` in CreateProject names.");
        library.MapPost(
                "/project-templates",
                static Results<Ok<Job>, NotFound, ProblemHttpResult>(SaveTemplateRequest request, Jobs jobs) =>
                    TypedResults.Ok(jobs.SaveProjectTemplate(request))
            )
            .WithName("SaveProjectTemplate")
            .WithSummary("Queues saving the open project, media and unsaved edits included, as a template of the user's.");
        library.MapDelete(
                "/project-templates/{id}",
                static Results<NoContent, NotFound>(string id, TemplateLibrary templates) =>
                    templates.DeleteProject(id) ? TypedResults.NoContent() : TypedResults.NotFound()
            )
            .WithName("DeleteProjectTemplate");
        library.MapGet(
                "/track-templates",
                static Ok<IReadOnlyList<TrackTemplate>>(TemplateLibrary templates) =>
                    TypedResults.Ok(templates.TrackTemplates())
            )
            .WithName("ListTrackTemplates");
        library.MapPost(
                "/track-templates",
                static async Task<Results<Ok<TrackTemplate>, NotFound, ProblemHttpResult>>(
                    SaveTrackTemplateRequest request,
                    ProjectSession session,
                    EngineConnection engine,
                    CancellationToken cancellationToken
                ) => {
                    session.Get(); // 404 before asking the engine anything
                    var states = await engine.InstrumentStatesAsync(cancellationToken);
                    return TypedResults.Ok(session.SaveTrackTemplate(request, states));
                }
            )
            .WithName("SaveTrackTemplate")
            .WithSummary(
                "Saves tracks as a template with their routing: their stack members, the auxes they reach, their plugins "
                + "and states. No regions or automation."
            );
        library.MapDelete(
                "/track-templates/{id}",
                static Results<NoContent, NotFound>(string id, TemplateLibrary templates) =>
                    templates.DeleteTrack(id) ? TypedResults.NoContent() : TypedResults.NotFound()
            )
            .WithName("DeleteTrackTemplate");
        library.MapPost(
                "/track-templates/{id}/apply",
                static Results<Ok<Job>, NotFound, ProblemHttpResult>(string id, Jobs jobs) =>
                    TypedResults.Ok(jobs.ApplyTrackTemplate(id))
            )
            .WithName("ApplyTrackTemplate")
            .WithSummary("Queues adding a template's tracks to the open project, on buses of their own, as one undo step.");
    }
}
