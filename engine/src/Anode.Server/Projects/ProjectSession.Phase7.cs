using Anode.Contracts;
using Anode.Model;
using Anode.Model.Audio;
using Anode.Model.Editing;
using Anode.Model.Storage;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Anode.Server.Projects;

/// <summary>
///     Phase 7 (docs/08-phase-7.md): step input (the state, held here outside the project, and the steps it resolves),
///     Scripter's run-time parameters and scripts, and the Audio File Editor's file versions.
/// </summary>
public sealed partial class ProjectSession {
    /// <summary>A 1/16, in ticks: a new step input's note value.</summary>
    public const long DefaultStepTicks = 240;

    /// <summary>Step input as a project opens: off, a 1/16 at velocity 100, at the start.</summary>
    public static readonly StepInputState DefaultStepInput =
        new(false, null, null, 0, DefaultStepTicks, false, false, Edits.DefaultStepVelocity, false, false);

    StepInputState stepInput = DefaultStepInput;

    /// <summary>The last step's notes (what a tie lengthens), in <see cref="lastStepRegion" />.</summary>
    IReadOnlyList<long> lastStep = [];

    string? lastStepRegion;

    /// <summary>Whether chord mode stacked notes at the position (turning it off then advances).</summary>
    bool stacked;

    /// <summary>MIDI step input turned on or off: the host forwards the keyboard's notes while it is on.</summary>
    public event Action<bool>? StepInputForwardChanged;

    /// <summary>A Scripter slot's script changed (an edit, undo or redo): track id, slot id, source.</summary>
    public event Action<string, string, string>? ScriptChanged;

    /// <summary>Whether the host should forward MIDI as steps now.</summary>
    public bool ForwardsStepInput {
        get {
            lock (gate) {
                return open is not null && stepInput is { On: true, MidiInput: true };
            }
        }
    }

    public StepInputState StepInput() {
        lock (gate) {
            Required();
            return stepInput;
        }
    }

    /// <summary>Changes the step input state (left-out properties stay); 422 for values out of range.</summary>
    public StepInputState SetStepInput(StepInputRequest request) {
        StepInputState state;
        bool forwardBefore, forwardAfter;
        lock (gate) {
            var o = Required();
            var s = stepInput;
            forwardBefore = s is { On: true, MidiInput: true };
            var next = Requested(o.State, s, request);
            // Turning chord mode off moves on past the stacked chord.
            if (s.Chord && !next.Chord && stacked) {
                next = next with { PositionTick = next.PositionTick + StepTicks(s) };
            }

            if (request.PositionTick is not null || !next.Chord) {
                stacked = false;
            }

            if (request.PositionTick is not null || request.RegionId is not null || request.TrackId is not null) {
                (lastStep, lastStepRegion) = ([], null);
            }

            stepInput = state = next;
            forwardAfter = next is { On: true, MidiInput: true };
        }

        hub.Publish(new StepInputEvent(state));
        if (forwardAfter != forwardBefore) {
            StepInputForwardChanged?.Invoke(forwardAfter);
        }

        return state;
    }

    /// <summary>The step input state a request asks for, checked: 404 for an unknown region, 422 for values out of range.</summary>
    static StepInputState Requested(ProjectState project, StepInputState s, StepInputRequest request) {
        var regionId = request.RegionId is null ? s.RegionId : NoneIfEmpty(request.RegionId);
        var trackId = request.TrackId is null ? s.TrackId : NoneIfEmpty(request.TrackId);
        if (regionId is not null && !project.MidiRegions.ContainsKey(regionId)) {
            throw ProjectException.NotFound($"No MIDI region '{regionId}'.");
        }

        if (trackId is not null && (project.FindTrack(trackId) is not { } track || !Edits.HoldsMidi(track))) {
            throw ProjectException.Unprocessable($"Track '{trackId}' is not a track that takes MIDI steps.");
        }

        var next = new StepInputState(
            request.On ?? s.On,
            regionId,
            trackId ?? (regionId is null ? null : project.MidiRegions[regionId].TrackId),
            request.PositionTick ?? s.PositionTick,
            request.NoteValueTicks ?? s.NoteValueTicks,
            request.Dotted ?? s.Dotted,
            request.Triplet ?? s.Triplet,
            request.Velocity ?? s.Velocity,
            request.Chord ?? s.Chord,
            request.MidiInput ?? s.MidiInput
        );
        if (next.PositionTick is < 0 or > Edits.MaxTick) {
            throw ProjectException.Unprocessable($"Tick {next.PositionTick} is outside the project.");
        }

        if (next.NoteValueTicks is < 1 or > Edits.MaxQuantizeTicks) {
            throw ProjectException.Unprocessable($"A note value is 1…{Edits.MaxQuantizeTicks} ticks.");
        }

        return next.Velocity is < 1 or > 127
            ? throw ProjectException.Unprocessable($"A step's velocity is 1…127, not {next.Velocity}.")
            : next;
    }

    static string? NoneIfEmpty(string text) => text.Length == 0 ? null : text;

    /// <summary>The input switches of a track's articulation set, or none.</summary>
    static IReadOnlyList<ArticulationSwitch>? SwitchesOf(ProjectState state, Track track) =>
        track.ArticulationSetId is { } setId && state.ArticulationSets.TryGetValue(setId, out var set)
            ? set.Switches
            : null;

    /// <summary>A rest: the step position moves on by one step (not an edit).</summary>
    public StepInputState StepRest() {
        StepInputState state;
        lock (gate) {
            Required();
            stepInput = state = stepInput with { PositionTick = stepInput.PositionTick + StepTicks(stepInput) };
            stacked = false;
            (lastStep, lastStepRegion) = ([], null);
        }

        hub.Publish(new StepInputEvent(state));
        return state;
    }

    /// <summary>
    ///     A step from the MIDI keyboard (the host forwards them while MIDI step input is on): the same
    ///     <c>stepNotes</c> as the keyboard window's; ignored (null) while step input or MIDI input is off, or when it
    ///     does not apply (logged).
    /// </summary>
    public CommandResult? StepFromMidi(IReadOnlyList<int> pitches, int velocity) {
        lock (gate) {
            if (open is null || stepInput is not { On: true, MidiInput: true } || pitches is not { Count: > 0 }) {
                return null;
            }
        }

        try {
            return Apply(
                new([new StepNotesCommand([..pitches.Distinct()], Math.Clamp(velocity, 1, 127))], "Step Input"),
                null
            );
        } catch (ProjectException e) {
            ProjectLog.StepIgnored(logger, e.Message);
            return null;
        }
    }

    /// <summary>A step's length: the note value, × 1.5 dotted, × 2/3 triplet.</summary>
    static long StepTicks(StepInputState s) {
        double ticks = s.NoteValueTicks;
        if (s.Dotted) {
            ticks *= 1.5;
        }

        if (s.Triplet) {
            ticks *= 2.0 / 3;
        }

        return Math.Max(1, (long)Math.Round(ticks));
    }

    /// <summary>Forgets step input (a project opened or closed); the host stops forwarding MIDI if it did.</summary>
    void ResetStepInput() {
        var forwarding = stepInput is { On: true, MidiInput: true };
        stepInput = DefaultStepInput;
        (lastStep, lastStepRegion, stacked) = ([], null, false);
        if (forwarding) {
            StepInputForwardChanged?.Invoke(false);
        }
    }

    /// <summary>
    ///     Applies a batch with steps in it: each command in turn, each step resolved against the step input state as
    ///     the steps before it left it (a new region the first one made, the position it advanced to). Returns the
    ///     edit and the state to keep.
    /// </summary>
    (EditResult Edit, StepState Steps) ApplySteps(
        Open o,
        IReadOnlyList<EditCommand> commands,
        IAnalysisSource analysis
    ) {
        var steps = new StepState(stepInput, lastStep, lastStepRegion, stacked);
        var forward = new List<DeltaOp>();
        var inverse = new List<DeltaOp>();
        var state = o.State;
        for (var i = 0; i < commands.Count; i++) {
            var command = commands[i] ?? throw new EditException("A command is null.");
            try {
                var resolved = command switch {
                    StepNotesCommand c => Resolve(state, steps, c),
                    StepTieCommand c => Resolve(steps, c),
                    _ => command
                };
                var before = state.LastEventId;
                var ops = Edits.Ops(state, resolved, ids, catalog.ForEdits, patches, analysis);
                state = Edits.Step(state, ops, forward, inverse);
                steps = resolved switch {
                    StepNotesCommand c => Stepped(steps, c, ops, before),
                    StepTieCommand c => steps with {
                        State = steps.State with { PositionTick = steps.State.PositionTick + c.LengthTicks!.Value },
                        Stacked = false
                    },
                    _ => steps
                };
            } catch (EditException e) when (commands.Count > 1) {
                throw new EditException($"Command {i + 1} ({command.GetType().Name}): {e.Message}", e);
            }
        }

        inverse.Reverse();
        return (new(state, forward, inverse), steps);
    }

    /// <summary>A step with every left-out property filled from the step input state.</summary>
    static StepNotesCommand Resolve(ProjectState state, StepState steps, StepNotesCommand c) {
        var s = steps.State;
        var position = c.PositionTick ?? s.PositionTick;
        // The state's region while it is there and starts at or before the step; else a new one on the track.
        var regionId = c.RegionId
            ?? (c.TrackId is null
                && s.RegionId is { } r
                && state.MidiRegions.TryGetValue(r, out var region)
                && region.StartTick <= position
                ? r
                : null);
        var trackId = c.TrackId ?? s.TrackId;
        if (regionId is null && trackId is null) {
            throw new EditException("Step input has no region or track to enter notes into.");
        }

        return c with {
            Velocity = c.Velocity ?? s.Velocity,
            RegionId = regionId,
            TrackId = regionId is null ? trackId : null,
            PositionTick = position,
            LengthTicks = c.LengthTicks ?? StepTicks(s),
            Channel = c.Channel ?? 1
        };
    }

    /// <summary>A tie of the last step's notes, by one step, unless the command says otherwise.</summary>
    static StepTieCommand Resolve(StepState steps, StepTieCommand c) {
        var regionId = c.RegionId ?? steps.LastRegion;
        var notes = c.NoteIds ?? (regionId == steps.LastRegion ? steps.LastNotes : []);
        return regionId is null || notes.Count == 0
            ? throw new EditException("There is no step to tie.")
            : c with { RegionId = regionId, NoteIds = notes, LengthTicks = c.LengthTicks ?? StepTicks(steps.State) };
    }

    /// <summary>The step input state after a step: its region, its notes, and the position moved on (not in chord mode).</summary>
    static StepState Stepped(StepState steps, StepNotesCommand c, IReadOnlyList<DeltaOp> ops, long before) {
        var region = ops.OfType<MidiRegionUpserted>().Last().Region;
        long[] added = [..region.Notes.Where(n => n.Id > before).Select(static n => n.Id)];
        var s = steps.State with { RegionId = region.Id, TrackId = region.TrackId };
        if (s.Chord) {
            var notes = steps.Stacked && steps.LastRegion == region.Id ? [..steps.LastNotes, ..added] : added;
            return new(s with { PositionTick = c.PositionTick!.Value }, notes, region.Id, true);
        }

        return new(s with { PositionTick = c.PositionTick!.Value + c.LengthTicks!.Value }, added, region.Id, false);
    }

    /// <summary>Keeps the step input state a batch with steps left, and tells clients.</summary>
    void KeepSteps(StepState steps) {
        (stepInput, lastStep, lastStepRegion, stacked) = (steps.State, steps.LastNotes, steps.LastRegion,
            steps.Stacked);
        hub.Publish(new StepInputEvent(steps.State));
    }

    /// <summary>
    ///     A MIDI FX slot's run-time parameters, as the host reported them after compiling its script: stored in the
    ///     slot (a change of its own, not an undo step) and announced as a <c>parameters</c> event.
    /// </summary>
    public void ApplySlotParameters(int engineTrack, string slotId, IReadOnlyList<ParameterInfo> parameters) {
        string? trackId;
        lock (gate) {
            if (open is not { } o || (trackId = o.Ids.TrackOf(engineTrack)) is null) {
                return;
            }

            if (Edits.WithRuntimeParameters(o.State, trackId, slotId, parameters) is not { } op) {
                hub.Publish(new SlotParametersEvent(trackId, slotId, [..parameters]));
                return;
            }

            hub.Publish(new SlotParametersEvent(trackId, slotId, [..parameters]));
            Commit(o, o.State.Apply(op), [op]);
        }

        Changed?.Invoke();
    }

    /// <summary>Tells the host about every Scripter script a change set or changed (undo and redo too).</summary>
    void NoteScripts(ProjectState before, ProjectState after) {
        if (ScriptChanged is not { } changed || ReferenceEquals(before.Tracks, after.Tracks)) {
            return;
        }

        foreach (var track in after.Tracks) {
            foreach (var slot in track.Strip.MidiEffects) {
                if (slot is not { PluginId: Edits.ScripterId, Source: { } source }) {
                    continue;
                }

                var old = before.FindTrack(track.Id)?.Strip.MidiEffects.FirstOrDefault(i => i.Id == slot.Id);
                if (old?.Source != source) {
                    changed(track.Id, slot.Id, source);
                }
            }
        }
    }

    /// <summary>
    ///     A file whose audio changed (another version): its cached analysis goes, and its new version's peaks are
    ///     built (each version keeps its own, so undo finds them).
    /// </summary>
    void NoteVersions(Open o, ProjectState before, ProjectState after) {
        if (ReferenceEquals(before.Files, after.Files)) {
            return;
        }

        foreach (var file in after.Files.Values) {
            if (before.Files.TryGetValue(file.Id, out var old) && old.Path != file.Path) {
                o.Analyses = o.Analyses.Remove(file.Id);
                FileAnalyses.Forget(o.Folder, file.Id);
                StartPeaks(o.Folder, file);
            }
        }
    }

    /// <summary>The key a file's peaks are kept under: its id, and its version after the first edit.</summary>
    public static string PeakKey(AudioFile file) => file.Version == 0 ? file.Id : $"{file.Id}-v{file.Version}";

    /// <summary>The next version's number of a file: past every version written beside it (an undone one too).</summary>
    public static int NextVersion(string folder, AudioFile file) {
        var (directory, stem) = VersionStem(folder, file);
        var highest = file.Version;
        if (Directory.Exists(directory)) {
            foreach (var path in Directory.EnumerateFiles(directory, stem + ".v*.wav")) {
                if (VersionOf(stem, Path.GetFileName(path)) is { } n) {
                    highest = Math.Max(highest, n);
                }
            }
        }

        return highest + 1;
    }

    /// <summary>Where a file's versions go, and their name before <c>.vN.wav</c> (the original's name).</summary>
    public static (string Directory, string Stem) VersionStem(string folder, AudioFile file) {
        var full = Path.GetFullPath(Path.Combine(folder, file.Path));
        var name = Path.GetFileNameWithoutExtension(full);
        return (Path.GetDirectoryName(full)!, VersionSuffix().Replace(name, string.Empty));
    }

    static int? VersionOf(string stem, string fileName) =>
        fileName.StartsWith(stem + ".v", StringComparison.Ordinal)
        && fileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
        && int.TryParse(fileName.AsSpan(stem.Length + 2, fileName.Length - stem.Length - 6), out var n)
        && n > 0
            ? n
            : null;

    [GeneratedRegex(@"\.v[0-9]+$")]
    private static partial Regex VersionSuffix();

    /// <summary>
    ///     A finished Audio File Editor operation: the file points at the version the job wrote (with the region shifts
    ///     a length change needs), one undo step named <paramref name="label" />. 409 when the file changed meanwhile.
    /// </summary>
    public CommandResult ApplyFileVersion(
        long session,
        string label,
        AudioFile read,
        AudioEditOperation operation,
        int version,
        string absolutePath,
        WavInfo info
    ) {
        CommandResult result;
        lock (gate) {
            var o = Required();
            if (o.Session != session) {
                throw ProjectException.Conflict("The project was closed while the file was edited.");
            }

            if (!o.State.Files.TryGetValue(read.Id, out var file) || file.Path != read.Path) {
                throw ProjectException.Conflict($"'{read.Name}' changed while it was edited.");
            }

            var relative = Path.GetRelativePath(o.Folder, absolutePath).Replace(Path.DirectorySeparatorChar, '/');
            var job = new JobEdit(o.State, ids, catalog.ForEdits);
            try {
                job.Step(
                    Edits.FileLengthChanged(
                        o.State,
                        file.Id,
                        operation.Kind,
                        operation.StartFrame,
                        operation.EndFrame,
                        info.SampleRate
                    )
                );
                job.Apply(new SetFileVersionCommand(file.Id, version, relative, info.Frames, info.SampleRate));
            } catch (EditException e) {
                throw ProjectException.Unprocessable(e.Message);
            }

            job.Inverse.Reverse();
            result = Commit(o, job.State, job.Forward);
            o.History.Push(label, o.Revision, new(job.Forward, job.Inverse), null);
        }

        Changed?.Invoke();
        return result;
    }

    /// <summary>
    ///     Removes the versions of the project's files (<c>&lt;name&gt;.vN.wav</c>, and their peaks) that nothing can
    ///     reach any more: not the project, not its undo or redo history, not a saved alternative or backup. On save.
    /// </summary>
    static void PruneVersions(Open o) {
        var reachable = Reachable(o);
        foreach (var file in o.State.Files.Values) {
            var (directory, stem) = VersionStem(o.Folder, file);
            if (!Directory.Exists(directory)) {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(directory, stem + ".v*.wav")) {
                if (VersionOf(stem, Path.GetFileName(path)) is { } n && !reachable.Contains(Path.GetFullPath(path))) {
                    Prune(o.Folder, file, path, n);
                }
            }
        }
    }

    /// <summary>Every audio file path the project, its undo and redo history, and its saved documents reach.</summary>
    static HashSet<string> Reachable(Open o) {
        var paths = o.State.Files.Values.Select(static f => f.Path)
            .Concat(
                o.History.Steps()
                    .SelectMany(static s => s.Forward.Concat(s.Inverse))
                    .OfType<AudioFileUpserted>()
                    .Select(static op => op.File.Path)
            )
            .Concat(SavedDocuments(o.Folder).SelectMany(DocumentPaths));
        return paths.Select(p => Full(o.Folder, p)).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Removes version <paramref name="n" /> of a file and its peaks (kept until a later save when it cannot).</summary>
    static void Prune(string folder, AudioFile file, string path, int n) {
        try {
            File.Delete(path);
            foreach (var level in PeakFormat.Levels) {
                File.Delete(Peaks.PathOf(Path.Combine(folder, ProjectPackage.PeaksFolder), $"{file.Id}-v{n}", level));
            }
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            _ = e;
        }
    }

    static string Full(string folder, string path) => Path.GetFullPath(Path.Combine(folder, path));

    /// <summary>The <c>project.json</c> of each saved alternative and backup of a project.</summary>
    static IEnumerable<string> SavedDocuments(string folder) {
        foreach (var sub in new[] { ProjectHistory.AlternativesFolder, ProjectPackage.BackupsFolder }) {
            var root = Path.Combine(folder, sub);
            if (!Directory.Exists(root)) {
                continue;
            }

            foreach (var directory in Directory.EnumerateDirectories(root)) {
                var document = Path.Combine(directory, ProjectPackage.ProjectFileName);
                if (File.Exists(document)) {
                    yield return document;
                }
            }
        }
    }

    /// <summary>The audio file paths a saved <c>project.json</c> names (none when it cannot be read).</summary>
    static IEnumerable<string> DocumentPaths(string document) {
        JsonNode? root;
        try {
            root = JsonNode.Parse(File.ReadAllText(document));
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) {
            return [];
        }

        return [
            ..(root?["audioFiles"] as JsonArray ?? [])
                .Select(static f => f?["path"] is JsonValue v && v.TryGetValue<string>(out var p) ? p : null)
                .OfType<string>()
        ];
    }

    /// <summary>Samples of a file of the open project for the Audio File Editor: raw, or min and max per bucket.</summary>
    public SampleRange Samples(string fileId, long start, long frames, int bucket) {
        var (_, folder, file) = FileOf(fileId);
        if (start < 0 || frames < 1 || bucket < 1) {
            throw ProjectException.Unprocessable(
                "Ask for at least one frame from frame 0 on, in buckets of 1 or more."
            );
        }

        if ((frames + bucket - 1) / bucket > MaxSampleValues) {
            throw ProjectException.Unprocessable(
                $"At most {MaxSampleValues} values per channel: ask for fewer frames or larger buckets."
            );
        }

        float[][] channels;
        try {
            channels = BounceProcessor.Read(Path.Combine(folder, file.Path)).Channels;
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or WavFormatException) {
            throw ProjectException.Unprocessable($"Could not read '{file.Name}': {e.Message}");
        }

        var length = channels.Length == 0 ? 0 : channels[0].LongLength;
        var from = Math.Min(start, length);
        var count = Math.Min(frames, length - from);
        if (bucket == 1) {
            return new(
                from,
                count,
                channels.Length,
                1,
                [..channels.Select(c => (IReadOnlyList<float>)c.AsSpan((int)from, (int)count).ToArray())],
                null,
                null
            );
        }

        var extremes = channels.Select(c => Extremes(c, from, count, bucket)).ToArray();
        return new(
            from,
            count,
            channels.Length,
            bucket,
            null,
            [..extremes.Select(static x => (IReadOnlyList<float>)x.Min)],
            [..extremes.Select(static x => (IReadOnlyList<float>)x.Max)]
        );
    }

    /// <summary>A channel's minimum and maximum per bucket of frames (the last bucket may be short).</summary>
    static (float[] Min, float[] Max) Extremes(float[] channel, long from, long count, int bucket) {
        var buckets = (int)((count + bucket - 1) / bucket);
        var (lows, highs) = (new float[buckets], new float[buckets]);
        for (var b = 0; b < buckets; b++) {
            var span = channel.AsSpan((int)(from + (long)b * bucket), (int)Math.Min(bucket, count - (long)b * bucket));
            (lows[b], highs[b]) = (float.MaxValue, float.MinValue);
            foreach (var x in span) {
                lows[b] = Math.Min(lows[b], x);
                highs[b] = Math.Max(highs[b], x);
            }
        }

        return (lows, highs);
    }

    /// <summary>The most values per channel <see cref="Samples" /> answers.</summary>
    public const int MaxSampleValues = 65_536;

    /// <summary>The step input state a batch works on, and what a tie needs.</summary>
    readonly record struct StepState(
        StepInputState State,
        IReadOnlyList<long> LastNotes,
        string? LastRegion,
        bool Stacked
    );
}

static partial class ProjectLog {
    [LoggerMessage(Level = LogLevel.Information, Message = "A MIDI step did not apply: {Message}")]
    public static partial void StepIgnored(ILogger logger, string message);
}
