namespace Anode.Engine.Environment;

/// <summary>
///     The Environment's graph and objects, whose contract is fixed (docs/12-phase-11.md) but whose implementation is
///     still to come. Every use goes away with the engine workstream (A).
/// </summary>
static class Pending {
    public static NotImplementedException WorkstreamA() => new("Phase 11 workstream A");
}
