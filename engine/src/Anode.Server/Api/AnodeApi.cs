namespace Anode.Server.Api;

/// <summary>
///     REST API v1 (docs/01-implementation-plan.md §4): one static class per area, each with a <c>Map(group)</c>, under
///     <c>/api/v1</c>; the realtime socket at <c>/ws</c>.
/// </summary>
public static class AnodeApi {
    public static void MapAnodeApi(this WebApplication app) {
        // Any endpoint can fail with an RFC 9457 problem (UseExceptionHandler and AddProblemDetails in Program.cs).
        var api = app.MapGroup("/api/v1").ProducesProblem(StatusCodes.Status500InternalServerError);
        SystemApi.Map(api);
        ProjectsApi.Map(api);
        ProjectApi.Map(api);
        TransportApi.Map(api);
        PluginsApi.Map(api);
        MidiApi.Map(api);
        LibraryApi.Map(api);
        MixApi.Map(api);
        FlexApi.Map(api);
        RealtimeApi.Map(app);
    }
}
