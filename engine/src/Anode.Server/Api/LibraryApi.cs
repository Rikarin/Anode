using Anode.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Anode.Server.Api;

/// <summary>
///     The open project's alternatives, backups and screensets; the user's key commands and bookmarks; and the All
///     Files browser over the server's disk, with a preview through the engine.
/// </summary>
static class LibraryApi {
    public static void Map(RouteGroupBuilder api) {
        var project = api.MapGroup("/project").WithTags("Project");
        project.MapGet(
                "/alternatives",
                static Results<Ok<IReadOnlyList<ProjectAlternative>>, NotFound>() =>
                    throw Pending.Phase3("ListAlternatives")
            )
            .WithName("ListAlternatives");
        project.MapPost(
                "/alternatives",
                static Results<Ok<ProjectAlternative>, NotFound, ProblemHttpResult>(CreateAlternativeRequest request) =>
                    throw Pending.Phase3("CreateAlternative")
            )
            .WithName("CreateAlternative")
            .WithSummary("Saves the open project as a new alternative and makes it current.");
        project.MapPost(
                "/alternatives/{id}/open",
                static Results<Ok<Project>, NotFound, ProblemHttpResult>(string id) =>
                    throw Pending.Phase3("OpenAlternative")
            )
            .WithName("OpenAlternative")
            .WithSummary("Switches to another alternative (unsaved edits are kept in a backup first).");
        project.MapDelete(
                "/alternatives/{id}",
                static Results<NoContent, NotFound, ProblemHttpResult>(string id) =>
                    throw Pending.Phase3("DeleteAlternative")
            )
            .WithName("DeleteAlternative")
            .WithSummary("Deletes an alternative other than the current one.");
        project.MapGet(
                "/backups",
                static Results<Ok<IReadOnlyList<ProjectBackup>>, NotFound>() =>
                    throw Pending.Phase3("ListBackups")
            )
            .WithName("ListBackups");
        project.MapPost(
                "/backups/{id}/revert",
                static Results<Ok<Project>, NotFound, ProblemHttpResult>(string id) =>
                    throw Pending.Phase3("RevertToBackup")
            )
            .WithName("RevertToBackup")
            .WithSummary(
                "Replaces the open project with a backup of it (an undoable step is not kept: like reopening)."
            );
        project.MapGet(
                "/screensets",
                static Results<Ok<IReadOnlyList<Screenset>>, NotFound>() =>
                    throw Pending.Phase3("ListScreensets")
            )
            .WithName("ListScreensets");
        project.MapPut(
                "/screensets/{number:int}",
                static Results<Ok<Screenset>, NotFound, ProblemHttpResult>(int number, Screenset screenset) =>
                    throw Pending.Phase3("SaveScreenset")
            )
            .WithName("SaveScreenset")
            .WithSummary("Saves screenset 1…9 in the project (not an undo step; it marks the project unsaved).");
        project.MapPost(
                "/files/import-midi",
                static Results<Ok<CommandResult>, NotFound, ProblemHttpResult>(ImportFileRequest request) =>
                    throw Pending.Phase3("ImportMidiFile")
            )
            .WithName("ImportMidiFile")
            .WithSummary(
                "Imports a Standard MIDI File from the server's disk as new instrument tracks with its tempo and "
                + "signature events, as one undoable step."
            );

        var settings = api.MapGroup("/settings").WithTags("Settings");
        settings.MapGet("/key-commands", static Ok<KeyCommandSet>() => throw Pending.Phase3("GetKeyCommands"))
            .WithName("GetKeyCommands")
            .WithSummary("The user's key bindings; empty bindings mean the editor's defaults.");
        settings.MapPut(
                "/key-commands",
                static Results<Ok<KeyCommandSet>, ProblemHttpResult>(KeyCommandSet set) =>
                    throw Pending.Phase3("SaveKeyCommands")
            )
            .WithName("SaveKeyCommands");
        settings.MapGet("/bookmarks", static Ok<Bookmarks>() => throw Pending.Phase3("GetBookmarks"))
            .WithName("GetBookmarks");
        settings.MapPut(
                "/bookmarks",
                static Results<Ok<Bookmarks>, ProblemHttpResult>(Bookmarks bookmarks) =>
                    throw Pending.Phase3("SaveBookmarks")
            )
            .WithName("SaveBookmarks");

        var files = api.MapGroup("/files").WithTags("Files");
        files.MapGet(
                string.Empty,
                static Results<Ok<DirectoryListing>, NotFound, ProblemHttpResult>(string? path) =>
                    throw Pending.Phase3("ListFiles")
            )
            .WithName("ListFiles")
            .WithSummary("A folder on the server's disk (the user's home when path is left out).");
        files.MapPost(
                "/preview",
                static Results<NoContent, NotFound, ProblemHttpResult>(PreviewRequest request) =>
                    throw Pending.Phase3("PreviewFile")
            )
            .WithName("PreviewFile")
            .WithSummary("Plays an audio file through the engine's preview channel; a null path stops it.");
    }
}
