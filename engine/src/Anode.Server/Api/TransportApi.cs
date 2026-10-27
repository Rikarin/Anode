using Anode.Contracts;
using Anode.Server.Engine;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Anode.Server.Api;

/// <summary>The transport. Its position also streams on <c>/ws</c> as <see cref="TransportEvent" />s.</summary>
static class TransportApi {
    public static void Map(RouteGroupBuilder api) {
        var transport = api.MapGroup("/transport").WithTags("Transport").AddEndpointFilter<ProblemFilter>();
        transport.MapGet(
                string.Empty,
                static Ok<TransportStatus>(EngineConnection engine) => TypedResults.Ok(engine.Transport())
            )
            .WithName("GetTransport");
        transport.MapPost(
                string.Empty,
                static Results<Ok<TransportStatus>, ProblemHttpResult>(
                    TransportRequest request,
                    EngineConnection engine
                ) =>
                    TypedResults.Ok(engine.Control(request))
            )
            .WithName("ControlTransport")
            .WithSummary("Play, stop, record or locate. Recording needs an open project and a record-armed track.");
    }
}
