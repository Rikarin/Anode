using Anode.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Anode.Server.Api;

/// <summary>
///     Phase 10 (docs/11-phase-10.md): the project's movie (import, metadata, removal, the file with range requests,
///     thumbnails, scene detection, its audio in and out), ADM BWF export and import, and the monitoring settings (user
///     settings). Media work runs as jobs through FFmpeg (docs/adr/0013-video-and-listen-in.md).
/// </summary>
static class MediaApi {
    public static void Map(RouteGroupBuilder api) {
        var movie = api.MapGroup("/project/movie").WithTags("Movie").AddEndpointFilter<ProblemFilter>();
        movie.MapPost(
                string.Empty,
                static Results<Accepted<Job>, NotFound, ProblemHttpResult>(MovieImportRequest request) =>
                    throw Pending.Phase10("ImportMovie")
            )
            .WithName("ImportMovie")
            .WithSummary(
                "Imports a movie (MOV, MP4, M4V) from the server's disk, copied into the project or referenced: a "
                + "movieImport job that probes it and sets it as the project's movie (one undo step)."
            );
        movie.MapGet(string.Empty, static Results<Ok<Movie>, NotFound>() => throw Pending.Phase10("GetMovie"))
            .WithName("GetMovie")
            .WithSummary("The project's movie (as Project.movie); 404 when it has none.");
        movie.MapDelete(
                string.Empty,
                static Results<Ok<CommandResult>, NotFound, ProblemHttpResult>() => throw Pending.Phase10("RemoveMovie")
            )
            .WithName("RemoveMovie")
            .WithSummary("Remove Movie: applies removeMovie (one undo step); 404 when there is no project.");
        movie.MapGet("/stream", static IResult() => throw Pending.Phase10("StreamMovie"))
            .WithName("StreamMovie")
            .WithSummary(
                "The movie file for the editor's and remotes' <video>, with HTTP range requests (206 Partial Content) and "
                + "its media type; 404 without a movie."
            )
            .Produces(StatusCodes.Status200OK, contentType: "video/mp4")
            .Produces(StatusCodes.Status206PartialContent, contentType: "video/mp4")
            .Produces(StatusCodes.Status404NotFound);
        movie.MapPost(
                "/thumbnails",
                static Results<Accepted<Job>, NotFound, ProblemHttpResult>(ThumbnailsRequest request) =>
                    throw Pending.Phase10("RenderMovieThumbnails")
            )
            .WithName("RenderMovieThumbnails")
            .WithSummary("Renders movie frames as JPEG thumbnails into the movie's cache: a thumbnails job.");
        movie.MapGet(
                "/thumbnails",
                static Results<Ok<MovieThumbnails>, NotFound>(int? height) =>
                    throw Pending.Phase10("ListMovieThumbnails")
            )
            .WithName("ListMovieThumbnails")
            .WithSummary("The frames cached as thumbnails `height` pixels high (left out: 64), and their size.");
        movie.MapGet(
                "/frames/{frame:long}",
                static IResult(long frame, int? height) => throw Pending.Phase10("GetMovieFrame")
            )
            .WithName("GetMovieFrame")
            .WithSummary(
                "A cached thumbnail as JPEG (`height` left out: 64); 404 when it is not rendered (RenderMovieThumbnails)."
            )
            .Produces(StatusCodes.Status200OK, contentType: "image/jpeg")
            .Produces(StatusCodes.Status404NotFound);
        movie.MapPost(
                "/scenes",
                static Results<Accepted<Job>, NotFound, ProblemHttpResult>(SceneDetectRequest request) =>
                    throw Pending.Phase10("DetectMovieScenes")
            )
            .WithName("DetectMovieScenes")
            .WithSummary("Detect Scene Cuts: a sceneDetect job that adds scene markers at the cuts (one undo step).");
        movie.MapPost(
                "/audio/import",
                static Results<Accepted<Job>, NotFound, ProblemHttpResult>(MovieAudioImportRequest request) =>
                    throw Pending.Phase10("ImportMovieAudio")
            )
            .WithName("ImportMovieAudio")
            .WithSummary(
                "Import Audio from Movie: a movieAudioImport job that adds an audio track with the movie's audio at its "
                + "first frame (one undo step)."
            );
        movie.MapPost(
                "/audio/export",
                static Results<Accepted<Job>, NotFound, ProblemHttpResult>(MovieAudioExportRequest request) =>
                    throw Pending.Phase10("ExportMovieAudio")
            )
            .WithName("ExportMovieAudio")
            .WithSummary(
                "Export Audio to Movie: a movieAudioExport job that bounces the movie's span and writes a new movie with "
                + "the video stream copied and the bounce as its audio."
            );

        var project = api.MapGroup("/project").WithTags("Surround").AddEndpointFilter<ProblemFilter>();
        project.MapPost(
                "/export/adm",
                static Results<Accepted<Job>, NotFound, ProblemHttpResult>(AdmExportRequest request) =>
                    throw Pending.Phase10("ExportAdm")
            )
            .WithName("ExportAdm")
            .WithSummary("Exports the mix as ADM BWF (bed and objects, chna and axml): an admExport job.");
        project.MapPost(
                "/import/adm",
                static Results<Accepted<Job>, NotFound, ProblemHttpResult>(AdmImportRequest request) =>
                    throw Pending.Phase10("ImportAdm")
            )
            .WithName("ImportAdm")
            .WithSummary(
                "Imports an ADM BWF from the server's disk as bed and object tracks: an admImport job, one undo step when "
                + "it finishes."
            );

        var settings = api.MapGroup("/settings").WithTags("Settings").AddEndpointFilter<ProblemFilter>();
        settings.MapGet("/monitoring", static Ok<MonitoringSettings>() => throw Pending.Phase10("GetMonitoring"))
            .WithName("GetMonitoring")
            .WithSummary("Monitoring: binaural, the down-mix and the speaker map (user settings, not the project).");
        settings.MapPut(
                "/monitoring",
                static Results<Ok<MonitoringSettings>, ProblemHttpResult>(MonitoringSettings monitoring) =>
                    throw Pending.Phase10("SetMonitoring")
            )
            .WithName("SetMonitoring")
            .WithSummary(
                "Replaces the monitoring settings (outputs 0…256, a down-mix narrower than the master; 422 otherwise); "
                + "the host follows at once."
            );
    }
}
