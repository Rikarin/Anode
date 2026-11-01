using Anode.Contracts;
using System.Xml;
using System.Xml.Linq;

namespace Anode.Interchange.MusicXml;

/// <summary>
///     MusicXML <c>score-partwise</c> (1.0 to 4.0; <c>.musicxml</c>, <c>.xml</c> or a <c>.mxl</c> container, told apart
///     by content) into the content of an <c>importTracks</c> command (docs/10-phase-9.md, workstream D;
///     docs/interchange/musicxml.md): a track per part with one MIDI region from the start (notes at concert pitch,
///     ties merged, tuplets exact, grace notes as short notes before the beat), a staff style per part (a factory one when
///     its clefs and transposition match, else one per distinct staff setup), the tempo, meter and key changes, rehearsal
///     marks as markers, and the symbols, lyrics and chord symbols; ids provisional as <see cref="ImportedContent" />
///     says. <c>score-timewise</c> is refused. Anything that is not readable MusicXML throws
///     <see cref="FormatException" />, and nothing else.
/// </summary>
public static class MusicXmlReader {
    /// <summary>The largest file read.</summary>
    public const long MaxBytes = MusicXmlContainer.MaxEntryBytes;

    static readonly XmlReaderSettings Settings = new() {
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        MaxCharactersInDocument = MaxBytes,
        IgnoreProcessingInstructions = true,
        IgnoreComments = true
    };

    public static ImportedContent Read(ReadOnlySpan<byte> data) {
        if (data.Length > MaxBytes) {
            throw new FormatException($"The file is larger than a MusicXML file Anode reads ({MaxBytes >> 20} MB).");
        }

        try {
            var bytes = MusicXmlContainer.IsZip(data) ? MusicXmlContainer.Read(data) : data.ToArray();
            return new MusicXmlScoreReader(Parse(bytes)).Read();
        } catch (FormatException) {
            throw;
        } catch (Exception e) when (e is not OutOfMemoryException) {
            // XML, ZIP and arithmetic failures alike: the file is not readable MusicXML.
            throw new FormatException($"Not a readable MusicXML file: {e.Message}", e);
        }
    }

    /// <summary>An XML document without DTD processing or external resolution.</summary>
    internal static XDocument Parse(byte[] bytes) {
        using var stream = new MemoryStream(bytes, false);
        using var reader = XmlReader.Create(stream, Settings);
        return XDocument.Load(reader);
    }
}
