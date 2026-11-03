using Anode.Contracts;
using System.Text.Json;

namespace Anode.Generative;

/// <summary>
///     The style library (workstream D): versioned data in the repository, authored for Anode (JSON phrase banks, the
///     variation grammar, feels and presets per style; ADR-0014), embedded in this assembly from <c>Styles/</c>. Loaded
///     once and immutable; its <see cref="Version" /> is part of every generation input, so a changed library
///     regenerates the regions. The format is in docs/generative/README.md.
/// </summary>
public sealed class StyleLibrary {
    const string LibraryFileName = "library.json";
    const string ResourcePrefix = "Anode.Generative.Styles.";

    readonly Dictionary<string, Style> styles;

    /// <summary>The loader's (<see cref="Builtin" />, <see cref="Load" />), from parsed style data.</summary>
    internal StyleLibrary(string version, IReadOnlyList<Style> styles) {
        Version = version;
        this.styles = styles.ToDictionary(static s => s.Info.Id, StringComparer.Ordinal);
        // Drummer styles first, then bass and keys, each by id.
        Catalog = new(
            version,
            [
                ..styles.OrderBy(static s => s.Player)
                    .ThenBy(static s => s.Info.Id, StringComparer.Ordinal)
                    .Select(static s => s.Info)
            ]
        );
    }

    /// <summary>The library's version (its own field, bumped with any change to its data).</summary>
    public string Version { get; }

    /// <summary>The catalogue the API serves (<c>GET /library/session-players</c>).</summary>
    public SessionPlayerCatalog Catalog { get; }

    internal Style? Find(string styleId) => styles.GetValueOrDefault(styleId);

    /// <summary>The library embedded in this assembly (<c>Styles/</c>).</summary>
    public static StyleLibrary Builtin() => BuiltinLibrary.Value;

    static readonly Lazy<StyleLibrary> BuiltinLibrary = new(static () => {
            var assembly = typeof(StyleLibrary).Assembly;
            var names = assembly.GetManifestResourceNames()
                .Where(static n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                    && n.EndsWith(".json", StringComparison.Ordinal)
                )
                .Order(StringComparer.Ordinal)
                .ToList();
            return Read(
                names.Select(n => (n[ResourcePrefix.Length..],
                        (Func<Stream>)(() => assembly.GetManifestResourceStream(n)!))
                )
            );
        }
    );

    /// <summary>A library from a folder of style files (tests, authoring); invalid data throws with the file named.</summary>
    public static StyleLibrary Load(string folder) {
        var files = Directory.EnumerateFiles(folder, "*.json", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();
        return Read(files.Select(f => (f, (Func<Stream>)(() => File.OpenRead(Path.Combine(folder, f))))));
    }

    /// <summary>
    ///     The library of <paramref name="files" /> (name, opener): <c>library.json</c> for the version, every other
    ///     file a style, in name order.
    /// </summary>
    static StyleLibrary Read(IEnumerable<(string Name, Func<Stream> Open)> files) {
        string? version = null;
        var styles = new List<Style>();
        foreach (var (name, open) in files) {
            try {
                using var stream = open();
                if (name == LibraryFileName || name.EndsWith("." + LibraryFileName, StringComparison.Ordinal)) {
                    var library = JsonSerializer.Deserialize(stream, StyleJsonContext.Default.LibraryFile)
                        ?? throw new InvalidDataException("empty.");
                    if (library.Format != StyleCompiler.Format || string.IsNullOrWhiteSpace(library.Version)) {
                        throw new InvalidDataException($"format {StyleCompiler.Format} and a version expected.");
                    }

                    version = library.Version;
                    continue;
                }

                var file = JsonSerializer.Deserialize(stream, StyleJsonContext.Default.StyleFile)
                    ?? throw new InvalidDataException("empty.");
                var style = StyleCompiler.Compile(file, name);
                if (styles.Exists(s => s.Info.Id == style.Info.Id)) {
                    throw new InvalidDataException($"style '{style.Info.Id}' is defined twice.");
                }

                styles.Add(style);
            } catch (Exception e) when (e is JsonException or InvalidDataException
                                        && !e.Message.StartsWith(name + ":", StringComparison.Ordinal)) {
                throw new InvalidDataException($"{name}: {e.Message}", e);
            }
        }

        if (version is null) {
            throw new InvalidDataException($"The style library has no {LibraryFileName}.");
        }

        return new(version, styles);
    }
}
