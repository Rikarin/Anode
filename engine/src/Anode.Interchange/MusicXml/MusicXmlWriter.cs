using Anode.Contracts;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Anode.Interchange.MusicXml;

/// <summary>
///     MusicXML 4.0 <c>score-partwise</c> from a score view (docs/10-phase-9.md, workstream D;
///     docs/interchange/musicxml.md): parts and measures as the view has them, with divisions chosen per file to express
///     every duration exactly; notes, rests, chords, voices, ties, tuplets, beams, clefs, keys, times, transposition
///     (<c>transpose</c> when the view is transposing, <c>for-part</c> in a concert score), the symbols (articulations,
///     dynamics, slurs, wedges, text, tempo, rehearsal, pedal, octave shifts), lyrics and harmony, and the layout's page,
///     title and composer. Valid against the MusicXML 4.0 XSD.
/// </summary>
public static class MusicXmlWriter {
    /// <summary>The <c>.mxl</c> extension when compressed, else <c>.musicxml</c>.</summary>
    public static string Extension(bool compressed) => compressed ? ".mxl" : ".musicxml";

    /// <summary>
    ///     The file's bytes: UTF-8 MusicXML, or with <paramref name="compressed" /> a <c>.mxl</c> ZIP container
    ///     (<c>META-INF/container.xml</c> naming the score). The same view gives the same bytes.
    /// </summary>
    public static byte[] Write(ScoreView view, bool compressed) {
        ArgumentNullException.ThrowIfNull(view);
        var score = Serialize(MusicXmlScoreWriter.Build(view));
        return compressed ? MusicXmlContainer.Write(score) : score;
    }

    /// <summary>UTF-8 without a byte order mark, two-space indents and LF line ends: the same document, the same bytes.</summary>
    internal static byte[] Serialize(XDocument document) {
        using var stream = new MemoryStream();
        var settings = new XmlWriterSettings {
            Encoding = new UTF8Encoding(false),
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.Replace
        };
        using (var writer = XmlWriter.Create(stream, settings)) {
            document.Save(writer);
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }
}
