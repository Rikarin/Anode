using Anode.Ipc.Segments;
using Anode.Ipc.Telemetry;

namespace Anode.Server.Engine;

/// <summary>
///     One consistent read of the host's telemetry region: the header, each track's meter, and the master's; and
///     (Phase 10) the per-channel peaks of each meter wider than stereo (<see cref="TrackPeaks" /> by track index,
///     <see cref="MasterPeaks" />), null for the others; (Phase 11) the Environment's monitors and faders in use (their
///     slots' entries with an object) and the Mastering Assistant's entry.
/// </summary>
public sealed record TelemetryFrame(TelemetryHeader Header, MeterEntry[] Tracks, MeterEntry Master) {
    public float[]?[] TrackPeaks { get; init; } = [];

    public float[]? MasterPeaks { get; init; }

    public EnvironmentMonitorEntry[] Monitors { get; init; } = [];

    public EnvironmentFaderEntry[] Faders { get; init; } = [];

    public MasteringEntry? Mastering { get; init; }
}

/// <summary>
///     Reads the host's telemetry region (<see cref="TelemetryHeader" />), named in its hello: mapped read-only, read
///     under the seqlock (retried while the host is writing or wrote under the read).
/// </summary>
public sealed unsafe class TelemetryReader : IDisposable {
    const int Attempts = 16;

    readonly SharedSegment segment;

    TelemetryReader(SharedSegment segment) {
        this.segment = segment;
    }

    /// <summary>Maps the region <paramref name="name" />; null when there is none or it cannot be mapped.</summary>
    public static TelemetryReader? TryOpen(string? name) {
        if (string.IsNullOrEmpty(name)) {
            return null;
        }

        try {
            return new(SharedSegment.Open(name, TelemetryLayout.Size));
        } catch (Exception e) when (e is IOException
                                        or UnauthorizedAccessException
                                        or InvalidDataException
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
            var channels = (ChannelMeterEntry*)(segment.Pointer + TelemetryLayout.ChannelMetersOffset);
            var peaks = new float[]?[count];
            for (var i = 0; i < count; i++) {
                peaks[i] = Peaks(tracks[i], channels + i);
            }

            var masterPeaks = Peaks(master, channels + count);
            var (monitors, faders, mastering) = Phase11(segment.Pointer);
            Interlocked.MemoryBarrier();
            if (Volatile.Read(ref header->Sequence) == before) {
                return new(copy, tracks, master) {
                    TrackPeaks = peaks,
                    MasterPeaks = masterPeaks,
                    Monitors = monitors,
                    Faders = faders,
                    Mastering = mastering
                };
            }
        }

        return null;
    }

    /// <summary>The Environment's monitor and fader entries in use, and the assistant's entry when it is active.</summary>
    static (EnvironmentMonitorEntry[], EnvironmentFaderEntry[], MasteringEntry?) Phase11(byte* region) {
        var monitors = (EnvironmentMonitorEntry*)(region + TelemetryLayout.MonitorsOffset);
        var faders = (EnvironmentFaderEntry*)(region + TelemetryLayout.FadersOffset);
        var inUse = new List<EnvironmentMonitorEntry>();
        for (var i = 0; i < TelemetryLayout.MaxMonitors; i++) {
            if (monitors[i].Object != 0) {
                inUse.Add(monitors[i]);
            }
        }

        var moved = new List<EnvironmentFaderEntry>();
        for (var i = 0; i < TelemetryLayout.MaxFaders; i++) {
            if (faders[i].Object != 0) {
                moved.Add(faders[i]);
            }
        }

        var mastering = *(MasteringEntry*)(region + TelemetryLayout.MasteringOffset);
        return ([..inUse], [..moved], mastering.Active != 0 ? mastering : null);
    }

    /// <summary>A meter's channel peaks when it has more than two channels, else null.</summary>
    static float[]? Peaks(MeterEntry entry, ChannelMeterEntry* channels) {
        if (entry.Channels <= 2) {
            return null;
        }

        var n = Math.Min(entry.Channels, ChannelMeterEntry.MaxChannels);
        var peaks = new float[n];
        for (var c = 0; c < n; c++) {
            peaks[c] = channels->Peaks[c];
        }

        return peaks;
    }

    public void Dispose() => segment.Dispose();
}

/// <summary>Writes a telemetry region, as the host does (and <see cref="FakeHost" />): a seqlock writer.</summary>
public sealed unsafe class TelemetryWriter : IDisposable {
    readonly SharedSegment segment;

    public string Name => segment.Name;

    TelemetryWriter(SharedSegment segment) {
        this.segment = segment;
    }

    public static TelemetryWriter Create(string prefix, long id) =>
        new(
            SharedSegment.Create(
                SegmentWriter.DefaultBackend,
                SharedSegment.DefaultDirectory(),
                prefix,
                id,
                TelemetryLayout.Size
            )
        );

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

    /// <summary>
    ///     Phase 11: the Environment's monitor and fader slots and the Mastering Assistant's entry, which the next read
    ///     sees (tests and <see cref="FakeHost" />; the host writes them with its meters).
    /// </summary>
    public void Write(
        ReadOnlySpan<EnvironmentMonitorEntry> monitors,
        ReadOnlySpan<EnvironmentFaderEntry> faders,
        MasteringEntry mastering
    ) {
        var m = (EnvironmentMonitorEntry*)(segment.Pointer + TelemetryLayout.MonitorsOffset);
        for (var i = 0; i < Math.Min(monitors.Length, TelemetryLayout.MaxMonitors); i++) {
            m[i] = monitors[i];
        }

        var f = (EnvironmentFaderEntry*)(segment.Pointer + TelemetryLayout.FadersOffset);
        for (var i = 0; i < Math.Min(faders.Length, TelemetryLayout.MaxFaders); i++) {
            f[i] = faders[i];
        }

        *(MasteringEntry*)(segment.Pointer + TelemetryLayout.MasteringOffset) = mastering;
    }

    public void Dispose() => segment.Dispose();
}
