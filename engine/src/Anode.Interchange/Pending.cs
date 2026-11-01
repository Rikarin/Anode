namespace Anode.Interchange;

/// <summary>
///     Interchange formats whose contract is fixed (docs/10-phase-9.md) but whose implementation is still to come. Every
///     use goes away with its workstream: MusicXML (D), AAF (E), the Standard MIDI File project mapping (B).
/// </summary>
static class Pending {
    public static NotImplementedException WorkstreamB() => new("Phase 9 workstream B");

    public static NotImplementedException WorkstreamD() => new("Phase 9 workstream D");

    public static NotImplementedException WorkstreamE() => new("Phase 9 workstream E");
}
