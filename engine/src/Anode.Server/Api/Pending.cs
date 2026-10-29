namespace Anode.Server.Api;

/// <summary>
///     Phase 7 endpoints whose contract is fixed (docs/08-phase-7.md) but whose implementation is still to come. Every
///     use goes away with the server workstream (B).
/// </summary>
static class Pending {
    public static NotImplementedException Phase7(string what) => new($"{what}: not implemented yet (Phase 7).");
}
