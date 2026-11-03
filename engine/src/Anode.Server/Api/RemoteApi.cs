using Anode.Contracts;
using Anode.Server.Library;
using Anode.Server.Projects;
using Anode.Server.Realtime;
using Anode.Server.Remote;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Anode.Server.Api;

/// <summary>
///     Phase 10 (docs/11-phase-10.md): what remotes need beyond the editor's API: discovery (also advertised over DNS-SD
///     as <c>_anode._tcp</c>), the key-command pad sets (user settings), and listen-in: its state, and the WebSocket
///     (<c>/api/v1/remote/listen</c>) that streams the master as Opus (docs/adr/0013-video-and-listen-in.md).
/// </summary>
static class RemoteApi {
    public static void Map(RouteGroupBuilder api) {
        api.MapGet(
                "/system/discovery",
                static Ok<DiscoveryInfo>(DiscoveryService discovery) => TypedResults.Ok(discovery.Info())
            )
            .WithName("GetDiscovery")
            .WithTags("System")
            .WithSummary(
                "This Server as remotes find it: name, host and port, the open project, whether pairing is needed (what "
                + "the _anode._tcp advertisement carries)."
            );

        var remote = api.MapGroup("/remote").WithTags("Remote").AddEndpointFilter<ProblemFilter>();
        remote.MapGet("/pads", static Ok<RemotePads>(UserSettings settings) => TypedResults.Ok(settings.RemotePads()))
            .WithName("GetRemotePads")
            .WithSummary("The remote's key-command pad sets (user settings).");
        remote.MapPut(
                "/pads",
                static Results<Ok<RemotePads>, ProblemHttpResult>(RemotePads pads, UserSettings settings) =>
                    TypedResults.Ok(settings.SaveRemotePads(pads))
            )
            .WithName("SetRemotePads")
            .WithSummary("Replaces the pad sets (ids unique, key command ids known; 422 otherwise).");
        remote.MapPost(
                "/pads/run",
                static Results<NoContent, ProblemHttpResult>(RunKeyCommandRequest request, EventHub hub) => {
                    if (request?.CommandId is not { Length: > 0 and <= UserSettings.MaxCommandIdLength } id
                        || string.IsNullOrWhiteSpace(id)) {
                        throw ProjectException.Unprocessable(
                            $"A key command id has 1…{UserSettings.MaxCommandIdLength} characters."
                        );
                    }

                    hub.Publish(new KeyCommandEvent(id));
                    return TypedResults.NoContent();
                }
            )
            .WithName("RunRemoteKeyCommand")
            .WithSummary(
                "Asks the desktop editor to run a key command the remote cannot run itself: broadcast as the keyCommand "
                + "event, as a controller assignment's (422 for a malformed id)."
            );
        remote.MapGet(
                "/listen/state",
                static Ok<ListenInState>(ListenInService listen) => TypedResults.Ok(listen.State())
            )
            .WithName("GetListenIn")
            .WithSummary(
                "Listen-in: whether the master is being encoded, the listeners (at most 4), the Server's delay."
            );

        // The listen-in WebSocket: a ListenInFormat text message, then a binary message per Opus frame (ListenIn's
        // header and the packet). OpenAPI cannot describe it, as /ws.
        api.Map(
                "/remote/listen",
                static async (HttpContext http, ListenInService listen) => {
                    if (!http.WebSockets.IsWebSocketRequest) {
                        return Results.BadRequest();
                    }

                    if (listen.TryJoin() is not { } listener) {
                        return TypedResults.Problem(
                            $"At most {ListenIn.MaxListeners} remotes listen in at once.",
                            statusCode: StatusCodes.Status503ServiceUnavailable
                        );
                    }

                    try {
                        using var socket = await http.WebSockets.AcceptWebSocketAsync();
                        await listen.ServeAsync(socket, listener, http.RequestAborted);
                    } finally {
                        listen.Leave(listener);
                    }

                    return Results.Empty;
                }
            )
            .ExcludeFromDescription();
    }
}
