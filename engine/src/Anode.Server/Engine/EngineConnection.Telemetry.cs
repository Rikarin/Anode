using Anode.Contracts;
using Anode.Ipc.Telemetry;
using Anode.Model;
using Anode.Model.Time;
using Anode.Server.Projects;
using Anode.Server.Realtime;

namespace Anode.Server.Engine;

public sealed partial class EngineConnection {
    /// <summary>Sends the host what it is owed, in order: the project's changes first, then each queued message.</summary>
    async Task PumpAsync(Connection connection, CancellationToken cancellationToken) {
        var sync = new HostSync(connection.Link, pool, logger, players);
        await foreach (var message in connection.Work.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) {
            await sync.SyncAsync(session.View, Volatile.Read(ref thru), cancellationToken).ConfigureAwait(false);
            if (message is not null) {
                await connection.Link.SendAsync(message, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Reads the telemetry region about 30 times a second and publishes the playhead and meters.</summary>
    async Task PublishAsync(TelemetryReader telemetry, CancellationToken cancellationToken) {
        using var timer = new PeriodicTimer(TelemetryInterval);
        TransportStatus? last = null;
        var metersLive = false;
        var cores = new CoreLoads();
        var ticks = 0;
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false)) {
            if (telemetry.Read() is not { } frame) {
                continue;
            }

            PublishLoad(frame.Header, cores, ref ticks);
            var view = session.View;
            var now = Status(frame.Header, view);
            Volatile.Write(ref transport, now);
            clock.Update(now.State != TransportState.Stopped, now.PositionTick);
            // While it moves, and once more when it stops or is located.
            var moving = now.State != TransportState.Stopped;
            if (moving || last is null || last.State != now.State || last.PositionSamples != now.PositionSamples) {
                hub.Publish(new TransportEvent(now));
            }

            last = now;
            ExpireDisplays();
            var readings = Interlocked.Exchange(ref pendingReadings, null);
            if (view is null) {
                continue;
            }

            var (meters, live) = Meters(frame, view);
            MetersRead?.Invoke(meters);
            if (moving || live || metersLive || readings is { Count: > 0 }) {
                hub.Publish(readings is { Count: > 0 } ? meters with { Readings = readings } : meters);
                metersLive = live;
            }

            PublishPhase11(frame, view, moving);
        }
    }

    static TransportStatus Status(TelemetryHeader header, OpenProjectView? view) {
        var position = header.PositionSamples;
        var rate = view?.State.SampleRate ?? (header.SampleRate > 0 ? header.SampleRate : ProjectDefaults.SampleRate);
        var map = view?.State.PlaybackMap ?? TempoMap.Constant(ProjectDefaults.Tempo);
        var state = header.State switch {
            TelemetryTransportState.Playing => TransportState.Playing,
            TelemetryTransportState.Recording => TransportState.Recording,
            _ => TransportState.Stopped
        };
        return new(state, position, map.TickAtSample(position, rate), rate);
    }

    /// <summary>Every <see cref="PerformanceEveryTicks" /> reads: the engine's load and the machine's cores.</summary>
    void PublishLoad(in TelemetryHeader header, CoreLoads cores, ref int ticks) {
        if (++ticks < PerformanceEveryTicks) {
            return;
        }

        ticks = 0;
        hub.Publish(
            new PerformanceEvent(
                new(
                    header.AudioLoad,
                    header.AudioPeakLoad,
                    cores.Sample(),
                    header.DiskLoad,
                    header.Overloads,
                    header.Underruns
                )
            )
        );
    }

    /// <summary>A meter's reading: its peaks, and the levels its type publishes.</summary>
    static TrackMeter Meter(string id, MeterEntry entry, IReadOnlyList<float>? peaks) {
        var rms = (entry.Levels & MeterEntry.HasRms) != 0;
        return new(
            id,
            entry.PeakLeft,
            entry.PeakRight,
            peaks,
            rms ? entry.RmsLeft : null,
            rms ? entry.RmsRight : null,
            (entry.Levels & MeterEntry.HasLufs) != 0 ? entry.Lufs : null
        );
    }

    /// <summary>The frame's meters by domain track id, and whether any shows a level.</summary>
    static (MetersEvent Meters, bool Live) Meters(TelemetryFrame frame, OpenProjectView view) {
        var meters = new List<TrackMeter>(frame.Tracks.Length);
        var live = frame.Master.PeakLeft > 0 || frame.Master.PeakRight > 0;
        for (var i = 0; i < frame.Tracks.Length; i++) {
            var entry = frame.Tracks[i];
            if (view.Ids.TrackOf(entry.TrackId) is { } id) {
                var peaks = i < frame.TrackPeaks.Length ? frame.TrackPeaks[i] : null;
                meters.Add(Meter(id, entry, peaks));
                live |= entry.PeakLeft > 0 || entry.PeakRight > 0 || peaks?.Any(static p => p > 0) == true;
            }
        }

        var master = Meter("master", frame.Master, frame.MasterPeaks);
        live |= frame.MasterPeaks?.Any(static p => p > 0) == true;
        return (new(meters, master), live);
    }
}
