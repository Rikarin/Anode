using Anode.Contracts;
using Anode.Server.Projects;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Anode.Server.Api;

/// <summary>Creating, opening and listing projects. One project is open at a time.</summary>
static class ProjectsApi {
    public static void Map(RouteGroupBuilder api) {
        var projects = api.MapGroup("/projects").WithTags("Projects").AddEndpointFilter<ProblemFilter>();
        projects.MapGet(
                "/recent",
                static Ok<IReadOnlyList<ProjectSummary>>(RecentProjects recent) => TypedResults.Ok(recent.List())
            )
            .WithName("ListRecentProjects")
            .WithSummary("Recently opened projects, most recent first.");
        projects.MapPost(
                string.Empty,
                static Results<Ok<Project>, ProblemHttpResult>(
                    CreateProjectRequest request,
                    ProjectSession session,
                    HttpContext http
                ) => Respond(http, session.Create(request))
            )
            .WithName("CreateProject")
            .WithSummary(
                "Creates a project folder and opens it, closing the open one (unsaved edits are kept in its backup)."
            );
        projects.MapPost(
                "/open",
                static Results<Ok<Project>, ProblemHttpResult>(
                    OpenProjectRequest request,
                    ProjectSession session,
                    HttpContext http
                ) => Respond(http, session.OpenFolder(request.Path))
            )
            .WithName("OpenProject")
            .WithSummary("Opens a project folder, closing the open one.");
        projects.MapPost(
                "/contents",
                static Results<Ok<ProjectContents>, NotFound, ProblemHttpResult>(OpenProjectRequest request, Jobs jobs) =>
                    TypedResults.Ok(jobs.Contents(request.Path))
            )
            .WithName("GetProjectContents")
            .WithSummary("What a project on the server's disk holds, read without opening it (Import from Project).");
        projects.MapGet(
                "/recoverable",
                static Ok<IReadOnlyList<RecoverableProject>>(ProjectSession session) =>
                    TypedResults.Ok(session.Recoverable())
            )
            .WithName("ListRecoverableProjects")
            .WithSummary("Recent projects with autosaved edits their last save does not have.");
        projects.MapPost(
                "/recover",
                static Results<Ok<Project>, NotFound, ProblemHttpResult>(
                    OpenProjectRequest request,
                    ProjectSession session,
                    HttpContext http
                ) => Respond(http, session.Recover(request.Path))
            )
            .WithName("RecoverProject")
            .WithSummary(
                "Opens a project with its autosaved edits, closing the open one. The saved project is untouched until "
                + "the next save; the edits show as unsaved."
            );
        projects.MapPost(
                "/recoverable/discard",
                static Results<NoContent, NotFound, ProblemHttpResult>(
                    OpenProjectRequest request,
                    ProjectSession session
                ) => {
                    session.DiscardRecovery(request.Path);
                    return TypedResults.NoContent();
                }
            )
            .WithName("DiscardRecovery")
            .WithSummary("Stops offering a project's autosaved edits; they stay available as its newest backup.");
    }

    static Ok<Project> Respond(HttpContext http, Project project) {
        Revisions.SetETag(http, project.Revision);
        return TypedResults.Ok(project);
    }
}
