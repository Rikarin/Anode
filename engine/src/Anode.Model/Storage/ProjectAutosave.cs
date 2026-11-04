using System.Text.Json;

namespace Anode.Model.Storage;

/// <summary>
///     <c>Autosave/autosave.json</c>: which slot holds the snapshot, whose it is, when it was taken, and when the saved
///     project it builds on was written.
/// </summary>
/// <param name="Slot">The slot under <c>Autosave/</c> that holds the snapshot.</param>
/// <param name="ProjectId">The project's id.</param>
/// <param name="Name">The project's name.</param>
/// <param name="Autosaved">When the snapshot was taken.</param>
/// <param name="BaseSaved">
///     The write time of the package root's <c>project.json</c> when the snapshot was taken: a save since then has
///     moved on from the snapshot.
/// </param>
public sealed record AutosaveManifest(
    string Slot,
    string ProjectId,
    string Name,
    DateTimeOffset Autosaved,
    DateTimeOffset BaseSaved
);

/// <summary>
///     The autosaved snapshot of a package's unsaved edits (docs/04-phase-3.md, "Autosave and crash recovery"), under
///     <c>Autosave/</c>, which nothing else in the package reads: an autosave never writes the saved document. Each of
///     the two slots is a package document, so a future schema migrates it as it does any package. A write fills the
///     slot the manifest does not name, one atomically renamed file at a time and only the files that differ from what
///     the slot holds, and then renames a new manifest over the old one. That rename is the commit: whenever a write
///     stops, the manifest still names a whole snapshot.
/// </summary>
public static class ProjectAutosave {
    public const string ManifestName = "autosave.json";
    const string SlotA = "a";
    const string SlotB = "b";

    /// <summary>
    ///     Writes <paramref name="state" /> and <paramref name="states" /> (plugin states by key) as the package's
    ///     snapshot. <paramref name="write" /> stores each file (by default <see cref="WriteChanged" />).
    /// </summary>
    public static AutosaveManifest Write(
        string folder,
        ProjectState state,
        IReadOnlyDictionary<string, byte[]> states,
        DateTimeOffset now,
        Action<string, byte[]>? write = null
    ) {
        write ??= WriteChanged;
        var slot = Read(folder)?.Slot == SlotA ? SlotB : SlotA;
        var target = SlotFolder(folder, slot);
        Directory.CreateDirectory(target);
        RemoveTemporaryFiles(Path.Combine(folder, ProjectPackage.AutosaveFolder));
        ProjectPackage.Save(target, state, false, write);
        ProjectPackage.SaveStates(target, states, write);
        var manifest = new AutosaveManifest(slot, state.Id, state.Name, now, ProjectHistory.Modified(folder));
        write(
            ManifestPath(folder),
            JsonSerializer.SerializeToUtf8Bytes(manifest, PackageJsonContext.Default.AutosaveManifest)
        );
        return manifest;
    }

    /// <summary>Stores <paramref name="bytes" /> atomically, unless the file already holds them.</summary>
    public static void WriteChanged(string path, byte[] bytes) {
        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) {
            AtomicRename.Write(path, bytes);
        }
    }

    /// <summary>The package's snapshot, or null when it has no whole one.</summary>
    public static AutosaveManifest? Read(string folder) {
        var path = ManifestPath(folder);
        try {
            return File.Exists(path)
                && JsonSerializer.Deserialize(File.ReadAllText(path), PackageJsonContext.Default.AutosaveManifest) is {
                    Slot: SlotA or SlotB, ProjectId: not null, Name: not null
                } manifest
                && ProjectPackage.Exists(SlotFolder(folder, manifest.Slot))
                    ? manifest
                    : null;
        } catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    /// <summary>The snapshot to offer: the package's, unless the project was saved after it was taken.</summary>
    public static AutosaveManifest? Recoverable(string folder) =>
        Read(folder) is { } manifest && manifest.BaseSaved == ProjectHistory.Modified(folder) ? manifest : null;

    /// <summary>The folder of the document <paramref name="manifest" /> names.</summary>
    public static string Document(string folder, AutosaveManifest manifest) => SlotFolder(folder, manifest.Slot);

    /// <summary>Removes the snapshot: its edits were saved, or kept in a backup.</summary>
    public static void Clear(string folder) {
        var manifest = ManifestPath(folder);
        if (File.Exists(manifest)) {
            File.Delete(manifest); // first: from here on nothing is offered
        }

        var autosave = Path.Combine(folder, ProjectPackage.AutosaveFolder);
        if (Directory.Exists(autosave)) {
            Directory.Delete(autosave, true);
        }
    }

    /// <summary>
    ///     Moves the snapshot to a new backup, so nothing an autosave kept is lost when it is not recovered; the backup's
    ///     id, or null when there is no whole snapshot.
    /// </summary>
    public static string? Keep(string folder, DateTimeOffset now) {
        var backup = Read(folder) is { } manifest
            ? ProjectHistory.BackupDocument(folder, Document(folder, manifest), now)
            : null;
        Clear(folder);
        return backup;
    }

    static string ManifestPath(string folder) => Path.Combine(folder, ProjectPackage.AutosaveFolder, ManifestName);

    static string SlotFolder(string folder, string slot) => Path.Combine(folder, ProjectPackage.AutosaveFolder, slot);

    /// <summary>What a killed write left behind (<see cref="AtomicRename" />'s temporary files).</summary>
    static void RemoveTemporaryFiles(string folder) {
        foreach (var file in Directory.EnumerateFiles(folder, "*.tmp", SearchOption.AllDirectories)) {
            File.Delete(file);
        }
    }
}
