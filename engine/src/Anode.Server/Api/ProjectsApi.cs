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
    }

    static Ok<Project> Respond(HttpContext http, Project project) {
        Revisions.SetETag(http, project.Revision);
        return TypedResults.Ok(project);
    }
}
