using Anode.Contracts;
using Anode.Server.Engine;

namespace Anode.Server.Api;

/// <summary>The system information endpoint.</summary>
static class SystemApi {
    public static void Map(RouteGroupBuilder api) {
        api.MapGet(
                "/system",
                static (EngineConnection engine) =>
                    TypedResults.Ok(new SystemInfo(EngineConnection.Version, engine.Status()))
            )
            .WithName("GetSystem")
            .WithTags("System")
            .WithSummary("The server's version and its link to the engine host.");
    }
}
