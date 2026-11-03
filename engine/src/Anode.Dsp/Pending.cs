namespace Anode.Dsp;

/// <summary>
///     DSP whose contract is fixed (docs/12-phase-11.md) but whose implementation is still to come: Chord ID (workstream
///     D), and the T3 instruments and effects and the Mastering Assistant (workstream E). Every use goes away with them.
/// </summary>
static class Pending {
    public static NotImplementedException WorkstreamD() => new("Phase 11 workstream D");

    public static NotImplementedException WorkstreamE() => new("Phase 11 workstream E");
}
