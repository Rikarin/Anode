using Anode.Contracts;
using System.Text.Json.Nodes;

namespace Anode.Model.Storage;

/// <summary>What a whole-package copy takes, beyond the media and the document.</summary>
/// <param name="History">Whether the backups and alternatives go too (a template has neither).</param>
public sealed record PackageCopyOptions(bool History = true);

/// <summary>
///     Writes a project as a whole package somewhere else (docs/04-phase-3.md, "Save As and Save a Copy"): every file of
///     the package, then the document as it is in memory. It is built in a hidden folder beside the target and renamed
///     to the target only when it is complete and reads back, so whatever stops it leaves no target and touches nothing
///     of the source: the source is only ever read. <c>Autosave/</c> stays behind, because the snapshot belongs to the
///     source's save and would be offered wrongly in the copy. A file that is offline in the source has nothing to copy
///     and stays offline; a track file that cannot be read is copied as it is, so it can still be repaired.
/// </summary>
public static class ProjectCopy {
    const string TemporaryExtension = ".tmp";
    const string BuildMarker = ".saving-";
    const int ChunkBytes = 1 << 20;

    /// <summary>A package's name for a folder: its last part without <see cref="ProjectPackage.Extension" />.</summary>
    public static string NameOf(string folder) {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        return name.EndsWith(ProjectPackage.Extension, StringComparison.OrdinalIgnoreCase)
            ? name[..^ProjectPackage.Extension.Length]
            : name;
    }

    /// <summary>
    ///     Builds the package of <paramref name="state" /> at <paramref name="target" />, which must not exist, with the
    ///     media and saved documents of the package at <paramref name="source" />. Saved documents take the new identity
    ///     too, so an alternative opened in the copy is the copy's. <paramref name="progress" /> hears 0…1. A failure, or
    ///     <paramref name="token" />, removes what was built and rethrows.
    /// </summary>
    public static void Write(
        string source,
        string target,
        ProjectState state,
        IReadOnlyDictionary<string, byte[]> states,
        PackageCopyOptions? options = null,
        Action<double>? progress = null,
        CancellationToken token = default
    ) {
        options ??= new();
        var parent = Path.GetDirectoryName(Path.GetFullPath(target))!;
        Directory.CreateDirectory(parent);
        var build = Path.Combine(parent, $"{BuildMarker}{Guid.NewGuid():n}");
        try {
            Directory.CreateDirectory(build);
            CopyTree(source, build, options, progress, token);
            Rename(build, state);
            ProjectPackage.Save(build, state);
            ProjectPackage.SaveStates(build, states);
            Check(build, state);
            token.ThrowIfCancellationRequested();
            Directory.Move(build, target);
            AtomicRename.FlushFolder(parent);
        } catch {
            Remove(build);
            throw;
        }
    }

    /// <summary>Copies the media of <paramref name="files" /> the package at <paramref name="target" /> lacks.</summary>
    public static void CopyMissing(string source, string target, IEnumerable<AudioFile> files) {
        foreach (var file in files) {
            var (from, to) = (Path.Combine(source, file.Path), Path.Combine(target, file.Path));
            if (File.Exists(from) && !File.Exists(to)) {
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                CopyFile(from, to, CancellationToken.None);
            }
        }
    }

    /// <summary>
    ///     Copies a file so that it counts only once its bytes are on the disk: a temporary file beside it, flushed, then
    ///     renamed.
    /// </summary>
    public static void CopyFile(string from, string to, CancellationToken token) {
        var temporary = $"{to}.{Guid.NewGuid():n}{TemporaryExtension}";
        try {
            using (var input = new FileStream(
                       from,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete
                   ))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                var buffer = new byte[ChunkBytes];
                int read;
                while ((read = input.Read(buffer)) > 0) {
                    token.ThrowIfCancellationRequested();
                    output.Write(buffer.AsSpan(0, read));
                }

                output.Flush(true);
            }

            AtomicRename.Replace(temporary, to);
        } catch {
            if (File.Exists(temporary)) {
                File.Delete(temporary);
            }

            throw;
        }
    }

    static void CopyTree(
        string source,
        string target,
        PackageCopyOptions options,
        Action<double>? progress,
        CancellationToken token
    ) {
        var files = Files(source, source, options).ToArray();
        var total = Math.Max(1, files.Sum(static f => new FileInfo(f).Length));
        long done = 0;
        foreach (var file in files) {
            token.ThrowIfCancellationRequested();
            var copy = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            CopyFile(file, copy, token);
            done += new FileInfo(file).Length;
            progress?.Invoke((double)done / total);
        }
    }

    /// <summary>The files of the package worth copying. A folder that cannot be read fails the copy, it is not skipped.</summary>
    static IEnumerable<string> Files(string root, string folder, PackageCopyOptions options) {
        foreach (var file in Directory.EnumerateFiles(folder)) {
            if (!file.EndsWith(TemporaryExtension, StringComparison.Ordinal)) {
                yield return file;
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(folder)) {
            if (Skipped(Path.GetRelativePath(root, directory), options)) {
                continue;
            }

            foreach (var file in Files(root, directory, options)) {
                yield return file;
            }
        }
    }

    static bool Skipped(string relative, PackageCopyOptions options) =>
        relative == ProjectPackage.AutosaveFolder
        || (!options.History && relative is ProjectPackage.BackupsFolder or ProjectHistory.AlternativesFolder);

    /// <summary>Gives the saved documents in a built package the project's id and name, leaving what cannot be read.</summary>
    static void Rename(string build, ProjectState state) {
        foreach (var sub in (string[])[ProjectPackage.BackupsFolder, ProjectHistory.AlternativesFolder]) {
            var root = Path.Combine(build, sub);
            if (!Directory.Exists(root)) {
                continue;
            }

            foreach (var document in Directory.EnumerateDirectories(root)
                         .Select(static d => Path.Combine(d, ProjectPackage.ProjectFileName))
                         .Where(File.Exists)) {
                Rename(document, state.Id, state.Name);
            }
        }
    }

    static void Rename(string document, string id, string name) {
        JsonObject? root;
        try {
            root = JsonNode.Parse(File.ReadAllText(document)) as JsonObject;
        } catch (System.Text.Json.JsonException) {
            return;
        }

        if (root is not null) {
            root["id"] = id;
            root["name"] = name;
            AtomicRename.Write(document, root.ToJsonString());
        }
    }

    /// <summary>The built package must read back as the project: a copy that does not is not put in place.</summary>
    static void Check(string build, ProjectState state) {
        var read = ProjectPackage.Load(build);
        if (read.Id != state.Id
            || read.SampleRate != state.SampleRate
            || read.Tracks.Count != state.Tracks.Count
            || read.Regions.Count != state.Regions.Count
            || read.MidiRegions.Count != state.MidiRegions.Count
            || read.Files.Count != state.Files.Count) {
            throw new PackageException($"The copy of '{state.Name}' did not read back as the project.");
        }
    }

    static void Remove(string build) {
        try {
            if (Directory.Exists(build)) {
                Directory.Delete(build, true);
            }
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            // What is left is hidden and named as a build; the error that stopped the copy is the one to report.
        }
    }
}
