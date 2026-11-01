using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace Anode.Interchange.MusicXml;

/// <summary>
///     The compressed MusicXML container (<c>.mxl</c>): a ZIP with a <c>mimetype</c> entry (stored, first),
///     <c>META-INF/container.xml</c> naming the root file, and the score. Written deterministically (fixed timestamps,
///     fixed order); read by the container's first MusicXML root file.
/// </summary>
static class MusicXmlContainer {
    public const string MimeType = "application/vnd.recordare.musicxml";
    public const string ScoreMediaType = "application/vnd.recordare.musicxml+xml";
    public const string ScoreEntry = "score.musicxml";
    const string ContainerEntry = "META-INF/container.xml";

    /// <summary>The largest uncompressed score the reader accepts from a container.</summary>
    public const long MaxEntryBytes = 128L << 20;

    static readonly DateTimeOffset Timestamp = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static bool IsZip(ReadOnlySpan<byte> data) => data is [(byte)'P', (byte)'K', 3, 4, ..];

    public static byte[] Write(byte[] score) {
        var container = new XDocument(
            new("1.0", "UTF-8", null),
            new XElement(
                "container",
                new XElement(
                    "rootfiles",
                    new XElement(
                        "rootfile",
                        new XAttribute("full-path", ScoreEntry),
                        new XAttribute("media-type", ScoreMediaType)
                    )
                )
            )
        );

        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true)) {
            Add(zip, "mimetype", Encoding.ASCII.GetBytes(MimeType), CompressionLevel.NoCompression);
            Add(zip, ContainerEntry, MusicXmlWriter.Serialize(container), CompressionLevel.Optimal);
            Add(zip, ScoreEntry, score, CompressionLevel.Optimal);
        }

        return stream.ToArray();
    }

    /// <summary>The root score's bytes from a container; a damaged or empty container throws <see cref="FormatException" />.</summary>
    public static byte[] Read(ReadOnlySpan<byte> data) {
        using var zip = new ZipArchive(new MemoryStream([..data]), ZipArchiveMode.Read);
        var path = RootPath(zip);
        var entry = (path is null ? null : zip.GetEntry(path))
            ?? zip.Entries.FirstOrDefault(static e =>
                !e.FullName.StartsWith("META-INF/", StringComparison.Ordinal)
                && (e.FullName.EndsWith(".musicxml", StringComparison.OrdinalIgnoreCase)
                    || e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            )
            ?? throw new FormatException("The .mxl container holds no MusicXML score.");
        return ReadEntry(entry);
    }

    static string? RootPath(ZipArchive zip) {
        var entry = zip.GetEntry(ContainerEntry);
        if (entry is null) {
            return null;
        }

        var document = MusicXmlReader.Parse(ReadEntry(entry));
        var roots = document.Root?.Element("rootfiles")?.Elements("rootfile") ?? [];
        return roots
            .Where(static r => (string?)r.Attribute("media-type") is null or ScoreMediaType)
            .Select(static r => (string?)r.Attribute("full-path"))
            .FirstOrDefault(static p => !string.IsNullOrEmpty(p));
    }

    static byte[] ReadEntry(ZipArchiveEntry entry) {
        if (entry.Length > MaxEntryBytes) {
            throw new FormatException($"'{entry.FullName}' in the .mxl container is too large.");
        }

        using var source = entry.Open();
        using var target = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = source.Read(buffer)) > 0) {
            if (target.Length + read > MaxEntryBytes) {
                throw new FormatException($"'{entry.FullName}' in the .mxl container is too large.");
            }

            target.Write(buffer, 0, read);
        }

        return target.ToArray();
    }

    static void Add(ZipArchive zip, string name, byte[] bytes, CompressionLevel level) {
        var entry = zip.CreateEntry(name, level);
        entry.LastWriteTime = Timestamp;
        using var stream = entry.Open();
        stream.Write(bytes);
    }
}
