using Anode.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Anode.Server.Api;

/// <summary>File analysis (transients, beats, pitch), bounce in place and selection-based processing (Phase 5).</summary>
static class FlexApi {
    public static void Map(RouteGroupBuilder api) {
        var project = api.MapGroup("/project").WithTags("Project");
        project.MapGet(
                "/files/{fileId}/analysis",
                static Results<Ok<FileAnalysis>, NotFound>(string fileId) => throw Pending.Phase5("GetFileAnalysis")
            )
            .WithName("GetFileAnalysis")
            .WithSummary(
                "A file's cached analysis: transients and beats (analysed now when missing), and its pitch curve and "
                + "notes once an analyze job has run."
            );
        project.MapPost(
                "/files/{fileId}/analyze",
                static Results<Accepted<Job>, NotFound, ProblemHttpResult>(string fileId) =>
                    throw Pending.Phase5("AnalyzeFile")
            )
            .WithName("AnalyzeFile")
            .WithSummary(
                "Starts pitch analysis of a file (Flex Pitch) as a job; an analysis event on /ws follows when it is cached."
            );
        project.MapPost(
                "/bounce-in-place",
                static Results<Accepted<Job>, NotFound, ProblemHttpResult>(BounceInPlaceRequest request) =>
                    throw Pending.Phase5("BounceInPlace")
            )
            .WithName("BounceInPlace")
            .WithSummary(
                "Renders tracks or regions in place to new files; when the job finishes, adds a track or replaces the "
                + "regions and leaves, mutes or deletes the source, in one undo step."
            );
        project.MapPost(
                "/process",
                static Results<Accepted<Job>, NotFound, ProblemHttpResult>(ProcessRequest request) =>
                    throw Pending.Phase5("ProcessRegions")
            )
            .WithName("ProcessRegions")
            .WithSummary(
                "Selection-based processing: runs a chain of built-in effects over part of regions; when the job "
                + "finishes, that part becomes a new file and region, in one undo step."
            );
    }
}
