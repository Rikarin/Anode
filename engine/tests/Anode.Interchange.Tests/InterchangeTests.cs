using Anode.Interchange.MusicXml;

namespace Anode.Interchange.Tests;

/// <summary>Placeholder until workstreams D's and E's schema, round-trip and fixture tests (docs/10-phase-9.md).</summary>
public sealed class InterchangeTests {
    [Fact]
    public void A_compressed_MusicXml_file_is_an_mxl() =>
        Assert.Equal((".mxl", ".musicxml"), (MusicXmlWriter.Extension(true), MusicXmlWriter.Extension(false)));
}
