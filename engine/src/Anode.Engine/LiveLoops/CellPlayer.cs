using Anode.Engine.Midi;
using Anode.Engine.Timing;
using Anode.Ipc;
using Anode.Ipc.Segments;

namespace Anode.Engine.LiveLoops;

/// <summary>
///     One track's Live Loops launcher (docs/07-phase-6.md, "Live Loops"), audio-thread state: the cell it plays (and
///     the transport clock it started at, so it plays on through cycle jumps and locates), at most one queued launch
///     and one queued stop, each waiting for its quantise boundary, and the block's schedule: which cell plays from
///     which frame of the block (<see cref="Segments" />). Boundaries come from the tempo map in play order; a request
///     closer than <see cref="AudioEngine.LateFrames" /> to a boundary takes the next one. Allocation-free.
/// </summary>
public sealed class CellPlayer {
    /// <summary>Most transitions one block holds.</summary>
    public const int MaxSegments = 8;

    readonly int[] segmentFrom = new int[MaxSegments];
    readonly int[] segmentCell = new int[MaxSegments];
    readonly long[] segmentStart = new long[MaxSegments];

    /// <summary>The cell playing (−1: none).</summary>
    public int Playing { get; private set; } = -1;

    /// <summary>The cell queued to launch (−1: none).</summary>
    public int Queued { get; private set; } = -1;

    /// <summary>A stop is queued for the playing cell.</summary>
    public bool Stopping { get; private set; }

    long startClock;
    EngineQuantize launchQuantize;
    long launchNotBefore;
    EngineQuantize stopQuantize;
    long stopNotBefore;
    long launchAt = -1;
    long stopAt = -1;
    long resolvedEnd = long.MinValue;

    /// <summary>The block's schedule: <see cref="Segment" /> 0 … Segments − 1, in frame order.</summary>
    public int Segments { get; private set; }

    /// <summary>
    ///     Segment <paramref name="i" />: from frame <c>From</c> of the block, cell <c>Cell</c> (−1: silence) plays, offset
    ///     <c>clock − StartClock</c> into it.
    /// </summary>
    public (int From, int Cell, long StartClock) Segment(int i) => (segmentFrom[i], segmentCell[i], segmentStart[i]);

    /// <summary>
    ///     Queues a launch of <paramref name="cell" /> (whose play mode is <paramref name="mode" />) at the next
    ///     <paramref name="quantize" /> boundary: it replaces a queued launch (which stops) or stop. Launching the cell
    ///     that plays restarts it only for a retrigger cell; otherwise it cancels a queued stop.
    /// </summary>
    internal void Launch(
        int cell,
        CellMode mode,
        EngineQuantize quantize,
        bool rolling,
        long position,
        long clock,
        int late,
        in TempoMap tempo,
        Cycle cycle,
        SpscChannel<CellStateEvent> states
    ) {
        if (cell == Playing && mode != CellMode.Retrigger) {
            if (Queued >= 0) {
                states.TryEnqueue(new(Queued, EngineCellState.Stopped, position));
                Queued = -1;
            }

            if (Stopping) {
                Stopping = false;
                states.TryEnqueue(new(cell, EngineCellState.Playing, position));
            }

            return;
        }

        if (Queued >= 0 && Queued != cell) {
            states.TryEnqueue(new(Queued, EngineCellState.Stopped, position));
        }

        Queued = cell;
        launchQuantize = quantize;
        launchNotBefore = rolling ? clock + late : long.MinValue;
        Stopping = false;
        resolvedEnd = long.MinValue;
        var at = Predict(quantize, launchNotBefore, rolling, position, clock, tempo, cycle);
        states.TryEnqueue(new(cell, EngineCellState.Queued, at));
    }

    /// <summary>Queues a stop of the playing cell at the next boundary; a queued launch stops at once.</summary>
    internal void Stop(
        EngineQuantize quantize,
        bool rolling,
        long position,
        long clock,
        int late,
        in TempoMap tempo,
        Cycle cycle,
        SpscChannel<CellStateEvent> states
    ) {
        if (Queued >= 0) {
            states.TryEnqueue(new(Queued, EngineCellState.Stopped, position));
            Queued = -1;
        }

        if (Playing < 0) {
            return;
        }

        if (!rolling) {
            states.TryEnqueue(new(Playing, EngineCellState.Stopped, position));
            Playing = -1;
            Stopping = false;
            return;
        }

        Stopping = true;
        stopQuantize = quantize;
        stopNotBefore = clock + late;
        resolvedEnd = long.MinValue;
        var at = Predict(quantize, stopNotBefore, rolling, position, clock, tempo, cycle);
        states.TryEnqueue(new(Playing, EngineCellState.Stopping, at));
    }

    /// <summary>The transport stopped: everything playing or queued stops at <paramref name="position" />.</summary>
    internal void StopAll(long position, SpscChannel<CellStateEvent> states) {
        if (Queued >= 0) {
            states.TryEnqueue(new(Queued, EngineCellState.Stopped, position));
        }

        if (Playing >= 0) {
            states.TryEnqueue(new(Playing, EngineCellState.Stopped, position));
        }

        Queued = Playing = -1;
        Stopping = false;
        Segments = 0;
        resolvedEnd = long.MinValue;
    }

    /// <summary>A cell the model no longer has stops (a recompile removed it).</summary>
    internal void Forget(CellTable cells, long position, SpscChannel<CellStateEvent> states) {
        if (Queued >= 0 && cells[Queued] is null) {
            states.TryEnqueue(new(Queued, EngineCellState.Stopped, position));
            Queued = -1;
        }

        if (Playing >= 0 && cells[Playing] is null) {
            states.TryEnqueue(new(Playing, EngineCellState.Stopped, position));
            Playing = -1;
            Stopping = false;
        }
    }

    /// <summary>Whether the player has anything to do (a cell playing or queued).</summary>
    public bool Busy => Playing >= 0 || Queued >= 0;

    /// <summary>
    ///     Works out the block of <paramref name="frames" /> from <paramref name="position" /> (which spans no cycle
    ///     jump): launches and stops whose boundary falls in it, and one-shots that end in it, each at its frame, with
    ///     their state changes; then the block's schedule. Nothing plays while the transport is stopped.
    /// </summary>
    internal void Step(
        bool rolling,
        long position,
        long clock,
        int frames,
        CellTable cells,
        in TempoMap tempo,
        Cycle cycle,
        SpscChannel<CellStateEvent> states
    ) {
        Segments = 0;
        if (!rolling) {
            resolvedEnd = long.MinValue;
            return;
        }

        if (position != resolvedEnd) {
            // A new block after a jump (a locate, a cycle wrap, a start): the boundaries are found again.
            launchAt = Queued >= 0 ? Boundary(launchQuantize, launchNotBefore, position, clock, tempo, cycle) : -1;
            stopAt = Stopping ? Boundary(stopQuantize, stopNotBefore, position, clock, tempo, cycle) : -1;
        }

        resolvedEnd = position + frames;
        Add(0, Playing, startClock);
        for (var guard = 0; guard < MaxSegments * 2; guard++) {
            var (next, launch) = NextChange(position, clock, frames, cells);
            if (next < 0) {
                break;
            }

            Change(launch, position + next, clock + next, states);
            Add(next, Playing, startClock);
        }
    }

    /// <summary>
    ///     A change at project sample <paramref name="at" /> (clock <paramref name="clock" />): a launch, or a stop or
    ///     one-shot's end.
    /// </summary>
    void Change(bool launch, long at, long clock, SpscChannel<CellStateEvent> states) {
        Stopping = false;
        if (!launch) {
            states.TryEnqueue(new(Playing, EngineCellState.Stopped, at));
            Playing = -1;
            stopAt = -1;
            return;
        }

        if (Playing >= 0 && Playing != Queued) {
            states.TryEnqueue(new(Playing, EngineCellState.Stopped, at));
        }

        Playing = Queued;
        Queued = -1;
        launchAt = -1;
        startClock = clock;
        states.TryEnqueue(new(Playing, EngineCellState.Playing, at));
    }

    /// <summary>
    ///     The block frame of the earliest change still due in it (−1: none) and whether it is a launch (else a stop or
    ///     a one-shot's end).
    /// </summary>
    (int Frame, bool Launch) NextChange(long position, long clock, int frames, CellTable cells) {
        long next = frames;
        var found = (Frame: -1, Launch: false);
        if (Queued >= 0 && launchAt >= 0 && launchAt - position < next) {
            next = Math.Max(0, launchAt - position);
            found = ((int)next, true);
        }

        if (Stopping && stopAt >= 0 && stopAt - position < next) {
            next = Math.Max(0, stopAt - position);
            found = ((int)next, false);
        }

        if (Playing >= 0 && cells[Playing] is { Mode: not CellMode.Loop } playing) {
            var end = startClock + playing.Length - clock;
            if (end < next) {
                found = ((int)Math.Max(0, end), false);
            }
        }

        return found;
    }

    void Add(int from, int cell, long start) {
        if (Segments > 0 && segmentFrom[Segments - 1] == from) {
            Segments--; // a later change at the same frame replaces it
        }

        if (Segments == MaxSegments) {
            return;
        }

        segmentFrom[Segments] = from;
        segmentCell[Segments] = cell;
        segmentStart[Segments] = start;
        Segments++;
    }

    /// <summary>
    ///     The boundary a queued request fires at in this lap of play order (−1: after the next cycle jump; found again
    ///     then): the first at or after the later of the block's start and <paramref name="notBefore" /> (a clock).
    /// </summary>
    static long Boundary(
        EngineQuantize quantize,
        long notBefore,
        long position,
        long clock,
        in TempoMap tempo,
        Cycle cycle
    ) {
        if (quantize == EngineQuantize.Off) {
            return position;
        }

        var from = notBefore <= clock ? position : position + (notBefore - clock);
        var boundary = tempo.NextBoundary(from, quantize);
        return cycle.Active && position < cycle.End && boundary >= cycle.End ? -1 : boundary;
    }

    /// <summary>The sample a queued request is expected to fire at (what <c>queued</c> and <c>stopping</c> report).</summary>
    static long Predict(
        EngineQuantize quantize,
        long notBefore,
        bool rolling,
        long position,
        long clock,
        in TempoMap tempo,
        Cycle cycle
    ) {
        if (!rolling) {
            return tempo.NextBoundary(position, quantize);
        }

        var at = Boundary(quantize, notBefore, position, clock, tempo, cycle);
        if (at >= 0) {
            return at;
        }

        return tempo.NextBoundary(cycle.Start, quantize);
    }
}
