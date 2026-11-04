using Anode.Contracts;
using Anode.Model.Compile;

namespace Anode.Model.Editing;

/// <summary>Region loops (<see cref="Loops" />): their length, and turning repeats into regions of their own.</summary>
public static partial class Edits {
    static void CheckLoop(long? loopTicks) {
        if (loopTicks is < 0 or > MaxTick) {
            throw new EditException($"A region's loop is 0…{MaxTick} ticks, not {loopTicks}.");
        }
    }

    /// <summary>A region that loops cannot be cut as it plays: its repeats need to become regions first.</summary>
    static void RefuseLooped(long loopTicks, string id, string edit) {
        if (loopTicks > 0) {
            throw new EditException($"Region '{id}' loops: convert its loops to regions to {edit} it.");
        }
    }

    /// <summary>
    ///     Replaces the loops of the regions with real regions: each repeat becomes a copy (the last one cut where it
    ///     stopped playing), so what plays does not change. Regions that do not loop are left alone.
    /// </summary>
    static List<DeltaOp> ConvertLoops(ProjectState state, ConvertLoopsCommand c, IIdSource ids) {
        if (c.RegionIds is not { Count: > 0 }) {
            throw new EditException("Name at least one region whose loops to convert.");
        }

        var ops = new List<DeltaOp>();
        var next = state.LastEventId;
        foreach (var id in c.RegionIds.Distinct(StringComparer.Ordinal)) {
            if (Timeline.Region(state, id) is not { } item) {
                throw new EditException($"No region '{id}'.");
            }

            ops.AddRange(LoopRegions(state, item, ids, ref next));
        }

        return ops;
    }

    /// <summary>The operations that turn one region's loop into regions; none for a kind or a region without one.</summary>
    static List<DeltaOp> LoopRegions(ProjectState state, TimelineItem item, IIdSource ids, ref long next) {
        var ops = new List<DeltaOp>();
        switch (item.Kind) {
            case TimelineKind.AudioRegion when state.Regions[item.Id] is { LoopTicks: > 0 } audio:
                ops.Add(new RegionUpserted(audio with { LoopTicks = 0 }));
                ops.AddRange(
                    Loops.Repeats(state, audio)
                        .Select(r => new RegionUpserted(r with { Id = ids.Next(IdPrefix.Region) }))
                );
                break;
            case TimelineKind.MidiRegion when state.MidiRegions[item.Id] is { LoopTicks: > 0 } midi:
                ops.Add(new MidiRegionUpserted(midi with { LoopTicks = 0 }));
                foreach (var repeat in Loops.Played(state, midi).Skip(1)) {
                    var copy = Timeline.Copy(state, item, repeat.StartTick - midi.StartTick, null, ids, ref next);
                    var region = ((MidiRegionUpserted)copy).Region;
                    ops.Add(
                        new MidiRegionUpserted(region with { LengthTicks = repeat.LengthTicks, LoopTicks = 0 })
                    );
                }

                break;
            case TimelineKind.PatternRegion when state.PatternRegions[item.Id] is { LoopTicks: > 0 } pattern:
                ops.Add(new PatternRegionUpserted(pattern with { LoopTicks = 0 }));
                ops.AddRange(
                    Loops.Played(state, pattern)
                        .Skip(1)
                        .Select(r => new PatternRegionUpserted(r with { Id = ids.Next(IdPrefix.PatternRegion) }))
                );
                break;
        }

        return ops;
    }
}
