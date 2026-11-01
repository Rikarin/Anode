using Anode.Contracts;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Xml.Linq;

namespace Anode.Notation.Tests;

/// <summary>
///     The exit criterion's step 1 region, golden views and MEI (rewritten with <c>ANODE_UPDATE_GOLDEN=1</c>), MEI
///     against the MEI 5 RELAX NG schema (<c>Schema/mei-all.rng</c>, MEI 5.0's <c>mei-all</c>, ECL-2.0, through
///     <c>xmllint</c> when it is installed) and its structure (ids, references, measures, layers), and determinism.
/// </summary>
public sealed class MeiTests {
    static readonly XNamespace Mei = "http://www.music-encoding.org/ns/mei";
    static readonly XNamespace Xml = XNamespace.Xml;

    static readonly JsonSerializerOptions Json = new(ContractsJsonContext.Default.Options) {
        TypeInfoResolver = JsonTypeInfoResolver.Combine(ContractsJsonContext.Default, new DefaultJsonTypeInfoResolver()),
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static TheoryData<string> Named => ["exit-criterion", "exit-criterion-eighths", "content", "content-part", "content-transposing", "drums", "meters"];

    static ScoreView View(string name) {
        switch (name) {
            case "exit-criterion":
                return NotationEngine.Render(Cases.ExitCriterion().State, ProjectScore.Empty, new());
            case "exit-criterion-eighths":
                return NotationEngine.Render(Cases.ExitCriterion(480, 320).State, ProjectScore.Empty, new());
            case "content": {
                var (state, score, _, _) = Cases.Content();
                return NotationEngine.Render(state, score, new("scs_both", Mode: ScoreViewMode.Page));
            }
            case "content-part": {
                var (state, score, _, clarinet) = Cases.Content();
                return NotationEngine.Render(state, score, new("scs_both", clarinet));
            }
            case "content-transposing": {
                var (state, score, _, _) = Cases.Content();
                return NotationEngine.Render(state, score with { Layout = score.Layout with { TransposingScore = true } }, new());
            }
            case "drums":
                return NotationEngine.Render(Cases.Drums(), ProjectScore.Empty, new());
            default:
                return NotationEngine.Render(Cases.Meters(), ProjectScore.Empty, new());
        }
    }

    [Fact]
    public void The_exit_criterion_region_shows_the_expected_score() {
        var (state, _, _) = Cases.ExitCriterion();
        var view = NotationEngine.Render(state, ProjectScore.Empty, new());
        Assert.Equal(
            """
            m1 s0 v1: C4+E4+G4/q D4/8t1b1 E4/8t1b1 F4/8t1b1 G4/h~
            m1 s1 v1: C3/w
            m2 s0 v1: G4/q E5/q~ E5/h
            m2 s0 v2: r/q C5/q D5/q B4/q
            m2 s1 v1: F2/h G2/h
            m3 s0 v1: E4/16b1 F4/16b1 G4/8b1 C4+E4+G4/q~ C4+E4+G4/h
            m3 s1 v1: C3/w
            """,
            Describe.View(view).Trim()
        );
        var stems = view.Parts[0].Measures[1].Staves[0].Voices.Select(v => v.Events.Where(e => e.Notes.Count > 0).Select(e => e.Stem).Distinct().Single());
        Assert.Equal([StemDirection.Up, StemDirection.Down], stems);

        // Every note id has an element in the MEI (the e2e checks Verovio's SVG for the same ids).
        var mei = XDocument.Parse(MeiWriter.Write(view));
        var ids = mei.Descendants(Mei + "note").Select(n => (string)n.Attribute(Xml + "id")!).ToHashSet();
        foreach (var note in state.MidiRegions.Values.Single().Notes) {
            Assert.Contains(ScoreIds.Note(note.Id), ids);
        }
    }

    [Fact]
    public void The_exit_criterion_region_at_eighths_changes_the_score_not_the_MIDI() {
        var sixteenths = Cases.ExitCriterion();
        var eighths = Cases.ExitCriterion(480, 320);
        Assert.Equal(sixteenths.State.MidiRegions[sixteenths.Region].Notes, eighths.State.MidiRegions[eighths.Region].Notes);
        var view = NotationEngine.Render(eighths.State, ProjectScore.Empty, new());
        Assert.Equal(
            "m3 s0 v1: E4/8b1 F4+G4/8b1 C4+E4+G4/q~ C4+E4+G4/h",
            Describe.View(view).Split('\n').Single(l => l.StartsWith("m3 s0", StringComparison.Ordinal))
        );
    }

    [Theory]
    [MemberData(nameof(Named))]
    public void Golden_views_and_MEI(string name) {
        var view = View(name);
        var json = JsonSerializer.Serialize(view, Json) + "\n";
        var mei = MeiWriter.Write(view);
        Golden($"{name}.json", json);
        Golden($"{name}.mei", mei);
    }

    [Theory]
    [MemberData(nameof(Named))]
    public void MEI_is_valid_MEI_5(string name) => Valid(View(name));

    [Theory]
    [MemberData(nameof(Named))]
    public void MEI_has_every_element_of_the_view(string name) => Structure(View(name));

    [Fact]
    public void The_same_project_gives_the_same_view_and_MEI_byte_for_byte() {
        var (state, score, piano, _) = Cases.Content();
        var first = NotationEngine.Render(state, score, new("scs_both"));
        var region = state.MidiRegions.Values.First(r => r.TrackId == piano);
        var shuffled = state with {
            MidiRegions = state.MidiRegions.SetItem(region.Id, region with { Notes = [..region.Notes.Reverse()] })
        };
        var reordered = score with {
            Symbols = [..score.Symbols.Reverse()],
            Lyrics = [..score.Lyrics.Reverse()],
            Chords = [..score.Chords.Reverse()]
        };
        var second = NotationEngine.Render(shuffled, reordered, new("scs_both"));
        Assert.Equal(JsonSerializer.Serialize(first, Json), JsonSerializer.Serialize(second, Json));
        Assert.Equal(MeiWriter.Write(first), MeiWriter.Write(second));
        Assert.Equal(MeiWriter.Write(first), MeiWriter.Write(NotationEngine.Render(state, score, new("scs_both"))));
    }

    [Fact]
    public void MEI_carries_the_title_block_transposition_clefs_and_the_symbols() {
        var mei = XDocument.Parse(MeiWriter.Write(View("content-part")));
        Assert.Equal("5.0", (string?)mei.Root!.Attribute("meiversion"));
        Assert.Equal("Test & Score", mei.Descendants(Mei + "title").First().Value);
        var staffDef = mei.Descendants(Mei + "staffDef").First();
        Assert.Equal(("-2", "-1", "2s"), ((string?)staffDef.Attribute("trans.semi"), (string?)staffDef.Attribute("trans.diat"), (string?)staffDef.Attribute("keysig")));
        Assert.Equal("Clarinet in B♭", staffDef.Element(Mei + "label")!.Value);
        Assert.Contains(mei.Descendants(Mei + "syl"), s => (string?)s.Attribute("wordpos") == "i" && (string?)s.Attribute("con") == "d");
        Assert.Contains(mei.Descendants(Mei + "syl"), s => s.Value == "jah" && (string?)s.Attribute("con") == "u");
        Assert.Contains(mei.Descendants(Mei + "artic"), a => (string?)a.Attribute("artic") == "marc");

        var full = XDocument.Parse(MeiWriter.Write(View("content")));
        var group = full.Descendants(Mei + "staffGrp").Single(g => (string?)g.Attribute("symbol") == "brace");
        Assert.Equal(2, group.Elements(Mei + "staffDef").Count());
        Assert.Contains(full.Descendants(Mei + "harm"), h => h.Value == "Fmaj7/A");
        Assert.Contains(full.Descendants(Mei + "harm"), h => h.Value == "F♯m7b5");
        // Note 4's tie is hidden: drawn in the view, left out of the MEI.
        Assert.Contains(View("content").Parts[0].Measures[0].Staves[0].Voices[0].Events, e => e.Notes.Any(n => n.NoteId == 4 && n.TieStart));
        Assert.Empty(full.Descendants(Mei + "tie"));
        Assert.Contains(full.Descendants(Mei + "hairpin"), h => (string?)h.Attribute("tstamp2") == "0m+5");
        Assert.Contains(full.Descendants(Mei + "pgHead").Descendants(Mei + "rend"), r => r.Value == "A. Composer");
        Assert.Equal("Footer", full.Descendants(Mei + "pgFoot").Single().Value);
        Assert.Contains(full.Descendants(Mei + "scoreDef"), d => (string?)d.Attribute("keysig") == "1s");
        Assert.Equal(["0", "0"], full.Descendants(Mei + "staffDef").Take(2).Select(d => (string?)d.Attribute("keysig") ?? "0"));

        var drums = XDocument.Parse(MeiWriter.Write(View("drums")));
        Assert.Equal("perc", (string?)drums.Descendants(Mei + "staffDef").Single().Attribute("clef.shape"));
        Assert.Contains(drums.Descendants(Mei + "note"), n => (string?)n.Attribute("head.shape") == "x");
    }

    /// <summary>Checks <paramref name="view" />'s MEI against the MEI 5 schema with xmllint (skipped without it).</summary>
    internal static void Valid(ScoreView view, [CallerFilePath] string source = "") {
        var xmllint = Xmllint();
        Assert.SkipWhen(xmllint is null, "xmllint is not installed; MEI is checked structurally only.");
        var schema = Path.Combine(Path.GetDirectoryName(source)!, "Schema", "mei-all.rng");
        var file = Path.Combine(Path.GetTempPath(), $"anode-mei-{Guid.NewGuid():N}.mei");
        File.WriteAllText(file, MeiWriter.Write(view));
        try {
            var start = new ProcessStartInfo(xmllint!, ["--noout", "--relaxng", schema, file]) {
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            using var process = Process.Start(start)!;
            var errors = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, errors);
        } finally {
            File.Delete(file);
        }
    }

    static string? Xmllint() =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
        .Select(d => Path.Combine(d, OperatingSystem.IsWindows() ? "xmllint.exe" : "xmllint"))
        .FirstOrDefault(File.Exists);

    /// <summary>
    ///     The structural checks that run everywhere: unique ids; an element per measure, staff, layer, event, note,
    ///     drawn symbol (but a hidden tie), syllable and chord symbol, with the view's ids; durations; every reference
    ///     resolves; a tie per tied note.
    /// </summary>
    internal static void Structure(ScoreView view) {
        var mei = XDocument.Parse(MeiWriter.Write(view));
        Assert.Equal(Mei + "mei", mei.Root!.Name);
        var elements = mei.Descendants().Where(e => e.Attribute(Xml + "id") is not null).ToList();
        var ids = elements.Select(e => (string)e.Attribute(Xml + "id")!).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        var byId = elements.ToDictionary(e => (string)e.Attribute(Xml + "id")!);

        var measures = mei.Descendants(Mei + "measure").ToList();
        Assert.Equal(view.Measures.Select(m => ScoreIds.Measure(m.Number)), measures.Select(m => (string)m.Attribute(Xml + "id")!));
        var staves = view.Parts.Sum(p => p.Staves.Count);
        Assert.Equal(staves, mei.Descendants(Mei + "scoreDef").First().Descendants(Mei + "staffDef").Count());
        var ties = 0;
        for (var m = 0; m < view.Measures.Count; m++) {
            var measure = measures[m];
            Assert.Equal(staves, measure.Elements(Mei + "staff").Count());
            var staff = 0;
            foreach (var part in view.Parts) {
                foreach (var staffMeasure in part.Measures[m].Staves) {
                    var element = measure.Elements(Mei + "staff").ElementAt(staff++);
                    Assert.Equal(staffMeasure.Voices.Select(v => v.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)), element.Elements(Mei + "layer").Select(l => (string)l.Attribute("n")!));
                    foreach (var e in staffMeasure.Voices.SelectMany(v => v.Events)) {
                        var named = byId[e.Id];
                        Assert.Equal(
                            e.Kind switch {
                                ScoreEventKind.Note => "note",
                                ScoreEventKind.Chord => "chord",
                                _ => named.Name.LocalName == "mRest" ? "mRest" : "rest"
                            },
                            named.Name.LocalName
                        );
                        if (named.Name.LocalName != "mRest") {
                            Assert.Equal(Dur(e.Duration.Value), (string?)named.Attribute("dur"));
                        }

                        foreach (var note in e.Notes) {
                            Assert.Equal("note", byId[note.Id].Name.LocalName);
                            Assert.Equal(note.Written.Step.ToString().ToLowerInvariant(), (string?)byId[note.Id].Attribute("pname"));
                            ties += note.TieStart && !view.Symbols.Any(s => s.Kind == ScoreSymbolKind.TieHidden && s.NoteId == note.NoteId) ? 1 : 0;
                        }
                    }
                }
            }
        }

        Assert.Equal(ties, mei.Descendants(Mei + "tie").Count());
        foreach (var symbol in view.Symbols.Where(s => s.Kind != ScoreSymbolKind.TieHidden)) {
            Assert.Contains(ScoreIds.Symbol(symbol.Id), byId.Keys);
        }

        foreach (var lyric in view.Lyrics) {
            Assert.Equal("syl", byId[ScoreIds.Lyric(lyric.Id)].Name.LocalName);
        }

        foreach (var chord in view.Chords) {
            Assert.Equal("harm", byId[ScoreIds.ChordSymbol(chord.Id)].Name.LocalName);
        }

        foreach (var reference in mei.Descendants().SelectMany(e => new[] { e.Attribute("startid"), e.Attribute("endid") }).OfType<XAttribute>()) {
            Assert.StartsWith("#", reference.Value, StringComparison.Ordinal);
            Assert.Contains(reference.Value[1..], byId.Keys);
        }
    }

    static string Dur(NoteValue value) =>
        value switch {
            NoteValue.Breve => "breve",
            NoteValue.Whole => "1",
            NoteValue.Half => "2",
            NoteValue.Quarter => "4",
            NoteValue.Eighth => "8",
            NoteValue.Sixteenth => "16",
            NoteValue.ThirtySecond => "32",
            NoteValue.SixtyFourth => "64",
            _ => "128"
        };

    static void Golden(string name, string actual, [CallerFilePath] string source = "") {
        var path = Path.Combine(Path.GetDirectoryName(source)!, "Golden", name);
        if (Environment.GetEnvironmentVariable("ANODE_UPDATE_GOLDEN") == "1") {
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path), $"No golden {name}; run with ANODE_UPDATE_GOLDEN=1 and review it.");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }
}
