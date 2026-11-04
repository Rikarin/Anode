using Anode.Contracts;
using Anode.Model;
using Anode.Model.Storage;
using System.Collections.Immutable;

namespace Anode.Server.Projects;

/// <summary>
///     Changing the project's sample rate (docs/01-implementation-plan.md §5.6): the job writes new files beside the old
///     ones, which nothing names yet, and only when they are all there does this write the document, through a backup
///     (<see cref="ProjectHistory.ReplaceDocument" />). Up to that point a failure costs the new files and nothing else.
///     It saves: a change this large is not kept as unsaved edits, and undo steps from the old rate cannot apply to the
///     new one, so the way back is the backup, which still names the old files.
/// </summary>
public sealed partial class ProjectSession {
    /// <summary>
    ///     A change checked against the open project: 422 for a rate there is not, or the one it has; 409 while it plays;
    ///     422 for what cannot follow: files the disk lacks, and track files that cannot be read.
    /// </summary>
    public RatePlan PlanRate(ChangeSampleRateRequest? request) {
        if (request is null || !Enum.IsDefined(request.Mode)) {
            throw ProjectException.Unprocessable("Say what happens to the audio: convert it, or keep its samples.");
        }

        if (!ProjectDefaults.SampleRates.Contains(request.SampleRate)) {
            throw ProjectException.Unprocessable(
                $"Sample rate {request.SampleRate} Hz is not one of {string.Join(", ", ProjectDefaults.SampleRates)}."
            );
        }

        var offline = Offline();
        lock (gate) {
            var o = Required();
            if (request.SampleRate == o.State.SampleRate) {
                throw ProjectException.Unprocessable($"The project is at {request.SampleRate} Hz already.");
            }

            if (clock.Playing is not null) {
                throw ProjectException.Conflict("Stop playback before changing the sample rate.");
            }

            if (offline.Files.Count + offline.UnreadableTracks.Count > 0) {
                var names = string.Join(", ", offline.Files.Select(static f => $"'{f.Name}'").Take(5));
                throw ProjectException.Unprocessable(
                    offline.Files.Count > 0
                        ? $"Find the offline files first ({names}): they cannot be converted, and could not be used after."
                        : "Repair or remove the unreadable tracks first: their positions cannot be converted."
                );
            }

            if (ProjectRate.Refusal(o.State, request.SampleRate, request.Mode) is { } refusal) {
                throw ProjectException.Unprocessable(refusal);
            }

            return new(
                o.Session,
                o.Folder,
                o.State,
                request.SampleRate,
                request.Mode,
                [..ProjectRate.FilesToWrite(o.State, request.SampleRate, request.Mode)]
            );
        }
    }

    /// <summary>
    ///     Applies a change whose files are written: the project at the new rate is proved readable, the last save and any
    ///     unsaved edits go to backups, the document is replaced, and the project reopens there (engine and clients take
    ///     it from the top, with no undo history). Whatever stops it leaves the project as it was; 409 when the project
    ///     was closed or its files changed meanwhile.
    /// </summary>
    public Project CommitRate(
        RatePlan plan,
        IReadOnlyDictionary<string, AudioFile> files,
        IReadOnlyDictionary<string, byte[]?>? hostStates
    ) {
        using var writes = autosaving.EnterScope();
        lock (gate) {
            var o = Required();
            if (o.Session != plan.Session) {
                throw ProjectException.Conflict("The project was closed while its audio was converted.");
            }

            if (clock.Playing is not null) {
                throw ProjectException.Conflict("Stop playback before changing the sample rate.");
            }

            if (!o.State.Files.SequenceEqual(plan.State.Files)) {
                throw ProjectException.Conflict("The project's audio files changed while they were converted.");
            }

            var next = ProjectRate.Changed(o.State, plan.Rate, plan.Mode, files);
            var states = States(o, hostStates);
            var staged = Path.Combine(o.Folder, $".rate-{Guid.NewGuid():n}");
            try {
                ProjectPackage.Save(staged, next, false);
                ProjectPackage.SaveStates(staged, states.ToDictionary(static s => s.Key, static s => s.Value.Bytes));
                var read = ProjectPackage.Load(staged);
                if (read.SampleRate != plan.Rate || read.Regions.Count != next.Regions.Count) {
                    throw new PackageException("The project at the new rate did not read back.");
                }

                var now = time.GetUtcNow();
                var backup = ProjectHistory.Backup(o.Folder, now);
                if (o.Dirty) {
                    ProjectHistory.BackupState(o.Folder, o.State, now);
                }

                ProjectHistory.ReplaceDocument(o.Folder, staged, backup);
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException or PackageException) {
                throw ProjectException.Unprocessable(
                    $"Could not change the sample rate; the project stays at {o.State.SampleRate} Hz: {e.Message}"
                );
            } finally {
                Remove(staged);
            }

            o.Dirty = false;
            DropAutosave(o);
            return Replace(o.Folder, next, states, false, new(null, o.Revision + 1));
        }
    }

    /// <summary>A package whose document a crash left half replaced gets the old one back before anything reads it.</summary>
    static void FinishInterrupted(string folder) {
        try {
            ProjectHistory.FinishInterrupted(folder);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            throw ProjectException.Unprocessable($"Could not restore '{folder}' from its backup: {e.Message}");
        }
    }

    void Remove(string folder) {
        try {
            if (Directory.Exists(folder)) {
                Directory.Delete(folder, true);
            }
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            ProjectLog.BackupFailed(logger, folder, e.Message);
        }
    }
}

/// <summary>A change of sample rate as checked, and the files it writes.</summary>
public sealed record RatePlan(
    long Session,
    string Folder,
    ProjectState State,
    int Rate,
    SampleRateChangeMode Mode,
    ImmutableList<AudioFile> Files
);
