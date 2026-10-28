using Anode.Contracts;
using Anode.Model.Time;

namespace Anode.Model.Editing;

/// <summary>One pass of a recording: <c>Frames</c> frames of its file from <c>FileOffset</c>, played at project sample <c>StartSample</c>.</summary>
public readonly record struct TakePass(long StartSample, long FileOffset, long Frames);

/// <summary>
///     What a finished recording (or a Flashback Capture) does to the project (docs/04-phase-3.md, "Cycle recording",
///     "Autopunch", "Flashback Capture"): an audio take's passes become one take folder (one take per pass, the comp on
///     the last) or a region per pass (the last on top); replace mode first removes or trims what the recording covers
///     on its track; autopunch keeps only the punch range. Each region or take starts on the first tick at or after
///     where it was played, its file offset moved to match, so the audio stays where it was played to the sample.
/// </summary>
public static class Recordings {
    /// <summary>The passes, clipped to the punch range when autopunch is on (empty ones dropped).</summary>
    public static IReadOnlyList<TakePass> Punched(ProjectState state, IReadOnlyList<TakePass> passes) {
        var recording = state.Settings.Recording;
        if (!recording.Autopunch) {
            return [..passes.Where(static p => p.Frames > 0)];
        }

        var map = state.TempoMap;
        var from = map.SampleAt(recording.PunchInTick, state.SampleRate);
        var to = map.SampleAt(recording.PunchOutTick, state.SampleRate);
        var punched = new List<TakePass>();
        foreach (var p in passes) {
            var start = Math.Max(p.StartSample, from);
            var end = Math.Min(p.StartSample + p.Frames, to);
            if (end > start) {
                punched.Add(new(start, p.FileOffset + (start - p.StartSample), end - start));
            }
        }

        return punched;
    }

    /// <summary>
    ///     The operations that place an audio take's file on its track: the file joins the pool, then (unless
    ///     <paramref name="flashback" />, and with replace on) what the passes cover is cleared, then the passes become
    ///     a take folder (several passes in take folder mode, or of a Flashback Capture) or regions.
    /// </summary>
    public static List<DeltaOp> Audio(
        ProjectState state,
        Track track,
        AudioFile file,
        IReadOnlyList<TakePass> passes,
        bool flashback,
        IIdSource ids
    ) {
        var ops = new List<DeltaOp> { new AudioFileUpserted(file) };
        passes = [..passes.Where(p => p.Frames > 0 && p.FileOffset >= 0 && p.FileOffset + p.Frames <= file.Frames)];
        if (passes.Count == 0) {
            return ops;
        }

        state = state.Apply(ops[0]);
        if (!flashback && state.Settings.Recording.Replace) {
            var cleared = Replace(
                state,
                track.Id,
                passes.Min(static p => p.StartSample),
                passes.Max(static p => p.StartSample + p.Frames),
                ids
            );
            ops.AddRange(cleared);
            state = state.Apply(cleared);
        }

        var placed = passes.Select(p => Place(state, p)).Where(static p => p.Length > 0).ToArray();
        if (placed.Length == 0) {
            return ops;
        }

        var takeFolder = placed.Length > 1 && (flashback || state.Settings.Recording.CycleMode == CycleRecordMode.TakeFolder);
        if (takeFolder) {
            ops.Add(new TakeFolderUpserted(Folder(state, track, file, placed, ids)));
            return ops;
        }

        // A region per pass, the last on top: at an equal start the later id wins, so the ids rise with the passes.
        var regionIds = placed.Select(_ => ids.Next(IdPrefix.Region)).Order(StringComparer.Ordinal).ToArray();
        for (var i = 0; i < placed.Length; i++) {
            var (tick, offset, length, _) = placed[i];
            ops.Add(
                new RegionUpserted(
                    new(regionIds[i], track.Id, file.Id, track.Name, tick, offset, length, 0, 0, 0, 0, 0, false)
                )
            );
        }

        return ops;
    }

    /// <summary>
    ///     Clears project samples <paramref name="from" />…<paramref name="to" /> on a track (replace mode): regions
    ///     inside go, regions across an edge are trimmed to it (one across both is split in two), take folders inside go.
    /// </summary>
    public static List<DeltaOp> Replace(ProjectState state, string trackId, long from, long to, IIdSource ids) {
        var map = state.TempoMap;
        var rate = state.SampleRate;
        var ops = new List<DeltaOp>();
        foreach (var r in state.RegionsOf(trackId)) {
            var start = map.SampleAt(r.StartTick, rate);
            var end = start + r.Length;
            if (end <= from || start >= to) {
                continue;
            }

            if (start >= from && end <= to) {
                ops.Add(new RegionRemoved(r.Id));
                continue;
            }

            if (start < from) {
                ops.Add(new RegionUpserted(Trimmed(r, r.FileOffset, from - start)));
            }

            if (end > to) {
                var tick = CeilingTick(map, to, rate);
                var shift = map.SampleAt(tick, rate) - start;
                if (shift < r.Length) {
                    var right = Trimmed(r, r.FileOffset + shift, r.Length - shift) with { StartTick = tick };
                    ops.Add(new RegionUpserted(start < from ? right with { Id = ids.Next(IdPrefix.Region) } : right));
                }
            }
        }

        foreach (var f in state.TakeFoldersOf(trackId)) {
            if (map.SampleAt(f.StartTick, rate) >= from && map.SampleAt(f.StartTick + f.LengthTicks, rate) <= to) {
                ops.Add(new TakeFolderRemoved(f.Id));
            }
        }

        return ops;
    }

    /// <summary>Clears ticks <paramref name="from" />…<paramref name="to" /> of a track's MIDI regions (replace mode).</summary>
    public static List<DeltaOp> ReplaceMidi(ProjectState state, string trackId, long from, long to, IIdSource ids) {
        var ops = new List<DeltaOp>();
        foreach (var r in state.MidiRegionsOf(trackId)) {
            var end = r.StartTick + r.LengthTicks;
            if (end <= from || r.StartTick >= to) {
                continue;
            }

            if (r.StartTick >= from && end <= to) {
                ops.Add(new MidiRegionRemoved(r.Id));
                continue;
            }

            if (r.StartTick < from) {
                var cut = from - r.StartTick;
                ops.Add(
                    new MidiRegionUpserted(
                        r with {
                            LengthTicks = cut,
                            Notes = [..r.Notes.Where(n => n.StartTick < cut)],
                            Controls = [..r.Controls.Where(e => e.Tick < cut)]
                        }
                    )
                );
            }

            if (end > to) {
                var cut = to - r.StartTick;
                var right = r with {
                    StartTick = to,
                    LengthTicks = end - to,
                    Notes = [..r.Notes.Where(n => n.StartTick >= cut).Select(n => n with { StartTick = n.StartTick - cut })],
                    Controls = [..r.Controls.Where(e => e.Tick >= cut).Select(e => e with { Tick = e.Tick - cut })]
                };
                ops.Add(new MidiRegionUpserted(r.StartTick < from ? right with { Id = ids.Next(IdPrefix.MidiRegion) } : right));
            }
        }

        return ops;
    }

    /// <summary>The first tick whose sample is at or after <paramref name="sample" />.</summary>
    public static long CeilingTick(TempoMap map, long sample, int rate) {
        var tick = Math.Max(0, map.TickAtSample(sample, rate));
        while (map.SampleAt(tick, rate) < sample) {
            tick++;
        }

        return tick;
    }

    /// <summary>A pass on the timeline: its start tick, file offset and length from there, and its end tick.</summary>
    static (long Tick, long Offset, long Length, long EndTick) Place(ProjectState state, TakePass pass) {
        var map = state.TempoMap;
        var rate = state.SampleRate;
        var tick = CeilingTick(map, Math.Max(0, pass.StartSample), rate);
        var shift = map.SampleAt(tick, rate) - pass.StartSample;
        var length = pass.Frames - shift;
        return (tick, pass.FileOffset + shift, length, map.TickAtSample(pass.StartSample + pass.Frames, rate));
    }

    static TakeFolder Folder(
        ProjectState state,
        Track track,
        AudioFile file,
        (long Tick, long Offset, long Length, long EndTick)[] placed,
        IIdSource ids
    ) {
        var takes = new Take[placed.Length];
        for (var i = 0; i < placed.Length; i++) {
            var (tick, offset, length, _) = placed[i];
            takes[i] = new(ids.Next(IdPrefix.Take), $"Take {i + 1}", file.Id, tick, offset, length);
        }

        var start = placed.Min(static p => p.Tick);
        var end = Math.Max(start + 1, placed.Max(static p => p.EndTick));
        var last = placed[^1];
        var lastEnd = Math.Min(Math.Max(last.EndTick, last.Tick + 1), end);
        return new(
            ids.Next(IdPrefix.TakeFolder),
            track.Id,
            track.Name,
            start,
            end - start,
            takes,
            [new(last.Tick, lastEnd, takes[^1].Id)],
            ProjectDefaults.CompCrossfade(state.SampleRate),
            false
        );
    }

    static AudioRegion Trimmed(AudioRegion r, long offset, long length) {
        var fadeIn = Math.Min(r.FadeIn, length);
        return r with { FileOffset = offset, Length = length, FadeIn = fadeIn, FadeOut = Math.Min(r.FadeOut, length - fadeIn) };
    }
}
