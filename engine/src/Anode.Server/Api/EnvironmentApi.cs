using Anode.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Anode.Server.Api;

/// <summary>
///     Phase 11 (docs/12-phase-11.md, docs/adr/0015-environment.md): the project's MIDI Environment, and live MIDI into
///     its on-screen objects. Edits go through the commands; monitors and faders report on <c>/ws</c>.
/// </summary>
static class EnvironmentApi {
    public static void Map(RouteGroupBuilder api) {
        var environment = api.MapGroup("/project/environment")
            .WithTags("Environment")
            .AddEndpointFilter<ProblemFilter>();
        environment.MapGet(
                string.Empty,
                static Results<Ok<MidiEnvironment>, NotFound>() => throw Pending.Phase11("GetEnvironment")
            )
            .WithName("GetEnvironment")
            .WithSummary(
                "The Environment (as Project.environment): layers, objects and cables; empty is the default input path."
            );
        environment.MapPost(
                "/objects/{objectId}/input",
                static Results<NoContent, NotFound, ProblemHttpResult>(
                    string objectId,
                    EnvironmentInputRequest request
                ) =>
                    throw Pending.Phase11("EnvironmentInput")
            )
            .WithName("EnvironmentInput")
            .WithSummary(
                "Live MIDI into an on-screen object (a keyboard plays it, a fader moves), as if it came from a device; "
                + "404 for an object that is not a keyboard or fader."
            );
    }
}
