using Anode.Contracts;

namespace Anode.Control.Surfaces;

/// <summary>
///     Mackie Control and Mackie Control XT (docs/09-phase-8.md, "Control surfaces"): 14-bit motor faders with touch,
///     VPots with LED rings, the strip buttons with LEDs, the two-line scribble strip, meters, the timecode and
///     assignment displays, banking by 8 and 1, transport, jog, cursor and zoom, automation modes, name/value; VPot modes
///     pan, sends and plug-in (the selected track's insert, parameters paged by 8). An extender has the strips alone.
///     Checked against <c>engine/tests/Anode.Control.Tests/McuVectors.json</c>.
/// </summary>
public sealed class MackieControl : IControlSurface {
    public MackieControl(bool extender) {
        throw Pending.WorkstreamD();
    }

    public ControlSurfaceKind Kind => throw Pending.WorkstreamD();

    public int Strips => throw Pending.WorkstreamD();

    public int BankOffset {
        get => throw Pending.WorkstreamD();
        set => throw Pending.WorkstreamD();
    }

    public bool Connected => throw Pending.WorkstreamD();

    public IReadOnlyList<SurfaceAction> Decode(ReadOnlySpan<byte> message, long timeNs) => throw Pending.WorkstreamD();

    public IReadOnlyList<byte[]> Encode(ProjectView view) => throw Pending.WorkstreamD();

    public IReadOnlyList<byte[]> EncodeMeters(ProjectView view) => throw Pending.WorkstreamD();

    public IReadOnlyList<byte[]> Poll(long timeNs) => throw Pending.WorkstreamD();

    public void Invalidate() => throw Pending.WorkstreamD();
}
