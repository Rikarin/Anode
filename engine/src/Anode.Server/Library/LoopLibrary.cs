using Anode.Contracts;
using Anode.Model.Storage;
using Anode.Server.Projects;
using Microsoft.Extensions.Options;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Anode.Server.Library;

/// <summary>A content pack's manifest, <c>pack.json</c> at the root of a <c>.anodepack</c>.</summary>
public sealed record PackManifest(string Id, string Name, string Version, string Author, string? Description = null);

/// <summary>The loop index on disk: <c>&lt;DataDirectory&gt;/Library/Loops/index.json</c>.</summary>
public sealed record LoopIndexFile(int Version, IReadOnlyList<LoopInfo> Loops);

/// <summary>
///     The loop library (docs/07-phase-6.md, "Loops", "Packs"): installed content packs under
///     <c>&lt;DataDirectory&gt;/Library/Packs/&lt;packId&gt;/</c> and the user's loops under <c>Library/Loops/User/</c>,
///     indexed in <c>Library/Loops/index.json</c>, rebuilt from them whenever a pack comes or goes or a user loop is
///     added (favourites, kept in <c>favourites.json</c>, survive a rebuild). A loop's id is a hash of where it is, so it
///     stays the same across rebuilds.
/// </summary>
public sealed class LoopLibrary(IOptions<ServerOptions> options) {
    public const int DefaultLimit = 100;
    public const int MaxLimit = 500;
    public const string PackExtension = ".anodepack";
    public const string ManifestName = "pack.json";

    /// <summary>The largest pack Anode installs (uncompressed).</summary>
    public const long MaxPackBytes = 4L << 30;

    readonly Lock gate = new();
    List<LoopInfo>? index;

    string Library => Path.Combine(options.Value.DataDirectory, "Library");
    public string PacksFolder => Path.Combine(Library, "Packs");
    public string LoopsFolder => Path.Combine(Library, "Loops");
    public string UserFolder => Path.Combine(LoopsFolder, "User");
    string IndexPath => Path.Combine(LoopsFolder, "index.json");
    string FavouritesPath => Path.Combine(LoopsFolder, "favourites.json");

    /// <summary>A page of the loops matching <paramref name="query" />, sorted by name.</summary>
    public LoopPage Query(LoopQuery query) {
        var limit = query.Limit ?? DefaultLimit;
        var offset = query.Offset ?? 0;
        if (limit is < 1 or > MaxLimit || offset < 0) {
            throw ProjectException.Unprocessable($"A page is 1…{MaxLimit} loops from an offset of 0 or more.");
        }

        IEnumerable<LoopInfo> loops = Loops();
        if (query.Text is { Length: > 0 } text) {
            loops = loops.Where(l => l.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                || l.Tags.Any(t => t.Contains(text, StringComparison.OrdinalIgnoreCase))
            );
        }

        foreach (var tag in query.Tags ?? []) {
            loops = loops.Where(l => l.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase));
        }

        if (query.Kind is { } kind) {
            loops = loops.Where(l => l.Kind == kind);
        }

        if (query.Key is { } key) {
            loops = loops.Where(l => l.Key == (key % 12 + 12) % 12);
        }

        if (query.Scale is { } scale) {
            loops = loops.Where(l => l.Scale == scale
                || (scale is LoopScale.Major or LoopScale.Minor && l.Scale == LoopScale.Both)
            );
        }

        if (query.FavouritesOnly == true) {
            loops = loops.Where(static l => l.Favourite);
        }

        var matching = loops.ToList();
        return new([..matching.Skip(offset).Take(limit)], matching.Count);
    }

    public LoopInfo? Find(string id) => Loops().Find(l => l.Id == id);

    public LoopInfo SetFavourite(string id, bool favourite) {
        lock (gate) {
            var loops = LoadedLocked();
            var i = loops.FindIndex(l => l.Id == id);
            if (i < 0) {
                throw ProjectException.NotFound($"No loop '{id}'.");
            }

            loops[i] = loops[i] with { Favourite = favourite };
            var favourites = Favourites();
            if (favourite) {
                favourites.Add(id);
            } else {
                favourites.Remove(id);
            }

            Directory.CreateDirectory(LoopsFolder);
            AtomicRename.Write(
                FavouritesPath,
                JsonSerializer.Serialize(
                    [..favourites.Order(StringComparer.Ordinal)],
                    LoopJsonContext.Default.StringArray
                )
            );
            SaveIndex(loops);
            return loops[i];
        }
    }

    /// <summary>The installed packs, by name.</summary>
    public IReadOnlyList<Pack> Packs() {
        lock (gate) {
            var loops = LoadedLocked();
            return [
                ..InstalledManifests()
                    .Select(m => Info(m.Manifest, m.Folder, loops))
                    .OrderBy(static p => p.Name, StringComparer.OrdinalIgnoreCase)
            ];
        }
    }

    /// <summary>
    ///     Installs a <c>.anodepack</c> from the server's disk: its manifest checked, its files extracted (none outside the
    ///     pack's folder) into a fresh folder that replaces an installed version of the same pack, and the index rebuilt.
    /// </summary>
    public Pack Install(string path) {
        string full;
        try {
            full = Path.GetFullPath(path ?? string.Empty);
        } catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) {
            throw ProjectException.Unprocessable($"'{path}' is not a file path.");
        }

        if (!File.Exists(full)) {
            throw ProjectException.NotFound($"No file '{full}'.");
        }

        ZipArchive archive;
        try {
            archive = ZipFile.OpenRead(full);
        } catch (InvalidDataException e) {
            throw ProjectException.Unprocessable($"'{full}' is not a content pack: {e.Message}");
        }

        using (archive) {
            var manifest = Manifest(archive)
                ?? throw ProjectException.Unprocessable($"'{full}' has no readable {ManifestName}.");
            if (archive.Entries.Sum(static e => e.Length) > MaxPackBytes) {
                throw ProjectException.Unprocessable("The pack is larger than Anode installs.");
            }

            lock (gate) {
                Directory.CreateDirectory(PacksFolder);
                var target = Path.Combine(PacksFolder, manifest.Id);
                var staging = Path.Combine(PacksFolder, $".install-{Guid.NewGuid():n}");
                try {
                    Extract(archive, staging);
                    if (Directory.Exists(target)) {
                        Directory.Delete(target, true);
                    }

                    Directory.Move(staging, target);
                } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                    throw ProjectException.Unprocessable($"Could not install pack '{manifest.Id}': {e.Message}");
                } finally {
                    if (Directory.Exists(staging)) {
                        Directory.Delete(staging, true);
                    }
                }

                var loops = RebuildLocked();
                return Info(manifest, target, loops);
            }
        }
    }

    /// <summary>Removes an installed pack and its loops from the index; false when there is none.</summary>
    public bool Remove(string id) {
        if (!ProjectPackage.IsSafeId(id)) {
            return false;
        }

        lock (gate) {
            var folder = Path.Combine(PacksFolder, id);
            if (!Directory.Exists(folder)) {
                return false;
            }

            Directory.Delete(folder, true);
            RebuildLocked();
            return true;
        }
    }

    /// <summary>
    ///     Saves a user loop: <paramref name="media" /> (a file written by the caller, or null for a pattern loop) moved
    ///     into the user folder as <paramref name="name" />, with its sidecar; the index rebuilt. Returns the new loop.
    /// </summary>
    public LoopInfo AddUserLoop(string name, string? media, string extension, LoopSidecar sidecar) {
        lock (gate) {
            Directory.CreateDirectory(UserFolder);
            var stem = Unique(Safe(name), extension);
            var target = Path.Combine(UserFolder, stem + extension);
            if (media is not null) {
                File.Move(media, target);
            }

            var sidecarPath = media is null ? target : target + LoopFiles.SidecarSuffix;
            AtomicRename.Write(
                sidecarPath,
                JsonSerializer.Serialize(sidecar with { Name = name }, ContractsJsonContext.Default.LoopSidecar)
            );
            var loops = RebuildLocked();
            return loops.First(l => l.Path == (media is null ? sidecarPath : target));
        }
    }

    string Unique(string stem, string extension) {
        var candidate = stem;
        for (var n = 2;
             File.Exists(Path.Combine(UserFolder, candidate + extension))
             || File.Exists(Path.Combine(UserFolder, candidate + extension + LoopFiles.SidecarSuffix));
             n++) {
            candidate = $"{stem} {n}";
        }

        return candidate;
    }

    static string Safe(string name) {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string([..name.Select(c => invalid.Contains(c) || c is '/' or '\\' or ':' ? '_' : c)]).Trim();
        return safe.Length == 0 ? "Loop" : safe.Length > 100 ? safe[..100] : safe;
    }

    /// <summary>Rebuilds the index from the installed packs and the user's loops.</summary>
    public IReadOnlyList<LoopInfo> Rebuild() {
        lock (gate) {
            return RebuildLocked();
        }
    }

    List<LoopInfo> Loops() {
        lock (gate) {
            return LoadedLocked();
        }
    }

    List<LoopInfo> LoadedLocked() {
        if (index is not null) {
            return index;
        }

        try {
            if (File.Exists(IndexPath)
                && JsonSerializer.Deserialize(File.ReadAllText(IndexPath), LoopJsonContext.Default.LoopIndexFile) is {
                    Loops: not null
                } file) {
                return index = [..file.Loops.Where(static l => l?.Id is not null && File.Exists(l.Path))];
            }
        } catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) {
            // a damaged index is rebuilt
        }

        return RebuildLocked();
    }

    List<LoopInfo> RebuildLocked() {
        var favourites = Favourites();
        var loops = new List<LoopInfo>();
        foreach (var (manifest, folder) in InstalledManifests()) {
            loops.AddRange(Scan(folder, manifest.Id, favourites));
        }

        if (Directory.Exists(UserFolder)) {
            loops.AddRange(Scan(UserFolder, null, favourites));
        }

        loops.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase) is var c
            && c != 0
                ? c
                : string.CompareOrdinal(a.Id, b.Id)
        );
        SaveIndex(loops);
        return index = loops;
    }

    void SaveIndex(List<LoopInfo> loops) {
        try {
            Directory.CreateDirectory(LoopsFolder);
            AtomicRename.Write(
                IndexPath,
                JsonSerializer.Serialize(new(1, loops), LoopJsonContext.Default.LoopIndexFile)
            );
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            // the index in memory serves; the next rebuild tries again
        }

        index = loops;
    }

    HashSet<string> Favourites() {
        try {
            return File.Exists(FavouritesPath)
                ? [
                    ..JsonSerializer.Deserialize(File.ReadAllText(FavouritesPath), LoopJsonContext.Default.StringArray)
                    ?? []
                ]
                : [];
        } catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) {
            return [];
        }
    }

    /// <summary>Every loop under <paramref name="folder" />: media files, and pattern loops (sidecars alone).</summary>
    static IEnumerable<LoopInfo> Scan(string folder, string? packId, HashSet<string> favourites) {
        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal)) {
            // A pack's patches and the samples its kits load are no loops.
            if (path.StartsWith(Path.Combine(folder, "patches") + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || LoopAt(path) is not { } loop) {
                continue;
            }

            var relative = Path.GetRelativePath(folder, path).Replace('\\', '/');
            var id = IdOf(packId, relative);
            var name = loop.Name ?? NameOf(path);
            var oneShot = loop.OneShot || (loop.Kind == LoopKind.Audio && loop.Tempo is null);
            yield return new(
                id,
                name,
                loop.Kind,
                packId,
                path,
                oneShot ? null : loop.Tempo,
                oneShot ? null : loop.Beats,
                loop.Key is { } k ? (k % 12 + 12) % 12 : null,
                Enum.IsDefined(loop.Scale) ? loop.Scale : LoopScale.Neither,
                [..(loop.Tags ?? []).Where(static t => !string.IsNullOrWhiteSpace(t))],
                oneShot,
                favourites.Contains(id)
            );
        }
    }

    /// <summary>The loop a file is: a media file's, or a pattern loop's sidecar alone; null for anything else.</summary>
    static LoopSidecar? LoopAt(string path) {
        if (LoopFiles.IsMedia(path)) {
            return LoopFiles.Read(path);
        }

        return path.EndsWith(LoopFiles.SidecarSuffix, StringComparison.OrdinalIgnoreCase)
            && !File.Exists(path[..^LoopFiles.SidecarSuffix.Length])
            && LoopFiles.Sidecar(path) is { Kind: LoopKind.Pattern, Pattern: not null } pattern
                ? pattern
                : null;
    }

    static string NameOf(string path) {
        var name = Path.GetFileName(path);
        if (name.EndsWith(LoopFiles.SidecarSuffix, StringComparison.OrdinalIgnoreCase)) {
            name = name[..^LoopFiles.SidecarSuffix.Length];
        }

        return Path.GetFileNameWithoutExtension(name);
    }

    /// <summary>A loop's id: where it is (its pack, or the user's folder, and its path there), hashed.</summary>
    public static string IdOf(string? packId, string relative) =>
        "lp_"
        + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{packId ?? "~user"}/{relative}")))[..16];

    IEnumerable<(PackManifest Manifest, string Folder)> InstalledManifests() {
        if (!Directory.Exists(PacksFolder)) {
            yield break;
        }

        foreach (var folder in Directory.EnumerateDirectories(PacksFolder).Order(StringComparer.Ordinal)) {
            if (Path.GetFileName(folder).StartsWith('.')) {
                continue;
            }

            var path = Path.Combine(folder, ManifestName);
            PackManifest? manifest = null;
            try {
                manifest = File.Exists(path) ? Checked(File.ReadAllText(path)) : null;
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                // an unreadable pack is not listed
            }

            if (manifest is not null && manifest.Id == Path.GetFileName(folder)) {
                yield return (manifest, folder);
            }
        }
    }

    static Pack Info(PackManifest manifest, string folder, List<LoopInfo> loops) =>
        new(
            manifest.Id,
            manifest.Name,
            manifest.Version,
            manifest.Author,
            loops.Count(l => l.PackId == manifest.Id),
            Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                    .Sum(static f => new FileInfo(f).Length)
                : 0,
            true
        );

    static PackManifest? Manifest(ZipArchive archive) {
        if (archive.GetEntry(ManifestName) is not { } entry || entry.Length > 1 << 20) {
            return null;
        }

        using var reader = new StreamReader(entry.Open());
        return Checked(reader.ReadToEnd());
    }

    /// <summary>A manifest that names a pack: a safe id, a name, version and author.</summary>
    static PackManifest? Checked(string json) {
        try {
            return JsonSerializer.Deserialize(json, LoopJsonContext.Default.PackManifest) is {
                    Id: not null, Name: not null
                } m
                && ProjectPackage.IsSafeId(m.Id)
                    ? m with { Version = m.Version ?? "1.0", Author = m.Author ?? string.Empty }
                    : null;
        } catch (JsonException) {
            return null;
        }
    }

    /// <summary>Extracts a pack's files into <paramref name="folder" />, refusing any that would land outside it.</summary>
    static void Extract(ZipArchive archive, string folder) {
        var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        foreach (var entry in archive.Entries) {
            var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!target.StartsWith(root, StringComparison.Ordinal)) {
                throw new IOException($"'{entry.FullName}' is outside the pack.");
            }

            if (entry.FullName.EndsWith('/')) {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
        }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true
)]
[JsonSerializable(typeof(LoopIndexFile))]
[JsonSerializable(typeof(PackManifest))]
[JsonSerializable(typeof(string[]))]
public sealed partial class LoopJsonContext : JsonSerializerContext;
