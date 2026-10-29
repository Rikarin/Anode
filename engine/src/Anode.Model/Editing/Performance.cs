using Anode.Contracts;

namespace Anode.Model.Editing;

/// <summary>A Live Loops cell played from project sample <c>StartSample</c> to <c>EndSample</c> (exclusive).</summary>
public sealed record CellSpan(string CellId, long StartSample, long EndSample);

/// <summary>A control moved during a performance: lane target <c>Target</c> of a track (null: the master) at a tick.</summary>
public sealed record ControlMove(long Tick, string? TrackId, string Target, double Value);

/// <summary>
///     Performance recording (docs/07-phase-6.md, "Live Loops"): what a performance played becomes Tracks-area content
///     where it played — each cell's span as regions of its content on its track (a looping cell repeats every cell
///     length, the last piece cut where it stopped; a one-shot or retrigger cell plays once) — and its control moves
///     (Remix FX) as automation points over the moved lanes. The server applies the result as one undo step.
/// </summary>
public static class Performance {
    public static List<DeltaOp> Ops(
        ProjectState state,
        IReadOnlyList<CellSpan> spans,
        IReadOnlyList<ControlMove> moves,
        IIdSource ids
    ) {
        var ops = new List<DeltaOp>();
        var map = state.PlaybackMap;
        var next = state.LastEventId;
        foreach (var span in spans.OrderBy(static s => s.StartSample)) {
            if (state.LiveLoops.Cells.FirstOrDefault(c => c.Id == span.CellId) is not { } cell
                || state.FindTrack(cell.TrackId) is null
                || span.EndSample <= span.StartSample) {
                continue;
            }

            var start = map.TickAtSample(span.StartSample, state.SampleRate);
            var end = Math.Max(start + 1, map.TickAtSample(span.EndSample, state.SampleRate));
            var length = Math.Max(1, cell.LengthTicks);
            for (var at = start; at < end; at += length) {
                var piece = Math.Min(length, end - at);
                if (Piece(state, cell, at, piece, ids, ref next) is { } op) {
                    ops.Add(op);
                }

                if (cell.PlayMode != CellPlayMode.Loop) {
                    break;
                }
            }
        }

        foreach (var lane in moves.GroupBy(static m => (m.TrackId, m.Target))) {
            var (trackId, target) = lane.Key;
            if (!Edits.TargetExists(state, trackId, target)) {
                continue;
            }

            var points = lane.OrderBy(static m => m.Tick).ToArray();
            var (from, to) = (points[0].Tick, points[^1].Tick);
            var existing = state.LaneOf(trackId, target);
            var kept = (existing?.Points ?? []).Where(p => p.Tick < from || p.Tick > to);
            var written = points.GroupBy(static m => m.Tick).Select(static g => new AutomationPoint(g.Key, g.Last().Value, 0));
            ops.Add(
                new AutomationLaneUpserted(
                    new(
                        existing?.Id ?? ids.Next(IdPrefix.Automation),
                        trackId,
                        target,
                        [..kept.Concat(written).OrderBy(static p => p.Tick)]
                    )
                )
            );
        }

        return ops;
    }

    /// <summary>One piece of a cell's content as a region at <paramref name="at" />, <paramref name="length" /> ticks long.</summary>
    static DeltaOp? Piece(ProjectState state, Cell cell, long at, long length, IIdSource ids, ref long next) {
        if (cell.Content.Midi is { } midi) {
            var notes = new List<Note>();
            foreach (var note in midi.Notes.Where(n => n.StartTick < length)) {
                notes.Add(note with { Id = ++next, LengthTicks = Math.Min(note.LengthTicks, length - note.StartTick) });
            }

            var controls = new List<ControlEvent>();
            foreach (var control in midi.Controls.Where(e => e.Tick < length)) {
                controls.Add(control with { Id = ++next });
            }

            return new MidiRegionUpserted(
                midi with {
                    Id = ids.Next(IdPrefix.MidiRegion),
                    TrackId = cell.TrackId,
                    Name = cell.Name,
                    StartTick = at,
                    LengthTicks = length,
                    Notes = [..notes],
                    Controls = [..controls],
                    Muted = false
                }
            );
        }

        if (cell.Content.Pattern is { } pattern) {
            return new PatternRegionUpserted(
                pattern with {
                    Id = ids.Next(IdPrefix.PatternRegion),
                    TrackId = cell.TrackId,
                    Name = cell.Name,
                    StartTick = at,
                    LengthTicks = length,
                    Muted = false
                }
            );
        }

        if (cell.Content.Audio is not { } audio || !state.Files.TryGetValue(audio.FileId, out var file)) {
            return null;
        }

        var seconds = state.TempoMap.SecondsAt(at + length) - state.TempoMap.SecondsAt(at);
        var frames = Math.Clamp((long)Math.Round(seconds * file.SampleRate), 1, audio.Length);
        return new RegionUpserted(
            audio with {
                Id = ids.Next(IdPrefix.Region),
                TrackId = cell.TrackId,
                Name = cell.Name,
                StartTick = at,
                Length = frames,
                FadeIn = Math.Min(audio.FadeIn, frames),
                FadeOut = Math.Min(audio.FadeOut, frames - Math.Min(audio.FadeIn, frames)),
                Muted = false
            }
        );
    }

    /// <summary>The tick a project sample plays at (Varispeed folded in).</summary>
    public static long TickAt(ProjectState state, long sample) => state.PlaybackMap.TickAtSample(sample, state.SampleRate);

    /// <summary>The project sample a tick plays at (Varispeed folded in).</summary>
    public static long SampleAt(ProjectState state, long tick) => state.PlaybackMap.SampleAt(tick, state.SampleRate);
}
