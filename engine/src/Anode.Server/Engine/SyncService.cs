using Anode.Contracts;
using Anode.Ipc;
using Anode.Model.History;
using Anode.Server.Projects;
using Anode.Server.Realtime;

namespace Anode.Server.Engine;

/// <summary>
///     Sync (docs/09-phase-8.md, "Sync"): the open project's <c>settings.sync</c> goes to the host as a
///     <see cref="SetSyncMessage" /> when a project opens, when the settings change, and on every host connection
///     (internal sync when no project is open); the host's <see cref="SyncStateMessage" />s become the live
///     <see cref="SyncState" /> (<c>GET /project/sync</c>) and <see cref="SyncEvent" />s, at most
///     <see cref="MaxEventsPerSecond" /> a second (the last change always goes out).
///     <para>
///         Following Link, the session's tempo becomes the project's (<see cref="SetTempoCommand" />, the tempo at the
///         project start), so regions, the grid and the LCD follow it and a save keeps it. The writes of one burst of
///         tempo changes (no more than <see cref="LinkTempoGap" /> apart) coalesce into one undo step. It cannot loop:
///         the host pushes the model's tempo to Link only when the model's changes and differs from Link's. MIDI Clock's
///         tempo, a smoothed estimate, is not written (docs/09-phase-8.md, "Sync"); the host's transport follows it.
///     </para>
/// </summary>
public sealed class SyncService(ProjectSession session, EngineConnection engine, EventHub hub, TimeProvider time)
    : IHostedService, IDisposable {
    public const int MaxEventsPerSecond = 10;

    static readonly TimeSpan EventInterval = TimeSpan.FromSeconds(1.0 / MaxEventsPerSecond);

    /// <summary>Link tempo writes this close together are one undo step.</summary>
    public static readonly TimeSpan LinkTempoGap = TimeSpan.FromSeconds(2);

    /// <summary>The project's tempo is taken to match Link's within this (bpm; the host's own threshold).</summary>
    const double TempoTolerance = 0.005;

    readonly Lock gate = new();
    SetSyncMessage? sent;
    long sentSession = -1;
    SyncState? reported;
    SyncState? announced;
    DateTimeOffset announcedAt;
    ITimer? pending;
    long linkBurst;
    DateTimeOffset linkWrittenAt;

    /// <summary>The live sync state of the open project: the host's, or what its settings imply until it reports.</summary>
    public SyncState State() {
        var view = session.View ?? throw ProjectException.NoProject();
        lock (gate) {
            return reported is { } state && state.Source == view.State.Settings.Sync.Source
                ? state
                : Implied(view.State.Settings.Sync);
        }
    }

    /// <summary>The state before the host has reported: only internal sync is locked without it.</summary>
    static SyncState Implied(SyncSettings settings) =>
        new(settings.Source, settings.Source == SyncSource.Internal, null, 0);

    public Task StartAsync(CancellationToken cancellationToken) {
        session.Changed += OnChanged;
        engine.HostConnected += OnConnected;
        engine.SyncStateArrived += OnState;
        engine.Changed += OnEngine;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) {
        session.Changed -= OnChanged;
        engine.HostConnected -= OnConnected;
        engine.SyncStateArrived -= OnState;
        engine.Changed -= OnEngine;
        return Task.CompletedTask;
    }

    public void Dispose() {
        lock (gate) {
            pending?.Dispose();
            pending = null;
        }
    }

    void OnConnected() {
        lock (gate) {
            sent = null;
            Push();
        }
    }

    void OnChanged() {
        lock (gate) {
            Push();
        }
    }

    /// <summary>A lost host's state is no longer known.</summary>
    void OnEngine(EngineStatus status) {
        if (status.State == EngineState.Disconnected) {
            lock (gate) {
                reported = null;
            }
        }
    }

    /// <summary>Sends the settings when they differ from those last sent (or another project opened).</summary>
    void Push() {
        var view = session.View;
        var message = Message(view?.State.Settings.Sync ?? SyncSettings.Default);
        var sessionId = view?.Session ?? -1;
        if (sent is not null && sentSession == sessionId && Same(sent, message)) {
            return;
        }

        if (engine.Post(message)) {
            sent = message;
            sentSession = sessionId;
        }
    }

    static bool Same(SetSyncMessage a, SetSyncMessage b) =>
        a with { Destinations = None } == b with { Destinations = None }
        && a.Destinations.SequenceEqual(b.Destinations);

    static readonly EngineSyncDestination[] None = [];

    /// <summary>The contract's settings in the host's terms.</summary>
    public static SetSyncMessage Message(SyncSettings settings) =>
        new(
            settings.Source switch {
                SyncSource.MidiClock => EngineSyncSource.MidiClock,
                SyncSource.Mtc => EngineSyncSource.Mtc,
                SyncSource.Link => EngineSyncSource.Link,
                _ => EngineSyncSource.Internal
            },
            [..settings.Destinations.Select(static d => new EngineSyncDestination(d.Port, d.Clock, d.Mtc, d.DelayMs))],
            settings.FrameRate switch {
                MtcFrameRate.Fps24 => EngineMtcRate.Fps24,
                MtcFrameRate.Fps2997Drop => EngineMtcRate.Fps2997Drop,
                MtcFrameRate.Fps30 => EngineMtcRate.Fps30,
                _ => EngineMtcRate.Fps25
            },
            settings.StartOffsetSeconds,
            settings.LinkStartStop,
            settings.ClockInPort,
            settings.MtcInPort
        );

    void OnState(SyncStateMessage message) {
        var state = new SyncState(
            message.Source switch {
                EngineSyncSource.MidiClock => SyncSource.MidiClock,
                EngineSyncSource.Mtc => SyncSource.Mtc,
                EngineSyncSource.Link => SyncSource.Link,
                _ => SyncSource.Internal
            },
            message.Locked,
            message.ExternalTempo is { } tempo && double.IsFinite(tempo) ? tempo : null,
            Math.Max(0, message.LinkPeers)
        );
        lock (gate) {
            reported = state;
            Announce();
        }

        FollowLinkTempo(state);
    }

    /// <summary>Link's tempo, when the project follows Link and its tempo differs, written into the project.</summary>
    void FollowLinkTempo(SyncState state) {
        if (state is not { Source: SyncSource.Link, ExternalTempo: { } tempo }
            || session.View is not { } view
            || view.State.Settings.Sync.Source != SyncSource.Link) {
            return;
        }

        var bpm = Math.Round(Math.Clamp(tempo, 5, 999), 3);
        if (Math.Abs(view.State.Settings.Tempo - bpm) <= TempoTolerance) {
            return;
        }

        string key;
        lock (gate) {
            var now = time.GetUtcNow();
            if (now - linkWrittenAt > LinkTempoGap) {
                linkBurst++;
            }

            linkWrittenAt = now;
            key = $"{EditHistory.GesturePrefix}link-tempo|{view.Session}|{linkBurst}";
        }

        try {
            session.Apply(new([new SetTempoCommand(bpm)]), null, key);
        } catch (ProjectException) {
            // A project that cannot take the tempo now (closed meanwhile): the next report tries again.
        }
    }

    /// <summary>Publishes the reported state when it changed, at most every <see cref="EventInterval" />.</summary>
    void Announce() {
        if (reported is not { } state || state == announced) {
            return;
        }

        var wait = announcedAt + EventInterval - time.GetUtcNow();
        if (wait > TimeSpan.Zero) {
            pending ??= time.CreateTimer(
                _ => {
                    lock (gate) {
                        pending?.Dispose();
                        pending = null;
                        Announce();
                    }
                },
                null,
                wait,
                Timeout.InfiniteTimeSpan
            );
            return;
        }

        announced = state;
        announcedAt = time.GetUtcNow();
        hub.Publish(new SyncEvent(state));
    }
}
