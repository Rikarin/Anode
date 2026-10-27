using Anode.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Anode.Server.Api;

/// <summary>
///     The open project: read it, edit it (command batches, undo and redo), save and close it, and import its media.
///     Every edit answers with the new revision (also the ETag) and the delta all clients receive on <c>/ws</c>.
/// </summary>
static class ProjectApi {
    public static void Map(RouteGroupBuilder api) {
        var project = api.MapGroup("/project").WithTags("Project");
        project.MapGet(string.Empty, static Results<Ok<Project>, NotFound>() => throw Pending.Phase1("GetProject"))
            .WithName("GetProject")
            .WithSummary("The open project, with its revision as the ETag; 404 when none is open.");
        project.MapPost("/save", static Results<Ok<ProjectSummary>, NotFound>() => throw Pending.Phase1("SaveProject"))
            .WithName("SaveProject");
        project.MapPost("/close", static Results<NoContent, NotFound>() => throw Pending.Phase1("CloseProject"))
            .WithName("CloseProject");
        project.MapPost(
                "/commands",
                static Results<Ok<CommandResult>, NotFound, ProblemHttpResult>(CommandBatch batch) =>
                    throw Pending.Phase1("ApplyCommands")
            )
            .WithName("ApplyCommands")
            .WithSummary(
                "Applies a batch of edits atomically as one undo step. With If-Match, a stale revision answers 409; an "
                + "edit that does not apply (an unknown id, a split outside the region) answers 422 and changes nothing."
            );
        project.MapPost(
                "/undo",
                static Results<Ok<CommandResult>, NotFound, ProblemHttpResult>() =>
                    throw Pending.Phase1("Undo")
            )
            .WithName("Undo")
            .WithSummary("Undoes the last edit; 422 when there is nothing to undo.");
        project.MapPost(
                "/redo",
                static Results<Ok<CommandResult>, NotFound, ProblemHttpResult>() =>
                    throw Pending.Phase1("Redo")
            )
            .WithName("Redo");
        project.MapGet("/history", static Results<Ok<UndoHistory>, NotFound>() => throw Pending.Phase1("GetHistory"))
            .WithName("GetHistory");
        project.MapPost(
                "/files",
                static Results<Ok<AudioFile>, NotFound, ProblemHttpResult>(IFormFile file) =>
                    throw Pending.Phase1("UploadAudioFile")
            )
            .WithName("UploadAudioFile")
            .WithSummary("Copies an uploaded WAV into the project's Audio Files folder (a browser on the network).")
            .DisableAntiforgery();
        project.MapPost(
                "/files/import",
                static Results<Ok<AudioFile>, NotFound, ProblemHttpResult>(ImportFileRequest request) =>
                    throw Pending.Phase1("ImportAudioFile")
            )
            .WithName("ImportAudioFile")
            .WithSummary(
                "Copies a WAV from the server's disk into the project's Audio Files folder (the desktop app)."
            );
        project.MapGet(
                "/files/{fileId}/peaks",
                static IResult(string fileId, int? framesPerPeak, long? start, long? count) =>
                    throw Pending.Phase1("GetPeaks")
            )
            .WithName("GetPeaks")
            .WithSummary(
                "Waveform peaks of a file in the binary format of PeakFormat (Anode.Contracts), at the nearest kept "
                + "resolution to framesPerPeak, optionally only peaks start … start + count."
            )
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .Produces(StatusCodes.Status404NotFound);
    }
}
