using Anode.Contracts;
using Anode.Model;
using Anode.Model.Editing;
using Anode.Model.Storage;
using System.Collections.Immutable;

namespace Anode.Server.Projects;

/// <summary>
///     Save As and Save a Copy (docs/04-phase-3.md, "Save As and Save a Copy"): the open project as a whole package
///     somewhere else (<see cref="ProjectCopy" />), under its own id, so the two stay apart in the recent list. A copy
///     leaves the session alone. Save As goes on in the new package with its undo history; the original keeps its last
///     save, its unsaved edits go to a backup there, and its autosave goes with them, so it offers no edits that are safe
///     elsewhere. Media is copied outside the locks, so edits go on; only a finished copy reaches the session.
/// </summary>
public sealed partial class ProjectSession {
    /// <summary>A package location as a client names it: full, with its extension, free, and outside the open project.</summary>
    public string SaveTarget(string? path) {
        if (string.IsNullOrWhiteSpace(path)) {
            throw ProjectException.Unprocessable("Name the folder to save the project to.");
        }

        string target;
        try {
            target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
        } catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) {
            throw ProjectException.Unprocessable($"'{path}' is not a folder path: {e.Message}");
        }

        if (!target.EndsWith(ProjectPackage.Extension, StringComparison.OrdinalIgnoreCase)) {
            target += ProjectPackage.Extension;
        }

        CheckProjectName(ProjectCopy.NameOf(target));
        string folder;
        lock (gate) {
            folder = Required().Folder;
        }

        if (target == folder || IsInside(folder, target)) {
            throw ProjectException.Unprocessable("A project cannot be saved inside itself.");
        }

        return Directory.Exists(target) || File.Exists(target)
            ? throw ProjectException.Conflict($"'{target}' already exists.")
            : target;
    }

    /// <summary>Writes the project as a package at <paramref name="target" /> and leaves the session where it is.</summary>
    public ProjectSummary SaveCopy(
        string target,
        IReadOnlyDictionary<string, byte[]?>? hostStates,
        Action<double>? progress,
        CancellationToken token
    ) {
        var copy = WriteCopy(target, hostStates, progress, token);
        var summary = new ProjectSummary(copy.State.Id, copy.State.Name, target, time.GetUtcNow());
        recent.Touch(summary);
        return summary;
    }

    /// <summary>
    ///     Writes the project as a package at <paramref name="target" /> and goes on there. Edits made during the copy go
    ///     on too, and leave the new package unsaved; files added during it are copied now. 409 when the project was
    ///     closed meanwhile (the package stays: it is whole).
    /// </summary>
    public Project SaveAs(
        string target,
        IReadOnlyDictionary<string, byte[]?>? hostStates,
        Action<double>? progress,
        CancellationToken token
    ) {
        if (clock.Playing is not null) {
            throw ProjectException.Conflict("Stop playback before Save As: the project goes on in the new package.");
        }

        var copy = WriteCopy(target, hostStates, progress, token);
        using var writes = autosaving.EnterScope();
        lock (gate) {
            if (open is not { } o || o.Session != copy.Session) {
                throw ProjectException.Conflict($"The project was closed; its copy is at '{target}'.");
            }

            try {
                ProjectCopy.CopyMissing(o.Folder, target, o.State.Files.Values);
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                throw ProjectException.Unprocessable($"Could not finish the copy at '{target}': {e.Message}");
            }

            var moved = !ReferenceEquals(o.State, copy.Read);
            return Replace(
                target,
                o.State with { Id = copy.State.Id, Name = copy.State.Name },
                moved ? o.States : copy.States,
                moved,
                new(o.History, o.Revision + 1)
            );
        }
    }

    PackageWritten WriteCopy(
        string target,
        IReadOnlyDictionary<string, byte[]?>? hostStates,
        Action<double>? progress,
        CancellationToken token
    ) {
        long session;
        string folder;
        ProjectState read;
        ImmutableDictionary<string, SavedState> states;
        lock (gate) {
            var o = Required();
            (session, folder, read) = (o.Session, o.Folder, o.State);
            states = States(o, hostStates);
        }

        var state = read with { Id = ids.Next(IdPrefix.Project), Name = ProjectCopy.NameOf(target) };
        try {
            ProjectCopy.Write(
                folder,
                target,
                state,
                states.ToDictionary(static s => s.Key, static s => s.Value.Bytes),
                null,
                progress,
                token
            );
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or PackageException) {
            throw ProjectException.Unprocessable($"Could not save to '{target}': {e.Message}");
        }

        return new(session, read, state, states);
    }

    /// <summary>A project name that can be a folder name: 422 otherwise.</summary>
    static string CheckProjectName(string name) =>
        name.Length is 0 or > Edits.MaxNameLength
        || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
        || name.StartsWith('.')
            ? throw ProjectException.Unprocessable("A project name has 1…128 characters and no path separators.")
            : name;

    sealed record PackageWritten(
        long Session,
        ProjectState Read,
        ProjectState State,
        ImmutableDictionary<string, SavedState> States
    );
}
