namespace Anode.ContentBuilder;

/// <summary>
///     Content whose entry point is fixed (docs/12-phase-11.md) but whose making is still to come: the Drummer kits
///     (workstream D) and the tape sets (workstream E). Every use goes away with them.
/// </summary>
static class Pending {
    public static NotImplementedException WorkstreamD() => new("Phase 11 workstream D");

    public static NotImplementedException WorkstreamE() => new("Phase 11 workstream E");
}
