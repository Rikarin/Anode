using Anode.Contracts;
using Anode.Server.Engine;
using Anode.Server.Library;
using Anode.Server.Projects;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Anode.Server.Api;

/// <summary>Bounce and freeze jobs, channel-strip patches, I/O labels and plugin editor windows (docs/05-phase-4.md).</summary>
static class MixApi {
    public static void Map(RouteGroupBuilder api) {
        var project = api.MapGroup("/project").WithTags("Project").AddEndpointFilter<ProblemFilter>();
        project.MapPost(
                "/bounce",
                static Results<Accepted<Job>, NotFound, ProblemHttpResult>(BounceRequest request, Jobs jobs) =>
                    Accepted(jobs.Bounce(request))
            )
            .WithName("Bounce")
            .WithSummary("Starts a bounce (export) job; its progress streams as job events on /ws.");
        project.MapPost(
                "/tracks/{trackId}/freeze",
                static Results<Accepted<Job>, NotFound, ProblemHttpResult>(string trackId, Jobs jobs) =>
                    Accepted(jobs.Freeze(trackId))
            )
            .WithName("FreezeTrack")
            .WithSummary(
                "Renders a track's source and inserts to a file and plays that instead (an undoable step when it finishes)."
            );

        var jobsGroup = api.MapGroup("/jobs").WithTags("Jobs");
        jobsGroup.MapGet(
                "/{id}",
                static Results<Ok<Job>, NotFound>(string id, Jobs jobs) =>
                    jobs.Get(id) is { } job ? TypedResults.Ok(job) : TypedResults.NotFound()
            )
            .WithName("GetJob");
        jobsGroup.MapPost(
                "/{id}/cancel",
                static Results<Ok<Job>, NotFound>(string id, Jobs jobs) =>
                    jobs.Cancel(id) is { } job ? TypedResults.Ok(job) : TypedResults.NotFound()
            )
            .WithName("CancelJob");

        var library = api.MapGroup("/library").WithTags("Library").AddEndpointFilter<ProblemFilter>();
        library.MapGet(
                "/patches",
                static Ok<IReadOnlyList<PatchInfo>>(PatchLibrary patches) => TypedResults.Ok(patches.List())
            )
            .WithName("ListPatches")
            .WithSummary("The user's saved channel-strip patches, by category.");
        library.MapPost(
                "/patches",
                static async Task<Results<Ok<PatchInfo>, NotFound, ProblemHttpResult>>(
                    SavePatchRequest request,
                    PatchLibrary patches,
                    ProjectSession session,
                    EngineConnection engine,
                    CancellationToken cancellationToken
                ) => {
                    session.Get(); // 404 before asking the engine anything
                    var states = await engine.InstrumentStatesAsync(cancellationToken);
                    var (track, saved) = session.PatchSource(request.TrackId ?? string.Empty, states);
                    return TypedResults.Ok(patches.Save(track, request.Name, request.Category ?? string.Empty, saved));
                }
            )
            .WithName("SavePatch")
            .WithSummary("Saves a track's instrument, inserts and sends as a patch.");
        library.MapDelete(
                "/patches/{id}",
                static Results<NoContent, NotFound>(string id, PatchLibrary patches) =>
                    patches.Delete(id) ? TypedResults.NoContent() : TypedResults.NotFound()
            )
            .WithName("DeletePatch");

        var settings = api.MapGroup("/settings").WithTags("Settings").AddEndpointFilter<ProblemFilter>();
        settings.MapGet("/io-labels", static Ok<IoLabels>(UserSettings user) => TypedResults.Ok(user.IoLabels()))
            .WithName("GetIoLabels");
        settings.MapPut(
                "/io-labels",
                static Results<Ok<IoLabels>, ProblemHttpResult>(IoLabels labels, UserSettings user) =>
                    TypedResults.Ok(user.SaveIoLabels(labels))
            )
            .WithName("SaveIoLabels");

        api.MapPost(
                "/plugins/editor",
                static Results<NoContent, NotFound, ProblemHttpResult>(
                    PluginEditorRequest request,
                    EngineConnection engine
                ) => {
                    engine.OpenEditor(request);
                    return TypedResults.NoContent();
                }
            )
            .WithName("OpenPluginEditor")
            .WithTags("Plugins")
            .AddEndpointFilter<ProblemFilter>()
            .WithSummary(
                "Opens or closes a plugin's own editor window on the machine the engine runs on (the desktop app); 409 "
                + "when the plugin has none or the engine runs headless."
            );
    }

    static Accepted<Job> Accepted(Job job) => TypedResults.Accepted($"/api/v1/jobs/{job.Id}", job);
}
