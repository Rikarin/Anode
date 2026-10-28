using Anode.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Anode.Server.Api;

/// <summary>Bounce and freeze jobs, channel-strip patches, I/O labels and plugin editor windows.</summary>
static class MixApi {
    public static void Map(RouteGroupBuilder api) {
        var project = api.MapGroup("/project").WithTags("Project");
        project.MapPost(
                "/bounce",
                static Results<Accepted<Job>, NotFound, ProblemHttpResult>(BounceRequest request) =>
                    throw Pending.Phase4("Bounce")
            )
            .WithName("Bounce")
            .WithSummary("Starts a bounce (export) job; its progress streams as job events on /ws.");
        project.MapPost(
                "/tracks/{trackId}/freeze",
                static Results<Accepted<Job>, NotFound, ProblemHttpResult>(string trackId) =>
                    throw Pending.Phase4("FreezeTrack")
            )
            .WithName("FreezeTrack")
            .WithSummary(
                "Renders a track's source and inserts to a file and plays that instead (an undoable step when it finishes)."
            );

        var jobs = api.MapGroup("/jobs").WithTags("Jobs");
        jobs.MapGet("/{id}", static Results<Ok<Job>, NotFound>(string id) => throw Pending.Phase4("GetJob"))
            .WithName("GetJob");
        jobs.MapPost("/{id}/cancel", static Results<Ok<Job>, NotFound>(string id) => throw Pending.Phase4("CancelJob"))
            .WithName("CancelJob");

        var library = api.MapGroup("/library").WithTags("Library");
        library.MapGet("/patches", static Ok<IReadOnlyList<PatchInfo>>() => throw Pending.Phase4("ListPatches"))
            .WithName("ListPatches")
            .WithSummary("The user's saved channel-strip patches, by category.");
        library.MapPost(
                "/patches",
                static Results<Ok<PatchInfo>, NotFound, ProblemHttpResult>(SavePatchRequest request) =>
                    throw Pending.Phase4("SavePatch")
            )
            .WithName("SavePatch")
            .WithSummary("Saves a track's instrument, inserts and sends as a patch.");
        library.MapDelete(
                "/patches/{id}",
                static Results<NoContent, NotFound>(string id) => throw Pending.Phase4("DeletePatch")
            )
            .WithName("DeletePatch");

        var settings = api.MapGroup("/settings").WithTags("Settings");
        settings.MapGet("/io-labels", static Ok<IoLabels>() => throw Pending.Phase4("GetIoLabels"))
            .WithName("GetIoLabels");
        settings.MapPut(
                "/io-labels",
                static Results<Ok<IoLabels>, ProblemHttpResult>(IoLabels labels) =>
                    throw Pending.Phase4("SaveIoLabels")
            )
            .WithName("SaveIoLabels");

        api.MapPost(
                "/plugins/editor",
                static Results<NoContent, NotFound, ProblemHttpResult>(PluginEditorRequest request) =>
                    throw Pending.Phase4("OpenPluginEditor")
            )
            .WithName("OpenPluginEditor")
            .WithTags("Plugins")
            .WithSummary(
                "Opens or closes a plugin's own editor window on the machine the engine runs on (the desktop app); 409 "
                + "when the plugin has none or the engine runs headless."
            );
    }
}
