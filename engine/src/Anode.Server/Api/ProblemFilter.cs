using Anode.Server.Projects;
using System.Globalization;

namespace Anode.Server.Api;

/// <summary>Turns <see cref="ProjectException" /> into an RFC 9457 problem response, as Cathode's RigExceptionFilter.</summary>
sealed class ProblemFilter : IEndpointFilter {
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) {
        try {
            return await next(context);
        } catch (ProjectException e) {
            return TypedResults.Problem(e.Message, statusCode: e.Status);
        }
    }
}

/// <summary>The revision as ETag and If-Match (docs/adr/0004-state-ownership.md).</summary>
static class Revisions {
    public static void SetETag(HttpContext http, long revision) =>
        http.Response.Headers.ETag = $"\"{revision.ToString(CultureInfo.InvariantCulture)}\"";

    /// <summary>The revision from <c>If-Match</c>, if the client sent one (<c>*</c> matches any).</summary>
    public static long? Expected(HttpContext http) {
        var header = http.Request.Headers.IfMatch.ToString().Trim();
        if (header.StartsWith("W/", StringComparison.Ordinal)) {
            header = header[2..];
        }

        header = header.Trim('"');
        return long.TryParse(header, NumberStyles.Integer, CultureInfo.InvariantCulture, out var revision)
            ? revision
            : header is { Length: > 0 } and not "*"
                ? throw ProjectException.Conflict($"If-Match '{header}' is not a revision of this project.")
                : null;
    }
}
