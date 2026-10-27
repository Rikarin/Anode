using Anode.Ipc.Telemetry;
using System.Runtime.InteropServices;

namespace Anode.Engine;

/// <summary>
///     Writes the telemetry region (<see cref="TelemetryHeader" />, docs/02-phase-1.md "Telemetry") about 60 times a
///     second on its own thread, from what the audio thread publishes: the transport, the callback counters and each
///     strip's peaks since the previous write. Readers (the server) copy it under the seqlock. Allocation-free.
/// </summary>
public sealed unsafe class TelemetryWriter : IDisposable {
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1.0 / 60);

    readonly AudioEngine engine;
    readonly byte* region;
    readonly Func<long> overloads;
    Thread? thread;
    volatile bool running;

    /// <param name="engine">The engine to report.</param>
    /// <param name="region">At least <see cref="TelemetryLayout.Size" /> writable bytes, shared with readers.</param>
    /// <param name="overloads">The device's dropouts so far (driver overloads, or the null clock's late ticks).</param>
    public TelemetryWriter(AudioEngine engine, byte* region, Func<long> overloads) {
        this.engine = engine;
        this.region = region;
        this.overloads = overloads;
        NativeMemory.Clear(region, (nuint)TelemetryLayout.Size);
        Header->SampleRate = engine.SampleRate;
        Volatile.Write(ref Header->Magic, TelemetryHeader.MagicValue); // last, so a reader never sees half of it
    }

    TelemetryHeader* Header => (TelemetryHeader*)region;

    MeterEntry* Meters => (MeterEntry*)(region + sizeof(TelemetryHeader));

    /// <summary>Writes every <see cref="Interval" /> on a background thread until disposed.</summary>
    public void Start() {
        running = true;
        thread = new(Run) { IsBackground = true, Name = "anode telemetry", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    /// <summary>One write: the transport as of the last callback and the peaks since the previous write.</summary>
    public void Write() {
        var snapshot = engine.Snapshot;
        var tracks = engine.Root.Tracks;
        var count = Math.Min(tracks.Length, TelemetryHeader.MaxTracks);
        var h = Header;
        Interlocked.Increment(ref h->Sequence); // odd: a write is in progress (and a full fence)
        h->TrackCount = count;
        h->PositionSamples = snapshot.Position;
        h->HostTimeNs = snapshot.HostTimeNs;
        h->State = snapshot.State switch {
            TransportState.Playing => TelemetryTransportState.Playing,
            TransportState.Recording => TelemetryTransportState.Recording,
            _ => TelemetryTransportState.Stopped
        };
        h->SampleRate = engine.SampleRate;
        h->Callbacks = snapshot.Callbacks;
        h->Overloads = overloads();
        h->Underruns = snapshot.Underruns;
        h->ModelVersion = snapshot.ModelVersion;
        var meters = Meters;
        for (var i = 0; i < count; i++) {
            var (left, right) = tracks[i].Strip.TakePeaks();
            meters[i] = new() { TrackId = tracks[i].Id, PeakLeft = left, PeakRight = right };
        }

        var (masterLeft, masterRight) = engine.Master.TakePeaks();
        meters[count] = new() { TrackId = -1, PeakLeft = masterLeft, PeakRight = masterRight };
        Volatile.Write(ref h->Sequence, h->Sequence + 1);
    }

    /// <summary>Reader side, for tests and in-process readers: a consistent copy of the header and its meters.</summary>
    public static bool TryRead(byte* region, out TelemetryHeader header, Span<MeterEntry> meters) {
        var h = (TelemetryHeader*)region;
        for (var attempt = 0; attempt < 64; attempt++) {
            var before = Volatile.Read(ref h->Sequence);
            if ((before & 1) != 0) {
                Thread.SpinWait(16);
                continue;
            }

            header = *h;
            var count = Math.Min(Math.Min(header.TrackCount, TelemetryHeader.MaxTracks) + 1, meters.Length);
            new ReadOnlySpan<MeterEntry>(region + sizeof(TelemetryHeader), count).CopyTo(meters);
            Interlocked.MemoryBarrier();
            if (Volatile.Read(ref h->Sequence) == before) {
                return true;
            }
        }

        header = default;
        return false;
    }

    void Run() {
        var next = DateTime.UtcNow;
        while (running) {
            Write();
            next += Interval;
            var wait = next - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) {
                Thread.Sleep(wait);
            } else {
                next = DateTime.UtcNow;
            }
        }
    }

    public void Dispose() {
        running = false;
        thread?.Join();
    }
}
