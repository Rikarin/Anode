using Anode.Contracts;

namespace Anode.Interchange.MusicXml;

/// <summary>
///     MusicXML (partwise or timewise, 1.0 to 4.0; <c>.musicxml</c>, <c>.xml</c> or a <c>.mxl</c> container, told apart
///     by content) into the content of an <c>importTracks</c> command (docs/10-phase-9.md, workstream D): a track per part
///     with one MIDI region from the start (notes at concert pitch, voices, ties merged, tuplets and grace notes as short
///     notes), a staff style per distinct staff setup (clefs, transposition), the tempo, meter and key changes, and the
///     symbols, lyrics and chord symbols; ids provisional as <see cref="ImportedContent" /> says. A file that is not
///     MusicXML throws <see cref="FormatException" />.
/// </summary>
public static class MusicXmlReader {
    public static ImportedContent Read(ReadOnlySpan<byte> data) => throw Pending.WorkstreamD();
}
