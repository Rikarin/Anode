using Anode.Contracts;
using Anode.Model.Editing;
using Anode.Server.Projects;
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
                static Results<Ok<ChordTrack>, NotFound>(ProjectSession session) =>
                    session.View is { } view ? TypedResults.Ok(view.State.ChordTrack) : TypedResults.NotFound()
            )
            .WithName("GetChordTrack")
            .WithSummary(
                "The chord track (as Project.chordTrack): chords, key and scale changes; 404 without a project."
            );
        chords.MapPost(
                "/detect",
                static Results<Accepted<Job>, NotFound, ProblemHttpResult>(ChordIdRequest request, Jobs jobs) =>
                    Accepted(jobs.DetectChords(request))
            )
            .WithName("DetectChords")
            .WithSummary(
                "Chord ID over MIDI and audio regions: a chordId job that writes the chord track (replace or merge over "
                + "the range) and the regions' detected keys, one undo step."
            );

        var library = api.MapGroup("/library").WithTags("Session Players").AddEndpointFilter<ProblemFilter>();
        library.MapGet(
                "/session-players",
                static Ok<SessionPlayerCatalog>(ISessionPlayerLibrary players) => TypedResults.Ok(players.Catalog)
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
                static Results<Ok<SessionPlayerNotes>, NotFound>(
                    string regionId,
                    ProjectSession session,
                    SessionPlayers players
                ) => {
                    if (session.View is not { } view
                        || !view.State.SessionPlayerRegions.TryGetValue(regionId, out var region)) {
                        return TypedResults.NotFound();
                    }

                    var notes = players.Notes(view, region);
                    return TypedResults.Ok(new SessionPlayerNotes(regionId, notes.Hash, notes.Notes));
                }
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
                    RegenerateRequest request,
                    ProjectSession session
                ) =>
                    session.View?.State.SessionPlayerRegions.ContainsKey(regionId) == true
                        ? TypedResults.Ok(
                            session.Apply(new([new RegenerateSessionPlayerCommand([regionId], request.Seed)]), null)
                        )
                        : TypedResults.NotFound()
            )
            .WithName("RegenerateSessionPlayer")
            .WithSummary("Regenerate: applies regenerateSessionPlayer to the region (a new seed; one undo step).");
        region.MapPost(
                "/convert",
                static Results<Accepted<Job>, NotFound, ProblemHttpResult>(
                    string regionId,
                    SessionPlayerConvertRequest request,
                    Jobs jobs
                ) =>
                    Accepted(jobs.ConvertSessionPlayer(regionId, request))
            )
            .WithName("ConvertSessionPlayer")
            .WithSummary(
                "Convert to MIDI (or, from a Drummer, to a pattern): a sessionPlayerConvert job that replaces the "
                + "regions by regions of their generated notes, one undo step."
            );

        var mastering = api.MapGroup("/project/mastering").WithTags("Mastering").AddEndpointFilter<ProblemFilter>();
        mastering.MapGet(
                string.Empty,
                static Results<Ok<MasteringState>, NotFound>(ProjectSession session) =>
                    session.View is { } view && Edits.MasteringAssistantOf(view.State) is { } insert
                        ? TypedResults.Ok(
                            new MasteringState(insert.Id, Edits.MasteringSettingsOf(insert), insert.Mastering)
                        )
                        : TypedResults.NotFound()
            )
            .WithName("GetMastering")
            .WithSummary(
                "The master's Mastering Assistant: its insert, settings and analysis; 404 when the master has none."
            );
        mastering.MapPut(
                string.Empty,
                static Results<Ok<CommandResult>, NotFound, ProblemHttpResult>(
                    MasteringSettings settings,
                    ProjectSession session
                ) =>
                    session.View is { } view && Edits.MasteringAssistantOf(view.State) is not null
                        ? TypedResults.Ok(
                            session.Apply(
                                new(
                                    [
                                        new SetMasteringCommand(
                                            null,
                                            settings.Character,
                                            settings.TargetLufs,
                                            settings.CeilingDbtp,
                                            settings.Eq,
                                            settings.Dynamics,
                                            settings.Width,
                                            settings.MatchedBypass
                                        )
                                    ]
                                ),
                                null
                            )
                        )
                        : TypedResults.NotFound()
            )
            .WithName("SetMastering")
            .WithSummary("Replaces the assistant's settings: applies setMastering (one undo step); 404 without one.");
        mastering.MapPost(
                "/analyse",
                static Results<Accepted<Job>, NotFound, ProblemHttpResult>(
                    MasteringAnalysisRequest request,
                    Jobs jobs
                ) =>
                    Accepted(jobs.AnalyseMastering(request))
            )
            .WithName("AnalyseMastering")
            .WithSummary(
                "Analyse: a masteringAnalysis job that renders the master before the assistant offline (adding one "
                + "last when asked), analyses it and stores the analysis in it (one undo step)."
            );
    }

    static Accepted<Job> Accepted(Job job) => TypedResults.Accepted($"/api/v1/jobs/{job.Id}", job);
}
