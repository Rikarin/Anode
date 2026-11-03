using Anode.Contracts;

namespace Anode.Generative.Tests;

/// <summary>The style library (docs/generative/README.md): what Phase 11 ships, its format checks, its loader.</summary>
public sealed class LibraryTests {
    [Fact]
    public void The_builtin_library_has_the_briefs_styles() {
        var catalog = Inputs.Library.Catalog;
        Assert.False(string.IsNullOrWhiteSpace(catalog.Version));
        var drums = Inputs.StylesOf(SessionPlayerKind.Drummer).Select(static s => s.Genre).ToList();
        Assert.Superset(new HashSet<string> { "Rock", "Pop", "Funk", "Electronic" }, drums.ToHashSet());
        Assert.True(Inputs.StylesOf(SessionPlayerKind.Bass).Count() >= 3);
        Assert.True(Inputs.StylesOf(SessionPlayerKind.Keyboard).Count() >= 3);
        Assert.Contains(Inputs.StylesOf(SessionPlayerKind.Keyboard), static s => s.SynthPlayer);
        Assert.Equal(catalog.Styles.Count, catalog.Styles.Select(static s => s.Id).Distinct().Count());
    }

    [Fact]
    public void Every_style_has_presets_section_defaults_and_its_players_switches() {
        foreach (var style in Inputs.Library.Catalog.Styles) {
            Assert.NotEmpty(style.Presets);
            Assert.Equal(Enum.GetValues<SectionKind>(), style.Sections.Select(static s => s.Kind));
            Assert.Equal(style.Player == SessionPlayerKind.Drummer, style.KitPatchId is not null);
            foreach (var parameters in style.Presets.Select(static p => p.Parameters)
                         .Concat(style.Sections.Select(static s => s.Parameters))) {
                Assert.Equal(style.Id, parameters.StyleId);
                Assert.Equal(style.Player == SessionPlayerKind.Drummer, parameters.Drums is not null);
                Assert.Equal(style.Player == SessionPlayerKind.Bass, parameters.Bass is not null);
                Assert.Equal(style.Player == SessionPlayerKind.Keyboard, parameters.Keys is not null);
                if (parameters.Keys?.RhythmId is { } rhythm) {
                    Assert.Contains(style.Rhythms, r => r.Id == rhythm);
                }
            }

            Assert.All(style.Presets, static p => Assert.Equal(p.Id, p.Parameters.PresetId));
            Assert.All(style.Sections, static s => Assert.Null(s.Parameters.PresetId));
        }
    }

    [Fact]
    public void Drum_styles_name_the_content_builders_kits() {
        var kits = new HashSet<string> { "kit_drummer_acoustic", "kit_drummer_electronic", "kit_drummer_brushes" };
        Assert.All(Inputs.StylesOf(SessionPlayerKind.Drummer), s => Assert.Contains(s.KitPatchId!, kits));
        Assert.Equal(kits, Inputs.StylesOf(SessionPlayerKind.Drummer).Select(static s => s.KitPatchId!).ToHashSet());
    }

    [Fact]
    public void The_source_folder_loads_as_the_embedded_library() {
        var loaded = StyleLibrary.Load(Inputs.StylesFolder());
        Assert.Equal(Inputs.Library.Version, loaded.Version);
        Assert.Equal(
            Inputs.Library.Catalog.Styles.Select(static s => s.Id),
            loaded.Catalog.Styles.Select(static s => s.Id)
        );
        var input = Inputs.Input(SessionPlayerKind.Drummer, Inputs.Parameters("drums-rock"));
        Assert.Equal(
            Inputs.Generator.Generate(input),
            new SessionPlayerGenerator(loaded).Generate(input),
            new NotesComparer()
        );
    }

    [Theory]
    [InlineData("\"stepsPerBar\": 16", "\"stepsPerBar\": 15", "stepsPerBar")]
    [InlineData("\"kick\": \"x...|....|x...|....\"", "\"kick\": \"x...|....|x...|...\"", "15 steps")]
    [InlineData("\"kick\": \"x...|....|x...|....\"", "\"kick\": \"x...|....|x...|...q\"", "'q'")]
    [InlineData("\"role\": \"intro\"", "\"role\": \"solo\"", "'solo'")]
    [InlineData("\"outro\": { \"form\"", "\"ending\": { \"form\"", "outro")]
    public void An_invalid_style_file_throws_naming_it(string find, string replace, string message) {
        var folder = Path.Combine(Path.GetTempPath(), $"anode-styles-{Guid.NewGuid():n}");
        try {
            Directory.CreateDirectory(Path.Combine(folder, "drums"));
            File.Copy(Path.Combine(Inputs.StylesFolder(), "library.json"), Path.Combine(folder, "library.json"));
            var text = File.ReadAllText(Path.Combine(Inputs.StylesFolder(), "drums", "rock.json"))
                .Replace("\"kitPatchId\"", "\"stepsPerBar\": 16, \"kitPatchId\"", StringComparison.Ordinal);
            Assert.Contains(find, text, StringComparison.Ordinal);
            File.WriteAllText(
                Path.Combine(folder, "drums", "rock.json"),
                text.Replace(find, replace, StringComparison.Ordinal)
            );
            var error = Assert.Throws<InvalidDataException>(() => StyleLibrary.Load(folder));
            Assert.StartsWith("drums/rock.json:", error.Message, StringComparison.Ordinal);
            Assert.Contains(message, error.Message, StringComparison.Ordinal);
        } finally {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void A_library_without_its_version_file_throws() {
        var folder = Path.Combine(Path.GetTempPath(), $"anode-styles-{Guid.NewGuid():n}");
        try {
            Directory.CreateDirectory(folder);
            Assert.Throws<InvalidDataException>(() => StyleLibrary.Load(folder));
        } finally {
            Directory.Delete(folder, true);
        }
    }

    /// <summary>Generated notes compared by value (their hash and every note).</summary>
    internal sealed class NotesComparer : IEqualityComparer<GeneratedNotes> {
        public bool Equals(GeneratedNotes? x, GeneratedNotes? y) =>
            x is not null && y is not null && x.Hash == y.Hash && x.Notes.SequenceEqual(y.Notes);

        public int GetHashCode(GeneratedNotes obj) => obj.Hash.GetHashCode(StringComparison.Ordinal);
    }
}
