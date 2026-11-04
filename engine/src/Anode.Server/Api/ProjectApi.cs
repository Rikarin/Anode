using Anode.Contracts;
using Anode.Model.Audio;
using Anode.Server.Engine;
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
                static async Task<Results<Ok<ProjectSummary>, NotFound>>(
                    ProjectSession session,
                    EngineConnection engine,
                    CancellationToken cancellationToken
                ) => {
                    session.Get(); // 404 before asking the engine anything
                    // The instruments' opaque states come from the engine (docs/03-phase-2.md, "State").
                    var states = await engine.InstrumentStatesAsync(cancellationToken);
                    return TypedResults.Ok(session.Save(states));
                }
            )
            .WithName("SaveProject");
        project.MapPost(
                "/save-as",
                static Results<Ok<Job>, NotFound, ProblemHttpResult>(SaveProjectAsRequest request, Jobs jobs) =>
                    TypedResults.Ok(jobs.SaveAs(request))
            )
            .WithName("SaveProjectAs")
            .WithSummary(
                "Queues writing the project, unsaved edits included, as a whole package at a new location and going on "
                + "working there; the original stays as last saved. 409 when something is there; a job that fails or is "
                + "cancelled leaves no package and the open project as it was."
            );
        project.MapPost(
                "/save-copy",
                static Results<Ok<Job>, NotFound, ProblemHttpResult>(SaveProjectAsRequest request, Jobs jobs) =>
                    TypedResults.Ok(jobs.SaveCopy(request))
            )
            .WithName("SaveProjectCopy")
            .WithSummary(
                "Queues writing the project, unsaved edits included, as a whole package at a new location; the open "
                + "project stays open where it is, unsaved edits and all."
            );
        project.MapPost(
                "/sample-rate",
                static Results<Ok<Job>, NotFound, ProblemHttpResult>(ChangeSampleRateRequest request, Jobs jobs) =>
                    TypedResults.Ok(jobs.ChangeSampleRate(request))
            )
            .WithName("ChangeSampleRate")
            .WithSummary(
                "Queues changing the project's sample rate, saving it: its files are written anew beside the old ones "
                + "(converted, or their samples kept), the last save and unsaved edits go to backups, and the project "
                + "reopens without undo history. 422 while files are offline or tracks unreadable, 409 while it plays. "
                + "A job that fails or is cancelled leaves the project as it was."
            );
        project.MapPost(
                "/import-tracks",
                static Results<Ok<Job>, NotFound, ProblemHttpResult>(ImportProjectTracksRequest request, Jobs jobs) =>
                    TypedResults.Ok(jobs.ImportProjectTracks(request))
            )
            .WithName("ImportProjectTracks")
            .WithSummary(
                "Queues importing chosen tracks of a project on the server's disk, with their regions, inserts, sends "
                + "and automation, as one undo step. The source is only read; a job that fails leaves the open project "
                + "as it was."
            );
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
        project.MapGet(
                "/offline",
                static Results<Ok<OfflineReport>, NotFound>(ProjectSession session) =>
                    TypedResults.Ok(session.Offline())
            )
            .WithName("GetOffline")
            .WithSummary("Reads the disk now: the project's offline files and unreadable tracks.");
        project.MapPost(
                "/offline/search",
                static Results<Ok<OfflineSearch>, NotFound, ProblemHttpResult>(
                    OfflineSearchRequest request,
                    ProjectSession session
                ) =>
                    TypedResults.Ok(session.SearchOffline(request))
            )
            .WithName("SearchOffline")
            .WithSummary(
                "Looks through a folder of the server's disk (left out: the project's) for WAVs named as the offline "
                + "files, and says of each whether its length, rate and channels match."
            );
        project.MapPost(
                "/offline/relink",
                static Results<Ok<CommandResult>, NotFound, ProblemHttpResult>(
                    RelinkRequest request,
                    AudioImport import
                ) =>
                    TypedResults.Ok(import.Relink(request))
            )
            .WithName("RelinkFiles")
            .WithSummary(
                "Points files of the project at WAVs on the server's disk, copied into Audio Files, as one undo step; "
                + "422, changing nothing, when one's length, rate or channels do not match the file it replaces."
            );
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
