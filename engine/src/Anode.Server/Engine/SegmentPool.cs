using Anode.Ipc.Segments;
using System.Numerics;
using System.Security.Cryptography;

namespace Anode.Server.Engine;

/// <summary>
///     The shared-memory segments the server writes models into (docs/adr/0006-bulk-ipc.md): a pool of two whole-model
///     slots, mapped once and reused (written into the slot the host is not playing from), and one-off per-track
///     segments, disposed (their names removed) once the host has mapped them. A slot too small for the model is
///     replaced by a larger one under a new name.
/// </summary>
public sealed class SegmentPool : IDisposable {
    const long MinSlotBytes = 1 << 20;

    readonly SharedSegment?[] slots = new SharedSegment?[2];
    readonly Lock gate = new();
    long nextId;
    long nextVersion;
    int live = -1;

    /// <summary>A name prefix of this pool's own (macOS limits shared-memory names to 31 characters).</summary>
    public string Prefix { get; } = "an" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3));

    /// <summary>The next model version (they only grow).</summary>
    public long NextVersion() => Interlocked.Increment(ref nextVersion);

    /// <summary>The slot the host is not using, holding at least <paramref name="bytes" />.</summary>
    public (int Slot, SharedSegment Segment) RentSlot(long bytes) {
        lock (gate) {
            var slot = live == 0 ? 1 : 0;
            if (slots[slot] is not { } segment || segment.Length < bytes) {
                slots[slot]?.Dispose();
                var capacity = Math.Max(MinSlotBytes, (long)BitOperations.RoundUpToPowerOf2((ulong)bytes));
                segment = SharedSegment.Create(
                    SegmentWriter.DefaultBackend,
                    SharedSegment.DefaultDirectory(),
                    Prefix,
                    Interlocked.Increment(ref nextId),
                    capacity
                );
                slots[slot] = segment;
            }

            return (slot, segment);
        }
    }

    /// <summary>The host plays from <paramref name="slot" /> now (it acknowledged the model written there).</summary>
    public void Loaded(int slot) {
        lock (gate) {
            live = slot;
        }
    }

    /// <summary>
    ///     The host did not acknowledge the model in <paramref name="slot" />: it may still map it, so it is never
    ///     written again; the next model there gets a new segment.
    /// </summary>
    public void Abandon(int slot) {
        lock (gate) {
            slots[slot]?.Dispose();
            slots[slot] = null;
        }
    }

    /// <summary>A new host has none of the slots mapped: either may be written.</summary>
    public void Reset() {
        lock (gate) {
            live = -1;
        }
    }

    /// <summary>A new per-track segment holding <paramref name="tracks" />; the caller disposes it.</summary>
    public SharedSegment WriteTracks(
        IReadOnlyList<TrackModel> tracks,
        long version,
        long lengthSamples,
        IReadOnlyList<CellModel>? cells = null
    ) =>
        SegmentWriter.Write(
            tracks,
            version,
            lengthSamples,
            Prefix,
            Interlocked.Increment(ref nextId),
            true,
            cells: cells
        );

    public void Dispose() {
        lock (gate) {
            for (var i = 0; i < slots.Length; i++) {
                slots[i]?.Dispose();
                slots[i] = null;
            }
        }
    }
}
