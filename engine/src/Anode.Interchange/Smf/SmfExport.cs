using Anode.Contracts;
using Anode.Model;

namespace Anode.Interchange.Smf;

/// <summary>
///     The Standard MIDI File project export (docs/10-phase-9.md, owned by workstream B): the project's mapping to a file,
///     here beside the other formats, over Anode.Model's byte-level writer
///     (<c>Anode.Model.Midi.StandardMidiFileWriter</c>,
///     which B extends with meta events: tempo map, signatures, keys, markers, track names, pitch bend, programs).
/// </summary>
public static class SmfExport {
    /// <summary>
    ///     The file of <paramref name="request" /> for <paramref name="state" /> at 960 PPQ: format 0 or 1, the tracks or
    ///     region asked for, with regions' playback transpose, velocity and quantise applied as they play.
    /// </summary>
    public static byte[] Write(ProjectState state, MidiExportRequest request) => throw Pending.WorkstreamB();
}
