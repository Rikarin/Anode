using Anode.Contracts;
using Anode.Model;
using Anode.Model.Editing;

namespace Anode.Server.Projects;

/// <summary>Phase 6 (docs/07-phase-6.md): chained edits (adding loops), performance recording, recording into cells.</summary>
public sealed partial class ProjectSession {
    (string TrackId, string SceneId)? cellRecording;

    /// <summary>
    ///     A control moved (a track's, or the master's with a null track): its lane target and value. Performance
    ///     recording listens for Remix FX moves.
    /// </summary>
    public event Action<string?, string, double>? ControlMoved;

    /// <summary>
    ///     Applies commands one after another as one undo step, each made from the project as the ones before left it
    ///     (and the project before the last one): how a step finds what an earlier one made, such as a new region's id.
    ///     A step that answers null is skipped; one that does not apply undoes the whole chain (422).
    /// </summary>
    public CommandResult ApplyChain(string label, IReadOnlyList<Func<ProjectState, ProjectState, EditCommand?>> steps) {
        CommandResult result;
        IReadOnlyList<FileAnalysis> built = [];
        try {
            lock (gate) {
                var o = Required();
                var analysis = new SessionAnalysis(analyses, o.Folder);
                var forward = new List<DeltaOp>();
                var inverse = new List<DeltaOp>();
                var state = o.State;
                var previous = state;
                var commands = new List<EditCommand>();
                try {
                    foreach (var step in steps) {
                        if (step(state, previous) is not { } command) {
                            continue;
                        }

                        commands.Add(command);
                        var ops = Edits.Ops(state, command, ids, catalog.ForEdits, patches, analysis);
                        previous = state;
                        state = Edits.Step(state, ops, forward, inverse);
                    }
                } catch (EditException e) {
                    built = Keep(o, analysis.Built);
                    throw ProjectException.Unprocessable(e.Message);
                }

                built = Keep(o, analysis.Built);
                inverse.Reverse();
                KeepStates(o, o.State, state, commands);
                result = Commit(o, state, forward);
                o.History.Push(label, o.Revision, new(forward, inverse), null);
            }
        } finally {
            Announce(built);
        }

        Changed?.Invoke();
        return result;
    }

    /// <summary>
    ///     Applies a performance recording (<see cref="Performance" />): the cells' spans as regions and the control moves
    ///     as automation, one undo step ("Live Loops Performance"); null when it recorded nothing.
    /// </summary>
    public CommandResult? ApplyPerformance(IReadOnlyList<CellSpan> spans, IReadOnlyList<ControlMove> moves) {
        CommandResult? result = null;
        lock (gate) {
            if (open is not { } o) {
                return null;
            }

            var ops = Performance.Ops(o.State, spans, moves, ids);
            if (ops.Count > 0) {
                var forward = new List<DeltaOp>();
                var inverse = new List<DeltaOp>();
                var state = Edits.Step(o.State, ops, forward, inverse);
                inverse.Reverse();
                result = Commit(o, state, forward);
                o.History.Push("Live Loops Performance", o.Revision, new(forward, inverse), null);
            }
        }

        if (result is not null) {
            Changed?.Invoke();
        }

        return result;
    }

    /// <summary>
    ///     Records into the Live Loops cell of <paramref name="trackId" /> and <paramref name="sceneId" />: the next
    ///     recording of that track becomes the cell's content instead of a Tracks-area region (null: none).
    /// </summary>
    public void RecordIntoCell(string? trackId, string? sceneId) {
        lock (gate) {
            var o = Required();
            if (trackId is null || sceneId is null) {
                cellRecording = null;
                return;
            }

            if (o.State.FindTrack(trackId) is null) {
                throw ProjectException.NotFound($"No track '{trackId}'.");
            }

            if (o.State.LiveLoops.Scenes.All(s => s.Id != sceneId)) {
                throw ProjectException.NotFound($"No scene '{sceneId}'.");
            }

            cellRecording = (trackId, sceneId);
        }
    }

    /// <summary>
    ///     A recording's operations with the cell recording applied: the last region (audio or MIDI) it made on the
    ///     recording track becomes that cell's content (at tick 0, its length the cell's) and leaves the Tracks area.
    /// </summary>
    ProjectState IntoCell(ProjectState state, List<DeltaOp> forward, List<DeltaOp> inverse) {
        if (cellRecording is not var (trackId, sceneId)) {
            return state;
        }

        CellContent? content = null;
        DeltaOp? removal = null;
        long length = 0;
        foreach (var op in forward) {
            switch (op) {
                case RegionUpserted(var region) when region.TrackId == trackId && state.Regions.ContainsKey(region.Id):
                    var r = state.Regions[region.Id];
                    content = new(Audio: r with { StartTick = 0 });
                    removal = new RegionRemoved(r.Id);
                    length = Math.Max(1, Edits.EndTick(state, r) - r.StartTick);
                    break;
                case MidiRegionUpserted(var midi) when midi.TrackId == trackId && state.MidiRegions.ContainsKey(midi.Id):
                    var m = state.MidiRegions[midi.Id];
                    content = new(Midi: m with { StartTick = 0 });
                    removal = new MidiRegionRemoved(m.Id);
                    length = m.LengthTicks;
                    break;
            }
        }

        if (content is null) {
            return state;
        }

        cellRecording = null;
        try {
            var ops = new List<DeltaOp> { removal! };
            ops.AddRange(
                Edits.Ops(
                    state.Apply(removal!),
                    new SetCellCommand(trackId, sceneId, content, LengthTicks: length),
                    ids,
                    catalog.ForEdits
                )
            );
            return Edits.Step(state, ops, forward, inverse);
        } catch (EditException e) {
            ProjectLog.CellRecordingFailed(logger, e.Message);
            return state;
        }
    }

    /// <summary>Tells performance recording about control moves in a batch (Remix FX on the master, mostly).</summary>
    void Moved(IReadOnlyList<EditCommand> commands, ProjectState after) {
        if (ControlMoved is not { } moved) {
            return;
        }

        foreach (var command in commands) {
            if (command is SetInsertParameterCommand { Chain: null or InsertChain.Inserts } c) {
                var strip = c.TrackId is null ? after.Master : after.FindTrack(c.TrackId)?.Strip;
                if (strip?.Inserts.FirstOrDefault(i => i.Id == c.InsertId) is { } insert
                    && insert.Parameters.TryGetValue(c.ParameterId, out var value)) {
                    moved(c.TrackId, $"insert:{c.InsertId}:{c.ParameterId}", value);
                }
            }
        }
    }
}

static partial class ProjectLog {
    [LoggerMessage(Level = LogLevel.Warning, Message = "Recording into a cell failed: {Message}")]
    public static partial void CellRecordingFailed(ILogger logger, string message);
}
