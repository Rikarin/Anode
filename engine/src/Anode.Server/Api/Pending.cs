namespace Anode.Server.Api;

/// <summary>
///     Phase 10 endpoints whose contract is fixed (docs/11-phase-10.md) but whose implementation is still to come. Every
///     use goes away with the server workstream (B).
/// </summary>
static class Pending {
    public static NotImplementedException Phase10(string what) => new($"{what}: not implemented yet (Phase 10).");
}
