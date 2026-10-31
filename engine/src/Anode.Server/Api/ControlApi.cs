using Anode.Contracts;
using Anode.Server.Control;
using Anode.Server.Engine;
using Anode.Server.Projects;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Anode.Server.Api;

/// <summary>
///     Phase 8 (docs/09-phase-8.md): controller assignments and Learn, control surfaces and the OSC server (user
///     settings); the editor's selected track; the project's sync state; and the Stem Splitter.
/// </summary>
static class ControlApi {
    public static void Map(RouteGroupBuilder api) {
        var control = api.MapGroup("/control").WithTags("Control").AddEndpointFilter<ProblemFilter>();
        control.MapGet(
                "/assignments",
                static Ok<ControllerAssignments>(ControlService service) => TypedResults.Ok(service.Assignments())
            )
            .WithName("GetControllerAssignments")
            .WithSummary("Every controller assignment, by zone and mode (user settings, not the project).");
        control.MapPut(
                "/assignments",
                static Results<Ok<ControllerAssignments>, ProblemHttpResult>(
                    ControllerAssignments assignments,
                    ControlService service
                ) =>
                    TypedResults.Ok(service.SetAssignments(assignments))
            )
            .WithName("SetControllerAssignments")
            .WithSummary(
                "Replaces every controller assignment (ids unique, references valid; 422 otherwise); the host's matcher "
                + "table follows."
            );
        control.MapGet("/learn", static Ok<LearnState>(ControlService service) => TypedResults.Ok(service.Learn()))
            .WithName("GetLearn")
            .WithSummary("The Learn state: on or off, its target, and the last message received while learning.");
        control.MapPost(
                "/learn/start",
                static Results<Ok<LearnState>, ProblemHttpResult>(LearnRequest request, ControlService service) =>
                    TypedResults.Ok(service.StartLearn(request))
            )
            .WithName("StartLearn")
            .WithSummary(
                "Turns Learn on, or changes its target while on. Moving a control then assigns it to the target; each "
                + "message received while learning is a controlLearn event on /ws."
            );
        control.MapPost(
                "/learn/stop",
                static Ok<LearnState>(ControlService service) => TypedResults.Ok(service.StopLearn())
            )
            .WithName("StopLearn")
            .WithSummary("Turns Learn off.");
        control.MapGet(
                "/surfaces",
                static Ok<IReadOnlyList<ControlSurface>>(ControlService service) => TypedResults.Ok(service.Surfaces())
            )
            .WithName("ListSurfaces")
            .WithSummary("The control surfaces, in order (user settings).");
        control.MapGet(
                "/surfaces/status",
                static Ok<IReadOnlyList<SurfaceStatus>>(ControlService service) =>
                    TypedResults.Ok(service.SurfaceStatuses())
            )
            .WithName("ListSurfaceStatus")
            .WithSummary("Each control surface's live state, in order; surfaces events on /ws report changes.");
        control.MapPost(
                "/surfaces",
                static Results<Ok<ControlSurface>, ProblemHttpResult>(
                    AddSurfaceRequest request,
                    ControlService service
                ) =>
                    TypedResults.Ok(service.AddSurface(request))
            )
            .WithName("AddSurface")
            .WithSummary(
                "Adds a control surface after the others; its ports become control ports (422 for a port another "
                + "surface uses)."
            );
        control.MapDelete(
                "/surfaces/{id}",
                static Results<NoContent, NotFound>(string id, ControlService service) =>
                    service.RemoveSurface(id) ? TypedResults.NoContent() : TypedResults.NotFound()
            )
            .WithName("RemoveSurface")
            .WithSummary("Removes a control surface; its ports play tracks again.");
        control.MapPut(
                "/surfaces/order",
                static Results<Ok<IReadOnlyList<ControlSurface>>, ProblemHttpResult>(
                    ReorderSurfacesRequest request,
                    ControlService service
                ) =>
                    TypedResults.Ok(service.ReorderSurfaces(request))
            )
            .WithName("ReorderSurfaces")
            .WithSummary("Puts the control surfaces in a new order (every id once; 422 otherwise).");
        control.MapGet("/osc", static Ok<OscStatus>(ControlService service) => TypedResults.Ok(service.Osc()))
            .WithName("GetOsc")
            .WithSummary(
                "The OSC server: its settings, whether it listens, this machine's addresses, its subscribers."
            );
        control.MapPut(
                "/osc",
                static Results<Ok<OscStatus>, ProblemHttpResult>(OscSettings settings, ControlService service) =>
                    TypedResults.Ok(service.SetOsc(settings))
            )
            .WithName("SetOsc")
            .WithSummary("Changes the OSC server's settings (user settings); it restarts on a new port.");

        var project = api.MapGroup("/project").WithTags("Project").AddEndpointFilter<ProblemFilter>();
        project.MapPut(
                "/selection",
                static Results<NoContent, NotFound, ProblemHttpResult>(
                    SelectionRequest request,
                    ControlService service
                ) => {
                    service.SetSelection(request);
                    return TypedResults.NoContent();
                }
            )
            .WithName("SetSelection")
            .WithSummary(
                "The editor's selected track, which selected-track controller assignments, surfaces and OSC follow "
                + "(not part of the project: not saved, not undoable)."
            );
        project.MapGet(
                "/sync",
                static Results<Ok<SyncState>, NotFound>(SyncService sync) => TypedResults.Ok(sync.State())
            )
            .WithName("GetSync")
            .WithSummary(
                "The live sync state (source, lock, external tempo, Link peers); sync events on /ws report changes. "
                + "The settings are the project's settings.sync, changed with the setSyncSettings command."
            );
        project.MapPost(
                "/regions/{regionId}/split-stems",
                static Results<Accepted<Job>, NotFound, ProblemHttpResult>(
                    string regionId,
                    StemSplitRequest request,
                    Jobs jobs
                ) => {
                    var job = jobs.SplitStems(regionId, request);
                    return TypedResults.Accepted($"/api/v1/jobs/{job.Id}", job);
                }
            )
            .WithName("SplitStems")
            .WithSummary(
                "Splits an audio region into stems as a stemSplit job: it writes a file per stem and, when it "
                + "finishes, adds a track per stem below the region's track and mutes the region (unless kept), in one "
                + "undo step."
            );
    }
}
