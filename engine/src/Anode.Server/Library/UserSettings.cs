using Anode.Contracts;
using Anode.Model.Storage;
using Anode.Server.Projects;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Anode.Server.Library;

/// <summary>
///     The user's own settings, kept per user rather than per project (docs/04-phase-3.md, "Screensets, key commands,
///     bookmarks"): key bindings and the All Files browser's bookmarks, as JSON in <c>&lt;DataDirectory&gt;/settings/</c>,
///     each written atomically.
/// </summary>
public sealed class UserSettings(IOptions<ServerOptions> options) {
    public const string Folder = "settings";
    public const int MaxCommands = 2_000;
    public const int MaxBindingsPerCommand = 8;
    public const int MaxCommandIdLength = 128;
    public const int MaxBindingLength = 64;
    public const int MaxBookmarks = 200;
    public const int MaxPathLength = 4096;

    readonly Lock gate = new();

    string Path(string name) => System.IO.Path.Combine(options.Value.DataDirectory, Folder, name);

    /// <summary>The user's key bindings (empty: the editor's defaults).</summary>
    public KeyCommandSet KeyCommands() =>
        Read("key-commands.json", ContractsJsonContext.Default.KeyCommandSet) is { Bindings: not null } set
            ? set
            : new(new Dictionary<string, IReadOnlyList<string>>());

    public KeyCommandSet SaveKeyCommands(KeyCommandSet? set) {
        if (set?.Bindings is not { Count: <= MaxCommands } bindings) {
            throw ProjectException.Unprocessable($"Key commands bind at most {MaxCommands} commands.");
        }

        var clean = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (command, keys) in bindings) {
            if (command is not { Length: > 0 and <= MaxCommandIdLength } || string.IsNullOrWhiteSpace(command)) {
                throw ProjectException.Unprocessable($"A command id has 1…{MaxCommandIdLength} characters.");
            }

            if (keys is not { Count: <= MaxBindingsPerCommand }
                || keys.Any(static k => k is not { Length: > 0 and <= MaxBindingLength } || string.IsNullOrWhiteSpace(k)
                )) {
                throw ProjectException.Unprocessable(
                    $"Command '{command}' has at most {MaxBindingsPerCommand} bindings of 1…{MaxBindingLength} characters."
                );
            }

            clean[command] = [..keys.Distinct(StringComparer.Ordinal)];
        }

        var saved = new KeyCommandSet(clean);
        Write("key-commands.json", saved, ContractsJsonContext.Default.KeyCommandSet);
        return saved;
    }

    /// <summary>The folders pinned in the All Files browser.</summary>
    public Bookmarks Bookmarks() =>
        Read("bookmarks.json", ContractsJsonContext.Default.Bookmarks) is { Paths: not null } bookmarks
            ? bookmarks
            : new([]);

    public Bookmarks SaveBookmarks(Bookmarks? bookmarks) {
        if (bookmarks?.Paths is not { Count: <= MaxBookmarks } paths) {
            throw ProjectException.Unprocessable($"Keep at most {MaxBookmarks} bookmarks.");
        }

        var clean = new List<string>();
        foreach (var path in paths) {
            if (path is not { Length: > 0 and <= MaxPathLength } || !System.IO.Path.IsPathFullyQualified(path)) {
                throw ProjectException.Unprocessable(
                    $"A bookmark is an absolute path of at most {MaxPathLength} characters."
                );
            }

            var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
            if (!clean.Contains(full, StringComparer.Ordinal)) {
                clean.Add(full);
            }
        }

        var saved = new Bookmarks(clean);
        Write("bookmarks.json", saved, ContractsJsonContext.Default.Bookmarks);
        return saved;
    }

    T? Read<T>(string name, JsonTypeInfo<T> type) where T : class {
        lock (gate) {
            try {
                var path = Path(name);
                return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), type) : null;
            } catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) {
                return null; // damaged or unreadable: the defaults
            }
        }
    }

    void Write<T>(string name, T value, JsonTypeInfo<T> type) {
        lock (gate) {
            try {
                Directory.CreateDirectory(System.IO.Path.Combine(options.Value.DataDirectory, Folder));
                AtomicRename.Write(Path(name), JsonSerializer.Serialize(value, type));
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                throw ProjectException.Unprocessable($"Could not save the settings: {e.Message}");
            }
        }
    }
}
