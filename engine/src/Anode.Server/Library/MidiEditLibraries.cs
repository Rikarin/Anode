using Anode.Contracts;
using Anode.Model;
using Anode.Model.Editing;
using Anode.Model.Storage;
using Anode.Model.Transform;
using Anode.Server.Projects;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Anode.Server.Library;

/// <summary>
///     One kind of the user's saved Phase 7 objects (docs/08-phase-7.md): a JSON file per object,
///     <c>&lt;folder&gt;/&lt;id&gt;.json</c>, checked as the model checks the project's own, listed by name.
/// </summary>
public sealed class EditLibrary<T>(
    string folder,
    JsonTypeInfo<T> type,
    Func<T?, T> check,
    Func<T, string> id,
    Func<T, string> name,
    string what
) where T : class {
    readonly Lock gate = new();

    public string Folder { get; } = folder;

    /// <summary>Every readable entry, by name (then id).</summary>
    public IReadOnlyList<T> List() {
        if (!Directory.Exists(Folder)) {
            return [];
        }

        return [
            ..Directory.EnumerateFiles(Folder, "*.json")
                .Select(static p => Path.GetFileNameWithoutExtension(p))
                .Select(Read)
                .OfType<T>()
                .OrderBy(name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(id, StringComparer.Ordinal)
        ];
    }

    /// <summary>The entry saved under <paramref name="key" />, or null (none, unreadable, or not well formed).</summary>
    public T? Read(string key) {
        if (!ProjectPackage.IsSafeId(key)) {
            return null;
        }

        try {
            var path = Path.Combine(Folder, key + ".json");
            if (!File.Exists(path)) {
                return null;
            }

            var item = check(JsonSerializer.Deserialize(File.ReadAllText(path), type));
            return id(item) == key ? item : null;
        } catch (Exception e) when (e is IOException
                                        or UnauthorizedAccessException
                                        or JsonException
                                        or EditException
                                        or NotSupportedException) {
            return null;
        }
    }

    /// <summary>Saves an entry (replacing the one with its id): 422 when it is not well formed.</summary>
    public T Save(T? item) {
        T good;
        try {
            good = check(item);
        } catch (EditException e) {
            throw ProjectException.Unprocessable(e.Message);
        }

        if (!ProjectPackage.IsSafeId(id(good))) {
            throw ProjectException.Unprocessable(
                $"A saved {what}'s id has 1…64 letters, digits, '_' or '-': '{id(good)}' does not."
            );
        }

        lock (gate) {
            try {
                Directory.CreateDirectory(Folder);
                AtomicRename.Write(Path.Combine(Folder, id(good) + ".json"), JsonSerializer.Serialize(good, type));
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                throw ProjectException.Unprocessable($"Could not save the {what}: {e.Message}");
            }
        }

        return good;
    }

    /// <summary>Deletes an entry; false when there is none.</summary>
    public bool Delete(string key) {
        if (!ProjectPackage.IsSafeId(key)) {
            return false;
        }

        lock (gate) {
            var path = Path.Combine(Folder, key + ".json");
            if (!File.Exists(path)) {
                return false;
            }

            try {
                File.Delete(path);
                return true;
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                throw ProjectException.Unprocessable($"Could not delete the {what}: {e.Message}");
            }
        }
    }
}

/// <summary>
///     The user library of Phase 7 (docs/08-phase-7.md): groove templates, MIDI Transform sets and articulation sets,
///     under <c>&lt;DataDirectory&gt;/Library/Grooves</c>, <c>Transform Sets</c> and <c>Articulation Sets</c>.
///     Projects keep their own copies; deleting from the library leaves them.
/// </summary>
public sealed class MidiEditLibraries(IOptions<ServerOptions> options) {
    public const string FactoryPrefix = "factory.";

    string Root => Path.Combine(options.Value.DataDirectory, "Library");

    public EditLibrary<GrooveTemplate> Grooves =>
        new(
            Path.Combine(Root, "Grooves"),
            ContractsJsonContext.Default.GrooveTemplate,
            Edits.CheckGroove,
            static g => g.Id,
            static g => g.Name,
            "groove template"
        );

    public EditLibrary<ArticulationSet> ArticulationSets =>
        new(
            Path.Combine(Root, "Articulation Sets"),
            ContractsJsonContext.Default.ArticulationSet,
            Edits.CheckArticulationSet,
            static a => a.Id,
            static a => a.Name,
            "articulation set"
        );

    /// <summary>The user's Transform sets (the factory presets are not saved: <see cref="TransformSets" />).</summary>
    public EditLibrary<TransformSet> UserTransformSets =>
        new(
            Path.Combine(Root, "Transform Sets"),
            ContractsJsonContext.Default.TransformSet,
            CheckTransformSet,
            static t => t.Id,
            static t => t.Name,
            "Transform set"
        );

    /// <summary>The factory presets (ids <c>factory.…</c>, in menu order), then the user's sets by name.</summary>
    public IReadOnlyList<TransformSet> TransformSets() => [..TransformEngine.Presets(), ..UserTransformSets.List()];

    /// <summary>Saves a user Transform set: a factory preset's id is refused (save a copy under a new id).</summary>
    public TransformSet SaveTransformSet(TransformSet? set) {
        if (set?.Id is { } id && id.StartsWith(FactoryPrefix, StringComparison.Ordinal)) {
            throw ProjectException.Unprocessable($"'{id}' is a factory preset: save a copy under a new id.");
        }

        return UserTransformSets.Save(set);
    }

    /// <summary>Deletes a user Transform set; false when there is none (a factory preset: 422).</summary>
    public bool DeleteTransformSet(string id) =>
        id.StartsWith(FactoryPrefix, StringComparison.Ordinal)
            ? throw ProjectException.Unprocessable($"'{id}' is a factory preset: it cannot be deleted.")
            : UserTransformSets.Delete(id);

    static TransformSet CheckTransformSet(TransformSet? set) {
        var good = Edits.CheckTransformSet(set);
        var name = good.Name.Trim();
        return name.Length is 0 or > Edits.MaxNameLength
            ? throw new EditException($"A Transform set's name has 1…{Edits.MaxNameLength} characters.")
            : good with { Name = name };
    }
}
