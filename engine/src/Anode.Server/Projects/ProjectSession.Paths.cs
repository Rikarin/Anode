using Anode.Model.Storage;

namespace Anode.Server.Projects;

public sealed partial class ProjectSession {
    /// <summary>The folder of a project that loaded insert files (impulse responses) are copied into.</summary>
    public const string ImpulseResponsesFolder = "Impulse Responses";

    /// <summary>
    ///     An insert's file (an impulse response) as the project keeps it: one outside the project is copied into its
    ///     <see cref="ImpulseResponsesFolder" /> (a file of that name and size there is reused), so the project stays whole
    ///     when it moves; the path is the project's relative one.
    /// </summary>
    static string InsertPath(string folder, string path) {
        var full = Path.GetFullPath(InstrumentPath(folder, path), folder);
        if (IsInside(folder, full)) {
            return Path.GetRelativePath(folder, full).Replace(Path.DirectorySeparatorChar, '/');
        }

        return CopiedInto(folder, ImpulseResponsesFolder, full);
    }

    /// <summary>
    ///     A copy of <paramref name="full" /> in the project's <paramref name="target" /> folder (a file of that name and
    ///     size there is reused), by the project's relative path.
    /// </summary>
    static string CopiedInto(string folder, string target, string full) {
        try {
            var into = Directory.CreateDirectory(Path.Combine(folder, target)).FullName;
            var copy = Path.Combine(into, Path.GetFileName(full));
            for (var n = 2; File.Exists(copy) && new FileInfo(copy).Length != new FileInfo(full).Length; n++) {
                copy = Path.Combine(into, $"{Path.GetFileNameWithoutExtension(full)} {n}{Path.GetExtension(full)}");
            }

            if (!File.Exists(copy)) {
                File.Copy(full, copy);
            }

            return Path.GetRelativePath(folder, copy).Replace(Path.DirectorySeparatorChar, '/');
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            throw ProjectException.Unprocessable($"Could not copy '{full}' into the project: {e.Message}");
        }
    }

    static string InstrumentPath(string folder, string path) {
        string full;
        try {
            full = Path.GetFullPath(path.Trim(), folder);
        } catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) {
            throw ProjectException.Unprocessable($"'{path}' is not a file path: {e.Message}");
        }

        if (!File.Exists(full)) {
            throw ProjectException.Unprocessable($"No file '{full}'.");
        }

        return IsInside(folder, full)
            ? Path.GetRelativePath(folder, full).Replace(Path.DirectorySeparatorChar, '/')
            : full;
    }
}
