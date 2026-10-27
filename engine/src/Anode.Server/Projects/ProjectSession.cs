using Anode.Contracts;
using Anode.Ipc;
using Anode.Model;
using Anode.Model.Audio;
using Anode.Model.Compile;
using Anode.Model.Editing;
using Anode.Model.History;
using Anode.Model.Storage;
using Anode.Server.Realtime;
using Microsoft.Extensions.Options;

namespace Anode.Server.Projects;

/// <summary>
///     What the engine link needs of the open project: the snapshot, its folder, the engine ids, and which opening of
///     a project it belongs to (<see cref="Session" /> changes with every open, create and close).
/// </summary>
public sealed record OpenProjectView(long Session, string Folder, ProjectState State, EngineIds Ids, long Revision) {
    public string AudioFolder => Path.Combine(Folder, ProjectPackage.AudioFolder);
}

/// <summary>
///     The open project (one at a time): the server's truth (docs/adr/0004-state-ownership.md). Every edit, undo,
///     redo, import and finished recording goes through here, under one lock: it raises the revision by one,
///     publishes exactly one <see cref="ProjectChangedEvent" /> and raises <see cref="Changed" />, which the engine
///     link answers by syncing the host.
/// </summary>
public sealed class ProjectSession(
    IOptions<ServerOptions> options,
    EventHub hub,
    RecentProjects recent,
    PeakJobs peaks,
    TimeProvider time,
    ILogger<ProjectSession> logger
) {
    readonly Lock gate = new();
    readonly RandomIds ids = RandomIds.Instance;
    Open? open;
    long sessions;
    OpenProjectView? view;

    /// <summary>The project changed, opened or closed; read <see cref="View" /> for the current one.</summary>
    public event Action? Changed;

    /// <summary>The open project as the engine link sees it, or null.</summary>
    public OpenProjectView? View => Volatile.Read(ref view);

    public string ProjectsDirectory => Path.Combine(options.Value.DataDirectory, "Projects");

    public Project Get() {
        lock (gate) {
            return Required().ToProject();
        }
    }

    public Project Create(CreateProjectRequest request) {
        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length is 0 or > Edits.MaxNameLength
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.StartsWith('.')) {
            throw ProjectException.Unprocessable("A project name has 1…128 characters and no path separators.");
        }

        var rate = request.SampleRate ?? ProjectDefaults.SampleRate;
        if (!ProjectDefaults.SampleRates.Contains(rate)) {
            throw ProjectException.Unprocessable(
                $"Sample rate {rate} Hz is not one of {string.Join(", ", ProjectDefaults.SampleRates)}."
            );
        }

        var directory = Path.GetFullPath(request.Directory is { Length: > 0 } d ? d : ProjectsDirectory);
        var folder = Path.Combine(directory, name + ProjectPackage.Extension);
        if (Directory.Exists(folder) || File.Exists(folder)) {
            throw ProjectException.Conflict($"'{folder}' already exists.");
        }

        var state = ProjectDefaults.NewProject(ids.Next(IdPrefix.Project), name, rate);
        try {
            ProjectPackage.Save(folder, state);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            throw ProjectException.Unprocessable($"Could not create '{folder}': {e.Message}");
        }

        return Replace(folder, state);
    }

    public Project OpenFolder(string path) {
        if (string.IsNullOrWhiteSpace(path)) {
            throw ProjectException.Unprocessable("Name the project folder to open.");
        }

        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!ProjectPackage.Exists(folder)) {
            throw ProjectException.NotFound($"'{folder}' is not an Anode project.");
        }

        ProjectState state;
        try {
            state = ProjectPackage.Load(folder);
        } catch (PackageException e) {
            throw ProjectException.Unprocessable(e.Message);
        }

        ProjectPackage.CreateFolders(folder);
        return Replace(folder, state);
    }

    public ProjectSummary Save() {
        ProjectSummary summary;
        lock (gate) {
            var o = Required();
            try {
                ProjectPackage.Save(o.Folder, o.State);
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                throw ProjectException.Unprocessable($"Could not save '{o.Folder}': {e.Message}");
            }

            o.Dirty = false;
            summary = o.Summary(time.GetUtcNow());
            recent.Touch(summary);
            hub.Publish(new ProjectSavedEvent(summary, o.Revision));
        }

        return summary;
    }

    public void Close() {
        lock (gate) {
            Required();
            open = null;
            Publish();
            hub.Publish(new ProjectClosedEvent());
        }

        Changed?.Invoke();
    }

    public CommandResult Apply(CommandBatch batch, long? expected) {
        CommandResult result;
        lock (gate) {
            var o = Required();
            CheckRevision(o, expected);
            if (batch.Commands is null) {
                throw ProjectException.Unprocessable("A batch needs a commands list.");
            }

            EditResult edit;
            try {
                edit = Edits.Apply(o.State, batch.Commands, ids);
            } catch (EditException e) {
                throw ProjectException.Unprocessable(e.Message);
            }

            result = Commit(o, edit.State, edit.Forward);
            o.History.Push(
                UndoLabels.For(batch),
                o.Revision,
                new(edit.Forward, edit.Inverse),
                UndoLabels.CoalesceKey(batch)
            );
        }

        Changed?.Invoke();
        return result;
    }

    public CommandResult Undo(long? expected) {
        CommandResult result;
        lock (gate) {
            var o = Required();
            CheckRevision(o, expected);
            var step = o.History.PeekUndo() ?? throw ProjectException.Unprocessable("There is nothing to undo.");
            result = Commit(o, o.State.Apply(step.Inverse), step.Inverse);
            o.History.Undone(o.Revision);
        }

        Changed?.Invoke();
        return result;
    }

    public CommandResult Redo(long? expected) {
        CommandResult result;
        lock (gate) {
            var o = Required();
            CheckRevision(o, expected);
            var step = o.History.PeekRedo() ?? throw ProjectException.Unprocessable("There is nothing to redo.");
            result = Commit(o, o.State.Apply(step.Forward), step.Forward);
            o.History.Redone(o.Revision);
        }

        Changed?.Invoke();
        return result;
    }

    public UndoHistory History() {
        lock (gate) {
            return Required().History.History();
        }
    }

    /// <summary>The open project's folder and sample rate, and which opening it is.</summary>
    public (long Session, string Folder, int SampleRate) Target() {
        lock (gate) {
            var o = Required();
            return (o.Session, o.Folder, o.State.SampleRate);
        }
    }

    /// <summary>
    ///     Adds a WAV already copied into the open project's <c>Audio Files</c> folder to its pool: a change of its
    ///     own (a new revision and a delta), not an undo step. Starts building its peaks.
    /// </summary>
    public AudioFile AddFile(long session, string absolutePath, WavInfo info) {
        AudioFile file;
        lock (gate) {
            var o = Required();
            if (o.Session != session) {
                throw ProjectException.Conflict("The project was closed while the file was imported.");
            }

            file = Describe(o, absolutePath, info);
            var op = new AudioFileUpserted(file);
            Commit(o, o.State.Apply(op), [op]);
        }

        StartPeaks(file);
        Changed?.Invoke();
        return file;
    }

    /// <summary>Waits until the peaks of a file of the open project exist; its level file's path.</summary>
    public async Task<string> PeaksAsync(string fileId, int framesPerPeak, CancellationToken cancellationToken) {
        string folder;
        AudioFile file;
        lock (gate) {
            var o = Required();
            file = o.State.Files.TryGetValue(fileId, out var f)
                ? f
                : throw ProjectException.NotFound($"No audio file '{fileId}'.");
            folder = o.Folder;
        }

        var peaksFolder = Path.Combine(folder, ProjectPackage.PeaksFolder);
        try {
            await peaks.EnsureAsync(peaksFolder, file.Id, Path.Combine(folder, file.Path), cancellationToken)
                .ConfigureAwait(false);
        } catch (Exception e) when (e is IOException
                                        or UnauthorizedAccessException
                                        or WavFormatException
                                        or InvalidDataException) {
            throw ProjectException.Unprocessable($"No peaks for '{fileId}': {e.Message}");
        }

        return Peaks.PathOf(peaksFolder, file.Id, framesPerPeak);
    }

    /// <summary>
    ///     Applies a finished recording as one undoable batch, "Record": each take's file joins the pool (that part
    ///     stays on undo) and becomes a region on its track at the position it was recorded at.
    /// </summary>
    public CommandResult? ApplyRecording(IReadOnlyList<RecordedTake> takes) {
        var probed = new List<(RecordedTake Take, WavInfo Info)>();
        foreach (var take in takes) {
            try {
                probed.Add((take, WavReader.Probe(take.Path)));
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException or WavFormatException) {
                ProjectLog.TakeSkipped(logger, take.Path, e.Message);
            }
        }

        CommandResult? result = null;
        var files = new List<AudioFile>();
        lock (gate) {
            if (open is not { } o || probed.Count == 0) {
                return null;
            }

            var forward = new List<DeltaOp>();
            var inverse = new List<DeltaOp>();
            var state = o.State;
            foreach (var (take, info) in probed) {
                if (Take(o, state, take, info) is not { } taken) {
                    continue;
                }

                var (file, region) = taken;
                files.Add(file);
                state = Edits.Step(state, [new AudioFileUpserted(file), new RegionUpserted(region)], forward, inverse);
            }

            if (forward.Count > 0) {
                inverse.Reverse();
                result = Commit(o, state, forward);
                o.History.Push("Record", o.Revision, new(forward, inverse), null);
            }
        }

        foreach (var file in files) {
            StartPeaks(file);
        }

        Changed?.Invoke();
        return result;
    }

    /// <summary>The file and region a recorded take becomes, or null (logged) when it cannot be used.</summary>
    (AudioFile File, AudioRegion Region)? Take(Open o, ProjectState state, RecordedTake take, WavInfo info) {
        var trackId = o.Ids.TrackOf(take.TrackId);
        var track = trackId is null ? null : state.FindTrack(trackId);
        if (track is null) {
            ProjectLog.TakeSkipped(logger, take.Path, "its track is gone");
            return null;
        }

        if (info.SampleRate != state.SampleRate || info.Frames == 0 || !IsInside(o.Folder, take.Path)) {
            ProjectLog.TakeSkipped(logger, take.Path, "not a usable take of this project");
            return null;
        }

        var file = Describe(o, take.Path, info);
        var start = state.TempoMap.TickAtSample(Math.Max(0, take.StartSample), state.SampleRate);
        return (file,
            new(ids.Next(IdPrefix.Region), track.Id, file.Id, track.Name, Math.Max(0, start), 0, info.Frames, 0, 0, 0));
    }

    static bool IsInside(string folder, string path) =>
        Path.GetFullPath(path)
            .StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    AudioFile Describe(Open o, string absolutePath, WavInfo info) {
        var relative = Path.GetRelativePath(o.Folder, absolutePath).Replace(Path.DirectorySeparatorChar, '/');
        return new(
            ids.Next(IdPrefix.File),
            Path.GetFileNameWithoutExtension(absolutePath),
            relative,
            info.SampleRate,
            info.Channels,
            info.Frames,
            info.BitDepth
        );
    }

    void StartPeaks(AudioFile file) {
        if (View is { } v) {
            _ = peaks.Start(
                    Path.Combine(v.Folder, ProjectPackage.PeaksFolder),
                    file.Id,
                    Path.Combine(v.Folder, file.Path)
                )
                .ContinueWith(static _ => { }, TaskScheduler.Default);
        }
    }

    Project Replace(string folder, ProjectState state) {
        Project project;
        lock (gate) {
            if (open is { Dirty: true } old) {
                // Unsaved edits of the project being replaced are kept beside it.
                try {
                    ProjectPackage.Save(Path.Combine(old.Folder, ProjectPackage.BackupsFolder, "Unsaved"), old.State);
                } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                    ProjectLog.BackupFailed(logger, old.Folder, e.Message);
                }
            }

            open = new(++sessions, folder, state, new(time));
            Publish();
            project = open.ToProject();
            var summary = open.Summary(time.GetUtcNow());
            recent.Touch(summary);
            hub.Publish(new ProjectOpenedEvent(summary, open.Revision));
        }

        foreach (var file in state.Files.Values) {
            StartPeaks(file);
        }

        Changed?.Invoke();
        return project;
    }

    CommandResult Commit(Open o, ProjectState state, IReadOnlyList<DeltaOp> ops) {
        o.State = state;
        o.Revision++;
        o.Dirty = true;
        Publish();
        var delta = new ProjectDelta(o.Revision, ops);
        hub.Publish(new ProjectChangedEvent(delta));
        return new(o.Revision, delta);
    }

    void Publish() {
        if (open is { } o) {
            o.Ids = o.Ids.With(o.State);
            Volatile.Write(ref view, new(o.Session, o.Folder, o.State, o.Ids, o.Revision));
        } else {
            Volatile.Write(ref view, null);
        }
    }

    Open Required() => open ?? throw ProjectException.NoProject();

    static void CheckRevision(Open o, long? expected) {
        if (expected is { } e && e != o.Revision) {
            throw ProjectException.Conflict($"The project is at revision {o.Revision}, not {e}.");
        }
    }

    sealed class Open(long session, string folder, ProjectState state, EditHistory history) {
        public long Session { get; } = session;
        public string Folder { get; } = folder;
        public ProjectState State { get; set; } = state;
        public long Revision { get; set; } = 1;
        public bool Dirty { get; set; }
        public EditHistory History { get; } = history;
        public EngineIds Ids { get; set; } = EngineIds.Empty;

        public Project ToProject() => State.ToProject(Folder, Revision, Dirty);

        public ProjectSummary Summary(DateTimeOffset now) => new(State.Id, State.Name, Folder, now);
    }
}
