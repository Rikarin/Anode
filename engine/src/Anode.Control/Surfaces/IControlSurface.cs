using Anode.Contracts;

namespace Anode.Control.Surfaces;

/// <summary>What a surface's control did, as the server acts on it.</summary>
public enum SurfaceActionKind {
    /// <summary>A fader or VPot set <c>Address</c> of <c>TrackId</c> to <c>Value</c> (0…1; a fader's position).</summary>
    SetParameter,

    /// <summary>A VPot stepped <c>Address</c> of <c>TrackId</c> by <c>Value</c> steps (signed; 1/128 of the range each).</summary>
    StepParameter,

    /// <summary>A fader was touched (<c>Value</c> 1) or released (0): a gesture on <c>Address</c> of <c>TrackId</c>.</summary>
    Touch,

    /// <summary>A strip's mute button: <c>Value</c> is the state it asks for (1 on, 0 off), the opposite of the view's.</summary>
    Mute,

    /// <summary>A strip's solo button, as <see cref="Mute" />.</summary>
    Solo,

    /// <summary>A strip's record button, as <see cref="Mute" />.</summary>
    RecordArm,

    /// <summary>Selects <c>TrackId</c>.</summary>
    Select,

    Play,
    Stop,
    Record,

    /// <summary>Rewind pressed (<c>Value</c> 1) or released (0).</summary>
    Rewind,

    /// <summary>Forward pressed (<c>Value</c> 1) or released (0).</summary>
    Forward,

    Cycle,

    /// <summary>The jog wheel turned <c>Value</c> steps (signed).</summary>
    Jog,

    CursorUp,
    CursorDown,
    CursorLeft,
    CursorRight,

    /// <summary>Zoom turned on (<c>Value</c> 1) or off (0): the cursor keys zoom while on.</summary>
    Zoom,

    /// <summary>
    ///     A bank or channel button moved the bank: from a lone surface, <c>Value</c> is its new bank offset; through a
    ///     <see cref="SurfaceGroup" />, the group's new <see cref="SurfaceGroup.BankBase" /> (every unit already set).
    /// </summary>
    Banked,

    /// <summary>The selected track's automation mode becomes <c>Automation</c>.</summary>
    SetAutomationMode
}

/// <summary>
///     One action decoded from a surface's input: its kind and what it applies to (the surface resolves strips to tracks
///     and VPots to addresses from the last <see cref="ProjectView" /> it encoded): strip index 0…7 (−1 none), track id,
///     automation address, value, automation mode.
/// </summary>
public readonly record struct SurfaceAction(
    SurfaceActionKind Kind,
    int Strip = -1,
    string? TrackId = null,
    string? Address = null,
    double Value = 0,
    AutomationMode Automation = AutomationMode.Read
);

/// <summary>
///     A control surface's protocol and model (docs/09-phase-8.md, workstream D): it decodes the surface's input into
///     actions and encodes a project view into feedback, sending only what changed since the last encode (faders that
///     are touched are not sent). It holds the bank offset, VPot mode, plug-in page and name/value display. Pure: the
///     server owns the ports, the clock and the rates (faders and LEDs at most 30 times a second, meters 10). Used from
///     one thread.
/// </summary>
public interface IControlSurface {
    ControlSurfaceKind Kind { get; }

    /// <summary>Its channel strips (8).</summary>
    int Strips { get; }

    /// <summary>The index in <see cref="ProjectView.Tracks" /> of the track on strip 0; extenders' are set by the server.</summary>
    int BankOffset { get; set; }

    /// <summary>Whether the surface answers: Mackie Control's handshake or any input recently; HUI's ping replies.</summary>
    bool Connected { get; }

    /// <summary>One message from the surface (a channel message or a SysEx), arriving at <paramref name="timeNs" />.</summary>
    IReadOnlyList<SurfaceAction> Decode(ReadOnlySpan<byte> message, long timeNs);

    /// <summary>The messages that bring the surface up to <paramref name="view" />, meters aside.</summary>
    IReadOnlyList<byte[]> Encode(ProjectView view);

    /// <summary>The meter messages for <paramref name="view" />.</summary>
    IReadOnlyList<byte[]> EncodeMeters(ProjectView view);

    /// <summary>
    ///     Called about every 100 ms, and after each batch of <see cref="Decode" />: the replies <c>Decode</c> queued (the
    ///     handshake's, HUI's ping answer), messages the protocol sends on its own (the device query, HUI's ping), and
    ///     the connection timeout.
    /// </summary>
    IReadOnlyList<byte[]> Poll(long timeNs);

    /// <summary>
    ///     Forgets what was sent, so the next <see cref="Encode" /> sends everything (and the meters their levels). A
    ///     unit does it itself when it first connects.
    /// </summary>
    void Invalidate();
}

/// <summary>Makes the protocol object for a surface kind.</summary>
public static class ControlSurfaces {
    public static IControlSurface Create(ControlSurfaceKind kind) =>
        kind switch {
            ControlSurfaceKind.MackieControl => new MackieControl(false),
            ControlSurfaceKind.MackieControlXt => new MackieControl(true),
            ControlSurfaceKind.Hui => new Hui(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown control surface kind.")
        };
}
