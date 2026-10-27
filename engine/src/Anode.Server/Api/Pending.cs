namespace Anode.Server.Api;

/// <summary>
///     Phase 2 endpoints whose contract is fixed (docs/03-phase-2.md) but whose implementation is still to come. Every
///     use goes away with the server workstream.
/// </summary>
static class Pending {
    public static NotImplementedException Phase2(string what) => new($"{what}: not implemented yet (Phase 2).");
}
