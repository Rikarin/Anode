using Anode.Contracts;
using Anode.Server.Engine;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Anode.Server.Api;

/// <summary>The plugins the engine can load: Anode's built-in instruments and the CLAP plugins it found.</summary>
static class PluginsApi {
    public static void Map(RouteGroupBuilder api) {
        var plugins = api.MapGroup("/plugins").WithTags("Plugins").AddEndpointFilter<ProblemFilter>();
        plugins.MapGet(
                string.Empty,
                static Ok<IReadOnlyList<PluginInfo>>(EngineCatalog catalog) => TypedResults.Ok(catalog.Plugins)
            )
            .WithName("ListPlugins")
            .WithSummary("Every plugin the engine can load, built-in first; empty while no engine is connected.");
        plugins.MapPost(
                "/rescan",
                static Results<Accepted, ProblemHttpResult>(EngineConnection engine) => {
                    engine.Rescan();
                    return TypedResults.Accepted((string?)null);
                }
            )
            .WithName("RescanPlugins")
            .WithSummary("Rescans the CLAP folders; pluginsChanged follows on /ws when the catalogue is in.");
    }
}
