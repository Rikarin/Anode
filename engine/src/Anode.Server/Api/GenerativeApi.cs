using Anode.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Anode.Server.Api;

/// <summary>
///     Phase 11 (docs/12-phase-11.md): the chord track and Chord ID, the Session Players' style library and their
///     regions' notes, Regenerate and Convert, and the master's Mastering Assistant
///     (docs/adr/0014-session-players-and-chord-id.md).
/// </summary>
static class GenerativeApi {
    public static void Map(RouteGroupBuilder api) {
        var chords = api.MapGroup("/project/chords").WithTags("Chords").AddEndpointFilter<ProblemFilter>();
        chords.MapGet(
                string.Empty,
                static Results<Ok<ChordTrack>, NotFound>() => throw Pending.Phase11("GetChordTrack")
            )
            .WithName("GetChordTrack")
            .WithSummary(
                "The chord track (as Project.chordTrack): chords, key and scale changes; 404 without a project."
            );
        chords.MapPost(
                "/detect",
                static Results<Accepted<Job>, NotFound, ProblemHttpResult>(ChordIdRequest request) =>
                    throw Pending.Phase11("DetectChords")
            )
            .WithName("DetectChords")
            .WithSummary(
                "Chord ID over MIDI and audio regions: a chordId job that writes the chord track (replace or merge over "
                + "the range) and the regions' detected keys, one undo step."
            );

        var library = api.MapGroup("/library").WithTags("Session Players").AddEndpointFilter<ProblemFilter>();
        library.MapGet(
                "/session-players",
                static Ok<SessionPlayerCatalog>() => throw Pending.Phase11("GetSessionPlayerCatalog")
            )
            .WithName("GetSessionPlayerCatalog")
            .WithSummary(
                "The style library's catalogue: its version and every Drummer, Bass and Keyboard Player style."
            );

        var region = api.MapGroup("/project/regions/{regionId}/session-player")
            .WithTags("Session Players")
            .AddEndpointFilter<ProblemFilter>();
        region.MapGet(
                string.Empty,
                static Results<Ok<SessionPlayerNotes>, NotFound>(string regionId) =>
                    throw Pending.Phase11("GetSessionPlayerNotes")
            )
            .WithName("GetSessionPlayerNotes")
            .WithSummary(
                "A Session Player region's generated notes (relative to its start) and their input hash, for the "
                + "editor to draw; 404 when it is not a Session Player region."
            );
        region.MapPost(
                "/regenerate",
                static Results<Ok<CommandResult>, NotFound, ProblemHttpResult>(
                    string regionId,
                    RegenerateRequest request
                ) =>
                    throw Pending.Phase11("RegenerateSessionPlayer")
            )
            .WithName("RegenerateSessionPlayer")
            .WithSummary("Regenerate: applies regenerateSessionPlayer to the region (a new seed; one undo step).");
        region.MapPost(
                "/convert",
                static Results<Accepted<Job>, NotFound, ProblemHttpResult>(
                    string regionId,
                    SessionPlayerConvertRequest request
                ) =>
                    throw Pending.Phase11("ConvertSessionPlayer")
            )
            .WithName("ConvertSessionPlayer")
            .WithSummary(
                "Convert to MIDI (or, from a Drummer, to a pattern): a sessionPlayerConvert job that replaces the "
                + "regions by regions of their generated notes, one undo step."
            );

        var mastering = api.MapGroup("/project/mastering").WithTags("Mastering").AddEndpointFilter<ProblemFilter>();
        mastering.MapGet(
                string.Empty,
                static Results<Ok<MasteringState>, NotFound>() => throw Pending.Phase11("GetMastering")
            )
            .WithName("GetMastering")
            .WithSummary(
                "The master's Mastering Assistant: its insert, settings and analysis; 404 when the master has none."
            );
        mastering.MapPut(
                string.Empty,
                static Results<Ok<CommandResult>, NotFound, ProblemHttpResult>(MasteringSettings settings) =>
                    throw Pending.Phase11("SetMastering")
            )
            .WithName("SetMastering")
            .WithSummary("Replaces the assistant's settings: applies setMastering (one undo step); 404 without one.");
        mastering.MapPost(
                "/analyse",
                static Results<Accepted<Job>, NotFound, ProblemHttpResult>(MasteringAnalysisRequest request) =>
                    throw Pending.Phase11("AnalyseMastering")
            )
            .WithName("AnalyseMastering")
            .WithSummary(
                "Analyse: a masteringAnalysis job that renders the master before the assistant offline (adding one "
                + "last when asked), analyses it and stores the analysis in it (one undo step)."
            );
    }
}
