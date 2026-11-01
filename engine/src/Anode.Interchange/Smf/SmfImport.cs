using Anode.Contracts;
using Anode.Model;
using Anode.Model.Midi;

namespace Anode.Interchange.Smf;

/// <summary>
///     The Standard MIDI File project import (docs/10-phase-9.md, owned by workstream B), over Anode.Model's reader
///     (<c>StandardMidiFile</c>, which B extends with markers, keys and programs).
/// </summary>
public static class SmfImport {
    /// <summary>
    ///     The <c>importTracks</c> content of <paramref name="file" /> for <paramref name="state" />: an instrument track
    ///     per file track with events (one region from its first bar to the bar after its last note), or one track with
    ///     everything when <c>merge</c>; the tempo map and signatures when <c>importTempo</c>; the markers when
    ///     <c>importMarkers</c>.
    /// </summary>
    public static ImportedContent ToContent(SmfFile file, ProjectState state, MidiImportRequest request) =>
        throw Pending.WorkstreamB();
}
