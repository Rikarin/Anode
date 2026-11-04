using Anode.Contracts;
using Anode.Ipc;
using Anode.Model;
using Anode.Server.Library;
using Anode.Server.Projects;

namespace Anode.Server.Engine;

/// <summary>
///     Monitoring (docs/11-phase-10.md): the user settings' <see cref="MonitoringSettings" />, sent to the host as
///     <see cref="SetMonitoringMessage" /> when a host connects, when they change, and when the open project's format
///     changes (a down-mix no narrower than the master is dropped). The contract's 1-based speaker map (0: not sent)
///     becomes the IPC's 0-based one (−1), binaural listening names the HRTF set (<see cref="HrtfLocator" />), and the
///     monitor section becomes one gain and the mono check.
/// </summary>
public sealed class MonitoringService(
    EngineConnection engine,
    ProjectSession session,
    UserSettings settings,
    HrtfLocator hrtfs
) : IHostedService {
    readonly Lock gate = new();
    ChannelFormat? format;

    public MonitoringSettings Get() => settings.Monitoring();

    /// <summary>Saves the settings (422 when they do not fit the open project) and sends them to the host at once.</summary>
    public MonitoringSettings Set(MonitoringSettings monitoring) {
        var saved = settings.SaveMonitoring(monitoring, session.View?.State.Settings.SurroundFormat);
        Send();
        return saved;
    }

    /// <summary>
    ///     What the host is sent for <paramref name="monitoring" /> with a master of <paramref name="master" />: the
    ///     down-mix only when narrower, the map 0-based (−1 not sent), the HRTF set with binaural.
    /// </summary>
    public static SetMonitoringMessage Message(MonitoringSettings monitoring, ChannelFormat master, string? hrtf) =>
        new(
            monitoring.Binaural,
            monitoring.DownmixTo is { } to && SpeakerLayouts.ChannelCount(to) < SpeakerLayouts.ChannelCount(master)
                ? (EngineChannelFormat)to
                : null,
            [..monitoring.SpeakerMap.Select(static output => output - 1)],
            monitoring.Binaural ? hrtf : null,
            Gain(monitoring.Section ?? MonitorSection.Default),
            monitoring.Section?.Mono ?? false
        );

    /// <summary>The monitor section's gain: the level or the reference, less the dim when it is on.</summary>
    static float Gain(MonitorSection section) =>
        Units.DbToGain((section.Reference ? section.ReferenceDb : section.LevelDb) + (section.Dim ? section.DimDb : 0));

    void Send() {
        var master = session.View?.State.Settings.SurroundFormat ?? ChannelFormat.Stereo;
        lock (gate) {
            format = master;
        }

        engine.Post(Message(settings.Monitoring(), master, hrtfs.Path));
    }

    /// <summary>After an edit: sends again when the project's format changed (or a project opened or closed).</summary>
    void Changed() {
        var master = session.View?.State.Settings.SurroundFormat ?? ChannelFormat.Stereo;
        lock (gate) {
            if (format == master) {
                return;
            }
        }

        Send();
    }

    public Task StartAsync(CancellationToken cancellationToken) {
        engine.HostConnected += Send;
        session.Changed += Changed;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) {
        engine.HostConnected -= Send;
        session.Changed -= Changed;
        return Task.CompletedTask;
    }
}
