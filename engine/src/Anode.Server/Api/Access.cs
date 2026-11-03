using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Anode.Server.Api;

/// <summary>
///     Who may call the API. With <see cref="ServerOptions.Token" /> set (the desktop app always sets one, and a Server
///     reachable beyond localhost must) every request needs <c>Authorization: Bearer &lt;token&gt;</c>, or
///     <c>?access_token=</c> on the WebSockets (<c>/ws</c> and Phase 10's listen-in) and the movie's stream and frames, to
///     which browsers cannot give headers;
///     any origin may then call, as a page
///     cannot forge the token. Without a token (development) only loopback origins pass CORS, so an arbitrary web page
///     cannot drive the engine through the browser.
/// </summary>
public static class Access {
    public static void AddAnodeCors(this IServiceCollection services) => services.AddCors();

    public static void UseAnodeAccess(this WebApplication app, string? token) {
        app.UseCors(policy => policy.SetIsOriginAllowed(origin => token is not null || IsLoopbackOrigin(origin))
            .AllowAnyHeader()
            .AllowAnyMethod()
            .WithExposedHeaders("ETag")
        );

        if (token is null) {
            // Browsers apply no CORS to WebSockets, so check the origin here: no foreign page may listen in.
            app.Use(static async (context, next) => {
                    if (IsSocket(context.Request)
                        && context.Request.Headers.Origin.ToString() is { Length: > 0 } origin
                        && !IsLoopbackOrigin(origin)) {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return;
                    }

                    await next(context);
                }
            );
            return;
        }

        var expected = Encoding.UTF8.GetBytes(token);
        app.Use(async (context, next) => {
                var presented = Presented(context.Request);
                // Out of the query once read, so nothing logged later (a request's URL in a log line) carries it.
                StripQueryToken(context.Request);
                if (HttpMethods.IsOptions(context.Request.Method)
                    || (presented is not null
                        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), expected))) {
                    await next(context);
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
            }
        );
    }

    /// <summary>Refuses to expose an unauthenticated Server beyond this machine.</summary>
    public static void RequireTokenBeyondLoopback(string urls, string? token) {
        if (token is not null) {
            return;
        }

        foreach (var url in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            var host = new Uri(
                url.Replace("*", "0.0.0.0", StringComparison.Ordinal)
                    .Replace(
                        "+",
                        "0.0.0.0",
                        StringComparison.Ordinal
                    )
            ).Host;
            if (host != "localhost" && !(IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address))) {
                throw new InvalidOperationException(
                    $"Listening on {url} needs an access token: set Anode:Token (or the ANODE__TOKEN variable)."
                );
            }
        }
    }

    static string? Presented(HttpRequest request) {
        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) {
            return header[7..].Trim();
        }

        return IsSocket(request) || IsMedia(request) ? request.Query["access_token"].ToString() : null;
    }

    /// <summary>
    ///     The URLs elements load themselves (<c>&lt;video src&gt;</c>, <c>&lt;img src&gt;</c>, the editor's
    ///     <c>mediaUrl</c>), which take the token in the query as the WebSockets do: the movie's stream and frames.
    /// </summary>
    static bool IsMedia(HttpRequest request) =>
        (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
        && (request.Path.StartsWithSegments(MoviePath + "/stream")
            || request.Path.StartsWithSegments(MoviePath + "/frames"));

    /// <summary>The movie's endpoints (Phase 10).</summary>
    const string MoviePath = "/api/v1/project/movie";

    /// <summary>The WebSockets, which take the token in the query and need the origin check: <c>/ws</c> and listen-in.</summary>
    static bool IsSocket(HttpRequest request) =>
        request.Path.StartsWithSegments("/ws") || request.Path.StartsWithSegments(ListenInPath);

    /// <summary>The listen-in WebSocket (Phase 10, docs/11-phase-10.md).</summary>
    public const string ListenInPath = "/api/v1/remote/listen";

    static void StripQueryToken(HttpRequest request) {
        if (!request.Query.ContainsKey("access_token")) {
            return;
        }

        request.QueryString = QueryString.Create(
            request.Query.Where(static q => q.Key != "access_token")
                .SelectMany(static q => q.Value.Select(v => KeyValuePair.Create(q.Key, v)))
        );
    }

    static bool IsLoopbackOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && (uri.Host == "localhost"
            || (IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address)));
}
