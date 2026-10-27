using Anode.Ipc.Segments;
using Anode.Ipc.Telemetry;

namespace Anode.Server.Engine;

/// <summary>One consistent read of the host's telemetry region: the header, each track's meter, and the master's.</summary>
public sealed record TelemetryFrame(TelemetryHeader Header, MeterEntry[] Tracks, MeterEntry Master);

/// <summary>
///     Reads the host's telemetry region (<see cref="TelemetryHeader" />), named in its hello: mapped read-only, read
///     under the seqlock (retried while the host is writing or wrote under the read).
/// </summary>
public sealed unsafe class TelemetryReader : IDisposable {
    const int Attempts = 16;

    readonly SharedSegment segment;

    TelemetryReader(SharedSegment segment) => this.segment = segment;

    /// <summary>Maps the region <paramref name="name" />; null when there is none or it cannot be mapped.</summary>
    public static TelemetryReader? TryOpen(string? name) {
        if (string.IsNullOrEmpty(name)) {
            return null;
        }

        try {
            return new(SharedSegment.Open(name, TelemetryLayout.Size));
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException
                                        or PlatformNotSupportedException) {
            return null;
        }
    }

    /// <summary>A consistent frame, or null when the region is not (yet) valid or the host kept writing.</summary>
    public TelemetryFrame? Read() {
        var header = (TelemetryHeader*)segment.Pointer;
        var meters = (MeterEntry*)(segment.Pointer + sizeof(TelemetryHeader));
        for (var attempt = 0; attempt < Attempts; attempt++) {
            var before = Volatile.Read(ref header->Sequence);
            if ((before & 1) != 0) {
                Thread.SpinWait(64);
                continue;
            }

            var copy = *header;
            if (copy.Magic != TelemetryHeader.MagicValue) {
                return null;
            }

            var count = Math.Clamp(copy.TrackCount, 0, TelemetryHeader.MaxTracks);
            var tracks = new MeterEntry[count];
            for (var i = 0; i < count; i++) {
                tracks[i] = meters[i];
            }

            var master = meters[count];
            Interlocked.MemoryBarrier();
            if (Volatile.Read(ref header->Sequence) == before) {
                return new(copy, tracks, master);
            }
        }

        return null;
    }

    public void Dispose() => segment.Dispose();
}

/// <summary>Writes a telemetry region, as the host does (and <see cref="FakeHost" />): a seqlock writer.</summary>
public sealed unsafe class TelemetryWriter : IDisposable {
    readonly SharedSegment segment;

    public string Name => segment.Name;

    TelemetryWriter(SharedSegment segment) => this.segment = segment;

    public static TelemetryWriter Create(string prefix, long id) =>
        new(SharedSegment.Create(SegmentWriter.DefaultBackend, SharedSegment.DefaultDirectory(), prefix, id,
            TelemetryLayout.Size));

    /// <summary>Publishes <paramref name="header" /> (its sequence is managed here) and the meters.</summary>
    public void Write(TelemetryHeader header, ReadOnlySpan<MeterEntry> tracks, MeterEntry master) {
        var h = (TelemetryHeader*)segment.Pointer;
        var meters = (MeterEntry*)(segment.Pointer + sizeof(TelemetryHeader));
        var count = Math.Min(tracks.Length, TelemetryHeader.MaxTracks);
        var sequence = h->Sequence;
        Volatile.Write(ref h->Sequence, sequence + 1);
        Interlocked.MemoryBarrier();
        header.Magic = TelemetryHeader.MagicValue;
        header.TrackCount = count;
        header.Sequence = sequence + 1;
        *h = header;
        for (var i = 0; i < count; i++) {
            meters[i] = tracks[i];
        }

        meters[count] = master;
        Interlocked.MemoryBarrier();
        Volatile.Write(ref h->Sequence, sequence + 2);
    }

    public void Dispose() => segment.Dispose();
}
