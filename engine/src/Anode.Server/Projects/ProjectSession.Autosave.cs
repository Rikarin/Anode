using Anode.Contracts;
using Anode.Model;
using Anode.Model.Storage;
using System.Collections.Immutable;

namespace Anode.Server.Projects;

/// <summary>
///     Autosave and crash recovery (docs/04-phase-3.md, "Autosave and crash recovery"): unsaved edits go to the
///     package's <c>Autosave/</c> (<see cref="ProjectAutosave" />), never over the saved document. A save, or keeping the
///     edits in a backup, removes the snapshot, so one that outlives its session holds edits nobody saved.
/// </summary>
public sealed partial class ProjectSession {
    bool autosaveFailing;

    /// <summary>
    ///     Writes the open project's unsaved edits, if any are new, to its autosave; whether it wrote one. Only the
    ///     immutable snapshot is read under the lock, so edits go on during the write. It asks nothing of the engine:
    ///     plugin states are the ones last saved or brought by a patch.
    /// </summary>
    public bool Autosave() {
        lock (autosaving) {
            AutosaveWork? work;
            lock (gate) {
                work = open is { Dirty: true } o && o.Changes != o.Autosaved
                    ? new(
                        o,
                        o.Changes,
                        o.Revision,
                        o.State,
                        States(o, null).ToDictionary(static s => s.Key, static s => s.Value.Bytes)
                    )
                    : null;
            }

            if (work is null) {
                return false;
            }

            var now = time.GetUtcNow();
            try {
                ProjectAutosave.Write(work.Open.Folder, work.State, work.States, now);
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) {
                AutosaveFailed(work.Open.Folder, e.Message);
                return false;
            }

            lock (gate) {
                work.Open.MarkAutosaved(work.Changes);
            }

            autosaveFailing = false;
            hub.Publish(new ProjectAutosavedEvent(work.Revision, now));
            return true;
        }
    }

    /// <summary>The recent projects with autosaved edits their last save lacks; the open one is not among them.</summary>
    public IReadOnlyList<RecoverableProject> Recoverable() {
        var current = View?.Folder;
        return [
            ..recent.List()
                .Where(p => p.Path != current)
                .Select(static p => (p.Path, Manifest: ProjectAutosave.Recoverable(p.Path)))
                .Where(static p => p.Manifest is not null)
                .Select(static p => new RecoverableProject(
                        p.Manifest!.ProjectId,
                        p.Manifest.Name,
                        p.Path,
                        p.Manifest.Autosaved
                    )
                )
        ];
    }

    /// <summary>
    ///     Opens a project with its autosaved edits, closing the open one. Nothing is written to the saved project, and
    ///     the snapshot stays until the edits are saved or kept: a second crash loses nothing.
    /// </summary>
    public Project Recover(string path) {
        var (folder, manifest) = Snapshot(path);
        var document = ProjectAutosave.Document(folder, manifest);
        ProjectState state;
        Dictionary<string, byte[]> states;
        try {
            state = ProjectPackage.Load(document);
            states = ProjectPackage.LoadStates(document, state);
            ProjectPackage.CreateFolders(folder);
        } catch (Exception e) when (e is PackageException or IOException or UnauthorizedAccessException) {
            throw ProjectException.Unprocessable($"Could not read the autosaved edits: {e.Message}");
        }

        var plugins = PluginsByKey(state);
        return Replace(
            folder,
            state,
            states.Where(s => plugins.ContainsKey(s.Key))
                .ToImmutableDictionary(
                    static s => s.Key,
                    s => new SavedState(plugins[s.Key], s.Value),
                    StringComparer.Ordinal
                ),
            true
        );
    }

    /// <summary>Stops offering a project's autosaved edits, keeping them as its newest backup.</summary>
    public void DiscardRecovery(string path) {
        var (folder, _) = Snapshot(path);
        lock (autosaving) {
            lock (gate) {
                try {
                    ProjectAutosave.Keep(folder, time.GetUtcNow());
                } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                    throw ProjectException.Unprocessable($"Could not keep the autosaved edits: {e.Message}");
                }
            }
        }
    }

    /// <summary>The folder and snapshot of a project that is not open; 404 without a snapshot, 409 while it is open.</summary>
    (string Folder, AutosaveManifest Manifest) Snapshot(string path) {
        if (string.IsNullOrWhiteSpace(path)) {
            throw ProjectException.Unprocessable("Name the project folder.");
        }

        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (View?.Folder == folder) {
            throw ProjectException.Conflict($"'{folder}' is open.");
        }

        return (folder, ProjectAutosave.Recoverable(folder)
            ?? throw ProjectException.NotFound($"'{folder}' has no autosaved edits."));
    }

    /// <summary>A save, or a backup, now holds the edits: the snapshot goes. Stale, it is ignored if this fails.</summary>
    void DropAutosave(Open o) {
        try {
            ProjectAutosave.Clear(o.Folder);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            ProjectLog.AutosaveFailed(logger, o.Folder, e.Message);
        }

        o.MarkAutosaved(o.Changes);
    }

    /// <summary>
    ///     A snapshot not recovered when its project opens goes to a backup, out of the way of the new session's own and
    ///     never lost. If it cannot be moved it stays where it is.
    /// </summary>
    void KeepAutosave(string folder) {
        try {
            ProjectAutosave.Keep(folder, time.GetUtcNow());
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            ProjectLog.BackupFailed(logger, folder, e.Message);
        }
    }

    /// <summary>The project is closing: unsaved edits go to a backup. If that fails, its snapshot stays.</summary>
    void KeepOnClose(Open o) {
        try {
            KeepUnsaved(o);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            ProjectLog.BackupFailed(logger, o.Folder, e.Message);
        }
    }

    /// <summary>Logs every failed autosave, and tells clients of the first since one worked.</summary>
    void AutosaveFailed(string folder, string message) {
        ProjectLog.AutosaveFailed(logger, folder, message);
        if (!autosaveFailing) {
            autosaveFailing = true;
            hub.Publish(new AutosaveFailedEvent($"Autosave failed: {message}"));
        }
    }

    sealed record AutosaveWork(
        Open Open,
        long Changes,
        long Revision,
        ProjectState State,
        Dictionary<string, byte[]> States
    );
}

static partial class ProjectLog {
    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not autosave {Path}: {Error}")]
    public static partial void AutosaveFailed(ILogger logger, string path, string error);
}
