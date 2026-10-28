namespace Anode.Server.Api;

/// <summary>
///     Phase 4 endpoints whose contract is fixed (docs/05-phase-4.md) but whose implementation is still to come. Every
///     use goes away with the server workstream.
/// </summary>
static class Pending {
    public static NotImplementedException Phase4(string what) => new($"{what}: not implemented yet (Phase 4).");
}
