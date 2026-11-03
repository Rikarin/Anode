using Anode.Contracts;
using Anode.Dsp.Spatial;
using Anode.Ipc;
using Anode.Server.Library;
using Anode.Server.Projects;

namespace Anode.Server.Engine;

/// <summary>
///     Monitoring (docs/11-phase-10.md): the user settings' <see cref="MonitoringSettings" />, sent to the host as
///     <see cref="SetMonitoringMessage" /> when a host connects, when they change, and when the open project's format
///     changes (a down-mix no narrower than the master is dropped). The contract's 1-based speaker map (0: not sent)
///     becomes the IPC's 0-based one (−1), and binaural listening names the HRTF set (<see cref="HrtfPath" />).
/// </summary>
/// <remarks>
///     <para>
///         Where the HRTF set lives (workstream D converts SADIE II into Anode's <c>.anhrtf</c>): <c>Anode:HrtfPath</c>
///         when set; else the first <c>*.anhrtf</c> in the <c>hrtf</c> folder beside the Server (a release bundles it
///         there, as the content folder; the Server's build copies D's committed set, its <c>LICENSE</c> and <c>NOTICE</c>
///         there too); else, in development, the committed set found from the Server's folder up
///         (<see cref="HrtfSet.Find" />: <c>engine/src/Anode.Dsp/Spatial/Hrtf</c>). None found: binaural is sent without a
///         set, and
///         the host falls back as it can.
///     </para>
/// </remarks>
public sealed class MonitoringService(
    EngineConnection engine,
    ProjectSession session,
    UserSettings settings,
    IConfiguration configuration
) : IHostedService {
    public const string HrtfExtension = ".anhrtf";

    readonly Lock gate = new();
    ChannelFormat? format;

    public MonitoringSettings Get() => settings.Monitoring();

    /// <summary>Saves the settings (422 when they do not fit the open project) and sends them to the host at once.</summary>
    public MonitoringSettings Set(MonitoringSettings monitoring) {
        var saved = settings.SaveMonitoring(monitoring, session.View?.State.Settings.SurroundFormat);
        Send();
        return saved;
    }

    /// <summary>The HRTF set binaural monitoring uses, or null when there is none (see the remarks).</summary>
    public string? HrtfPath {
        get {
            if (configuration["Anode:HrtfPath"] is { Length: > 0 } configured) {
                return Path.GetFullPath(configured);
            }

            if (First(Path.Combine(AppContext.BaseDirectory, "hrtf")) is { } bundled) {
                return bundled;
            }

            // Development without a build's copy: the committed set in the checkout (engine/src/Anode.Dsp/Spatial/Hrtf).
            if (HrtfSet.Find(AppContext.BaseDirectory) is { } committed) {
                return committed;
            }

            return null;

            static string? First(string folder) =>
                Directory.Exists(folder)
                    ? Directory.EnumerateFiles(folder, "*" + HrtfExtension)
                        .Order(StringComparer.Ordinal)
                        .FirstOrDefault()
                    : null;
        }
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
            monitoring.Binaural ? hrtf : null
        );

    void Send() {
        var master = session.View?.State.Settings.SurroundFormat ?? ChannelFormat.Stereo;
        lock (gate) {
            format = master;
        }

        engine.Post(Message(settings.Monitoring(), master, HrtfPath));
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
