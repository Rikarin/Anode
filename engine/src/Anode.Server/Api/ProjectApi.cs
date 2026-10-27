using Anode.Contracts;
using Anode.Model.Audio;
using Anode.Server.Projects;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Anode.Server.Api;

/// <summary>
///     The open project: read it, edit it (command batches, undo and redo), save and close it, and import its media.
///     Every edit answers with the new revision (also the ETag) and the delta all clients receive on <c>/ws</c>.
/// </summary>
static class ProjectApi {
    public static void Map(RouteGroupBuilder api) {
        var project = api.MapGroup("/project").WithTags("Project").AddEndpointFilter<ProblemFilter>();
        project.MapGet(
                string.Empty,
                static Results<Ok<Project>, NotFound>(ProjectSession session, HttpContext http) => {
                    var current = session.Get();
                    Revisions.SetETag(http, current.Revision);
                    return TypedResults.Ok(current);
                }
            )
            .WithName("GetProject")
            .WithSummary("The open project, with its revision as the ETag; 404 when none is open.");
        project.MapPost(
                "/save",
                static Results<Ok<ProjectSummary>, NotFound>(ProjectSession session) => TypedResults.Ok(session.Save())
            )
            .WithName("SaveProject");
        project.MapPost(
                "/close",
                static Results<NoContent, NotFound>(ProjectSession session) => {
                    session.Close();
                    return TypedResults.NoContent();
                }
            )
            .WithName("CloseProject");
        project.MapPost(
                "/commands",
                static Results<Ok<CommandResult>, NotFound, ProblemHttpResult>(
                    CommandBatch batch,
                    ProjectSession session,
                    HttpContext http
                ) => Respond(http, session.Apply(batch, Revisions.Expected(http)))
            )
            .WithName("ApplyCommands")
            .WithSummary(
                "Applies a batch of edits atomically as one undo step. With If-Match, a stale revision answers 409; an "
                + "edit that does not apply (an unknown id, a split outside the region) answers 422 and changes nothing."
            );
        project.MapPost(
                "/undo",
                static Results<Ok<CommandResult>, NotFound, ProblemHttpResult>(
                    ProjectSession session,
                    HttpContext http
                ) =>
                    Respond(http, session.Undo(Revisions.Expected(http)))
            )
            .WithName("Undo")
            .WithSummary("Undoes the last edit; 422 when there is nothing to undo.");
        project.MapPost(
                "/redo",
                static Results<Ok<CommandResult>, NotFound, ProblemHttpResult>(
                    ProjectSession session,
                    HttpContext http
                ) =>
                    Respond(http, session.Redo(Revisions.Expected(http)))
            )
            .WithName("Redo");
        project.MapGet(
                "/history",
                static Results<Ok<UndoHistory>, NotFound>(ProjectSession session) => TypedResults.Ok(session.History())
            )
            .WithName("GetHistory");
        project.MapPost(
                "/files",
                static async Task<Results<Ok<AudioFile>, NotFound, ProblemHttpResult>>(
                    IFormFile file,
                    AudioImport import,
                    CancellationToken cancellationToken
                ) => TypedResults.Ok(await import.UploadAsync(file, cancellationToken))
            )
            .WithName("UploadAudioFile")
            .WithSummary("Copies an uploaded WAV into the project's Audio Files folder (a browser on the network).")
            .DisableAntiforgery();
        project.MapPost(
                "/files/import",
                static Results<Ok<AudioFile>, NotFound, ProblemHttpResult>(
                    ImportFileRequest request,
                    AudioImport import
                ) =>
                    TypedResults.Ok(import.Import(request.Path))
            )
            .WithName("ImportAudioFile")
            .WithSummary(
                "Copies a WAV from the server's disk into the project's Audio Files folder (the desktop app)."
            );
        project.MapGet(
                "/files/{fileId}/peaks",
                static async Task<IResult>(
                    string fileId,
                    int? framesPerPeak,
                    long? start,
                    long? count,
                    ProjectSession session,
                    CancellationToken cancellationToken
                ) => {
                    var path = await session.PeaksAsync(fileId, Peaks.NearestLevel(framesPerPeak), cancellationToken);
                    return TypedResults.Bytes(Peaks.Slice(path, start ?? 0, count), "application/octet-stream");
                }
            )
            .WithName("GetPeaks")
            .WithSummary(
                "Waveform peaks of a file in the binary format of PeakFormat (Anode.Contracts), at the nearest kept "
                + "resolution to framesPerPeak, optionally only peaks start … start + count."
            )
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .Produces(StatusCodes.Status404NotFound);
    }

    static Ok<CommandResult> Respond(HttpContext http, CommandResult result) {
        Revisions.SetETag(http, result.Revision);
        return TypedResults.Ok(result);
    }
}
