namespace Anode.Engine.Sync;

/// <summary>
///     Phase 8 sync engines whose contract is fixed (docs/09-phase-8.md) but whose implementation is still to come.
///     Every use goes away with the engine workstream (A).
/// </summary>
static class Pending {
    public static NotImplementedException WorkstreamA() => new("Phase 8 workstream A");
}
