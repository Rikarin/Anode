using Anode.Contracts;
using Anode.Server.Engine;
using Anode.Server.Library;
using Anode.Server.Projects;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Anode.Server.Api;

/// <summary>
///     Phase 7 (docs/08-phase-7.md): the user library's groove templates, MIDI Transform sets and articulation sets;
///     the Audio File Editor (operations as jobs, loop search, samples, range preview); and the step input state.
/// </summary>
static class MidiEditApi {
    public static void Map(RouteGroupBuilder api) {
        var library = api.MapGroup("/library").WithTags("Library").AddEndpointFilter<ProblemFilter>();
        library.MapGet(
                "/grooves",
                static Ok<IReadOnlyList<GrooveTemplate>>(MidiEditLibraries libraries) =>
                    TypedResults.Ok(libraries.Grooves.List())
            )
            .WithName("ListGrooves")
            .WithSummary("The groove templates saved in the user library.");
        library.MapPost(
                "/grooves",
                static Results<Ok<GrooveTemplate>, ProblemHttpResult>(
                    GrooveTemplate groove,
                    MidiEditLibraries libraries
                ) =>
                    TypedResults.Ok(libraries.Grooves.Save(groove))
            )
            .WithName("SaveGroove")
            .WithSummary("Saves a groove template in the user library (replacing the one with its id).");
        library.MapDelete(
                "/grooves/{id}",
                static Results<NoContent, NotFound>(string id, MidiEditLibraries libraries) =>
                    libraries.Grooves.Delete(id) ? TypedResults.NoContent() : TypedResults.NotFound()
            )
            .WithName("DeleteGroove")
            .WithSummary("Deletes a groove template from the user library; projects keep their copies.");
        library.MapGet(
                "/transform-sets",
                static Ok<IReadOnlyList<TransformSet>>(MidiEditLibraries libraries) =>
                    TypedResults.Ok(libraries.TransformSets())
            )
            .WithName("ListTransformSets")
            .WithSummary(
                "The MIDI Transform factory presets (ids factory.…) followed by the user's saved sets, by name."
            );
        library.MapPost(
                "/transform-sets",
                static Results<Ok<TransformSet>, ProblemHttpResult>(TransformSet set, MidiEditLibraries libraries) =>
                    TypedResults.Ok(libraries.SaveTransformSet(set))
            )
            .WithName("SaveTransformSet")
            .WithSummary(
                "Saves a MIDI Transform set in the user library (replacing the one with its id; a factory preset's id "
                + "is refused: save a copy under a new id)."
            );
        library.MapDelete(
                "/transform-sets/{id}",
                static Results<NoContent, NotFound, ProblemHttpResult>(string id, MidiEditLibraries libraries) =>
                    libraries.DeleteTransformSet(id) ? TypedResults.NoContent() : TypedResults.NotFound()
            )
            .WithName("DeleteTransformSet")
            .WithSummary("Deletes a user MIDI Transform set (factory presets cannot be deleted).");
        library.MapGet(
                "/articulation-sets",
                static Ok<IReadOnlyList<ArticulationSet>>(MidiEditLibraries libraries) =>
                    TypedResults.Ok(libraries.ArticulationSets.List())
            )
            .WithName("ListArticulationSets")
            .WithSummary("The articulation sets saved in the user library.");
        library.MapPost(
                "/articulation-sets",
                static Results<Ok<ArticulationSet>, ProblemHttpResult>(ArticulationSet set, MidiEditLibraries libraries) =>
                    TypedResults.Ok(libraries.ArticulationSets.Save(set))
            )
            .WithName("SaveArticulationSet")
            .WithSummary("Saves an articulation set in the user library (replacing the one with its id).");
        library.MapDelete(
                "/articulation-sets/{id}",
                static Results<NoContent, NotFound>(string id, MidiEditLibraries libraries) =>
                    libraries.ArticulationSets.Delete(id) ? TypedResults.NoContent() : TypedResults.NotFound()
            )
            .WithName("DeleteArticulationSet")
            .WithSummary("Deletes an articulation set from the user library; projects keep their copies.");

        var project = api.MapGroup("/project").WithTags("Project").AddEndpointFilter<ProblemFilter>();
        project.MapPost(
                "/files/{fileId}/edit",
                static Results<Accepted<Job>, NotFound, ProblemHttpResult>(
                    string fileId,
                    AudioEditOperation operation,
                    Jobs jobs
                ) => {
                    var job = jobs.EditFile(fileId, operation);
                    return TypedResults.Accepted($"/api/v1/jobs/{job.Id}", job);
                }
            )
            .WithName("EditFile")
            .WithSummary(
                "Runs an Audio File Editor operation on a file as an audioEdit job: it writes a new version of the file "
                + "and, when it finishes, points the file at it in one undo step (undo points it back)."
            );
        project.MapPost(
                "/files/{fileId}/loop-search",
                static Results<Ok<IReadOnlyList<LoopSearchResult>>, NotFound, ProblemHttpResult>(
                    string fileId,
                    LoopSearchRequest request,
                    Jobs jobs
                ) => TypedResults.Ok(jobs.SearchLoops(fileId, request))
            )
            .WithName("SearchLoops")
            .WithSummary("Loop search over a range of a file: the best loops found, best first.");
        project.MapGet(
                "/files/{fileId}/samples",
                static Results<Ok<SampleRange>, NotFound, ProblemHttpResult>(
                    string fileId,
                    long start,
                    long frames,
                    int? bucket,
                    ProjectSession session
                ) => TypedResults.Ok(session.Samples(fileId, start, frames, bucket ?? 1))
            )
            .WithName("GetFileSamples")
            .WithSummary(
                "Samples of a file's current version for the Audio File Editor: frames from start, raw (bucket 1, the "
                + "default) or as min and max per bucket of frames; at most 65536 values per channel."
            );
        project.MapPost(
                "/files/{fileId}/preview",
                static Results<NoContent, NotFound, ProblemHttpResult>(
                    string fileId,
                    PreviewRangeRequest request,
                    ProjectSession session,
                    EngineConnection engine
                ) => {
                    var (_, folder, file) = session.FileOf(fileId);
                    if (request is null
                        || request.StartFrame < 0
                        || request.EndFrame > file.Frames
                        || request.EndFrame <= request.StartFrame) {
                        throw ProjectException.Unprocessable($"Preview a range inside the file's 0…{file.Frames} frames.");
                    }

                    engine.PreviewRange(
                        Path.Combine(folder, file.Path),
                        request.StartFrame,
                        request.EndFrame,
                        request.Loop ?? false
                    );
                    return TypedResults.NoContent();
                }
            )
            .WithName("PreviewFileRange")
            .WithSummary(
                "Plays a range of a file through the preview channel, looping if asked; POST /files/preview with a null "
                + "path stops it."
            );
        project.MapGet(
                "/step-input",
                static Results<Ok<StepInputState>, NotFound>(ProjectSession session) =>
                    TypedResults.Ok(session.StepInput())
            )
            .WithName("GetStepInput")
            .WithSummary("The step input state (not part of the project: not saved, not undoable).");
        project.MapPut(
                "/step-input",
                static Results<Ok<StepInputState>, NotFound, ProblemHttpResult>(
                    StepInputRequest request,
                    ProjectSession session
                ) =>
                    TypedResults.Ok(session.SetStepInput(request ?? new()))
            )
            .WithName("SetStepInput")
            .WithSummary(
                "Changes the step input state (left-out properties stay); a stepInput event on /ws reports it. Steps "
                + "are the stepNotes and stepTie commands."
            );
        project.MapPost(
                "/step-input/rest",
                static Results<Ok<StepInputState>, NotFound, ProblemHttpResult>(ProjectSession session) =>
                    TypedResults.Ok(session.StepRest())
            )
            .WithName("StepRest")
            .WithSummary("A rest: advances the step position by one step (not an edit).");
    }
}
