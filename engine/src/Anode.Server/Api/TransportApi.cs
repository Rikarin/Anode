using Anode.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Anode.Server.Api;

/// <summary>The transport. Its position also streams on <c>/ws</c> as <see cref="TransportEvent" />s.</summary>
static class TransportApi {
    public static void Map(RouteGroupBuilder api) {
        var transport = api.MapGroup("/transport").WithTags("Transport");
        transport.MapGet(string.Empty, static Ok<TransportStatus>() => throw Pending.Phase1("GetTransport"))
            .WithName("GetTransport");
        transport.MapPost(
                string.Empty,
                static Results<Ok<TransportStatus>, ProblemHttpResult>(TransportRequest request) =>
                    throw Pending.Phase1("ControlTransport")
            )
            .WithName("ControlTransport")
            .WithSummary("Play, stop, record or locate. Recording needs an open project and a record-armed track.");
    }
}
