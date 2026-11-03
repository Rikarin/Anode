using Anode.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Anode.Server.Api;

/// <summary>
///     Phase 10 (docs/11-phase-10.md): what remotes need beyond the editor's API: discovery (also advertised over DNS-SD
///     as <c>_anode._tcp</c>), the key-command pad sets (user settings), and listen-in: its state, and the WebSocket
///     (<c>/api/v1/remote/listen</c>) that streams the master as Opus (docs/adr/0013-video-and-listen-in.md).
/// </summary>
static class RemoteApi {
    public static void Map(RouteGroupBuilder api) {
        api.MapGet("/system/discovery", static Ok<DiscoveryInfo>() => throw Pending.Phase10("GetDiscovery"))
            .WithName("GetDiscovery")
            .WithTags("System")
            .WithSummary(
                "This Server as remotes find it: name, host and port, the open project, whether pairing is needed (what "
                + "the _anode._tcp advertisement carries)."
            );

        var remote = api.MapGroup("/remote").WithTags("Remote").AddEndpointFilter<ProblemFilter>();
        remote.MapGet("/pads", static Ok<RemotePads>() => throw Pending.Phase10("GetRemotePads"))
            .WithName("GetRemotePads")
            .WithSummary("The remote's key-command pad sets (user settings).");
        remote.MapPut(
                "/pads",
                static Results<Ok<RemotePads>, ProblemHttpResult>(RemotePads pads) =>
                    throw Pending.Phase10("SetRemotePads")
            )
            .WithName("SetRemotePads")
            .WithSummary("Replaces the pad sets (ids unique, key command ids known; 422 otherwise).");
        remote.MapGet("/listen/state", static Ok<ListenInState>() => throw Pending.Phase10("GetListenIn"))
            .WithName("GetListenIn")
            .WithSummary(
                "Listen-in: whether the master is being encoded, the listeners (at most 4), the Server's delay."
            );

        // The listen-in WebSocket: a ListenInFormat text message, then a binary message per Opus frame (ListenIn's
        // header and the packet). OpenAPI cannot describe it, as /ws.
        remote.Map(
                "/listen",
                static IResult() => TypedResults.Problem(
                    "Listen-in: not implemented yet (Phase 10).",
                    statusCode: StatusCodes.Status501NotImplemented
                )
            )
            .ExcludeFromDescription();
    }
}
