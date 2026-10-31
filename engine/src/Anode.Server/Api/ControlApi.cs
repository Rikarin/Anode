using Anode.Contracts;
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
                static Ok<ControllerAssignments>() => throw Pending.Phase8("GetControllerAssignments")
            )
            .WithName("GetControllerAssignments")
            .WithSummary("Every controller assignment, by zone and mode (user settings, not the project).");
        control.MapPut(
                "/assignments",
                static Results<Ok<ControllerAssignments>, ProblemHttpResult>(ControllerAssignments assignments) =>
                    throw Pending.Phase8("SetControllerAssignments")
            )
            .WithName("SetControllerAssignments")
            .WithSummary(
                "Replaces every controller assignment (ids unique, references valid; 422 otherwise); the host's matcher "
                + "table follows."
            );
        control.MapGet("/learn", static Ok<LearnState>() => throw Pending.Phase8("GetLearn"))
            .WithName("GetLearn")
            .WithSummary("The Learn state: on or off, its target, and the last message received while learning.");
        control.MapPost(
                "/learn/start",
                static Results<Ok<LearnState>, ProblemHttpResult>(LearnRequest request) =>
                    throw Pending.Phase8("StartLearn")
            )
            .WithName("StartLearn")
            .WithSummary(
                "Turns Learn on, or changes its target while on. Moving a control then assigns it to the target; each "
                + "message received while learning is a controlLearn event on /ws."
            );
        control.MapPost("/learn/stop", static Ok<LearnState>() => throw Pending.Phase8("StopLearn"))
            .WithName("StopLearn")
            .WithSummary("Turns Learn off.");
        control.MapGet(
                "/surfaces",
                static Ok<IReadOnlyList<ControlSurface>>() => throw Pending.Phase8("ListSurfaces")
            )
            .WithName("ListSurfaces")
            .WithSummary("The control surfaces, in order (user settings).");
        control.MapGet(
                "/surfaces/status",
                static Ok<IReadOnlyList<SurfaceStatus>>() => throw Pending.Phase8("ListSurfaceStatus")
            )
            .WithName("ListSurfaceStatus")
            .WithSummary("Each control surface's live state, in order; surfaces events on /ws report changes.");
        control.MapPost(
                "/surfaces",
                static Results<Ok<ControlSurface>, ProblemHttpResult>(AddSurfaceRequest request) =>
                    throw Pending.Phase8("AddSurface")
            )
            .WithName("AddSurface")
            .WithSummary(
                "Adds a control surface after the others; its ports become control ports (422 for a port another "
                + "surface uses)."
            );
        control.MapDelete(
                "/surfaces/{id}",
                static Results<NoContent, NotFound>(string id) => throw Pending.Phase8("RemoveSurface")
            )
            .WithName("RemoveSurface")
            .WithSummary("Removes a control surface; its ports play tracks again.");
        control.MapPut(
                "/surfaces/order",
                static Results<Ok<IReadOnlyList<ControlSurface>>, ProblemHttpResult>(ReorderSurfacesRequest request) =>
                    throw Pending.Phase8("ReorderSurfaces")
            )
            .WithName("ReorderSurfaces")
            .WithSummary("Puts the control surfaces in a new order (every id once; 422 otherwise).");
        control.MapGet("/osc", static Ok<OscStatus>() => throw Pending.Phase8("GetOsc"))
            .WithName("GetOsc")
            .WithSummary(
                "The OSC server: its settings, whether it listens, this machine's addresses, its subscribers."
            );
        control.MapPut(
                "/osc",
                static Results<Ok<OscStatus>, ProblemHttpResult>(OscSettings settings) =>
                    throw Pending.Phase8("SetOsc")
            )
            .WithName("SetOsc")
            .WithSummary("Changes the OSC server's settings (user settings); it restarts on a new port.");

        var project = api.MapGroup("/project").WithTags("Project").AddEndpointFilter<ProblemFilter>();
        project.MapPut(
                "/selection",
                static Results<NoContent, NotFound, ProblemHttpResult>(SelectionRequest request) =>
                    throw Pending.Phase8("SetSelection")
            )
            .WithName("SetSelection")
            .WithSummary(
                "The editor's selected track, which selected-track controller assignments, surfaces and OSC follow "
                + "(not part of the project: not saved, not undoable)."
            );
        project.MapGet("/sync", static Results<Ok<SyncState>, NotFound>() => throw Pending.Phase8("GetSync"))
            .WithName("GetSync")
            .WithSummary(
                "The live sync state (source, lock, external tempo, Link peers); sync events on /ws report changes. "
                + "The settings are the project's settings.sync, changed with the setSyncSettings command."
            );
        project.MapPost(
                "/regions/{regionId}/split-stems",
                static Results<Accepted<Job>, NotFound, ProblemHttpResult>(string regionId, StemSplitRequest request) =>
                    throw Pending.Phase8("SplitStems")
            )
            .WithName("SplitStems")
            .WithSummary(
                "Splits an audio region into stems as a stemSplit job: it writes a file per stem and, when it "
                + "finishes, adds a track per stem below the region's track and mutes the region (unless kept), in one "
                + "undo step."
            );
    }
}
