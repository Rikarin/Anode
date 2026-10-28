namespace Anode.Server.Api;

/// <summary>
///     Phase 6 endpoints whose contract is fixed (docs/07-phase-6.md) but whose implementation is still to come. Every
///     use goes away with the server workstream (B).
/// </summary>
static class Pending {
    public static NotImplementedException Phase6(string what) => new($"{what}: not implemented yet (Phase 6).");
}
