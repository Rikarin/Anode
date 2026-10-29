using Anode.Dsp.Flex;
using Anode.Engine.LiveLoops;
using Anode.Engine.Streaming;
using Anode.Engine.Timing;
using Anode.Ipc;
using Anode.Ipc.Segments;
using System.Text;

namespace Anode.Engine;

// Phase 6 on the control thread (docs/07-phase-6.md, workstream A): the tempo map and the Live Loops cells of each
// model load (audio cells rendered into memory here, through the flex renderers, so a launch never waits for the disk),
// launch and stop requests on their way to the audio thread, and the launcher's state changes on their way to the
// server.
public sealed partial class EngineController {
    readonly Lock cellStateGate = new();
    PlaylistRenderer? cellRenderer;
    Timer? cellPump;

    /// <summary>How often the launcher's state changes are pushed.</summary>
    static readonly TimeSpan CellPumpInterval = TimeSpan.FromMilliseconds(5);

    /// <summary>A cell changed state (on the control thread's timer, or <see cref="PumpCellStates" />'s caller).</summary>
    public Action<CellStateMessage>? CellState { get; set; }

    /// <summary>
    ///     Queues launches and stops as one decision: the audio thread applies them in the same block, each starting or
    ///     stopping at its next quantise boundary. Unknown cells and tracks are reported stopped. Throws
    ///     <see cref="InvalidOperationException" /> when the audio thread is not taking requests.
    /// </summary>
    public void LaunchCells(IReadOnlyList<CellLaunch> cells, IReadOnlyList<CellStop>? stops = null) {
        var commands = new List<LaunchCommand>(cells.Count + (stops?.Count ?? 0));
        foreach (var stop in stops ?? []) {
            commands.Add(new(LaunchKind.Stop, stop.TrackId, stop.Quantize));
        }

        foreach (var launch in cells) {
            commands.Add(new(LaunchKind.Launch, launch.Cell, launch.Quantize));
        }

        Enqueue(commands);
    }

    /// <summary>Queues stops, each at its quantise boundary (see <see cref="LaunchCells" />).</summary>
    public void StopCells(IReadOnlyList<CellStop> stops) =>
        Enqueue([..stops.Select(static s => new LaunchCommand(LaunchKind.Stop, s.TrackId, s.Quantize))]);

    void Enqueue(List<LaunchCommand> commands) {
        if (commands.Count == 0) {
            return;
        }

        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(commands);
        lock (Engine.Launches.ProducerGate) {
            var deadline = DateTime.UtcNow + CommandTimeout;
            while (!Engine.Launches.TryEnqueue(span)) {
                if (DateTime.UtcNow > deadline) {
                    throw new InvalidOperationException("The audio thread is not taking launches.");
                }

                Thread.Sleep(1);
            }
        }
    }

    /// <summary>
    ///     Sends the launcher's state changes since the last call to <see cref="CellState" />, in order; returns how
    ///     many. A timer calls it every 5 ms once <see cref="Start" />ed.
    /// </summary>
    public int PumpCellStates() {
        lock (cellStateGate) {
            var count = 0;
            while (Engine.CellStates.TryDequeue(out var e)) {
                CellState?.Invoke(new(e.Cell, e.State, e.Sample));
                count++;
            }

            return count;
        }
    }

    void StartCellPump() => cellPump = new(_ => PumpCellStates(), null, CellPumpInterval, CellPumpInterval);

    void StopCellPump() {
        cellPump?.Dispose();
        cellPump = null;
    }

    /// <summary>
    ///     The tempo map of the next model: a segment's tempo points (a whole model carries them), else, for a patch,
    ///     the current map (a per-track segment has none), else the default.
    /// </summary>
    unsafe (TempoMap Map, MappedSegment? Owner) TempoFor(MappedSegment segment, bool replace, ModelRoot current) {
        if (segment.TempoCount > 0) {
            return (new(segment.Tempo, segment.TempoCount, Options.SampleRate), segment);
        }

        return replace || current.Tempo.SampleRate != Options.SampleRate
            ? (new(Options.SampleRate), null)
            : (current.Tempo, current.TempoOwner);
    }

    /// <summary>
    ///     The next model's cells: a whole model's are its segment's; a per-track segment's cells replace those of the
    ///     tracks it holds. An audio cell's audio is rendered here (or kept, when what it is rendered from is unchanged).
    /// </summary>
    unsafe CellTable CellsFor(
        MappedSegment segment,
        bool replace,
        ModelRoot current,
        IReadOnlyList<TrackState> tracks,
        StreamFile?[] fileTable
    ) {
        if (segment.CellCount == 0 && (replace || current.Cells.ByIndex.Length == 0)) {
            return replace ? CellTable.Empty : current.Cells;
        }

        var held = new HashSet<int>();
        for (var k = 0; k < segment.Header->TrackCount; k++) {
            held.Add(segment.TrackId(k));
        }

        var cells = new List<CellContent>();
        if (!replace) {
            cells.AddRange(current.Cells.All.Where(c => !held.Contains(c.TrackId)));
        }

        for (long i = 0; i < segment.CellCount; i++) {
            var view = segment.Cell(i);
            if (view.Entry.Index is < 0 or > 1 << 20) {
                Log?.Invoke($"cell {view.Entry.Index}: index out of range, ignored");
                continue;
            }

            var track = tracks.FirstOrDefault(t => t.Id == view.Entry.TrackId);
            cells.Add(Cell(segment, view, track is { Stereo: false } ? 1 : 2, current, fileTable));
        }

        var size = cells.Count == 0 ? 0 : cells.Max(static c => c.Index) + 1;
        var byIndex = new CellContent?[size];
        foreach (var cell in cells) {
            byIndex[cell.Index] = cell;
        }

        return new(byIndex);
    }

    /// <summary>
    ///     One cell of <paramref name="segment" />: a MIDI cell as it is; an audio cell with its audio, kept from the
    ///     current model's cell of the same index when what it was rendered from is unchanged, else rendered.
    /// </summary>
    CellContent Cell(MappedSegment segment, CellView view, int channels, ModelRoot current, StreamFile?[] fileTable) {
        var entry = view.Entry;
        if (view.View.PlaylistCount == 0) {
            return new(entry, view.View, segment, null, channels, 0);
        }

        var hash = Hash(view.View, entry.Length, channels, fileTable);
        var known = current.Cells[entry.Index];
        var audio = known is { Audio: not null } && known.Hash == hash && known.Channels == channels
            ? known.Audio
            : RenderCell(view.View, entry, channels, fileTable);
        return new(entry, view.View, segment, audio, channels, hash);
    }

    /// <summary>
    ///     An audio cell's audio from its start: its playlist rendered into memory (at most 60 s), warped entries
    ///     through flex renderers made for it (each crossfade lane through its own), as the disk thread renders a track.
    /// </summary>
    float[] RenderCell(in TrackView view, in CellEntry entry, int channels, StreamFile?[] fileTable) {
        var limit = (long)CellContent.MaxSeconds * Options.SampleRate;
        if (entry.Length > limit) {
            Log?.Invoke($"cell {entry.Index}: longer than {CellContent.MaxSeconds} s; the rest plays silence");
        }

        var audio = new float[(int)Math.Clamp(entry.Length, 0, limit) * channels];
        var lanes = PlaylistLanes.Of(view);
        for (var lane = 0; lane < (lanes is null ? 1 : 2); lane++) {
            RenderLane(view, lanes, lane, channels, fileTable, audio);
        }

        return audio;
    }

    /// <summary>Adds the entries of one crossfade lane of a cell's playlist into <paramref name="audio" />.</summary>
    unsafe void RenderLane(
        in TrackView view,
        byte[]? lanes,
        int lane,
        int channels,
        StreamFile?[] fileTable,
        float[] audio
    ) {
        cellRenderer ??= new(DiskStreamer.ReadFrames);
        using var flex = new FlexLane(channels);
        for (long e = 0; e < view.PlaylistCount; e++) {
            if (view.Playlist[e].WarpCount > 0 && (lanes is null ? 0 : lanes[e]) == lane) {
                flex.Ensure((FlexKind)view.Playlist[e].Algorithm, Options.SampleRate, DiskStreamer.ReadFrames);
            }
        }

        flex.Begin(1);
        var frames = audio.Length / channels;
        var chunk = new float[DiskStreamer.ReadFrames * channels];
        for (var at = 0; at < frames; at += DiskStreamer.ReadFrames) {
            var n = Math.Min(DiskStreamer.ReadFrames, frames - at);
            var into = chunk.AsSpan(0, n * channels);
            cellRenderer.Render(view, lanes, lane, fileTable, at, n, channels, into, flex);
            var target = audio.AsSpan(at * channels, n * channels);
            for (var s = 0; s < into.Length; s++) {
                target[s] += into[s];
            }
        }
    }

    /// <summary>What an audio cell's audio depends on: its playlist, warp and pitch points, files, channels and length.</summary>
    static unsafe ulong Hash(in TrackView view, long length, int channels, StreamFile?[] fileTable) {
        var hash = 14695981039346656037UL;

        void Bytes(byte* data, long count) {
            for (long i = 0; i < count; i++) {
                hash = (hash ^ data[i]) * 1099511628211UL;
            }
        }

        void Value(long value) => Bytes((byte*)&value, sizeof(long));

        Value(length);
        Value(channels);
        for (long e = 0; e < view.PlaylistCount; e++) {
            var entry = view.Playlist[e];
            entry.WarpStart = entry.PitchStart = 0; // where its points sit in the segment does not matter
            Bytes((byte*)&entry, sizeof(AudioPlaylistEntry));
            var original = view.Playlist[e];
            Bytes((byte*)(view.Warp + original.WarpStart), (long)original.WarpCount * sizeof(WarpPoint));
            Bytes((byte*)(view.Pitch + original.PitchStart), (long)original.PitchCount * sizeof(PitchPoint));
            var path = (uint)entry.FileId < (uint)fileTable.Length ? fileTable[entry.FileId]?.Path : null;
            foreach (var b in Encoding.UTF8.GetBytes(path ?? string.Empty)) {
                hash = (hash ^ b) * 1099511628211UL;
            }
        }

        return hash;
    }
}
