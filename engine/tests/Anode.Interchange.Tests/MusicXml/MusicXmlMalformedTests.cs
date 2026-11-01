using Anode.Interchange.MusicXml;
using System.IO.Compression;
using System.Text;

namespace Anode.Interchange.Tests.MusicXml;

/// <summary>What is not readable MusicXML is refused with a <see cref="FormatException" />, and nothing else escapes.</summary>
public sealed class MusicXmlMalformedTests {
    static string Score(string measure) =>
        $"""
         <score-partwise version="4.0">
           <part-list><score-part id="P1"><part-name>A</part-name></score-part></part-list>
           <part id="P1"><measure number="1">{measure}</measure></part>
         </score-partwise>
         """;

    public static TheoryData<string, string> Documents() =>
        new() {
            { "empty", string.Empty },
            { "not XML", "MThd this is not a score" },
            { "another document", "<html><body/></html>" },
            { "timewise", """<score-timewise version="4.0"><part-list/><measure number="1"/></score-timewise>""" },
            { "truncated", Score("<note><pitch><step>C</step><octave>4</octave></pitch>")[..120] },
            { "no parts", """<score-partwise version="4.0"><part-list/></score-partwise>""" },
            { "a note without a step", Score("<note><pitch><octave>4</octave></pitch><duration>1</duration></note>") },
            { "a note without an octave", Score("<note><pitch><step>C</step></pitch><duration>1</duration></note>") },
            {
                "an unknown step",
                Score("<note><pitch><step>H</step><octave>4</octave></pitch><duration>1</duration></note>")
            },
            { "a note without a duration", Score("<note><pitch><step>C</step><octave>4</octave></pitch></note>") },
            {
                "a duration that is not a number",
                Score("<note><pitch><step>C</step><octave>4</octave></pitch><duration>x</duration></note>")
            },
            { "a negative duration", Score("<note><rest/><duration>-4</duration></note>") },
            {
                "zero divisions",
                Score("<attributes><divisions>0</divisions></attributes><note><rest/><duration>1</duration></note>")
            },
            { "a huge duration", Score("<note><rest/><duration>79228162514264337593543950335</duration></note>") },
            {
                "an external entity",
                """<?xml version="1.0"?><!DOCTYPE s [<!ENTITY x SYSTEM "file:///etc/passwd">]><score-partwise>&x;</score-partwise>"""
            }
        };

    [Theory]
    [MemberData(nameof(Documents))]
    public void A_damaged_document_is_refused(string what, string xml) {
        var e = Assert.Throws<FormatException>(() => MusicXmlReader.Read(Encoding.UTF8.GetBytes(xml)));
        Assert.False(string.IsNullOrWhiteSpace(e.Message), what);
    }

    [Fact]
    public void Timewise_scores_are_refused_with_a_clear_message() {
        var e = Assert.Throws<FormatException>(static () =>
            MusicXmlReader.Read("<score-timewise version=\"4.0\"><part-list/></score-timewise>"u8)
        );
        Assert.Contains("score-timewise", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_container_without_a_score_is_refused() {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true)) {
            using var entry = zip.CreateEntry("readme.txt").Open();
            entry.Write("hello"u8);
        }

        Assert.Throws<FormatException>(() => MusicXmlReader.Read(stream.ToArray()));
        Assert.Throws<FormatException>(() => MusicXmlReader.Read(stream.ToArray().AsSpan(0, 30)));
    }

    [Fact]
    public void Damaged_copies_of_valid_files_fail_only_with_format_errors() {
        // Truncations and byte flips of exported and hand-written files: each either reads or throws FormatException.
        var random = new Random(9);
        byte[][] files = [
            MusicXmlWriter.Write(MusicXmlRoundTripTests.PianoView(), false),
            MusicXmlWriter.Write(MusicXmlRoundTripTests.PianoView(), true),
            MusicXmlFixtureTests.Fixture("string-quartet.musicxml"),
            MusicXmlFixtureTests.Fixture("song.mxl")
        ];
        foreach (var file in files) {
            for (var i = 0; i < 150; i++) {
                var damaged = (byte[])file.Clone();
                if (i % 3 == 0) {
                    damaged = damaged[..random.Next(damaged.Length)];
                } else {
                    for (var flips = 0; flips < 1 + i % 4; flips++) {
                        damaged[random.Next(damaged.Length)] = (byte)random.Next(256);
                    }
                }

                try {
                    MusicXmlReader.Read(damaged);
                } catch (FormatException e) {
                    Assert.False(string.IsNullOrEmpty(e.Message));
                }
            }
        }
    }
}
