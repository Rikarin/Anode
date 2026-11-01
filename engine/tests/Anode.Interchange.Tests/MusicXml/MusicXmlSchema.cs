using System.IO.Compression;
using System.Xml;
using System.Xml.Schema;

namespace Anode.Interchange.Tests.MusicXml;

/// <summary>
///     The MusicXML 4.0 XSD (Schema/, from the W3C Music Notation Community Group's repository at v4.0, fetched by
///     scripts/fetch-musicxml-schema.mjs; W3C Community Final Specification Agreement), with the xml and xlink schemas it
///     imports resolved locally, and the container schema for <c>.mxl</c>'s <c>META-INF/container.xml</c>.
/// </summary>
static class MusicXmlSchema {
    static readonly Lazy<XmlSchemaSet> Score = new(() => Load("musicxml.xsd"));
    static readonly Lazy<XmlSchemaSet> Container = new(() => Load("container.xsd"));

    public static string Directory { get; } = Path.Combine(AppContext.BaseDirectory, "MusicXml", "Schema");

    /// <summary>The validation errors of a MusicXML document (empty: valid).</summary>
    public static IReadOnlyList<string> Errors(byte[] xml) => Validate(xml, Score.Value);

    public static IReadOnlyList<string> ContainerErrors(byte[] xml) => Validate(xml, Container.Value);

    /// <summary>Asserts a written file (plain or <c>.mxl</c>) is valid, its container too.</summary>
    public static void AssertValid(byte[] file) {
        if (file is [(byte)'P', (byte)'K', ..]) {
            using var zip = new ZipArchive(new MemoryStream(file), ZipArchiveMode.Read);
            Assert.Empty(ContainerErrors(Entry(zip, "META-INF/container.xml")));
            Assert.Empty(Errors(Entry(zip, "score.musicxml")));
            return;
        }

        Assert.Empty(Errors(file));
    }

    public static byte[] Entry(ZipArchive zip, string name) {
        using var stream = zip.GetEntry(name)!.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    static List<string> Validate(byte[] xml, XmlSchemaSet schemas) {
        var errors = new List<string>();
        var settings = new XmlReaderSettings {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            ValidationType = ValidationType.Schema,
            Schemas = schemas,
            ValidationFlags = XmlSchemaValidationFlags.ReportValidationWarnings
        };
        settings.ValidationEventHandler += (_, e) => errors.Add($"{e.Severity} {e.Exception.LineNumber}:{e.Exception.LinePosition} {e.Message}");
        using var reader = XmlReader.Create(new MemoryStream(xml), settings);
        while (reader.Read()) {
        }

        return errors;
    }

    static XmlSchemaSet Load(string file) {
        var set = new XmlSchemaSet { XmlResolver = new LocalResolver() };
        using (var reader = XmlReader.Create(Path.Combine(Directory, file), new() { DtdProcessing = DtdProcessing.Ignore })) {
            set.Add(null, reader);
        }

        set.Compile();
        return set;
    }

    /// <summary>The schema's imports (http://www.musicxml.org/xsd/xml.xsd, …/xlink.xsd) from the local copies.</summary>
    sealed class LocalResolver : XmlResolver {
        public override object? GetEntity(Uri absoluteUri, string? role, Type? ofObjectToReturn) {
            var name = Path.GetFileName(absoluteUri.AbsolutePath);
            var path = Path.Combine(Directory, name);
            return File.Exists(path) ? File.OpenRead(path) : throw new XmlException($"No local schema for {absoluteUri}.");
        }
    }
}
