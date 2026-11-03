namespace Anode.Interchange;

/// <summary>
///     Interchange formats whose contract is fixed (docs/11-phase-10.md) but whose implementation is still to come. Every
///     use goes away with its workstream: the ADM BWF writer and reader (D), the project mapping (B).
/// </summary>
static class Pending {
    public static NotImplementedException WorkstreamB() => new("Phase 10 workstream B");

    public static NotImplementedException WorkstreamD() => new("Phase 10 workstream D");
}
