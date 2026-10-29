using Anode.Contracts;
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
                static Ok<IReadOnlyList<GrooveTemplate>>() => throw Pending.Phase7("ListGrooves")
            )
            .WithName("ListGrooves")
            .WithSummary("The groove templates saved in the user library.");
        library.MapPost(
                "/grooves",
                static Results<Ok<GrooveTemplate>, ProblemHttpResult>(GrooveTemplate groove) =>
                    throw Pending.Phase7("SaveGroove")
            )
            .WithName("SaveGroove")
            .WithSummary("Saves a groove template in the user library (replacing the one with its id).");
        library.MapDelete(
                "/grooves/{id}",
                static Results<NoContent, NotFound>(string id) => throw Pending.Phase7("DeleteGroove")
            )
            .WithName("DeleteGroove")
            .WithSummary("Deletes a groove template from the user library; projects keep their copies.");
        library.MapGet(
                "/transform-sets",
                static Ok<IReadOnlyList<TransformSet>>() => throw Pending.Phase7("ListTransformSets")
            )
            .WithName("ListTransformSets")
            .WithSummary(
                "The MIDI Transform factory presets (ids factory.…) followed by the user's saved sets, by name."
            );
        library.MapPost(
                "/transform-sets",
                static Results<Ok<TransformSet>, ProblemHttpResult>(TransformSet set) =>
                    throw Pending.Phase7("SaveTransformSet")
            )
            .WithName("SaveTransformSet")
            .WithSummary(
                "Saves a MIDI Transform set in the user library (replacing the one with its id; a factory preset's id "
                + "is refused: save a copy under a new id)."
            );
        library.MapDelete(
                "/transform-sets/{id}",
                static Results<NoContent, NotFound, ProblemHttpResult>(string id) =>
                    throw Pending.Phase7("DeleteTransformSet")
            )
            .WithName("DeleteTransformSet")
            .WithSummary("Deletes a user MIDI Transform set (factory presets cannot be deleted).");
        library.MapGet(
                "/articulation-sets",
                static Ok<IReadOnlyList<ArticulationSet>>() => throw Pending.Phase7("ListArticulationSets")
            )
            .WithName("ListArticulationSets")
            .WithSummary("The articulation sets saved in the user library.");
        library.MapPost(
                "/articulation-sets",
                static Results<Ok<ArticulationSet>, ProblemHttpResult>(ArticulationSet set) =>
                    throw Pending.Phase7("SaveArticulationSet")
            )
            .WithName("SaveArticulationSet")
            .WithSummary("Saves an articulation set in the user library (replacing the one with its id).");
        library.MapDelete(
                "/articulation-sets/{id}",
                static Results<NoContent, NotFound>(string id) => throw Pending.Phase7("DeleteArticulationSet")
            )
            .WithName("DeleteArticulationSet")
            .WithSummary("Deletes an articulation set from the user library; projects keep their copies.");

        var project = api.MapGroup("/project").WithTags("Project").AddEndpointFilter<ProblemFilter>();
        project.MapPost(
                "/files/{fileId}/edit",
                static Results<Accepted<Job>, NotFound, ProblemHttpResult>(
                    string fileId,
                    AudioEditOperation operation
                ) => throw Pending.Phase7("EditFile")
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
                    LoopSearchRequest request
                ) => throw Pending.Phase7("SearchLoops")
            )
            .WithName("SearchLoops")
            .WithSummary("Loop search over a range of a file: the best loops found, best first.");
        project.MapGet(
                "/files/{fileId}/samples",
                static Results<Ok<SampleRange>, NotFound, ProblemHttpResult>(
                    string fileId,
                    long start,
                    long frames,
                    int? bucket
                ) => throw Pending.Phase7("GetFileSamples")
            )
            .WithName("GetFileSamples")
            .WithSummary(
                "Samples of a file's current version for the Audio File Editor: frames from start, raw (bucket 1, the "
                + "default) or as min and max per bucket of frames; at most 65536 values per channel."
            );
        project.MapPost(
                "/files/{fileId}/preview",
                static Results<NoContent, NotFound, ProblemHttpResult>(string fileId, PreviewRangeRequest request) =>
                    throw Pending.Phase7("PreviewFileRange")
            )
            .WithName("PreviewFileRange")
            .WithSummary(
                "Plays a range of a file through the preview channel, looping if asked; POST /files/preview with a null "
                + "path stops it."
            );
        project.MapGet(
                "/step-input",
                static Results<Ok<StepInputState>, NotFound>() => throw Pending.Phase7("GetStepInput")
            )
            .WithName("GetStepInput")
            .WithSummary("The step input state (not part of the project: not saved, not undoable).");
        project.MapPut(
                "/step-input",
                static Results<Ok<StepInputState>, NotFound, ProblemHttpResult>(StepInputRequest request) =>
                    throw Pending.Phase7("SetStepInput")
            )
            .WithName("SetStepInput")
            .WithSummary(
                "Changes the step input state (left-out properties stay); a stepInput event on /ws reports it. Steps "
                + "are the stepNotes and stepTie commands."
            );
        project.MapPost(
                "/step-input/rest",
                static Results<Ok<StepInputState>, NotFound, ProblemHttpResult>() =>
                    throw Pending.Phase7("StepRest")
            )
            .WithName("StepRest")
            .WithSummary("A rest: advances the step position by one step (not an edit).");
    }
}
