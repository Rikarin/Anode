using Anode.Contracts;
using Anode.Control.Surfaces;

namespace Anode.Control.Osc;

/// <summary>What an OSC message asks of Anode.</summary>
public enum OscRequestKind {
    /// <summary>Set <c>Target</c> to <c>Value</c>, normalised 0…1 (a function or key command acts on 1).</summary>
    Set,

    /// <summary>Set <c>Target</c> (a volume or send) to <c>Value</c> dB (addresses ending <c>/db</c>).</summary>
    SetDb,

    /// <summary>Rename track <c>Target.TrackId</c> to <c>Text</c> (addresses ending <c>/name</c>).</summary>
    SetName,

    /// <summary>Move the playhead to marker <c>Number</c> (1-based, in project order).</summary>
    GoToMarker,

    /// <summary>Send feedback to the sender's host on UDP port <c>Number</c> (<c>/anode/subscribe port</c>).</summary>
    Subscribe,

    /// <summary>Stop sending feedback to the sender's host on port <c>Number</c>.</summary>
    Unsubscribe
}

/// <summary>A resolved OSC request: its kind and what it applies to (a target with its track id resolved from an index).</summary>
public sealed record OscRequest(
    OscRequestKind Kind,
    ControlTarget? Target = null,
    double Value = 0,
    string? Text = null,
    int Number = 0
);

/// <summary>
///     Anode's OSC address space (documented in <c>docs/control/osc.md</c>), as a table of addresses to targets:
///     transport, tracks by index (1-based) and id (volume, pan, mute, solo, arm, sends, names), the selected track,
///     markers, Smart Controls and key commands, under <see cref="Root" />. Values are normalised 0…1 unless the address
///     says otherwise (<c>…/db</c>, <c>…/name</c>).
/// </summary>
public static class OscAddressSpace {
    public const string Root = "/anode";

    /// <summary>
    ///     What <paramref name="message" /> asks, with tracks resolved in <paramref name="view" />; null for an address
    ///     outside the space, a track index or id that does not exist, or arguments of the wrong type.
    /// </summary>
    public static OscRequest? Resolve(OscMessage message, ProjectView view) => throw Pending.WorkstreamD();

    /// <summary>
    ///     Every feedback message for <paramref name="view" />: each address a client can subscribe to with its current
    ///     value. The server sends those whose value changed, at most 30 a second per address.
    /// </summary>
    public static IReadOnlyList<OscMessage> Feedback(ProjectView view) => throw Pending.WorkstreamD();
}
