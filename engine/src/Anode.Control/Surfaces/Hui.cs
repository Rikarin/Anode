using Anode.Contracts;

namespace Anode.Control.Surfaces;

/// <summary>
///     HUI (docs/09-phase-8.md, "Control surfaces"): the ping, zones and ports, faders with touch, VPots, the
///     4-character strips, meters, transport, banking. Checked against
///     <c>engine/tests/Anode.Control.Tests/HuiVectors.json</c>.
/// </summary>
public sealed class Hui : IControlSurface {
    public Hui() {
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
