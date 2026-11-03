namespace Anode.Media;

/// <summary>
///     Media work whose contract is fixed (docs/11-phase-10.md) but whose implementation is still to come. Every use goes
///     away with the media workstream (E).
/// </summary>
static class Pending {
    public static NotImplementedException WorkstreamE() => new("Phase 10 workstream E");
}
