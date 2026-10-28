using Anode.Contracts;
using Anode.Server.Engine;
using Anode.Server.Library;
using Anode.Server.Projects;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Anode.Server.Api;

/// <summary>
///     The open project's alternatives, backups and screensets; the user's key commands and bookmarks; and the All
///     Files browser over the server's disk, with a preview through the engine.
/// </summary>
static class LibraryApi {
    public static void Map(RouteGroupBuilder api) {
        var project = api.MapGroup("/project").WithTags("Project").AddEndpointFilter<ProblemFilter>();
        project.MapGet(
                "/alternatives",
                static Results<Ok<IReadOnlyList<ProjectAlternative>>, NotFound>(ProjectSession session) =>
                    TypedResults.Ok(session.Alternatives())
            )
            .WithName("ListAlternatives");
        project.MapPost(
                "/alternatives",
                static async Task<Results<Ok<ProjectAlternative>, NotFound, ProblemHttpResult>>(
                    CreateAlternativeRequest request,
                    ProjectSession session,
                    EngineConnection engine,
                    CancellationToken cancellationToken
                ) => {
                    session.Get(); // 404 before asking the engine anything
                    var states = await engine.InstrumentStatesAsync(cancellationToken);
                    return TypedResults.Ok(session.CreateAlternative(request.Name, states));
                }
            )
            .WithName("CreateAlternative")
            .WithSummary("Saves the open project as a new alternative and makes it current.");
        project.MapPost(
                "/alternatives/{id}/open",
                static Results<Ok<Project>, NotFound, ProblemHttpResult>(string id, ProjectSession session) =>
                    TypedResults.Ok(session.OpenAlternative(id))
            )
            .WithName("OpenAlternative")
            .WithSummary("Switches to another alternative (unsaved edits are kept in a backup first).");
        project.MapDelete(
                "/alternatives/{id}",
                static Results<NoContent, NotFound, ProblemHttpResult>(string id, ProjectSession session) => {
                    session.DeleteAlternative(id);
                    return TypedResults.NoContent();
                }
            )
            .WithName("DeleteAlternative")
            .WithSummary("Deletes an alternative other than the current one.");
        project.MapGet(
                "/backups",
                static Results<Ok<IReadOnlyList<ProjectBackup>>, NotFound>(ProjectSession session) =>
                    TypedResults.Ok(session.Backups())
            )
            .WithName("ListBackups");
        project.MapPost(
                "/backups/{id}/revert",
                static Results<Ok<Project>, NotFound, ProblemHttpResult>(string id, ProjectSession session) =>
                    TypedResults.Ok(session.RevertToBackup(id))
            )
            .WithName("RevertToBackup")
            .WithSummary(
                "Replaces the open project with a backup of it (an undoable step is not kept: like reopening)."
            );
        project.MapGet(
                "/screensets",
                static Results<Ok<IReadOnlyList<Screenset>>, NotFound>(ProjectSession session) =>
                    TypedResults.Ok(session.Screensets())
            )
            .WithName("ListScreensets");
        project.MapPut(
                "/screensets/{number:int}",
                static Results<Ok<Screenset>, NotFound, ProblemHttpResult>(
                    int number,
                    Screenset screenset,
                    ProjectSession session
                ) => TypedResults.Ok(session.SaveScreenset(number, screenset))
            )
            .WithName("SaveScreenset")
            .WithSummary("Saves screenset 1…9 in the project (not an undo step; it marks the project unsaved).");
        project.MapPost(
                "/files/import-midi",
                static Results<Ok<CommandResult>, NotFound, ProblemHttpResult>(
                    ImportFileRequest request,
                    ProjectSession session,
                    HttpContext http
                ) => {
                    var result = session.ImportMidi(request.Path);
                    Revisions.SetETag(http, result.Revision);
                    return TypedResults.Ok(result);
                }
            )
            .WithName("ImportMidiFile")
            .WithSummary(
                "Imports a Standard MIDI File from the server's disk as new instrument tracks with its tempo and "
                + "signature events, as one undoable step."
            );

        var settings = api.MapGroup("/settings").WithTags("Settings").AddEndpointFilter<ProblemFilter>();
        settings.MapGet(
                "/key-commands",
                static Ok<KeyCommandSet>(UserSettings user) => TypedResults.Ok(user.KeyCommands())
            )
            .WithName("GetKeyCommands")
            .WithSummary("The user's key bindings; empty bindings mean the editor's defaults.");
        settings.MapPut(
                "/key-commands",
                static Results<Ok<KeyCommandSet>, ProblemHttpResult>(KeyCommandSet set, UserSettings user) =>
                    TypedResults.Ok(user.SaveKeyCommands(set))
            )
            .WithName("SaveKeyCommands");
        settings.MapGet("/bookmarks", static Ok<Bookmarks>(UserSettings user) => TypedResults.Ok(user.Bookmarks()))
            .WithName("GetBookmarks");
        settings.MapPut(
                "/bookmarks",
                static Results<Ok<Bookmarks>, ProblemHttpResult>(Bookmarks bookmarks, UserSettings user) =>
                    TypedResults.Ok(user.SaveBookmarks(bookmarks))
            )
            .WithName("SaveBookmarks");

        var files = api.MapGroup("/files").WithTags("Files").AddEndpointFilter<ProblemFilter>();
        files.MapGet(
                string.Empty,
                static Results<Ok<DirectoryListing>, NotFound, ProblemHttpResult>(string? path) =>
                    TypedResults.Ok(FileBrowser.List(path))
            )
            .WithName("ListFiles")
            .WithSummary("A folder on the server's disk (the user's home when path is left out).");
        files.MapPost(
                "/preview",
                static Results<NoContent, NotFound, ProblemHttpResult>(
                    PreviewRequest request,
                    EngineConnection engine
                ) => {
                    engine.Preview(request.Path);
                    return TypedResults.NoContent();
                }
            )
            .WithName("PreviewFile")
            .WithSummary("Plays an audio file through the engine's preview channel; a null path stops it.");
    }
}
