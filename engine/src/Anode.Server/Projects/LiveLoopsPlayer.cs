using Anode.Contracts;
using Anode.Ipc;
using Anode.Model;
using Anode.Model.Compile;
using Anode.Model.Editing;
using Anode.Server.Engine;
using Anode.Server.Realtime;

namespace Anode.Server.Projects;

/// <summary>
///     The Live Loops launcher, server side (docs/07-phase-6.md, "Live Loops"): resolves launches (a scene's cells, and
///     stops on the tracks with none in it; each cell's quantise or the grid's) and stops into one host message each;
///     relays the host's cell states as <c>cell</c> events; and records performances: while one records, it keeps
///     where each cell played (from the host's states) and the control moves on the master (Remix FX), and when it
///     stops turns them into regions and automation as one undo step.
/// </summary>
public sealed class LiveLoopsPlayer {
    readonly ProjectSession session;
    readonly EventHub hub;
    readonly TransportClock clock;
    readonly Lock gate = new();
    readonly Dictionary<string, (CellState State, long Sample)> states = new(StringComparer.Ordinal);
    Performing? recording;

    public LiveLoopsPlayer(ProjectSession session, EventHub hub, TransportClock clock) {
        this.session = session;
        this.hub = hub;
        this.clock = clock;
        session.ControlMoved += OnControlMoved;
    }

    /// <summary>Whether a performance is being recorded.</summary>
    public bool IsRecording {
        get {
            lock (gate) {
                return recording is not null;
            }
        }
    }

    /// <summary>The last state the host reported of each cell, by cell id.</summary>
    public IReadOnlyDictionary<string, (CellState State, long Sample)> States {
        get {
            lock (gate) {
                return new Dictionary<string, (CellState, long)>(states, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>The message that launches <paramref name="request" />'s cells (or scene) in the open project.</summary>
    public LaunchCellsMessage Launch(LaunchRequest request) {
        var view = session.View ?? throw ProjectException.NoProject();
        var state = view.State;
        var grid = state.LiveLoops;
        IEnumerable<Cell> cells;
        var stops = new List<CellStop>();
        if (request.SceneId is { } sceneId) {
            if (grid.Scenes.All(s => s.Id != sceneId)) {
                throw ProjectException.NotFound($"No scene '{sceneId}'.");
            }

            cells = grid.Cells.Where(c => c.SceneId == sceneId).ToArray();
            var launched = cells.Select(static c => c.TrackId).ToHashSet(StringComparer.Ordinal);
            foreach (var trackId in grid.Cells.Select(static c => c.TrackId).Distinct(StringComparer.Ordinal)) {
                if (!launched.Contains(trackId) && Engine(view, trackId) is { } engineId) {
                    stops.Add(new(engineId, Quantize(grid.Quantize)));
                }
            }
        } else if (request.CellIds is { Count: > 0 } ids) {
            cells = [
                ..ids.Distinct(StringComparer.Ordinal)
                    .Select(id => grid.Cells.FirstOrDefault(c => c.Id == id)
                        ?? throw ProjectException.NotFound($"No cell '{id}'.")
                    )
            ];
        } else {
            throw ProjectException.Unprocessable("Name the cells or the scene to launch.");
        }

        var launches = new List<CellLaunch>();
        foreach (var cell in cells) {
            if (Engine(view, cell.TrackId) is not null) {
                launches.Add(new(view.Ids.Cell(cell.Id), Quantize(cell.Quantize ?? grid.Quantize)));
            }
        }

        return new(launches, stops.Count == 0 ? null : stops);
    }

    /// <summary>The message that stops the cells of <paramref name="request" />'s tracks (every track with cells).</summary>
    public StopCellsMessage Stop(StopRequest request) {
        var view = session.View ?? throw ProjectException.NoProject();
        var grid = view.State.LiveLoops;
        var trackIds = request.TrackIds is { Count: > 0 } named
            ? named.Distinct(StringComparer.Ordinal)
                .Select(id => view.State.FindTrack(id)?.Id ?? throw ProjectException.NotFound($"No track '{id}'."))
            : grid.Cells.Select(static c => c.TrackId).Distinct(StringComparer.Ordinal);
        return new([
            ..trackIds.Select(id => Engine(view, id)).OfType<int>().Select(id => new CellStop(id, Quantize(grid.Quantize)))
        ]);
    }

    static int? Engine(OpenProjectView view, string trackId) =>
        view.State.FindTrack(trackId) is { } track && Stacks.IsPlayable(track) ? view.Ids.Track(track.Id) : null;

    /// <summary>The engine's quantise for a contract one (the same values, in the same order).</summary>
    public static EngineQuantize Quantize(LaunchQuantize quantize) => (EngineQuantize)(int)quantize;

    /// <summary>The host reports a cell's state: relayed as a <c>cell</c> event, and kept for a performance.</summary>
    public void OnCellState(CellStateMessage message) {
        if (session.View is not { } view || view.Ids.CellOf(message.Cell) is not { } cellId) {
            return;
        }

        var state = (Contracts.CellState)(int)message.State;
        lock (gate) {
            states[cellId] = (state, message.Sample);
            recording?.Cell(cellId, state, message.Sample);
        }

        hub.Publish(new CellEvent(cellId, state, message.Sample));
    }

    /// <summary>
    ///     Starts or stops recording: into a cell (the track's next recording becomes it), or a performance. Stopping a
    ///     performance applies it; false when there was nothing to stop.
    /// </summary>
    public void Record(LiveRecordRequest request) {
        if (request.TrackId is not null || request.SceneId is not null) {
            session.RecordIntoCell(request.On ? request.TrackId : null, request.On ? request.SceneId : null);
            return;
        }

        var view = session.View ?? throw ProjectException.NoProject();
        if (request.On) {
            lock (gate) {
                var now = Now(view.State);
                recording = new(view.Session);
                foreach (var (cellId, (state, sample)) in states) {
                    if (state is CellState.Playing or CellState.Stopping) {
                        recording.Cell(cellId, CellState.Playing, Math.Max(sample, now));
                    }
                }
            }

            return;
        }

        Performing? done;
        lock (gate) {
            done = recording;
            recording = null;
        }

        if (done is null || session.View is not { } current || current.Session != done.Session) {
            return;
        }

        session.ApplyPerformance(done.Spans(Now(current.State)), done.Moves);
    }

    /// <summary>The playhead in samples (the transport clock's tick through the playback map).</summary>
    long Now(ProjectState state) =>
        clock.Playing is var (_, _, tick) ? Performance.SampleAt(state, tick) : 0;

    void OnControlMoved(string? trackId, string target, double value) {
        if (clock.Playing is not var (_, _, tick)) {
            return;
        }

        lock (gate) {
            // Remix FX moves: the master's inserts (and any other control the performance touched on the master).
            if (trackId is null && recording is not null) {
                recording.Moves.Add(new(tick, null, target, value));
            }
        }
    }

    /// <summary>A performance being recorded: the cells' play spans and the control moves.</summary>
    sealed class Performing(long session) {
        readonly Dictionary<string, long> playing = new(StringComparer.Ordinal);
        readonly List<CellSpan> spans = [];

        public long Session { get; } = session;
        public List<ControlMove> Moves { get; } = [];

        public void Cell(string cellId, CellState state, long sample) {
            switch (state) {
                case CellState.Playing:
                    if (playing.Remove(cellId, out var restarted)) {
                        spans.Add(new(cellId, restarted, sample));
                    }

                    playing[cellId] = sample;
                    break;
                case CellState.Stopped when playing.Remove(cellId, out var start):
                    spans.Add(new(cellId, start, sample));
                    break;
            }
        }

        /// <summary>Every span, the cells still playing ending at <paramref name="now" />.</summary>
        public List<CellSpan> Spans(long now) => [
            ..spans,
            ..playing.Where(p => now > p.Value).Select(p => new CellSpan(p.Key, p.Value, now))
        ];
    }
}
