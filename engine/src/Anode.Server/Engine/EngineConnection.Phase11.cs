using Anode.Contracts;
using Anode.Ipc;
using Anode.Ipc.Telemetry;
using Anode.Model.Compile;
using Anode.Model.Editing;
using Anode.Server.Projects;

namespace Anode.Server.Engine;

/// <summary>
///     Phase 11 (docs/12-phase-11.md): live MIDI into an on-screen Environment object, the master's render before its
///     Mastering Assistant (for the analysis job), and the Environment's monitors and faders and the assistant's
///     readings from the telemetry as <see cref="EnvironmentMonitorEvent" />s, <see cref="EnvironmentFaderEvent" />s
///     and <see cref="MasteringEvent" />s at the meter rate.
/// </summary>
public sealed partial class EngineConnection {
    readonly Dictionary<int, long> monitorCounts = [];
    readonly Dictionary<int, float> faderValues = [];
    (MidiEnvironment Environment, IReadOnlyList<EnvironmentObject> Order)? environmentOrder;

    /// <summary>
    ///     Plays live MIDI into an on-screen Environment object (a keyboard, or a fader's move): 404 for an object that is
    ///     neither, 422 for events that are not channel messages.
    /// </summary>
    public void EnvironmentInput(string objectId, EnvironmentInputRequest request) {
        var view = session.View ?? throw ProjectException.NoProject();
        var order = EnvironmentCompiler.Order(view.State.Environment);
        var index = -1;
        for (var i = 0; i < order.Count; i++) {
            if (order[i].Id == objectId) {
                index = i;
            }
        }

        if (index < 0 || order[index].Kind is not (EnvironmentObjectKind.Keyboard or EnvironmentObjectKind.Fader)) {
            throw ProjectException.NotFound($"No keyboard or fader '{objectId}' in the Environment.");
        }

        Send(new EnvironmentMidiMessage(index, LiveEvents(request?.Events)));
    }

    /// <summary>Live MIDI as the host takes it: 1…<see cref="MaxLiveEvents" /> channel messages, else 422.</summary>
    static EngineMidiEvent[] LiveEvents(IReadOnlyList<LiveMidiEvent>? events) {
        if (events is not { Count: > 0 and <= MaxLiveEvents }) {
            throw ProjectException.Unprocessable($"Send 1…{MaxLiveEvents} MIDI events.");
        }

        var engine = new EngineMidiEvent[events.Count];
        for (var i = 0; i < engine.Length; i++) {
            if (events[i] is not { Status: >= 0x80 and <= 0xEF, Data1: >= 0 and <= 127, Data2: >= 0 and <= 127 } e) {
                throw ProjectException.Unprocessable(
                    "A MIDI event is a channel message: status 0x80…0xEF, data bytes 0…127."
                );
            }

            engine[i] = new(e.Status, e.Data1, e.Data2);
        }

        return engine;
    }

    /// <summary>
    ///     Renders the master as it reaches Mastering Assistant <paramref name="insertId" /> (docs/12-phase-11.md): the
    ///     host's master strip is swapped for one with the assistant and every insert after it bypassed, at unity gain,
    ///     for the render, then restored to the project's (the render is offline: nothing is heard). Without the
    ///     assistant on the master, the master's output as it is.
    /// </summary>
    public async Task<RenderFinishedMessage> RenderBeforeAsync(
        string? insertId,
        RenderMessage message,
        Action<double> progress,
        CancellationToken cancellationToken
    ) {
        var view = session.View ?? throw ProjectException.NoProject();
        var index = insertId is null ? -1 : view.State.Master.Inserts.ToList().FindIndex(i => i.Id == insertId);
        if (index < 0) {
            return await RenderAsync(message, progress, cancellationToken).ConfigureAwait(false);
        }

        var master = HostSync.Master(view, false);
        Send(
            new SetChannelMessage(
                -1,
                master with {
                    Gain = 1,
                    Pan = 0,
                    Mute = false,
                    Inserts = [..(master.Inserts ?? []).Select((x, i) => i >= index ? x with { Bypassed = true } : x)]
                }
            )
        );
        try {
            return await RenderAsync(message, progress, cancellationToken).ConfigureAwait(false);
        } finally {
            // The project's master again (as it is now: an edit may have come in the meantime).
            if (session.View is { } now) {
                Post(new SetChannelMessage(-1, HostSync.Master(now, false)));
            }
        }
    }

    /// <summary>
    ///     The Environment's monitors and faders that changed since the last frame, and the master's assistant while the
    ///     transport plays.
    /// </summary>
    void PublishPhase11(TelemetryFrame frame, OpenProjectView view, bool moving) {
        if (frame.Monitors.Length + frame.Faders.Length > 0) {
            var order = Order(view.State.Environment);
            PublishMonitors(frame.Monitors, order);
            PublishFaders(frame.Faders, order);
        }

        if (moving
            && frame.Mastering is { Active: not 0 } m
            && Edits.MasteringAssistantOf(view.State) is { } assistant) {
            hub.Publish(
                new MasteringEvent(
                    assistant.Id,
                    m.MomentaryLufs,
                    m.ShortTermLufs,
                    m.IntegratedLufs,
                    m.TruePeakDbtp,
                    m.CompressorReductionDb,
                    m.LimiterReductionDb
                )
            );
        }
    }

    void PublishMonitors(EnvironmentMonitorEntry[] monitors, IReadOnlyList<EnvironmentObject> order) {
        foreach (var monitor in monitors) {
            if (monitor.Object > 0
                && monitor.Object <= order.Count
                && monitorCounts.GetValueOrDefault(monitor.Object) != monitor.Count) {
                monitorCounts[monitor.Object] = monitor.Count;
                hub.Publish(new EnvironmentMonitorEvent(order[monitor.Object - 1].Id, Monitored(monitor)));
            }
        }
    }

    void PublishFaders(EnvironmentFaderEntry[] faders, IReadOnlyList<EnvironmentObject> order) {
        foreach (var fader in faders) {
            if (fader.Object > 0
                && fader.Object <= order.Count
                && !(faderValues.TryGetValue(fader.Object, out var was) && was.Equals(fader.Value))) {
                faderValues[fader.Object] = fader.Value;
                hub.Publish(new EnvironmentFaderEvent(order[fader.Object - 1].Id, fader.Value));
            }
        }
    }

    /// <summary>The Environment's objects in the host's order (by index), kept while the document is the same.</summary>
    IReadOnlyList<EnvironmentObject> Order(MidiEnvironment environment) {
        if (environmentOrder is { } kept && ReferenceEquals(kept.Environment, environment)) {
            return kept.Order;
        }

        var order = EnvironmentCompiler.Order(environment);
        environmentOrder = (environment, order);
        monitorCounts.Clear();
        faderValues.Clear();
        return order;
    }

    /// <summary>A monitor's last events, oldest first (at most <see cref="EnvironmentMonitorEntry.Capacity" />).</summary>
    public static unsafe MonitoredEvent[] Monitored(EnvironmentMonitorEntry monitor) {
        var count = (int)Math.Min(monitor.Count, EnvironmentMonitorEntry.Capacity);
        var events = new MonitoredEvent[count];
        for (var i = 0; i < count; i++) {
            var n = (int)((monitor.Count - count + i) % EnvironmentMonitorEntry.Capacity);
            var bytes = monitor.Events[2 * n + 1];
            events[i] = new(
                (int)(bytes & 0xFF),
                (int)((bytes >> 8) & 0xFF),
                (int)((bytes >> 16) & 0xFF),
                monitor.Events[2 * n]
            );
        }

        return events;
    }
}
