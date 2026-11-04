using Microsoft.Extensions.Options;

namespace Anode.Server.Projects;

/// <summary>
///     Autosaves the open project's unsaved edits every <see cref="ServerOptions.AutosaveSeconds" /> on the thread pool,
///     and once more when the Server stops. The disk writes happen here, in the Server: anode-host and its audio thread
///     never hear of them.
/// </summary>
public sealed class AutosaveService(
    ProjectSession session,
    IOptions<ServerOptions> options,
    TimeProvider time,
    ILogger<AutosaveService> logger
) : BackgroundService {
    readonly double seconds = options.Value.AutosaveSeconds;

    public override async Task StopAsync(CancellationToken cancellationToken) {
        await base.StopAsync(cancellationToken);
        if (seconds > 0) {
            Tick();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        if (seconds <= 0) {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds), time);
        try {
            while (await timer.WaitForNextTickAsync(stoppingToken)) {
                Tick();
            }
        } catch (OperationCanceledException) {
            // stopping
        }
    }

    /// <summary>A failure here costs one autosave; it must not stop the Server (and the edits it still holds).</summary>
    void Tick() {
        try {
            session.Autosave();
        } catch (Exception e) when (e is not OutOfMemoryException) {
            ProjectLog.AutosaveStopped(logger, e.Message);
        }
    }
}

static partial class ProjectLog {
    [LoggerMessage(Level = LogLevel.Error, Message = "An autosave stopped: {Error}")]
    public static partial void AutosaveStopped(ILogger logger, string error);
}
