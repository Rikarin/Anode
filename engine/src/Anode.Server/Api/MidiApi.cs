using Anode.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Anode.Server.Api;

/// <summary>MIDI ports, live MIDI from the editor, and which track hears live MIDI.</summary>
static class MidiApi {
    public static void Map(RouteGroupBuilder api) {
        var midi = api.MapGroup("/midi").WithTags("MIDI");
        midi.MapGet("/devices", static Ok<MidiDevices>() => throw Pending.Phase2("ListMidiDevices"))
            .WithName("ListMidiDevices")
            .WithSummary("The MIDI ports of the machine the engine runs on.");
        midi.MapPost(
                "/live",
                static Results<NoContent, NotFound, ProblemHttpResult>(LiveMidiRequest request) =>
                    throw Pending.Phase2("SendLiveMidi")
            )
            .WithName("SendLiveMidi")
            .WithSummary(
                "Plays MIDI on an instrument or external MIDI track now, as a keyboard would (and records it when the "
                + "track is armed and recording)."
            );
        midi.MapPost(
                "/thru",
                static Results<NoContent, NotFound>(MidiThruRequest request) => throw Pending.Phase2("SetMidiThru")
            )
            .WithName("SetMidiThru")
            .WithSummary(
                "Sends live MIDI to this track when no instrument track is record-armed (the selected track)."
            );
    }
}
