namespace Anode.Server.Api;

/// <summary>
///     Phase 3 endpoints whose contract is fixed (docs/04-phase-3.md) but whose implementation is still to come. Every
///     use goes away with the server workstream.
/// </summary>
static class Pending {
    public static NotImplementedException Phase3(string what) => new($"{what}: not implemented yet (Phase 3).");
}
