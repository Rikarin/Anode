using Anode.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Anode.Server.Api;

/// <summary>Creating, opening and listing projects. One project is open at a time.</summary>
static class ProjectsApi {
    public static void Map(RouteGroupBuilder api) {
        var projects = api.MapGroup("/projects").WithTags("Projects");
        projects.MapGet(
                "/recent",
                static Ok<IReadOnlyList<ProjectSummary>>() => throw Pending.Phase1("ListRecentProjects")
            )
            .WithName("ListRecentProjects")
            .WithSummary("Recently opened projects, most recent first.");
        projects.MapPost(
                string.Empty,
                static Results<Ok<Project>, ProblemHttpResult>(CreateProjectRequest request) =>
                    throw Pending.Phase1("CreateProject")
            )
            .WithName("CreateProject")
            .WithSummary(
                "Creates a project folder and opens it, closing the open one (unsaved edits are kept in its backup)."
            );
        projects.MapPost(
                "/open",
                static Results<Ok<Project>, ProblemHttpResult>(OpenProjectRequest request) =>
                    throw Pending.Phase1("OpenProject")
            )
            .WithName("OpenProject")
            .WithSummary("Opens a project folder, closing the open one.");
    }
}
