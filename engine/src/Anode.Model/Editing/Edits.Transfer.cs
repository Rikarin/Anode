using Anode.Contracts;
using System.Collections.Immutable;

namespace Anode.Model.Editing;

/// <summary>Inserts between strips (<see cref="TransferInsertCommand" />).</summary>
public static partial class Edits {
    /// <summary>
    ///     An insert to another strip, or a copy of it: see <see cref="TransferInsertCommand" />. A move within its strip
    ///     is a plain move.
    /// </summary>
    static List<DeltaOp> TransferInsert(ProjectState state, TransferInsertCommand c, IIdSource ids) {
        var (track, strip) = InsertStripOf(state, c.TrackId);
        var insert = InsertOf(strip, c.InsertId);
        if (!c.Copy && c.TrackId == c.ToTrackId) {
            return MoveInsert(state, new(insert.Id, c.Index ?? strip.Inserts.Count - 1, c.TrackId));
        }

        var (toTrack, toStrip) = InsertStripOf(state, c.ToTrackId);
        if (toStrip.Inserts.Count >= MaxInserts) {
            throw new EditException($"A channel strip holds at most {MaxInserts} inserts.");
        }

        var index = TransferIndex(c, toStrip.Inserts.Count);
        var landed = insert with {
            Id = c.Copy ? ids.Next(IdPrefix.Insert) : insert.Id,
            Sidechain = insert.Sidechain is { } source && IsSidechain(state, toTrack, source) ? source : null,
            Mastering = toTrack is null ? insert.Mastering : null
        };
        var inserts = toStrip.Inserts.ToImmutableList().Insert(index, landed);
        if (c.Copy) {
            return [Upsert(state, toTrack, toStrip with { Inserts = inserts })];
        }

        var ops = MovedLanes(state, c, $"insert:{insert.Id}:");
        ops.Add(Upsert(state, track, strip with { Inserts = [..strip.Inserts.Where(i => i.Id != insert.Id)] }));
        ops.Add(Upsert(state, toTrack, toStrip with { Inserts = inserts }));
        return ops;
    }

    static int TransferIndex(TransferInsertCommand c, int count) {
        var index = c.Index ?? count;
        return index >= 0 && index <= count
            ? index
            : throw new EditException($"Insert slot {index} is outside 0…{count}.");
    }

    /// <summary>The automation lanes of a moved slot (their targets start with <paramref name="target" />), on its new strip.</summary>
    static List<DeltaOp> MovedLanes(ProjectState state, TransferInsertCommand c, string target) => [
        ..LanesFor(state, c.TrackId, target).Select(l => new AutomationLaneUpserted(l with { TrackId = c.ToTrackId }))
    ];
}
