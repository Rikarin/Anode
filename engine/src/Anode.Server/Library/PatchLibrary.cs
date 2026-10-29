using Anode.Contracts;
using Anode.Model;
using Anode.Model.Editing;
using Anode.Model.Storage;
using Anode.Server.Projects;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Anode.Server.Library;

/// <summary>
///     A saved channel strip on disk: the patch and the opaque states of its instrument and inserts (base64, by insert
///     id), as <c>&lt;DataDirectory&gt;/Library/Patches/&lt;id&gt;.json</c>.
/// </summary>
public sealed record PatchFile(
    int SchemaVersion,
    string Id,
    string Name,
    string Category,
    TrackKind Kind,
    InstrumentSlot? Instrument,
    IReadOnlyList<Insert> Inserts,
    IReadOnlyList<Send> Sends,
    string? InstrumentState = null,
    IReadOnlyDictionary<string, string>? InsertStates = null,
    IReadOnlyList<KitPad>? Pads = null
);

/// <summary>
///     The user's channel-strip patches (docs/05-phase-4.md, "Patches"): Logic's patches, saved per user from a track's
///     instrument, inserts (with parameters and states) and sends, applied to a track with <c>applyPatch</c>.
/// </summary>
public sealed class PatchLibrary(IOptions<ServerOptions> options) : IPatchSource {
    public const int SchemaVersion = 1;
    public const int MaxCategoryLength = 64;

    readonly Lock gate = new();

    public string Folder => Path.Combine(options.Value.DataDirectory, "Library", "Patches");

    /// <summary>Where installed content packs keep their patches and kits: <c>Packs/&lt;packId&gt;/patches</c>.</summary>
    string PacksFolder => Path.Combine(options.Value.DataDirectory, "Library", "Packs");

    /// <summary>Every readable patch (the user's, then the installed packs' patches and kits), by category then name.</summary>
    public IReadOnlyList<PatchInfo> List() {
        var folders = PatchFolders().ToArray();
        if (folders.Length == 0) {
            return [];
        }

        return [
            ..folders.SelectMany(static f => Directory.EnumerateFiles(f, "*.json"))
                .Select(static p => Path.GetFileNameWithoutExtension(p))
                .Distinct(StringComparer.Ordinal)
                .Select(Read)
                .OfType<PatchFile>()
                .Select(static p => new PatchInfo(p.Id, p.Name, p.Category, p.Kind))
                .OrderBy(static p => p.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static p => p.Name, StringComparer.OrdinalIgnoreCase)
        ];
    }

    public ChannelPatch? Find(string id) =>
        Read(id) is { } p
            ? new(p.Id, p.Name, p.Category, p.Kind, p.Instrument, p.Inserts, p.Sends) {
                Pads = p.Pads?.Where(static x => x?.Instrument?.PluginId is not null && x.Name is not null)
                    .Select(static x => x with { Inserts = x.Inserts ?? [] })
                    .ToArray()
            }
            : null;

    /// <summary>The user's patch folder, then each installed pack's <c>patches</c> folder (those that exist).</summary>
    IEnumerable<string> PatchFolders() {
        if (Directory.Exists(Folder)) {
            yield return Folder;
        }

        if (!Directory.Exists(PacksFolder)) {
            yield break;
        }

        foreach (var pack in Directory.EnumerateDirectories(PacksFolder).Order(StringComparer.Ordinal)) {
            var patches = Path.Combine(pack, "patches");
            if (Directory.Exists(patches)) {
                yield return patches;
            }
        }
    }

    /// <summary>The saved patch file, or null when there is none (or it is unreadable).</summary>
    public PatchFile? Read(string id) {
        if (!ProjectPackage.IsSafeId(id)) {
            return null;
        }

        lock (gate) {
            try {
                var path = PatchFolders().Select(f => Path.Combine(f, id + ".json")).FirstOrDefault(File.Exists)
                    ?? Path.Combine(Folder, id + ".json");
                return File.Exists(path)
                    && JsonSerializer.Deserialize(File.ReadAllText(path), PatchJsonContext.Default.PatchFile) is {
                        Id: not null, Name: not null, Category: not null, Inserts: not null, Sends: not null
                    } patch
                    && patch.Id == id
                        ? patch
                        : null;
            } catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) {
                return null;
            }
        }
    }

    /// <summary>
    ///     Saves a track's strip as a new patch, with the states of its plugins (by state key, as the project keeps
    ///     them).
    /// </summary>
    public PatchInfo Save(Track track, string name, string category, IReadOnlyDictionary<string, byte[]> states) {
        var trimmedName = (name ?? string.Empty).Trim();
        var trimmedCategory = (category ?? string.Empty).Trim();
        if (trimmedName.Length is 0 or > Edits.MaxNameLength) {
            throw ProjectException.Unprocessable($"A patch name has 1…{Edits.MaxNameLength} characters.");
        }

        if (trimmedCategory.Length > MaxCategoryLength) {
            throw ProjectException.Unprocessable($"A patch category has at most {MaxCategoryLength} characters.");
        }

        if (!Edits.HasAudioStrip(track.Kind)) {
            throw ProjectException.Unprocessable("Only audio, instrument and aux tracks make patches.");
        }

        var id = RandomIds.Instance.Next(IdPrefix.Patch);
        var file = new PatchFile(
            SchemaVersion,
            id,
            trimmedName,
            trimmedCategory.Length == 0 ? "Uncategorized" : trimmedCategory,
            track.Kind,
            track.Kind == TrackKind.Instrument ? track.Instrument : null,
            track.Strip.Inserts,
            track.Strip.Sends,
            track.Instrument is not null && states.TryGetValue(track.Id, out var instrument)
                ? Convert.ToBase64String(instrument)
                : null,
            track.Strip.Inserts.Where(i => states.ContainsKey(ProjectPackage.StateKey(track.Id, i.Id)))
                .ToDictionary(
                    static i => i.Id,
                    i => Convert.ToBase64String(states[ProjectPackage.StateKey(track.Id, i.Id)]),
                    StringComparer.Ordinal
                )
        );
        lock (gate) {
            try {
                Directory.CreateDirectory(Folder);
                AtomicRename.Write(
                    Path.Combine(Folder, id + ".json"),
                    JsonSerializer.Serialize(file, PatchJsonContext.Default.PatchFile)
                );
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                throw ProjectException.Unprocessable($"Could not save the patch: {e.Message}");
            }
        }

        return new(id, file.Name, file.Category, file.Kind);
    }

    public bool Delete(string id) {
        if (!ProjectPackage.IsSafeId(id)) {
            return false;
        }

        lock (gate) {
            var path = Path.Combine(Folder, id + ".json");
            if (!File.Exists(path)) {
                return false;
            }

            File.Delete(path);
            return true;
        }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true
)]
[JsonSerializable(typeof(PatchFile))]
sealed partial class PatchJsonContext : JsonSerializerContext;
