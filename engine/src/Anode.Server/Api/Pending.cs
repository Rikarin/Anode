namespace Anode.Server.Api;

/// <summary>
///     Phase 11 endpoints whose contract is fixed (docs/12-phase-11.md) but whose implementation is still to come. Every
///     use goes away with the server workstream (B).
/// </summary>
static class Pending {
    public static NotImplementedException Phase11(string what) => new($"{what}: not implemented yet (Phase 11).");
}
