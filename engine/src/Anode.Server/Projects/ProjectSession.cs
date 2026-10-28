using Anode.Contracts;
using Anode.Ipc;
using Anode.Model;
using Anode.Model.Audio;
using Anode.Model.Compile;
using Anode.Model.Editing;
using Anode.Model.History;
using Anode.Model.Midi;
using Anode.Model.Storage;
using Anode.Server.Engine;
using Anode.Server.Library;
using Anode.Server.Realtime;
using Microsoft.Extensions.Options;
using System.Collections.Immutable;

namespace Anode.Server.Projects;

/// <summary>
///     What the engine link needs of the open project: the snapshot, its folder, the engine ids, which opening of a
///     project it belongs to (<see cref="Session" /> changes with every open, create and close), and the plugin states
///     last saved, opened or brought by a patch, by state key (<see cref="ProjectPackage.StateKey" />: the track id for
///     an instrument, <c>&lt;track id&gt;.&lt;insert id&gt;</c> for an insert).
/// </summary>
public sealed record OpenProjectView(
    long Session,
    string Folder,
    ProjectState State,
    EngineIds Ids,
    long Revision,
    ImmutableDictionary<string, SavedState> States
) {
    public string AudioFolder => Path.Combine(Folder, ProjectPackage.AudioFolder);

    /// <summary>The state saved for <paramref name="track" />'s instrument, when it is still that instrument's.</summary>
    public byte[]? StateOf(Track track) =>
        track.Instrument is { } slot && States.TryGetValue(track.Id, out var saved) && saved.PluginId == slot.PluginId
            ? saved.Bytes
            : null;

    /// <summary>The state saved for an insert of a track (null: the master), when it is still that plugin's.</summary>
    public byte[]? StateOf(string? trackId, Insert insert) =>
        States.TryGetValue(ProjectPackage.StateKey(trackId, insert.Id), out var saved) && saved.PluginId == insert.PluginId
            ? saved.Bytes
            : null;

    /// <summary>Where an instrument's file is on disk: <c>instrument.file</c> is relative to the project inside it.</summary>
    public string? InstrumentFile(InstrumentSlot slot) =>
        slot.File is { } file ? Path.GetFullPath(Path.Combine(Folder, file)) : null;
}

/// <summary>A plugin's opaque state (<c>Plugin States/&lt;key&gt;.bin</c>) and the plugin it belongs to.</summary>
public sealed record SavedState(string PluginId, byte[] Bytes);

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
    EngineDevice device,
    EngineCatalog catalog,
    PatchLibrary patches,
    TransportClock clock,
    TimeProvider time,
    ILogger<ProjectSession> logger
) {
    /// <summary>The largest screenset layout the server keeps, in characters.</summary>
    public const int MaxLayoutLength = 1 << 20;

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

        var rate = request.SampleRate ?? device.SampleRate ?? ProjectDefaults.SampleRate;
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
        var plugins = PluginsByKey(state);
        var states = ProjectPackage.LoadStates(folder, state)
            .Where(s => plugins.ContainsKey(s.Key))
            .ToImmutableDictionary(static s => s.Key, s => new SavedState(plugins[s.Key], s.Value), StringComparer.Ordinal);
        return Replace(folder, state, states);
    }

    /// <summary>
    ///     Saves the open project. <paramref name="hostStates" /> are the instruments' states the engine reported (by
    ///     track id; null: none to keep), or null without an engine: the states saved before are kept for the
    ///     instruments they belong to.
    /// </summary>
    public ProjectSummary Save(IReadOnlyDictionary<string, byte[]?>? hostStates = null) {
        ProjectSummary summary;
        lock (gate) {
            summary = SaveLocked(Required(), hostStates);
        }

        return summary;
    }

    /// <summary>
    ///     Saves under the lock: first the previous save goes to <c>Backups/</c> (the newest ten are kept), then the
    ///     project and its instruments' states.
    /// </summary>
    ProjectSummary SaveLocked(Open o, IReadOnlyDictionary<string, byte[]?>? hostStates) {
        var states = States(o, hostStates);
        try {
            ProjectHistory.Backup(o.Folder, time.GetUtcNow());
            ProjectPackage.Save(o.Folder, o.State);
            ProjectPackage.SaveStates(o.Folder, states.ToDictionary(static s => s.Key, static s => s.Value.Bytes));
            ProjectPackage.CreateFolders(o.Folder);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            throw ProjectException.Unprocessable($"Could not save '{o.Folder}': {e.Message}");
        }

        o.States = states;
        Publish();
        o.Dirty = false;
        var summary = o.Summary(time.GetUtcNow());
        recent.Touch(summary);
        hub.Publish(new ProjectSavedEvent(summary, o.Revision));
        return summary;
    }

    /// <summary>The open project's screensets, by number.</summary>
    public IReadOnlyList<Screenset> Screensets() {
        lock (gate) {
            return [..Required().State.Screensets.Values];
        }
    }

    /// <summary>Saves a screenset in the project: it marks the project unsaved, but is not an undo step.</summary>
    public Screenset SaveScreenset(int number, Screenset screenset) {
        if (number is < 1 or > 9) {
            throw ProjectException.NotFound($"No screenset {number}: they are 1…9.");
        }

        var name = (screenset?.Name ?? string.Empty).Trim();
        if (name.Length is 0 or > Edits.MaxNameLength) {
            throw ProjectException.Unprocessable($"A screenset name has 1…{Edits.MaxNameLength} characters.");
        }

        if (screenset!.Layout is not { Length: <= MaxLayoutLength } layout) {
            throw ProjectException.Unprocessable($"A screenset layout has at most {MaxLayoutLength} characters.");
        }

        var saved = new Screenset(number, name, screenset.Locked, layout);
        lock (gate) {
            var o = Required();
            o.State = o.State with { Screensets = o.State.Screensets.SetItem(number, saved) };
            o.Dirty = true;
            Publish();
        }

        return saved;
    }

    /// <summary>The open project's alternatives, the current one marked.</summary>
    public IReadOnlyList<ProjectAlternative> Alternatives() {
        lock (gate) {
            var o = Required();
            var manifest = ProjectHistory.Alternatives(o.Folder, o.State.Name);
            return [
                ..manifest.Alternatives.Select(a => new ProjectAlternative(
                        a.Id,
                        a.Name,
                        a.Id == manifest.Current
                            ? ProjectHistory.Modified(o.Folder)
                            : ProjectHistory.Modified(ProjectHistory.AlternativeFolder(o.Folder, a.Id)),
                        a.Id == manifest.Current
                    )
                )
            ];
        }
    }

    /// <summary>
    ///     Saves the open project as a new alternative and makes it current: the alternative it was keeps its last
    ///     save.
    /// </summary>
    public ProjectAlternative CreateAlternative(string? name, IReadOnlyDictionary<string, byte[]?>? hostStates) {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length is 0 or > Edits.MaxNameLength) {
            throw ProjectException.Unprocessable($"An alternative's name has 1…{Edits.MaxNameLength} characters.");
        }

        lock (gate) {
            var o = Required();
            var id = ids.Next(IdPrefix.Alternative);
            var now = time.GetUtcNow();
            try {
                ProjectHistory.CreateAlternative(o.Folder, o.State.Name, id, trimmed, now);
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException or PackageException) {
                throw ProjectException.Unprocessable($"Could not save the alternative: {e.Message}");
            }

            SaveLocked(o, hostStates);
            return new(id, trimmed, now, true);
        }
    }

    /// <summary>Switches to another alternative; unsaved edits of the current one are kept in a backup first.</summary>
    public Project OpenAlternative(string id) {
        string folder;
        lock (gate) {
            var o = Required();
            var manifest = ProjectHistory.Alternatives(o.Folder, o.State.Name);
            if (id == manifest.Current) {
                return o.ToProject();
            }

            if (!manifest.Alternatives.Any(a => a.Id == id)
                || !ProjectPackage.IsSafeId(id)
                || !ProjectPackage.Exists(ProjectHistory.AlternativeFolder(o.Folder, id))) {
                throw ProjectException.NotFound($"No alternative '{id}'.");
            }

            try {
                KeepUnsaved(o);
                ProjectHistory.OpenAlternative(o.Folder, o.State.Name, id, time.GetUtcNow());
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException or PackageException) {
                throw ProjectException.Unprocessable($"Could not open the alternative: {e.Message}");
            }

            folder = o.Folder;
        }

        return OpenFolder(folder);
    }

    public void DeleteAlternative(string id) {
        lock (gate) {
            var o = Required();
            var manifest = ProjectHistory.Alternatives(o.Folder, o.State.Name);
            if (!manifest.Alternatives.Any(a => a.Id == id)) {
                throw ProjectException.NotFound($"No alternative '{id}'.");
            }

            try {
                ProjectHistory.DeleteAlternative(o.Folder, o.State.Name, id);
            } catch (PackageException e) {
                throw ProjectException.Unprocessable(e.Message);
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                throw ProjectException.Unprocessable($"Could not delete the alternative: {e.Message}");
            }
        }
    }

    /// <summary>The open project's backups, newest first.</summary>
    public IReadOnlyList<ProjectBackup> Backups() {
        lock (gate) {
            return ProjectHistory.Backups(Required().Folder);
        }
    }

    /// <summary>
    ///     Replaces the open project with a backup, like reopening it: the project as last saved and any unsaved edits
    ///     are kept as backups first.
    /// </summary>
    public Project RevertToBackup(string id) {
        string folder;
        lock (gate) {
            var o = Required();
            var backup = ProjectHistory.BackupFolder(o.Folder, id)
                ?? throw ProjectException.NotFound($"No backup '{id}'.");
            try {
                var state = ProjectPackage.Load(backup);
                var states = ProjectPackage.LoadStates(backup, state);
                KeepUnsaved(o);
                ProjectHistory.Backup(o.Folder, time.GetUtcNow());
                ProjectPackage.Save(o.Folder, state);
                ProjectPackage.SaveStates(o.Folder, states);
            } catch (PackageException e) {
                throw ProjectException.Unprocessable(e.Message);
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                throw ProjectException.Unprocessable($"Could not revert to the backup: {e.Message}");
            }

            folder = o.Folder;
        }

        return OpenFolder(folder);
    }

    /// <summary>Unsaved edits go to a backup (and the project then counts as saved, so they are not kept twice).</summary>
    void KeepUnsaved(Open o) {
        if (o.Dirty) {
            ProjectHistory.BackupState(o.Folder, o.State, time.GetUtcNow());
            o.Dirty = false;
        }
    }

    /// <summary>
    ///     Imports a Standard MIDI File from the server's disk as one undoable step: an instrument track per MIDI track
    ///     with notes, and the file's tempo and signature tracks.
    /// </summary>
    public CommandResult ImportMidi(string? path) {
        if (string.IsNullOrWhiteSpace(path)) {
            throw ProjectException.Unprocessable("Name the MIDI file to import.");
        }

        string full;
        try {
            full = Path.GetFullPath(path);
        } catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) {
            throw ProjectException.Unprocessable($"'{path}' is not a file path: {e.Message}");
        }

        Required();
        if (!File.Exists(full)) {
            throw ProjectException.NotFound($"No file '{full}'.");
        }

        SmfFile file;
        try {
            file = StandardMidiFile.Read(full);
        } catch (FormatException e) {
            throw ProjectException.Unprocessable(e.Message);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            throw ProjectException.Unprocessable($"Could not read '{full}': {e.Message}");
        }

        CommandResult result;
        lock (gate) {
            var o = Required();
            EditResult edit;
            try {
                edit = Edits.ImportMidi(o.State, file, Path.GetFileNameWithoutExtension(full), ids, catalog.ForEdits);
            } catch (EditException e) {
                throw ProjectException.Unprocessable(e.Message);
            }

            result = Commit(o, edit.State, edit.Forward);
            o.History.Push("Import MIDI File", o.Revision, new(edit.Forward, edit.Inverse), null);
        }

        Changed?.Invoke();
        return result;
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

    /// <summary>
    ///     Applies a batch as one undo step. During playback, control moves on tracks in touch, latch or write mode also
    ///     record into their lanes (<see cref="AutomationPass" />), and the pass's batches coalesce into one step.
    /// </summary>
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
                edit = Edits.Apply(o.State, Resolve(o, batch.Commands), ids, catalog.ForEdits, patches);
            } catch (EditException e) {
                throw ProjectException.Unprocessable(e.Message);
            }

            var forward = edit.Forward;
            var inverse = edit.Inverse;
            var state = edit.State;
            var key = UndoLabels.CoalesceKey(batch);
            if (AutomationPass.Records(batch.Commands) && PassOf(o) is var (pass, tick)) {
                var recorded = pass.Record(o.State, state, tick, ids);
                if (recorded.Count > 0) {
                    var more = new List<DeltaOp>(forward);
                    var back = new List<DeltaOp>();
                    state = Edits.Step(state, recorded, more, back);
                    back.Reverse();
                    (forward, inverse, key) = (more, [..back, ..inverse], pass.CoalesceKey);
                }
            }

            KeepStates(o, o.State, state, batch.Commands);
            result = Commit(o, state, forward);
            o.History.Push(UndoLabels.For(batch), o.Revision, new(forward, inverse), key);
        }

        Changed?.Invoke();
        return result;
    }

    /// <summary>The pass recording now (started when playback did), with the playhead's tick; null while stopped.</summary>
    (AutomationPass Pass, long Tick)? PassOf(Open o) {
        if (clock.Playing is not var (number, start, tick)) {
            return null;
        }

        if (o.Pass?.Number != number) {
            o.Pass = new(number, start);
        }

        return (o.Pass, tick);
    }

    /// <summary>
    ///     Playback stopped: the pass's latch and write lanes are completed to <paramref name="stopTick" />, in the
    ///     pass's undo step when it is still the last one.
    /// </summary>
    public void EndPass(long number, long stopTick) {
        lock (gate) {
            if (open is not { Pass: { } pass } o || pass.Number != number) {
                return;
            }

            o.Pass = null;
            var ops = pass.Finish(o.State, stopTick, ids);
            if (ops.Count == 0) {
                return;
            }

            var forward = new List<DeltaOp>();
            var inverse = new List<DeltaOp>();
            var state = Edits.Step(o.State, ops, forward, inverse);
            inverse.Reverse();
            Commit(o, state, forward);
            o.History.Push("Automation", o.Revision, new(forward, inverse), pass.CoalesceKey);
        }

        Changed?.Invoke();
    }

    /// <summary>
    ///     A plugin's own editor (or a controller) moved a parameter: applied as <c>setInsertParameter</c> (or
    ///     <c>setParameter</c> for the instrument), coalescing like a drag and recording automation like one; ignored
    ///     when it does not apply.
    /// </summary>
    public void ApplyHostParameter(int engineTrack, string? insertId, string parameterId, double value) {
        var view = View;
        if (view is null) {
            return;
        }

        string? trackId = null;
        if (engineTrack >= 0 && (trackId = view.Ids.TrackOf(engineTrack)) is null) {
            return;
        }

        EditCommand? command = insertId is not null
            ? new SetInsertParameterCommand(insertId, parameterId, value, trackId)
            : trackId is not null
                ? new SetParameterCommand(trackId, parameterId, value)
                : null;
        if (command is null) {
            return;
        }

        try {
            Apply(new([command]), null);
        } catch (ProjectException e) {
            ProjectLog.HostParameterIgnored(logger, parameterId, e.Message);
        }
    }

    /// <summary>
    ///     Keeps the plugin states in step with an edit: a patch brings its plugins' states (for the inserts it adds, in
    ///     order, and its instrument), and a plugin that changed or went loses its state.
    /// </summary>
    void KeepStates(Open o, ProjectState before, ProjectState after, IReadOnlyList<EditCommand> commands) {
        var states = o.States.ToBuilder();
        foreach (var apply in commands.OfType<ApplyPatchCommand>()) {
            if (patches.Read(apply.PatchId ?? string.Empty) is not { } patch || after.FindTrack(apply.TrackId) is not { } track) {
                continue;
            }

            var inserts = track.Strip.Inserts;
            for (var i = 0; i < Math.Min(inserts.Count, patch.Inserts.Count); i++) {
                if (patch.InsertStates?.GetValueOrDefault(patch.Inserts[i].Id) is { } text && Bytes(text) is { } bytes) {
                    states[ProjectPackage.StateKey(track.Id, inserts[i].Id)] = new(inserts[i].PluginId, bytes);
                }
            }

            if (track.Instrument is { } slot && patch.Instrument is not null && patch.InstrumentState is { } state && Bytes(state) is { } b) {
                states[track.Id] = new(slot.PluginId, b);
            }
        }

        var plugins = PluginsByKey(after);
        var previous = PluginsByKey(before);
        foreach (var key in states.Keys.ToArray()) {
            // A state stays with its plugin: gone, or another plugin in its place (an instrument replaced).
            if (!plugins.TryGetValue(key, out var plugin)
                || (previous.TryGetValue(key, out var was) && was != plugin && !commands.OfType<ApplyPatchCommand>().Any())) {
                states.Remove(key);
            }
        }

        o.States = states.ToImmutable();
        return;

        static byte[]? Bytes(string base64) {
            try {
                return Convert.FromBase64String(base64);
            } catch (FormatException) {
                return null;
            }
        }
    }

    /// <summary>Every plugin of the project by state key: instruments by track id, inserts by track and insert id.</summary>
    static Dictionary<string, string> PluginsByKey(ProjectState state) {
        var plugins = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var track in state.Tracks) {
            if (track.Instrument is { } slot) {
                plugins[track.Id] = slot.PluginId;
            }

            foreach (var insert in track.Strip.Inserts) {
                plugins[ProjectPackage.StateKey(track.Id, insert.Id)] = insert.PluginId;
            }
        }

        foreach (var insert in state.Master.Inserts) {
            plugins[ProjectPackage.StateKey(null, insert.Id)] = insert.PluginId;
        }

        return plugins;
    }

    /// <summary>A track and the states of its plugins (by state key), for a patch; the host's when it reports them.</summary>
    public (Track Track, IReadOnlyDictionary<string, byte[]> States) PatchSource(
        string trackId,
        IReadOnlyDictionary<string, byte[]?>? hostStates
    ) {
        lock (gate) {
            var o = Required();
            var track = o.State.FindTrack(trackId) ?? throw ProjectException.NotFound($"No track '{trackId}'.");
            var states = States(o, hostStates).ToDictionary(static s => s.Key, static s => s.Value.Bytes, StringComparer.Ordinal);
            return (track, states);
        }
    }

    /// <summary>
    ///     A freeze job finished: the file it rendered (already in the project's <c>Audio Files</c>) joins the pool
    ///     and the track plays it, as one undoable step "Freeze Track" (the file stays in the pool on undo).
    /// </summary>
    public CommandResult Freeze(long session, string trackId, string absolutePath, WavInfo info) {
        CommandResult result;
        AudioFile file;
        lock (gate) {
            var o = Required();
            if (o.Session != session) {
                throw ProjectException.Conflict("The project was closed while the track was frozen.");
            }

            file = Describe(o, absolutePath, info);
            var forward = new List<DeltaOp>();
            var inverse = new List<DeltaOp>();
            var state = Edits.Step(o.State, [new AudioFileUpserted(file)], forward, inverse);
            EditResult frozen;
            try {
                frozen = Edits.Freeze(state, trackId, file.Id);
            } catch (EditException e) {
                throw ProjectException.Unprocessable(e.Message);
            }

            forward.AddRange(frozen.Forward);
            inverse.Reverse();
            result = Commit(o, frozen.State, forward);
            o.History.Push("Freeze Track", o.Revision, new(forward, [..frozen.Inverse, ..inverse]), null);
        }

        StartPeaks(file);
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

    /// <summary>
    ///     Adds bounced files (already in the project's <c>Audio Files</c>) to the pool as one undo step, "Add Bounce"
    ///     (a file stays in the pool on undo, as an import's does: the step is there to name what happened).
    /// </summary>
    public CommandResult AddBounce(long session, IReadOnlyList<(string Path, WavInfo Info)> files) {
        CommandResult result;
        var added = new List<AudioFile>();
        lock (gate) {
            var o = Required();
            if (o.Session != session) {
                throw ProjectException.Conflict("The project was closed while it bounced.");
            }

            var forward = new List<DeltaOp>();
            var inverse = new List<DeltaOp>();
            var state = o.State;
            foreach (var (path, info) in files) {
                var file = Describe(o, path, info);
                added.Add(file);
                state = Edits.Step(state, [new AudioFileUpserted(file)], forward, inverse);
            }

            inverse.Reverse();
            result = Commit(o, state, forward);
            o.History.Push("Add Bounce", o.Revision, new(forward, inverse), null);
        }

        foreach (var file in added) {
            StartPeaks(file);
        }

        Changed?.Invoke();
        return result;
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
    ///     Applies a finished recording (or with <paramref name="flashback" /> a Flashback Capture) as one undoable
    ///     batch, "Record" (or "Flashback Capture"): each take's file joins the pool (that part stays on undo) and its
    ///     passes become a take folder or regions on its track where they were played, after replace mode cleared what
    ///     they cover, and only inside the punch range with autopunch (<see cref="Recordings" />, docs/04-phase-3.md);
    ///     each MIDI take becomes a MIDI region spanning the pass, rounded out to bars, or with
    ///     <c>mergeMidiRecording</c> joins the MIDI region under the start of the pass (docs/03-phase-2.md,
    ///     "Recording").
    /// </summary>
    public CommandResult? ApplyRecording(
        IReadOnlyList<RecordedTake> takes,
        IReadOnlyList<RecordedMidiTake>? midiTakes = null,
        bool flashback = false
    ) {
        midiTakes ??= [];
        var probed = Probe(takes);

        CommandResult? result = null;
        var files = new List<AudioFile>();
        lock (gate) {
            if (open is not { } o || probed.Count + midiTakes.Count == 0) {
                return null;
            }

            var forward = new List<DeltaOp>();
            var inverse = new List<DeltaOp>();
            var state = o.State;
            foreach (var (take, info) in probed) {
                if (Take(o, state, take, info, flashback) is not { } taken) {
                    continue;
                }

                var (file, ops) = taken;
                files.Add(file);
                state = Edits.Step(state, ops, forward, inverse);
            }

            foreach (var take in midiTakes) {
                if (MidiTake(o, state, take, flashback) is { } ops) {
                    state = Edits.Step(state, ops, forward, inverse);
                }
            }

            if (forward.Count > 0) {
                inverse.Reverse();
                result = Commit(o, state, forward);
                o.History.Push(flashback ? "Flashback Capture" : "Record", o.Revision, new(forward, inverse), null);
            }
        }

        foreach (var file in files) {
            StartPeaks(file);
        }

        Changed?.Invoke();
        return result;
    }

    /// <summary>The takes whose files can be read, with their formats (the others logged).</summary>
    List<(RecordedTake Take, WavInfo Info)> Probe(IReadOnlyList<RecordedTake> takes) {
        var probed = new List<(RecordedTake Take, WavInfo Info)>();
        foreach (var take in takes) {
            try {
                probed.Add((take, WavReader.Probe(take.Path)));
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException or WavFormatException) {
                ProjectLog.TakeSkipped(logger, take.Path, e.Message);
            }
        }

        return probed;
    }

    /// <summary>The file a recorded take becomes and what places it, or null (logged) when it cannot be used.</summary>
    (AudioFile File, List<DeltaOp> Ops)? Take(
        Open o,
        ProjectState state,
        RecordedTake take,
        WavInfo info,
        bool flashback
    ) {
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

        if (track.Kind != TrackKind.Audio) {
            ProjectLog.TakeSkipped(logger, take.Path, "its track is not an audio track");
            return null;
        }

        var file = Describe(o, take.Path, info);
        IReadOnlyList<TakePass> passes = take.Passes is { Count: > 0 } reported
            ? [
                ..reported.Where(static p => p is not null)
                    .Select(static p => new TakePass(p.StartSample, p.FileOffset, p.Frames))
            ]
            : [new(Math.Max(0, take.StartSample), 0, info.Frames)];
        if (!flashback) {
            passes = Recordings.Punched(state, passes);
        }

        return (file, Recordings.Audio(state, track, file, passes, flashback, ids));
    }

    /// <summary>
    ///     What places a MIDI take: replace mode's clearing (not when merging), then the region it becomes (a new one,
    ///     or the one it merges into); null when there is nothing to keep. Positions come in project samples, already
    ///     shifted back by the device's input latency; with autopunch only the punch range is kept.
    /// </summary>
    List<DeltaOp>? MidiTake(Open o, ProjectState state, RecordedMidiTake take, bool flashback) {
        var trackId = o.Ids.TrackOf(take.TrackId);
        if (trackId is null || state.FindTrack(trackId) is not { } track || !Edits.IsMidiKind(track.Kind)) {
            ProjectLog.MidiTakeSkipped(logger, take.TrackId, "its track is gone or takes no MIDI");
            return null;
        }

        var map = state.TempoMap;
        var rate = state.SampleRate;
        var passStart = map.TickAtSample(Math.Max(0, take.StartSample), rate);
        var passEnd = Math.Max(passStart + 1, map.TickAtSample(Math.Max(0, take.EndSample), rate));
        var (notes, controls) = MidiRecording.Events(take, map, rate, passEnd);
        var recording = state.Settings.Recording;
        if (!flashback && recording.Autopunch) {
            var (from, to) = (recording.PunchInTick, recording.PunchOutTick);
            notes = [..notes.Where(n => n.StartTick >= from && n.StartTick < to)];
            controls = [..controls.Where(e => e.Tick >= from && e.Tick < to)];
            (passStart, passEnd) = (Math.Max(passStart, from),
                Math.Max(Math.Max(passStart, from) + 1, Math.Min(passEnd, to)));
        }

        if (notes.Count + controls.Count == 0) {
            return null;
        }

        var ops = new List<DeltaOp>();
        var merge = state.Settings.MergeMidiRecording;
        if (!flashback && !merge && recording.Replace) {
            ops.AddRange(Recordings.ReplaceMidi(state, track.Id, passStart, passEnd, ids));
            state = state.Apply(ops);
        }

        var bars = state.SignatureMap;
        var end = Math.Max(passEnd, notes.Count == 0 ? 0 : notes.Max(static n => n.StartTick + n.LengthTicks));
        var roundedEnd = bars.BarLineAtOrAfter(end);
        var target = merge
            ? state.MidiRegionsOf(track.Id)
                .LastOrDefault(r => r.StartTick <= passStart && passStart < r.StartTick + r.LengthTicks)
            : null;
        var next = state.LastEventId;
        if (target is null) {
            var start = bars.BarLineAtOrBefore(passStart);
            target = new(
                ids.Next(IdPrefix.MidiRegion),
                track.Id,
                track.Name,
                start,
                Math.Max(Model.Time.SignatureMap.BarTicks(bars.At(start)), roundedEnd - start),
                ProjectDefaults.Playback,
                [],
                [],
                false
            );
        } else if (roundedEnd > target.StartTick + target.LengthTicks) {
            target = target with { LengthTicks = roundedEnd - target.StartTick };
        }

        var origin = target.StartTick;
        ops.Add(
            new MidiRegionUpserted(
                target with {
                    Notes = Edits.Sorted(
                        target.Notes.Concat(notes.Select(n => n with { Id = ++next, StartTick = n.StartTick - origin }))
                    ),
                    Controls = Edits.Sorted(
                        target.Controls.Concat(controls.Select(e => e with { Id = ++next, Tick = e.Tick - origin }))
                    )
                }
            )
        );
        return ops;
    }

    /// <summary>
    ///     The batch with each <c>loadInstrumentFile</c> path resolved on the server's disk (a relative path is the
    ///     project's): the file must exist, and inside the project it is kept relative to it.
    /// </summary>
    static IReadOnlyList<EditCommand> Resolve(Open o, IReadOnlyList<EditCommand> commands) {
        if (!commands.Any(static c => c is LoadInstrumentFileCommand)) {
            return commands;
        }

        return [
            ..commands.Select(EditCommand(c) => c is LoadInstrumentFileCommand { Path: { Length: > 0 } path } load
                    ? load with { Path = InstrumentPath(o.Folder, path) }
                    : c
            )
        ];
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

    /// <summary>
    ///     The states to save, by state key: the engine's answers for the plugins it reported (instruments and inserts),
    ///     the earlier ones for the rest when they still belong to the same plugin.
    /// </summary>
    static ImmutableDictionary<string, SavedState> States(Open o, IReadOnlyDictionary<string, byte[]?>? hostStates) {
        var states = ImmutableDictionary.CreateBuilder<string, SavedState>(StringComparer.Ordinal);
        foreach (var (key, plugin) in PluginsByKey(o.State)) {
            if (hostStates is not null && hostStates.TryGetValue(key, out var reported)) {
                if (reported is { Length: > 0 }) {
                    states[key] = new(plugin, reported);
                }
            } else if (o.States.TryGetValue(key, out var saved) && saved.PluginId == plugin) {
                states[key] = saved;
            }
        }

        return states.ToImmutable();
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

    Project Replace(string folder, ProjectState state, ImmutableDictionary<string, SavedState>? states = null) {
        Project project;
        lock (gate) {
            if (open is { Dirty: true } old) {
                // Unsaved edits of the project being replaced are kept beside it, as a backup.
                try {
                    ProjectHistory.BackupState(old.Folder, old.State, time.GetUtcNow());
                } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                    ProjectLog.BackupFailed(logger, old.Folder, e.Message);
                }
            }

            open = new(++sessions, folder, state, new(time)) {
                States = states ?? ImmutableDictionary.Create<string, SavedState>(StringComparer.Ordinal)
            };
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
            Volatile.Write(ref view, new(o.Session, o.Folder, o.State, o.Ids, o.Revision, o.States));
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

        /// <summary>The automation recording pass of the current playback, once a move recorded.</summary>
        public AutomationPass? Pass { get; set; }

        public ImmutableDictionary<string, SavedState> States { get; set; } =
            ImmutableDictionary.Create<string, SavedState>(StringComparer.Ordinal);

        public Project ToProject() => State.ToProject(Folder, Revision, Dirty);

        public ProjectSummary Summary(DateTimeOffset now) => new(State.Id, State.Name, Folder, now);
    }
}
