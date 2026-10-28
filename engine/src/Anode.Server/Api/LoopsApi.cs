using Anode.Contracts;
using Anode.Server.Projects;
using Microsoft.AspNetCore.Http.HttpResults;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Anode.Server.Api;

/// <summary>
///     The loop library and content packs, adding loops to the project, and the Live Loops launcher (Phase 6,
///     docs/07-phase-6.md).
/// </summary>
static class LoopsApi {
    public static void Map(RouteGroupBuilder api) {
        var library = api.MapGroup("/library").WithTags("Library").AddEndpointFilter<ProblemFilter>();
        library.MapGet(
                "/loops",
                // kind and scale are their contract names (audio, pingPong …), which query binding does not parse.
                static Results<Ok<LoopPage>, ProblemHttpResult>(
                    string? text,
                    string[]? tags,
                    string? kind,
                    int? key,
                    string? scale,
                    bool? favouritesOnly,
                    int? offset,
                    int? limit
                ) => throw Pending.Phase6(
                    "QueryLoops "
                    + new LoopQuery(
                        text,
                        tags,
                        Parse(kind, ContractsJsonContext.Default.LoopKind),
                        key,
                        Parse(scale, ContractsJsonContext.Default.LoopScale),
                        favouritesOnly,
                        offset,
                        limit
                    )
                )
            )
            .WithName("QueryLoops")
            .WithSummary(
                "Searches the loop index (installed packs and the user's loops) by text, tags (all of them), kind "
                + "(a LoopKind), key (a tonic 0…11), scale (a LoopScale) and favourites, a page at a time (offset, "
                + "limit: 100 by default, at most 500), sorted by name."
            );
        library.MapPost(
                "/loops/{id}/preview",
                static Results<NoContent, NotFound, ProblemHttpResult>(string id) =>
                    throw Pending.Phase6("PreviewLoop")
            )
            .WithName("PreviewLoop")
            .WithSummary(
                "Plays a loop through the preview channel at the project's tempo and key, starting on the next bar while "
                + "the transport plays; POST /files/preview with a null path stops it."
            );
        library.MapPut(
                "/loops/{id}/favourite",
                static Results<Ok<LoopInfo>, NotFound>(string id, LoopFavouriteRequest request) =>
                    throw Pending.Phase6("SetLoopFavourite")
            )
            .WithName("SetLoopFavourite")
            .WithSummary("Marks a loop a favourite, or not.");
        library.MapPost(
                "/loops",
                static Results<Ok<LoopInfo>, NotFound, ProblemHttpResult>(AddToLoopLibraryRequest request) =>
                    throw Pending.Phase6("AddToLoopLibrary")
            )
            .WithName("AddToLoopLibrary")
            .WithSummary(
                "Add to Loop Library: saves a region of the open project as a user loop, with a sidecar, in the index."
            );
        library.MapGet(
                "/packs",
                static Ok<IReadOnlyList<Pack>>() => throw Pending.Phase6("ListPacks")
            )
            .WithName("ListPacks")
            .WithSummary("The installed content packs.");
        library.MapPost(
                "/packs",
                static Results<Ok<Pack>, NotFound, ProblemHttpResult>(InstallPackRequest request) =>
                    throw Pending.Phase6("InstallPack")
            )
            .WithName("InstallPack")
            .WithSummary("Installs a .anodepack from the server's disk and adds its loops to the index.");
        library.MapDelete(
                "/packs/{id}",
                static Results<NoContent, NotFound>(string id) => throw Pending.Phase6("RemovePack")
            )
            .WithName("RemovePack")
            .WithSummary(
                "Removes an installed pack and its loops from the index; projects keep the copies of the loops they use."
            );

        var project = api.MapGroup("/project").WithTags("Project").AddEndpointFilter<ProblemFilter>();
        project.MapPost(
                "/loops",
                static Results<Ok<CommandResult>, NotFound, ProblemHttpResult>(AddLoopRequest request) =>
                    throw Pending.Phase6("AddLoop")
            )
            .WithName("AddLoop")
            .WithSummary(
                "Adds a loop to the open project (its file copied in), following the project's tempo and key, at a "
                + "position or into a Live Loops cell, as one undoable step."
            );
        project.MapPost(
                "/live-loops/launch",
                static Results<NoContent, NotFound, ProblemHttpResult>(LaunchRequest request) =>
                    throw Pending.Phase6("LaunchCells")
            )
            .WithName("LaunchCells")
            .WithSummary(
                "Launches Live Loops cells or a scene at their quantise boundaries; cell events on /ws report their "
                + "states."
            );
        project.MapPost(
                "/live-loops/stop",
                static Results<NoContent, NotFound, ProblemHttpResult>(StopRequest request) =>
                    throw Pending.Phase6("StopCells")
            )
            .WithName("StopCells")
            .WithSummary("Stops the cells playing on some tracks (or every track) at the grid's quantise boundary.");
        project.MapPost(
                "/live-loops/record",
                static Results<NoContent, NotFound, ProblemHttpResult>(LiveRecordRequest request) =>
                    throw Pending.Phase6("RecordLiveLoops")
            )
            .WithName("RecordLiveLoops")
            .WithSummary(
                "Starts or stops recording into a cell, or a performance recording into the Tracks area (one undo step "
                + "when it stops)."
            );
    }

    /// <summary>An enum query parameter by its contract name (<c>audio</c>, <c>pingPong</c>); null when left out.</summary>
    static T? Parse<T>(string? value, JsonTypeInfo<T> type) where T : struct {
        if (value is null) {
            return null;
        }

        try {
            return JsonSerializer.Deserialize(
                JsonSerializer.Serialize(value, ContractsJsonContext.Default.String),
                type
            );
        } catch (JsonException) {
            throw ProjectException.Unprocessable($"'{value}' is not a {typeof(T).Name}.");
        }
    }
}
