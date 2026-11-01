namespace Anode.Server.Api;

/// <summary>
///     Phase 9 endpoints whose contract is fixed (docs/10-phase-9.md) but whose implementation is still to come. Every
///     use goes away with the server workstream (B).
/// </summary>
static class Pending {
    public static NotImplementedException Phase9(string what) => new($"{what}: not implemented yet (Phase 9).");
}
