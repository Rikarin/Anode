using Anode.Ipc.Telemetry;
using System.Runtime.InteropServices;

namespace Anode.Engine;

/// <summary>
///     Writes the telemetry region (<see cref="TelemetryHeader" />, docs/02-phase-1.md "Telemetry") about 60 times a
///     second on its own thread, from what the audio thread publishes: the transport, the callback counters and each
///     strip's peaks since the previous write; and (Phase 11) the Environment's monitors and faders and the Mastering
///     Assistant's loudness and gain reduction. Readers (the server) copy it under the seqlock. Allocation-free.
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

    ChannelMeterEntry* ChannelMeters => (ChannelMeterEntry*)(region + TelemetryLayout.ChannelMetersOffset);

    EnvironmentMonitorEntry* Monitors => (EnvironmentMonitorEntry*)(region + TelemetryLayout.MonitorsOffset);

    EnvironmentFaderEntry* Faders => (EnvironmentFaderEntry*)(region + TelemetryLayout.FadersOffset);

    MasteringEntry* Mastering => (MasteringEntry*)(region + TelemetryLayout.MasteringOffset);

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
            meters[i] = Entry(tracks[i].Id, tracks[i].Strip, i);
        }

        meters[count] = Entry(-1, engine.Master, count);
        // Phase 11: the Environment's monitors and faders in their slots, its drops, and the Mastering Assistant.
        h->EnvironmentDropped = engine.EnvironmentDropped;
        engine.Environment.Publish(
            new(Monitors, TelemetryLayout.MaxMonitors),
            new(Faders, TelemetryLayout.MaxFaders)
        );
        *Mastering = engine.MasteringReading();
        Volatile.Write(ref h->Sequence, h->Sequence + 1);
    }

    /// <summary>
    ///     A strip's meter entry, its channel peaks (a surround meter) into the channel table at
    ///     <paramref name="index" />.
    /// </summary>
    MeterEntry Entry(int trackId, Strip strip, int index) {
        var peaks = new Span<float>(ChannelMeters[index].Peaks, ChannelMeterEntry.MaxChannels);
        var (left, right) = strip.TakePeaks(peaks, out var channels);
        return new() { TrackId = trackId, PeakLeft = left, PeakRight = right, Channels = channels > 2 ? channels : 0 };
    }

    /// <summary>
    ///     Reader side (Phase 10): a consistent copy of the header, its meters and their channel meters (as many as
    ///     <paramref name="channelMeters" /> holds).
    /// </summary>
    public static bool TryRead(
        byte* region,
        out TelemetryHeader header,
        Span<MeterEntry> meters,
        Span<ChannelMeterEntry> channelMeters
    ) {
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
            var channels = Math.Min(count, channelMeters.Length);
            new ReadOnlySpan<ChannelMeterEntry>(region + TelemetryLayout.ChannelMetersOffset, channels)
                .CopyTo(channelMeters);
            Interlocked.MemoryBarrier();
            if (Volatile.Read(ref h->Sequence) == before) {
                return true;
            }
        }

        header = default;
        return false;
    }

    /// <summary>
    ///     Reader side (Phase 11): a consistent copy of the Environment's monitor and fader slots and the Mastering
    ///     Assistant's entry.
    /// </summary>
    public static bool TryRead(
        byte* region,
        Span<EnvironmentMonitorEntry> monitors,
        Span<EnvironmentFaderEntry> faders,
        out MasteringEntry mastering
    ) {
        var h = (TelemetryHeader*)region;
        for (var attempt = 0; attempt < 64; attempt++) {
            var before = Volatile.Read(ref h->Sequence);
            if ((before & 1) != 0) {
                Thread.SpinWait(16);
                continue;
            }

            new ReadOnlySpan<EnvironmentMonitorEntry>(
                region + TelemetryLayout.MonitorsOffset,
                Math.Min(monitors.Length, TelemetryLayout.MaxMonitors)
            ).CopyTo(monitors);
            new ReadOnlySpan<EnvironmentFaderEntry>(
                region + TelemetryLayout.FadersOffset,
                Math.Min(faders.Length, TelemetryLayout.MaxFaders)
            ).CopyTo(faders);
            mastering = *(MasteringEntry*)(region + TelemetryLayout.MasteringOffset);
            Interlocked.MemoryBarrier();
            if (Volatile.Read(ref h->Sequence) == before) {
                return true;
            }
        }

        mastering = default;
        return false;
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
