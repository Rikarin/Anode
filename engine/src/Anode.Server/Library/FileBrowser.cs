using Anode.Contracts;
using Anode.Model.Audio;
using Anode.Model.Storage;
using Anode.Server.Projects;

namespace Anode.Server.Library;

/// <summary>
///     The All Files browser over the server's disk (docs/04-phase-3.md, "Files and MIDI import"): a folder's entries,
///     folders first, then files, each by name; hidden entries (a leading dot) left out. Anode project packages are
///     <c>project</c> entries rather than folders, and a WAV file carries its format when it could be read.
/// </summary>
public static class FileBrowser {
    public const int MaxEntries = 10_000;

    static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase) {
        ".wav", ".wave", ".aif", ".aiff", ".aifc", ".caf", ".flac", ".mp3", ".m4a", ".ogg"
    };

    static readonly HashSet<string> MidiExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mid", ".midi", ".smf" };

    static readonly HashSet<string> InstrumentExtensions = new(StringComparer.OrdinalIgnoreCase) { ".sfz", ".clap" };

    /// <summary>The user's home folder: where the browser starts.</summary>
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static DirectoryListing List(string? path) {
        string folder;
        try {
            folder = path is { Length: > 0 } given ? Path.GetFullPath(given) : Home;
        } catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) {
            throw ProjectException.Unprocessable($"'{path}' is not a folder path: {e.Message}");
        }

        if (path is { Length: > 0 } && !Path.IsPathFullyQualified(path)) {
            throw ProjectException.Unprocessable($"'{path}' is not an absolute path.");
        }

        if (!Directory.Exists(folder)) {
            throw ProjectException.NotFound($"No folder '{folder}'.");
        }

        var directory = new DirectoryInfo(folder);
        var entries = new List<FileEntry>();
        try {
            foreach (var info in directory.EnumerateFileSystemInfos()) {
                if (entries.Count >= MaxEntries) {
                    break;
                }

                if (Entry(info) is { } entry) {
                    entries.Add(entry);
                }
            }
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) {
            throw ProjectException.Unprocessable($"Could not read '{folder}': {e.Message}");
        }

        var parent = directory.Parent?.FullName;
        return new(
            Path.TrimEndingDirectorySeparator(directory.FullName) is { Length: > 0 } trimmed ? trimmed : directory.FullName,
            parent,
            [
                ..entries.OrderBy(static e => e.Kind == FileKind.Folder ? 0 : 1)
                    .ThenBy(static e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static e => e.Name, StringComparer.Ordinal)
            ]
        );
    }

    /// <summary>What kind of file a path is, by its extension (a folder ending in <c>.anode</c> is a project).</summary>
    public static FileKind KindOf(string path, bool isFolder) {
        var extension = Path.GetExtension(path);
        if (isFolder) {
            return string.Equals(extension, ProjectPackage.Extension, StringComparison.OrdinalIgnoreCase)
                ? FileKind.Project
                : FileKind.Folder;
        }

        return AudioExtensions.Contains(extension)
            ? FileKind.Audio
            : MidiExtensions.Contains(extension)
                ? FileKind.Midi
                : InstrumentExtensions.Contains(extension)
                    ? FileKind.Instrument
                    : FileKind.Other;
    }

    static FileEntry? Entry(FileSystemInfo info) {
        if (info.Name.StartsWith('.')) {
            return null;
        }

        try {
            var isFolder = info is DirectoryInfo;
            var kind = KindOf(info.FullName, isFolder);
            var size = info is FileInfo file ? file.Length : 0;
            var modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            int? rate = null, channels = null;
            long? frames = null;
            if (kind == FileKind.Audio && Path.GetExtension(info.Name) is var ext
                && (ext.Equals(".wav", StringComparison.OrdinalIgnoreCase) || ext.Equals(".wave", StringComparison.OrdinalIgnoreCase))) {
                try {
                    var wav = WavReader.Probe(info.FullName);
                    (rate, channels, frames) = (wav.SampleRate, wav.Channels, wav.Frames);
                } catch (Exception e) when (e is WavFormatException or IOException or UnauthorizedAccessException) {
                    // Not a WAV Anode reads: listed without its format.
                }
            }

            return new(info.Name, info.FullName, kind, size, modified, rate, channels, frames);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            return null; // gone or unreadable meanwhile
        }
    }
}
