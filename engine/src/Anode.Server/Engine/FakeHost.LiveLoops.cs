using Anode.Ipc;
using Anode.Ipc.Segments;
using Anode.Ipc.Telemetry;

namespace Anode.Server.Engine;

/// <summary>
///     Phase 6 in the fake host (docs/07-phase-6.md): it keeps the segment's tempo points and cell table, the preview
///     sync and MIDI FX slots (as pass-through: kept, never run), and plays cells: a launch or stop waits for its
///     quantise boundary (computed from the tempo points, as the host does: a request within 1 ms of a boundary takes the
///     next), a launch replaces the track's playing cell there, a one-shot stops at its end, a retrigger launched while
///     playing restarts; every change is pushed as <see cref="CellStateMessage" /> with its sample. While the transport
///     stands, launches and stops take effect at once.
/// </summary>
public sealed partial class FakeHost {
    static readonly TimeSpan CellInterval = TimeSpan.FromMilliseconds(4);

    readonly Dictionary<int, CellInfo> cells = [];
    readonly Dictionary<int, Launched> launcher = [];
    TempoMapPoint[] tempoPoints = [];
    SetPreviewSyncMessage previewSync = new(1, 0, false);
    Task? cellLoop;

    /// <summary>A cell of the loaded segment: its track, length in samples and play mode.</summary>
    public sealed record CellInfo(int Index, int TrackId, long Length, CellMode Mode, long EventCount, long PlaylistCount);

    /// <summary>A cell in the launcher: its state, the sample it changed (or will change) at, what comes next.</summary>
    sealed record Launched(int Cell, EngineCellState State, long Sample, long? StopAt);

    /// <summary>The tempo points of the model loaded last.</summary>
    public IReadOnlyList<TempoMapPoint> TempoPoints {
        get {
            lock (gate) {
                return [..tempoPoints];
            }
        }
    }

    /// <summary>The cells of the loaded model, by index.</summary>
    public IReadOnlyDictionary<int, CellInfo> Cells {
        get {
            lock (gate) {
                return new Dictionary<int, CellInfo>(cells);
            }
        }
    }

    /// <summary>How the preview channel plays (<see cref="SetPreviewSyncMessage" />).</summary>
    public SetPreviewSyncMessage PreviewSync {
        get {
            lock (gate) {
                return previewSync;
            }
        }
    }

    /// <summary>Each cell's state in the launcher, by index.</summary>
    public IReadOnlyDictionary<int, (EngineCellState State, long Sample)> CellStates {
        get {
            lock (gate) {
                return launcher.ToDictionary(static p => p.Key, static p => (p.Value.State, p.Value.Sample));
            }
        }
    }

    /// <summary>Reads a segment's tempo points and cells (a per-track segment's cells replace its tracks').</summary>
    unsafe void LoadPhase6(MappedSegment mapped, bool replace, IReadOnlyCollection<int> loadedTracks) {
        var points = new TempoMapPoint[mapped.TempoCount];
        for (var i = 0; i < points.Length; i++) {
            points[i] = mapped.Tempo[i];
        }

        var read = new List<CellInfo>();
        for (long i = 0; i < mapped.CellCount; i++) {
            var view = mapped.Cell(i);
            var e = view.Entry;
            read.Add(new(e.Index, e.TrackId, e.Length, e.Mode, view.View.EventCount, view.View.PlaylistCount));
        }

        lock (gate) {
            if (replace) {
                tempoPoints = points;
                cells.Clear();
            } else {
                foreach (var stale in cells.Values.Where(c => loadedTracks.Contains(c.TrackId)).ToArray()) {
                    cells.Remove(stale.Index);
                }
            }

            foreach (var cell in read) {
                cells[cell.Index] = cell;
            }
        }
    }

    /// <summary>The Live Loops and preview messages (Phase 6); false for any other message.</summary>
    bool HandleLiveLoops(IpcMessage message) {
        List<CellStateMessage> changes;
        switch (message) {
            case SetPreviewSyncMessage sync:
                lock (gate) {
                    previewSync = sync;
                }

                return true;
            case LaunchCellsMessage launch:
                lock (gate) {
                    changes = Launch(launch);
                }

                break;
            case StopCellsMessage stop:
                lock (gate) {
                    changes = Stop(stop.Stops);
                }

                break;
            default:
                return false;
        }

        cellLoop ??= CellLoopAsync(stopping.Token);
        Push(changes);
        return true;
    }

    List<CellStateMessage> Launch(LaunchCellsMessage message) {
        var changes = new List<CellStateMessage>();
        var now = Position();
        var moving = state != TelemetryTransportState.Stopped;
        foreach (var launch in message.Cells ?? []) {
            if (!cells.TryGetValue(launch.Cell, out var cell)) {
                continue;
            }

            var at = moving ? Boundary(now, launch.Quantize) : now;
            if (launcher.TryGetValue(cell.Index, out var current)
                && current.State == EngineCellState.Playing
                && cell.Mode == CellMode.Loop) {
                continue; // a looping cell launched while it plays keeps playing
            }

            // The track's other cells give way at the boundary.
            foreach (var other in launcher.Values.Where(l => l.Cell != cell.Index
                         && cells.TryGetValue(l.Cell, out var o)
                         && o.TrackId == cell.TrackId
                         && l.State is EngineCellState.Playing or EngineCellState.Queued).ToArray()) {
                changes.Add(Set(other with { State = EngineCellState.Stopping, Sample = at, StopAt = at }));
            }

            changes.Add(Set(new(cell.Index, at <= now ? EngineCellState.Playing : EngineCellState.Queued, at, null)));
        }

        changes.AddRange(Stop(message.Stops ?? []));
        return changes;
    }

    List<CellStateMessage> Stop(IReadOnlyList<CellStop> stops) {
        var changes = new List<CellStateMessage>();
        var now = Position();
        var moving = state != TelemetryTransportState.Stopped;
        foreach (var stop in stops) {
            var at = moving ? Boundary(now, stop.Quantize) : now;
            foreach (var playing in launcher.Values.Where(l => cells.TryGetValue(l.Cell, out var c)
                             && c.TrackId == stop.TrackId
                             && l.State is EngineCellState.Playing or EngineCellState.Queued)
                         .ToArray()) {
                changes.Add(
                    Set(
                        at <= now
                            ? playing with { State = EngineCellState.Stopped, Sample = at, StopAt = null }
                            : playing with { State = EngineCellState.Stopping, Sample = at, StopAt = at }
                    )
                );
            }
        }

        return changes;
    }

    CellStateMessage Set(Launched launched) {
        launcher[launched.Cell] = launched;
        return new(launched.Cell, launched.State, launched.Sample);
    }

    /// <summary>Moves the launcher on as the position passes its boundaries; pushes what changed.</summary>
    async Task CellLoopAsync(CancellationToken cancellationToken) {
        using var timer = new PeriodicTimer(CellInterval);
        try {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false)) {
                List<CellStateMessage> changes;
                lock (gate) {
                    changes = Advance(Position());
                }

                Push(changes);
            }
        } catch (OperationCanceledException) {
            // stopping
        }
    }

    List<CellStateMessage> Advance(long now) {
        var changes = new List<CellStateMessage>();
        foreach (var l in launcher.Values.ToArray()) {
            switch (l.State) {
                case EngineCellState.Queued when now >= l.Sample:
                    changes.Add(Set(l with { State = EngineCellState.Playing }));
                    break;
                case EngineCellState.Stopping when l.StopAt is { } at && now >= at:
                    changes.Add(Set(l with { State = EngineCellState.Stopped, Sample = at, StopAt = null }));
                    break;
                case EngineCellState.Playing when cells.TryGetValue(l.Cell, out var cell)
                    && cell.Mode != CellMode.Loop
                    && now >= l.Sample + cell.Length:
                    changes.Add(Set(l with { State = EngineCellState.Stopped, Sample = l.Sample + cell.Length }));
                    break;
            }
        }

        return changes;
    }

    void Push(List<CellStateMessage> changes) {
        if (changes.Count == 0) {
            return;
        }

        _ = Task.Run(
            async () => {
                foreach (var change in changes) {
                    try {
                        await SendAsync(change).ConfigureAwait(false);
                    } catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException) {
                        return; // the server went away
                    }
                }
            },
            CancellationToken.None
        );
    }

    /// <summary>
    ///     The next launch boundary after <paramref name="sample" /> (a request within 1 ms of a boundary takes the next):
    ///     a note value from the bar line, or 1, 2 or 4 bars, through the tempo points.
    /// </summary>
    public long Boundary(long sample, EngineQuantize quantize) => Boundary(tempoPoints, SampleRate, sample, quantize);

    public static long Boundary(IReadOnlyList<TempoMapPoint> points, int rate, long sample, EngineQuantize quantize) {
        if (quantize == EngineQuantize.Off || points.Count == 0) {
            return sample;
        }

        var late = sample + rate / 1000;
        var q = QuarterAt(points, rate, late);
        var point = At(points, p => p.Quarter <= q);
        var bar = point.Denominator > 0 ? point.Numerator * 4.0 / point.Denominator : 4;
        var step = quantize switch {
            EngineQuantize.Sixteenth => 0.25,
            EngineQuantize.Eighth => 0.5,
            EngineQuantize.Quarter => 1,
            EngineQuantize.Half => 2,
            EngineQuantize.TwoBars => bar * 2,
            EngineQuantize.FourBars => bar * 4,
            _ => bar
        };
        var from = q - point.BarQuarter;
        var next = point.BarQuarter + (Math.Floor(from / step + 1e-9) + 1) * step;
        return SampleAt(points, rate, next);
    }

    static TempoMapPoint At(IReadOnlyList<TempoMapPoint> points, Func<TempoMapPoint, bool> before) {
        var found = points[0];
        foreach (var p in points) {
            if (before(p)) {
                found = p;
            }
        }

        return found;
    }

    /// <summary>The quarter note at a sample: exact between points (an exponential in time on a ramp).</summary>
    public static double QuarterAt(IReadOnlyList<TempoMapPoint> points, int rate, long sample) {
        var p = At(points, x => x.Sample <= sample);
        var e = (double)(sample - p.Sample) / rate;
        return p.Slope == 0
            ? p.Quarter + p.Bpm * e / 60
            : p.Quarter + p.Bpm / p.Slope * (Math.Exp(p.Slope * e / 60) - 1);
    }

    /// <summary>The sample a quarter note plays at, rounded.</summary>
    public static long SampleAt(IReadOnlyList<TempoMapPoint> points, int rate, double quarter) {
        var p = At(points, x => x.Quarter <= quarter);
        var dq = quarter - p.Quarter;
        var e = p.Slope == 0 ? dq * 60 / p.Bpm : 60 / p.Slope * Math.Log(1 + p.Slope * dq / p.Bpm);
        return p.Sample + (long)Math.Round(e * rate);
    }
}
