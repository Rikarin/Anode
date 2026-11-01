using Anode.Contracts;
using System.Globalization;
using System.Xml.Linq;

namespace Anode.Interchange.MusicXml;

/// <summary>
///     Builds the MusicXML document of a score view. It runs twice: once to collect every duration as a fraction of a
///     quarter (so the divisions are their denominators' least common multiple, and every duration an exact integer),
///     then to write with those divisions.
/// </summary>
sealed class MusicXmlScoreWriter {
    readonly ScoreView view;
    readonly long? divisions;
    readonly Dictionary<string, ScoreSymbol> symbols = [];
    readonly Dictionary<string, LyricSyllable> lyrics = [];
    readonly Dictionary<string, ChordSymbol> chords = [];
    long denominators = 1;

    MusicXmlScoreWriter(ScoreView view, long? divisions) {
        this.view = view;
        this.divisions = divisions;
        foreach (var symbol in view.Symbols) {
            symbols.TryAdd(symbol.Id, symbol);
        }

        foreach (var lyric in view.Lyrics) {
            lyrics.TryAdd(lyric.Id, lyric);
        }

        foreach (var chord in view.Chords) {
            chords.TryAdd(chord.Id, chord);
        }
    }

    internal IReadOnlyDictionary<string, ScoreSymbol> Symbols => symbols;

    internal IReadOnlyDictionary<string, LyricSyllable> Lyrics => lyrics;

    internal IReadOnlyDictionary<string, ChordSymbol> Chords => chords;

    internal ScoreView View => view;

    public static XDocument Build(ScoreView view) {
        // MusicXML needs a part and a measure: a view without them is written as one empty 4/4 bar of an empty part.
        if (view.Measures.Count == 0) {
            view = view with {
                Measures = [new(1, 0, 3840, new(4, 4), new(0, KeyMode.Major), true, true, [], [], Barline.Final)]
            };
        }

        if (view.Parts.Count == 0) {
            view = view with {
                Parts = [
                    new(
                        string.Empty,
                        "Score",
                        string.Empty,
                        StaffStyleIds.Treble,
                        0,
                        false,
                        [new(Clef.Treble, 0, null, 1)],
                        []
                    )
                ]
            };
        }

        var collect = new MusicXmlScoreWriter(view, null);
        _ = collect.Parts();
        return new MusicXmlScoreWriter(view, collect.denominators).Document();
    }

    /// <summary>A duration in divisions; while collecting, it records the fraction's denominator instead.</summary>
    internal string Duration(Rational quarters) {
        if (divisions is not { } d) {
            denominators = Rational.Lcm(denominators, quarters.Denominator);
            return "0";
        }

        return Format(quarters.InDivisions(d));
    }

    internal long Divisions => divisions ?? 1;

    XDocument Document() {
        var layout = view.Layout;
        var root = new XElement(
            "score-partwise",
            new XAttribute("version", "4.0"),
            layout.Title.Length > 0 ? new XElement("work", new XElement("work-title", Clean(layout.Title))) : null,
            Identification(layout),
            Defaults(layout),
            Credits(layout),
            PartList(),
            Parts()
        );
        return new(
            new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType(
                "score-partwise",
                "-//Recordare//DTD MusicXML 4.0 Partwise//EN",
                "http://www.musicxml.org/dtds/partwise.dtd",
                null
            ),
            root
        );
    }

    static XElement Identification(ScoreLayout layout) =>
        new(
            "identification",
            layout.Composer.Length > 0
                ? new XElement("creator", new XAttribute("type", "composer"), Clean(layout.Composer))
                : null,
            new XElement(
                "encoding",
                new XElement("software", "Anode"),
                Supports("accidental"),
                Supports("beam"),
                Supports("stem")
            )
        );

    static XElement Supports(string element) =>
        new("supports", new XAttribute("element", element), new XAttribute("type", "yes"));

    XElement Defaults(ScoreLayout layout) {
        var concert = !view.Transposing && view.Parts.Any(static p => p.Staves.Any(static s => s.Transpose != 0));
        return new(
            "defaults",
            new XElement(
                "scaling",
                new XElement("millimeters", Format(layout.StaffHeightMm)),
                new XElement("tenths", "40")
            ),
            concert ? new XElement("concert-score") : null,
            new XElement(
                "page-layout",
                new XElement("page-height", Format(Tenths(layout, layout.PageHeightMm))),
                new XElement("page-width", Format(Tenths(layout, layout.PageWidthMm))),
                new XElement(
                    "page-margins",
                    new XAttribute("type", "both"),
                    new XElement("left-margin", Format(Tenths(layout, layout.Margins.Left))),
                    new XElement("right-margin", Format(Tenths(layout, layout.Margins.Right))),
                    new XElement("top-margin", Format(Tenths(layout, layout.Margins.Top))),
                    new XElement("bottom-margin", Format(Tenths(layout, layout.Margins.Bottom)))
                )
            )
        );
    }

    static IEnumerable<XElement> Credits(ScoreLayout layout) {
        var width = Tenths(layout, layout.PageWidthMm);
        var top = Tenths(layout, layout.PageHeightMm - layout.Margins.Top);
        (string Type, string Text, string Justify, double X, double Y, int Size)[] credits = [
            ("title", layout.Title, "center", width / 2, top, 24),
            ("subtitle", layout.Subtitle, "center", width / 2, top - 60, 16),
            ("composer", layout.Composer, "right", width - Tenths(layout, layout.Margins.Right), top - 100, 12)
        ];
        foreach (var credit in credits.Where(static c => c.Text.Length > 0)) {
            yield return new(
                "credit",
                new XAttribute("page", "1"),
                new XElement("credit-type", credit.Type),
                new XElement(
                    "credit-words",
                    new XAttribute("default-x", Format(credit.X)),
                    new XAttribute("default-y", Format(credit.Y)),
                    new XAttribute("font-size", Format(credit.Size)),
                    new XAttribute("justify", credit.Justify),
                    new XAttribute("valign", "top"),
                    Clean(credit.Text)
                )
            );
        }
    }

    static double Tenths(ScoreLayout layout, double mm) =>
        Math.Round(mm * 40 / (layout.StaffHeightMm > 0 ? layout.StaffHeightMm : 7), 2);

    XElement PartList() {
        var list = new XElement("part-list");
        var grouped = view.Parts.Count > 1;
        if (grouped) {
            list.Add(
                new XElement(
                    "part-group",
                    new XAttribute("type", "start"),
                    new XAttribute("number", "1"),
                    new XElement("group-symbol", "bracket"),
                    new XElement("group-barline", "yes")
                )
            );
        }

        for (var i = 0; i < view.Parts.Count; i++) {
            var part = view.Parts[i];
            var id = PartId(i);
            var drums = MusicXmlPartWriter.DrumPitches(part);
            list.Add(
                new XElement(
                    "score-part",
                    new XAttribute("id", id),
                    new XElement("part-name", Clean(part.Name.Length > 0 ? part.Name : $"Part {i + 1}")),
                    part.ShortName.Length > 0 ? new XElement("part-abbreviation", Clean(part.ShortName)) : null,
                    drums.Select(p => new XElement(
                            "score-instrument",
                            new XAttribute("id", MusicXmlPartWriter.InstrumentId(id, p)),
                            new XElement("instrument-name", MusicXmlVocabulary.DrumName(p))
                        )
                    ),
                    drums.Select(p => new XElement(
                            "midi-instrument",
                            new XAttribute("id", MusicXmlPartWriter.InstrumentId(id, p)),
                            new XElement("midi-channel", "10"),
                            new XElement("midi-unpitched", Format(p + 1))
                        )
                    )
                )
            );
        }

        if (grouped) {
            list.Add(new XElement("part-group", new XAttribute("type", "stop"), new XAttribute("number", "1")));
        }

        return list;
    }

    List<XElement> Parts() {
        var parts = new List<XElement>();
        for (var i = 0; i < view.Parts.Count; i++) {
            parts.Add(new MusicXmlPartWriter(this, view.Parts[i], PartId(i), i == 0).Write());
        }

        return parts;
    }

    static string PartId(int index) => $"P{(index + 1).ToString(CultureInfo.InvariantCulture)}";

    internal static string Format(long value) => value.ToString(CultureInfo.InvariantCulture);

    internal static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Text without the characters XML cannot hold.</summary>
    internal static string Clean(string text) =>
        text.All(System.Xml.XmlConvert.IsXmlChar) ? text : string.Concat(text.Where(System.Xml.XmlConvert.IsXmlChar));
}
