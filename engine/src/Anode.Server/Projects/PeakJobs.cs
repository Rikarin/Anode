using Anode.Model.Audio;
using System.Collections.Concurrent;

namespace Anode.Server.Projects;

/// <summary>
///     Builds waveform peaks in the background (a job per audio file, started on import and after recording); a peaks
///     request for a file still being built waits for its job, and one for a file whose peaks are missing (an older
///     package, a deleted <c>Peaks/</c> folder) starts it.
/// </summary>
public sealed class PeakJobs(ILogger<PeakJobs> logger) : IDisposable {
    static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    readonly ConcurrentDictionary<string, Lazy<Task>> jobs = new(StringComparer.Ordinal);
    readonly CancellationTokenSource stopping = new();
    int disposed;

    /// <summary>Starts building the peaks of <paramref name="wavPath" /> unless they exist or are being built.</summary>
    public Task Start(string peaksFolder, string fileId, string wavPath) {
        if (Volatile.Read(ref disposed) != 0) {
            return Task.CompletedTask;
        }

        var key = Path.Combine(peaksFolder, fileId);
        var job = jobs.GetOrAdd(
            key,
            _ => new(() => Task.Run(() => Build(peaksFolder, fileId, wavPath), stopping.Token))
        );
        return job.Value;
    }

    /// <summary>Waits until the peaks of the file exist (building them if needed).</summary>
    public async Task EnsureAsync(
        string peaksFolder,
        string fileId,
        string wavPath,
        CancellationToken cancellationToken
    ) {
        if (Peaks.Exist(peaksFolder, fileId) && !jobs.ContainsKey(Path.Combine(peaksFolder, fileId))) {
            return;
        }

        await Start(peaksFolder, fileId, wavPath).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    void Build(string peaksFolder, string fileId, string wavPath) {
        try {
            if (!Peaks.Exist(peaksFolder, fileId)) {
                Peaks.Build(wavPath, peaksFolder, fileId, stopping.Token);
            }
        } catch (Exception e) when (e is IOException
                                        or UnauthorizedAccessException
                                        or WavFormatException
                                        or InvalidDataException) {
            ProjectLog.PeaksFailed(logger, wavPath, e.Message);
            throw;
        } finally {
            jobs.TryRemove(Path.Combine(peaksFolder, fileId), out _);
        }
    }

    /// <summary>Stops the jobs (on shutdown) and waits for them to let go of their files.</summary>
    public void Dispose() {
        if (Interlocked.Exchange(ref disposed, 1) != 0) {
            return;
        }

        stopping.Cancel();
        try {
            Task.WaitAll([..jobs.Values.Where(static j => j.IsValueCreated).Select(static j => j.Value)], DrainTimeout);
        } catch (AggregateException) {
            // cancelled or failed: either way done with the files
        }

        stopping.Dispose();
    }
}

/// <summary>The project domain's log messages.</summary>
static partial class ProjectLog {
    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not build the peaks of {Path}: {Error}")]
    public static partial void PeaksFailed(ILogger logger, string path, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped the unreadable {Path}: {Error}")]
    public static partial void Unreadable(ILogger logger, string path, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped the recorded take {Path}: {Reason}")]
    public static partial void TakeSkipped(ILogger logger, string path, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped the MIDI take of engine track {TrackId}: {Reason}")]
    public static partial void MidiTakeSkipped(ILogger logger, int trackId, string reason);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Could not keep a backup of the unsaved project {Path}: {Error}"
    )]
    public static partial void BackupFailed(ILogger logger, string path, string error);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Ignored a parameter change {Parameter} from the engine: {Reason}"
    )]
    public static partial void HostParameterIgnored(ILogger logger, string parameter, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job {Job} failed: {Error}")]
    public static partial void JobFailed(ILogger logger, string job, string error);
}
