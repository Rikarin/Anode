using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Anode.Media.Ffmpeg;

/// <summary>A finished FFmpeg or FFprobe run: its exit code and what it wrote to standard output and error.</summary>
public sealed record FfmpegResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
///     Runs <c>ffmpeg</c> and <c>ffprobe</c> as child processes (never linked): arguments as a list (no shell), a timeout,
///     progress from <c>-progress pipe:1</c> as 0…1 of <c>durationSeconds</c>, and cancellation (the process is killed;
///     its partial output is the caller's to delete). The tools run without the dynamic loader's search variables
///     (<c>DYLD_*</c>, <c>LD_LIBRARY_PATH</c>, <c>LD_PRELOAD</c>): a <c>DYLD_LIBRARY_PATH</c> of Homebrew's lib folder
///     would otherwise load Homebrew's (GPL) FFmpeg libraries into the pinned LGPL executables.
/// </summary>
public sealed class FfmpegRunner(FfmpegTools tools) {
    /// <summary>How many of the last lines of standard error an <see cref="FfmpegException" /> carries.</summary>
    const int ErrorLines = 12;

    public FfmpegTools Tools { get; } = tools;

    /// <summary>
    ///     Runs ffmpeg with <paramref name="arguments" /> (after <c>-hide_banner -nostdin</c>, and
    ///     <c>-progress pipe:1 -nostats</c> when <paramref name="progress" /> is given, which then owns standard output);
    ///     a non-zero exit throws <see cref="FfmpegException" /> with its last error lines, the timeout
    ///     <see cref="TimeoutException" />, cancellation <see cref="OperationCanceledException" />.
    /// </summary>
    public Task<FfmpegResult> RunFfmpegAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        double? durationSeconds = null,
        IProgress<double>? progress = null,
        CancellationToken cancellation = default
    ) {
        List<string> all = ["-hide_banner", "-nostdin"];
        if (progress is not null) {
            all.AddRange(["-progress", "pipe:1", "-nostats"]);
        }

        all.AddRange(arguments);
        return RunAsync(Tools.FfmpegPath, all, timeout, durationSeconds, progress, cancellation);
    }

    /// <summary>Runs ffprobe with <paramref name="arguments" /> (after <c>-hide_banner</c>), as <see cref="RunFfmpegAsync" />.</summary>
    public Task<FfmpegResult> RunFfprobeAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellation = default
    ) =>
        RunAsync(Tools.FfprobePath, ["-hide_banner", ..arguments], timeout, null, null, cancellation);

    static async Task<FfmpegResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        double? durationSeconds,
        IProgress<double>? progress,
        CancellationToken cancellation
    ) {
        cancellation.ThrowIfCancellationRequested();
        using var process = new Process();
        process.StartInfo = StartInfo(executable, arguments);
        var output = new StringBuilder();
        var error = new StringBuilder();
        var outputDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errorDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, e) => {
            if (e.Data is null) {
                outputDone.TrySetResult();
                return;
            }

            output.AppendLine(e.Data);
            if (progress is not null) {
                Report(e.Data, durationSeconds, progress);
            }
        };
        process.ErrorDataReceived += (_, e) => {
            if (e.Data is null) {
                errorDone.TrySetResult();
            } else {
                error.AppendLine(e.Data);
            }
        };

        try {
            process.Start();
        } catch (System.ComponentModel.Win32Exception e) {
            throw new FfmpegException($"{Path.GetFileName(executable)} could not be started: {e.Message}", e) {
                ExitCode = -1
            };
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await WaitAsync(process, executable, timeout, cancellation).ConfigureAwait(false);

        // The pipes close after the exit; their last lines are read before the result is made.
        await Task.WhenAll(outputDone.Task, errorDone.Task).ConfigureAwait(false);
        var result = new FfmpegResult(process.ExitCode, output.ToString(), error.ToString());
        if (result.ExitCode != 0) {
            throw new FfmpegException(
                $"{Path.GetFileName(executable)} failed (exit code {result.ExitCode}): {Tail(result.StandardError)}"
            ) { ExitCode = result.ExitCode, StandardError = result.StandardError };
        }

        if (progress is not null && durationSeconds is not null) {
            progress.Report(1);
        }

        return result;
    }

    static ProcessStartInfo StartInfo(string executable, IReadOnlyList<string> arguments) {
        var start = new ProcessStartInfo(executable) {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) {
            start.ArgumentList.Add(argument);
        }

        foreach (var name in start.Environment.Keys.ToList()) {
            if (name.StartsWith("DYLD_", StringComparison.Ordinal) || name is "LD_LIBRARY_PATH" or "LD_PRELOAD") {
                start.Environment.Remove(name);
            }
        }

        return start;
    }

    /// <summary>Waits for the exit; past the timeout or on cancellation, kills the process and waits for that.</summary>
    static async Task WaitAsync(Process process, string executable, TimeSpan timeout, CancellationToken cancellation) {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(timeout);
        try {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            throw new TimeoutException(
                $"{Path.GetFileName(executable)} did not finish within {timeout.TotalSeconds:0.#} s."
            );
        }
    }

    /// <summary>A <c>-progress</c> line: <c>out_time_us=…</c> (microseconds written) as a fraction of the duration.</summary>
    static void Report(string line, double? durationSeconds, IProgress<double> progress) {
        if (durationSeconds is not > 0
            || !line.StartsWith("out_time_us=", StringComparison.Ordinal)
            || !long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var micros)) {
            return;
        }

        progress.Report(Math.Clamp(micros / 1e6 / durationSeconds.Value, 0, 1));
    }

    static void Kill(Process process) {
        try {
            process.Kill(true);
        } catch (InvalidOperationException) {
            // It had already exited.
        }
    }

    /// <summary>The last non-empty lines of <paramref name="text" />, joined.</summary>
    internal static string Tail(string text) {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "(no error output)" : string.Join(Environment.NewLine, lines.TakeLast(ErrorLines));
    }
}

/// <summary>FFmpeg or FFprobe failed: its exit code (−1 when it could not be started) and its error output.</summary>
public sealed class FfmpegException : Exception {
    public FfmpegException() { }

    public FfmpegException(string message) : base(message) { }

    public FfmpegException(string message, Exception innerException) : base(message, innerException) { }

    public int ExitCode { get; init; }

    /// <summary>Everything the tool wrote to standard error (empty when the failure was not the tool's exit).</summary>
    public string StandardError { get; init; } = string.Empty;
}
