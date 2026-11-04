using Anode.Contracts;
using Anode.Model;
using Anode.Model.Audio;
using Anode.Model.Storage;

namespace Anode.Server.Projects;

/// <summary>Offline files: what the disk lacks of the open project (<see cref="OfflineFiles" />), and relinking them.</summary>
public sealed partial class ProjectSession {
    /// <summary>
    ///     Reads the disk now: what is offline. The report follows it, so a file put back comes online here (and the
    ///     engine opens it).
    /// </summary>
    public OfflineReport Offline() {
        long session;
        string folder;
        ProjectState state;
        lock (gate) {
            var o = Required();
            (session, folder, state) = (o.Session, o.Folder, o.State);
        }

        var report = OfflineFiles.Report(folder, state);
        var changed = false;
        lock (gate) {
            if (open is { } o && o.Session == session && ReferenceEquals(o.State, state)) {
                changed = Note(o, report);
            }
        }

        if (changed) {
            Changed?.Invoke();
        }

        return report;
    }

    /// <summary>
    ///     Looks for the offline files in a folder of the server's disk (left out: the project's), by name, and judges what
    ///     it finds by what it holds.
    /// </summary>
    public OfflineSearch SearchOffline(OfflineSearchRequest request) {
        var offline = Offline().Files.Select(static f => f.FileId).ToHashSet(StringComparer.Ordinal);
        string folder;
        AudioFile[] wanted;
        lock (gate) {
            var o = Required();
            folder = o.Folder;
            wanted = [..o.State.Files.Values.Where(f => offline.Contains(f.Id))];
        }

        string directory;
        try {
            directory = Path.GetFullPath(request.Directory is { Length: > 0 } d ? d : folder);
        } catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) {
            throw ProjectException.Unprocessable($"'{request.Directory}' is not a folder path: {e.Message}");
        }

        return Directory.Exists(directory)
            ? OfflineFiles.Search(directory, wanted)
            : throw ProjectException.NotFound($"No folder '{directory}'.");
    }

    /// <summary>A file of the open project, for a relink: which opening it is for, its folder, and the file.</summary>
    public (long Session, string Folder, AudioFile File) RelinkTarget(string? fileId) {
        lock (gate) {
            var o = Required();
            return o.State.Files.TryGetValue(fileId ?? string.Empty, out var file)
                ? (o.Session, o.Folder, file)
                : throw ProjectException.NotFound($"No audio file '{fileId}'.");
        }
    }

    /// <summary>
    ///     Points files at the WAVs that replace them, in the project's folder and checked, as one undo step "Relink
    ///     Files". Each counts as a new version of its file, so its peaks are its own, undo finds the previous ones, and a
    ///     later edit's versions go on past it. 409 when the project was closed or a file changed meanwhile.
    /// </summary>
    public CommandResult Relink(long session, IReadOnlyList<(AudioFile Read, string Path, WavInfo Info)> links) {
        CommandResult result;
        lock (gate) {
            var o = Required();
            if (o.Session != session) {
                throw ProjectException.Conflict("The project was closed while its files were relinked.");
            }

            var job = new JobEdit(o.State, ids, catalog.ForEdits);
            try {
                foreach (var (read, absolutePath, info) in links) {
                    if (!o.State.Files.TryGetValue(read.Id, out var file) || file.Path != read.Path) {
                        throw ProjectException.Conflict($"'{read.Name}' changed while it was relinked.");
                    }

                    job.Apply(
                        new SetFileVersionCommand(
                            file.Id,
                            NextVersion(o.Folder, file),
                            Path.GetRelativePath(o.Folder, absolutePath).Replace(Path.DirectorySeparatorChar, '/'),
                            info.Frames,
                            info.SampleRate,
                            info.Channels,
                            info.BitDepth
                        )
                    );
                }
            } catch (EditException e) {
                throw ProjectException.Unprocessable(e.Message);
            }

            job.Inverse.Reverse();
            result = Commit(o, job.State, job.Forward);
            o.History.Push("Relink Files", o.Revision, new(job.Forward, job.Inverse), null);
        }

        Changed?.Invoke();
        return result;
    }

    /// <summary>Takes in a fresh report; true, with the view republished and clients told, when it says something new.</summary>
    bool Note(Open o, OfflineReport report) {
        if (OfflineFiles.Same(o.Offline, report)) {
            return false;
        }

        o.Offline = report;
        Publish();
        hub.Publish(new OfflineChangedEvent(report, o.Revision));
        return true;
    }
}
