using Anode.Contracts;

namespace Anode.Interchange.MusicXml;

/// <summary>
///     MusicXML 4.0 <c>score-partwise</c> from a score view (docs/10-phase-9.md, workstream D): parts and measures as the
///     view has them, with divisions chosen per file to express every duration exactly; notes, rests, chords, voices,
///     ties, tuplets, beams, clefs, keys, times, transposition (<c>transpose</c> when the view is transposing), the
///     symbols (articulations, dynamics, slurs, wedges, text, tempo, rehearsal, pedal, octave shifts), lyrics and
///     harmony, and the layout's page, title and composer. Valid against the MusicXML 4.0 XSD.
/// </summary>
public static class MusicXmlWriter {
    /// <summary>The <c>.mxl</c> extension when compressed, else <c>.musicxml</c>.</summary>
    public static string Extension(bool compressed) => compressed ? ".mxl" : ".musicxml";

    /// <summary>
    ///     The file's bytes: UTF-8 MusicXML, or with <paramref name="compressed" /> a <c>.mxl</c> ZIP container
    ///     (<c>META-INF/container.xml</c> naming the score). The same view gives the same bytes.
    /// </summary>
    public static byte[] Write(ScoreView view, bool compressed) => throw Pending.WorkstreamD();
}
