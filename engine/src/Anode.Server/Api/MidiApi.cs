using Anode.Contracts;
using Anode.Server.Engine;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Anode.Server.Api;

/// <summary>MIDI ports, live MIDI from the editor, and which track hears live MIDI.</summary>
static class MidiApi {
    public static void Map(RouteGroupBuilder api) {
        var midi = api.MapGroup("/midi").WithTags("MIDI").AddEndpointFilter<ProblemFilter>();
        midi.MapGet("/devices", static Ok<MidiDevices>(EngineCatalog catalog) => TypedResults.Ok(catalog.Devices))
            .WithName("ListMidiDevices")
            .WithSummary("The MIDI ports of the machine the engine runs on.");
        midi.MapPost(
                "/live",
                static Results<NoContent, NotFound, ProblemHttpResult>(
                    LiveMidiRequest request,
                    EngineConnection engine
                ) =>
                    engine.SendLiveMidi(request) ? TypedResults.NoContent() : TypedResults.NotFound()
            )
            .WithName("SendLiveMidi")
            .WithSummary(
                "Plays MIDI on an instrument or external MIDI track now, as a keyboard would (and records it when the "
                + "track is armed and recording)."
            );
        midi.MapPost(
                "/thru",
                static Results<NoContent, NotFound>(MidiThruRequest request, EngineConnection engine) =>
                    engine.SetThru(request) ? TypedResults.NoContent() : TypedResults.NotFound()
            )
            .WithName("SetMidiThru")
            .WithSummary(
                "Sends live MIDI to this track when no instrument track is record-armed (the selected track)."
            );
    }
}
