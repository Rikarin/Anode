using Anode.Contracts;
using Anode.Server.Engine;
using Anode.Server.Realtime;

namespace Anode.Server.Api;

/// <summary>
///     <c>/ws</c>: the realtime event stream. The first message is <see cref="ConnectedEvent" />; the client then fetches
///     the state it shows and applies the events that follow.
/// </summary>
static class RealtimeApi {
    public static void Map(WebApplication app) {
        app.Map(
                "/ws",
                static async (HttpContext http, EventHub hub) => {
                    if (!http.WebSockets.IsWebSocketRequest) {
                        return Results.BadRequest();
                    }

                    using var socket = await http.WebSockets.AcceptWebSocketAsync();
                    await hub.ServeAsync(socket, new ConnectedEvent(EngineConnection.Version), http.RequestAborted);
                    return Results.Empty;
                }
            )
            .ExcludeFromDescription();
    }
}
