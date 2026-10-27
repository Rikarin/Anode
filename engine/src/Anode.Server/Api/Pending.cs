namespace Anode.Server.Api;

/// <summary>
///     Phase 1 endpoints whose contract is fixed (docs/02-phase-1.md) but whose implementation is still to come. Every
///     use goes away with the server workstream.
/// </summary>
static class Pending {
    public static NotImplementedException Phase1(string what) => new($"{what}: not implemented yet (Phase 1).");
}
