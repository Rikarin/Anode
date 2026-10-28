using Anode.Contracts;
using System.Globalization;
using System.Text.Json;

namespace Anode.Model.Storage;

/// <summary>An alternative as <c>Alternatives/alternatives.json</c> lists it.</summary>
public sealed record AlternativeEntry(string Id, string Name, DateTimeOffset Modified);

/// <summary><c>Alternatives/alternatives.json</c>: which alternative the package root holds, and every alternative.</summary>
public sealed record AlternativesFile(string Current, IReadOnlyList<AlternativeEntry> Alternatives);

/// <summary>
///     A package's alternatives and backups (docs/04-phase-3.md, "Alternatives and backups"). A package's document is
///     its <c>project.json</c>, <c>tracks/</c> and <c>Plugin States/</c>; its media (<c>Audio Files/</c>,
///     <c>Peaks/</c>) is shared. The current alternative's document is the package root; every other alternative's is
///     under <c>Alternatives/&lt;id&gt;/</c>. Each save first copies the root's document to
///     <c>Backups/&lt;timestamp&gt;/</c>, keeping the newest <see cref="MaxBackups" />.
/// </summary>
public static class ProjectHistory {
    public const string AlternativesFolder = "Alternatives";
    public const string ManifestName = "alternatives.json";
    public const int MaxBackups = 10;

    /// <summary>The id of the alternative a package starts with, before it has a manifest.</summary>
    public const string FirstAlternative = "alt_main";

    const string StampFormat = "yyyyMMdd'T'HHmmssfff'Z'";

    /// <summary>Copies the document of the package at <paramref name="from" /> over the one at <paramref name="to" />.</summary>
    public static void CopyDocument(string from, string to) {
        ClearDocument(to);
        Directory.CreateDirectory(to);
        foreach (var sub in new[] { ProjectPackage.TracksFolder, ProjectPackage.PluginStatesFolder }) {
            var source = Path.Combine(from, sub);
            if (!Directory.Exists(source)) {
                continue;
            }

            var target = Path.Combine(to, sub);
            Directory.CreateDirectory(target);
            foreach (var file in Directory.EnumerateFiles(source)) {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
            }
        }

        // The root file last, so a copy cut short is not a readable package.
        File.Copy(Path.Combine(from, ProjectPackage.ProjectFileName), Path.Combine(to, ProjectPackage.ProjectFileName), true);
    }

    /// <summary>Removes a package's document (not its media).</summary>
    public static void ClearDocument(string folder) {
        var root = Path.Combine(folder, ProjectPackage.ProjectFileName);
        if (File.Exists(root)) {
            File.Delete(root);
        }

        foreach (var sub in new[] { ProjectPackage.TracksFolder, ProjectPackage.PluginStatesFolder }) {
            var path = Path.Combine(folder, sub);
            if (Directory.Exists(path)) {
                Directory.Delete(path, true);
            }
        }
    }

    /// <summary>
    ///     Copies the root's saved document to a new backup (nothing when there is none yet) and keeps the newest
    ///     <see cref="MaxBackups" />; the new backup's id, or null.
    /// </summary>
    public static string? Backup(string folder, DateTimeOffset now) {
        if (!File.Exists(Path.Combine(folder, ProjectPackage.ProjectFileName))) {
            return null;
        }

        var target = NewBackupFolder(folder, now);
        CopyDocument(folder, target);
        Prune(folder);
        return Path.GetFileName(target);
    }

    /// <summary>Writes <paramref name="state" /> (unsaved edits) as a new backup; its id.</summary>
    public static string BackupState(string folder, ProjectState state, DateTimeOffset now) {
        var target = NewBackupFolder(folder, now);
        ProjectPackage.Save(target, state, false);
        Prune(folder);
        return Path.GetFileName(target);
    }

    /// <summary>The backups, newest first.</summary>
    public static IReadOnlyList<ProjectBackup> Backups(string folder) {
        var backups = Path.Combine(folder, ProjectPackage.BackupsFolder);
        if (!Directory.Exists(backups)) {
            return [];
        }

        return [
            ..Directory.EnumerateDirectories(backups)
                .Where(static d => ProjectPackage.Exists(d) && ProjectPackage.IsSafeId(Path.GetFileName(d)))
                .Select(static d => new ProjectBackup(Path.GetFileName(d), SavedAt(d)))
                .OrderByDescending(static b => b.Id, StringComparer.Ordinal)
        ];
    }

    /// <summary>The folder of backup <paramref name="id" />, or null when there is none.</summary>
    public static string? BackupFolder(string folder, string id) {
        if (!ProjectPackage.IsSafeId(id)) {
            return null;
        }

        var path = Path.Combine(folder, ProjectPackage.BackupsFolder, id);
        return ProjectPackage.Exists(path) ? path : null;
    }

    /// <summary>The alternatives (the current one first), named after the project when it has no manifest yet.</summary>
    public static AlternativesFile Alternatives(string folder, string projectName) {
        var path = Path.Combine(folder, AlternativesFolder, ManifestName);
        AlternativesFile? manifest = null;
        try {
            if (File.Exists(path)) {
                manifest = JsonSerializer.Deserialize(File.ReadAllText(path), PackageJsonContext.Default.AlternativesFile);
            }
        } catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) {
            // A damaged manifest: start again from what is on disk.
        }

        var entries = (manifest?.Alternatives ?? [])
            .Where(static a => a?.Id is not null && a.Name is not null && ProjectPackage.IsSafeId(a.Id))
            .DistinctBy(static a => a.Id)
            .ToList();
        var current = manifest?.Current is { } c && ProjectPackage.IsSafeId(c) ? c : FirstAlternative;
        entries.RemoveAll(a => a.Id != current && !ProjectPackage.Exists(Path.Combine(folder, AlternativesFolder, a.Id)));
        if (!entries.Exists(a => a.Id == current)) {
            entries.Insert(0, new(current, projectName, Modified(folder)));
        }

        return new(current, entries);
    }

    /// <summary>
    ///     Keeps the current alternative as last saved under <c>Alternatives/</c> and makes a new one current; the
    ///     caller then saves the open project to the root.
    /// </summary>
    public static AlternativesFile CreateAlternative(
        string folder,
        string projectName,
        string id,
        string name,
        DateTimeOffset now
    ) {
        var manifest = Alternatives(folder, projectName);
        CopyDocument(folder, AlternativeFolder(folder, manifest.Current));
        var updated = new AlternativesFile(id, [..manifest.Alternatives, new(id, name, now)]);
        Write(folder, updated);
        return updated;
    }

    /// <summary>Swaps alternative <paramref name="id" /> into the root, keeping the current one under <c>Alternatives/</c>.</summary>
    public static AlternativesFile OpenAlternative(string folder, string projectName, string id, DateTimeOffset now) {
        var manifest = Alternatives(folder, projectName);
        var source = AlternativeFolder(folder, id);
        if (!ProjectPackage.Exists(source)) {
            throw new PackageException($"No alternative '{id}'.");
        }

        CopyDocument(folder, AlternativeFolder(folder, manifest.Current));
        CopyDocument(source, folder);
        ClearDocument(source);
        Directory.Delete(source, true);
        var updated = new AlternativesFile(
            id,
            [..manifest.Alternatives.Select(a => a.Id == manifest.Current ? a with { Modified = now } : a)]
        );
        Write(folder, updated);
        return updated;
    }

    public static void DeleteAlternative(string folder, string projectName, string id) {
        var manifest = Alternatives(folder, projectName);
        if (id == manifest.Current) {
            throw new PackageException("The current alternative cannot be deleted: open another one first.");
        }

        var path = AlternativeFolder(folder, id);
        if (!manifest.Alternatives.Any(a => a.Id == id) && !ProjectPackage.Exists(path)) {
            throw new PackageException($"No alternative '{id}'.");
        }

        if (Directory.Exists(path)) {
            Directory.Delete(path, true);
        }

        Write(folder, manifest with { Alternatives = [..manifest.Alternatives.Where(a => a.Id != id)] });
    }

    /// <summary>When the document at <paramref name="folder" /> was last written.</summary>
    public static DateTimeOffset Modified(string folder) {
        var root = Path.Combine(folder, ProjectPackage.ProjectFileName);
        return File.Exists(root) ? new DateTimeOffset(File.GetLastWriteTimeUtc(root), TimeSpan.Zero) : DateTimeOffset.UnixEpoch;
    }

    public static string AlternativeFolder(string folder, string id) =>
        ProjectPackage.IsSafeId(id)
            ? Path.Combine(folder, AlternativesFolder, id)
            : throw new PackageException($"'{id}' is not an alternative id.");

    static void Write(string folder, AlternativesFile manifest) {
        Directory.CreateDirectory(Path.Combine(folder, AlternativesFolder));
        AtomicRename.Write(
            Path.Combine(folder, AlternativesFolder, ManifestName),
            JsonSerializer.Serialize(manifest, PackageJsonContext.Default.AlternativesFile)
        );
    }

    static string NewBackupFolder(string folder, DateTimeOffset now) {
        var backups = Path.Combine(folder, ProjectPackage.BackupsFolder);
        Directory.CreateDirectory(backups);
        var stamp = now.UtcDateTime.ToString(StampFormat, CultureInfo.InvariantCulture);
        for (var n = 1;; n++) {
            var candidate = Path.Combine(backups, n == 1 ? stamp : $"{stamp}-{n}");
            if (!Directory.Exists(candidate)) {
                Directory.CreateDirectory(candidate);
                return candidate;
            }
        }
    }

    /// <summary>Removes all but the newest <see cref="MaxBackups" /> backups.</summary>
    static void Prune(string folder) {
        var backups = Path.Combine(folder, ProjectPackage.BackupsFolder);
        foreach (var stale in Directory.EnumerateDirectories(backups)
                     .OrderByDescending(static d => Path.GetFileName(d), StringComparer.Ordinal)
                     .Skip(MaxBackups)) {
            Directory.Delete(stale, true);
        }
    }

    static DateTimeOffset SavedAt(string backup) {
        var name = Path.GetFileName(backup);
        return name.Length >= 19
               && DateTime.TryParseExact(
                   name[..19],
                   StampFormat,
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                   out var stamp
               )
            ? new(stamp, TimeSpan.Zero)
            : Modified(backup);
    }
}
