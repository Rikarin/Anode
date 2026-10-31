namespace Anode.Server.Api;

/// <summary>
///     Phase 8 endpoints whose contract is fixed (docs/09-phase-8.md) but whose implementation is still to come. Every
///     use goes away with the server workstream (B).
/// </summary>
static class Pending {
    public static NotImplementedException Phase8(string what) => new($"{what}: not implemented yet (Phase 8).");
}
