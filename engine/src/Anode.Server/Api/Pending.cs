namespace Anode.Server.Api;

/// <summary>
///     Phase 5 endpoints whose contract is fixed (docs/06-phase-5.md) but whose implementation is still to come. Every
///     use goes away with the server workstream (B).
/// </summary>
static class Pending {
    public static NotImplementedException Phase5(string what) => new($"{what}: not implemented yet (Phase 5).");
}
