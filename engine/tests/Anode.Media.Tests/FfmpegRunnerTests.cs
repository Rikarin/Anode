using Anode.Media.Ffmpeg;
using System.Diagnostics;

namespace Anode.Media.Tests;

/// <summary>The runner: errors with their output, timeouts, cancellation (the process killed), progress, locating.</summary>
public sealed class FfmpegRunnerTests(MediaFixture media) : IClassFixture<MediaFixture> {
    /// <summary>A minute of test pattern encoded to nothing: long enough to cancel, short enough not to matter.</summary>
    static readonly string[] Long = ["-f", "lavfi", "-i", "testsrc2=s=1280x720:r=25:d=600", "-f", "null", "-"];

    [Fact]
    public async Task A_failure_carries_the_exit_code_and_the_error_output() {
        var runner = media.Require();
        var missing = Path.Combine(media.Folder, "missing.mov");

        var error = await Assert.ThrowsAsync<FfmpegException>(() =>
            runner.RunFfmpegAsync(["-i", missing, "-f", "null", "-"], TimeSpan.FromMinutes(1), cancellation: Token)
        );

        Assert.NotEqual(0, error.ExitCode);
        Assert.Contains("missing.mov", error.Message, StringComparison.Ordinal);
        Assert.Contains("No such file", error.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tool_that_cannot_start_is_a_failure_too() {
        var runner = new FfmpegRunner(
            new(Path.Combine(media.Folder, "no-ffmpeg"), Path.Combine(media.Folder, "no-ffprobe"))
        );

        var error = await Assert.ThrowsAsync<FfmpegException>(() => runner.RunFfprobeAsync(
                ["-version"],
                TimeSpan.FromSeconds(5),
                Token
            )
        );

        Assert.Equal(-1, error.ExitCode);
    }

    [Fact]
    public async Task Cancellation_kills_the_process_promptly() {
        var runner = media.Require();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(400));
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunFfmpegAsync(Long, TimeSpan.FromMinutes(5), cancellation: cancel.Token)
        );

        // The run returns only after the process has exited (it is waited for after the kill).
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"cancelled after {clock.Elapsed.TotalSeconds:0.0} s");
    }

    [Fact]
    public async Task A_timeout_kills_the_process_and_says_so() {
        var runner = media.Require();

        var error = await Assert.ThrowsAsync<TimeoutException>(() =>
            runner.RunFfmpegAsync(Long, TimeSpan.FromMilliseconds(400), cancellation: Token)
        );

        Assert.Contains("did not finish", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Progress_rises_to_one() {
        var runner = media.Require();
        var reports = new List<double>();
        var progress = new Inline(reports);

        await runner.RunFfmpegAsync(
            ["-f", "lavfi", "-i", "testsrc2=s=320x180:r=25:d=4", "-f", "null", "-"],
            TimeSpan.FromMinutes(1),
            4,
            progress,
            Token
        );

        Assert.NotEmpty(reports);
        Assert.Equal(reports.Order(), reports);
        Assert.Equal(1, reports[^1]);
    }

    [Fact]
    public void Locate_finds_a_folder_or_its_bin_and_nothing_else() {
        var folder = Path.Combine(media.Folder, "tools");
        var bin = Path.Combine(folder, "bin");
        Directory.CreateDirectory(bin);
        var extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        Assert.Null(FfmpegTools.Locate(folder));
        File.WriteAllText(Path.Combine(bin, "ffmpeg" + extension), string.Empty);
        Assert.Null(FfmpegTools.Locate(folder));
        File.WriteAllText(Path.Combine(bin, "ffprobe" + extension), string.Empty);

        var tools = FfmpegTools.Locate(folder);

        Assert.Equal(Path.Combine(bin, "ffmpeg" + extension), tools?.FfmpegPath);
        Assert.Equal(Path.Combine(bin, "ffprobe" + extension), tools?.FfprobePath);
        Assert.Matches("^(macos|windows|linux)-(arm64|x64)$", FfmpegTools.Platform);
    }

    [Fact]
    public async Task The_tools_found_are_an_lgpl_build() {
        var runner = media.Require();

        var configuration = (await runner.RunFfmpegAsync(["-buildconf"], TimeSpan.FromMinutes(1), cancellation: Token))
            .StandardOutput;
        var license = (await runner.RunFfmpegAsync(["-L"], TimeSpan.FromMinutes(1), cancellation: Token))
            .StandardOutput;

        // A development machine may point ANODE_FFMPEG at another build; only the pinned one must be LGPL.
        Assert.SkipWhen(
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(FfmpegTools.EnvironmentVariable)),
            "ANODE_FFMPEG names another build."
        );
        Assert.DoesNotContain("--enable-gpl", configuration, StringComparison.Ordinal);
        Assert.DoesNotContain("--enable-nonfree", configuration, StringComparison.Ordinal);
        Assert.Contains("GNU Lesser General Public License", license, StringComparison.Ordinal);
    }

    static CancellationToken Token => TestContext.Current.CancellationToken;

    sealed class Inline(List<double> reports) : IProgress<double> {
        public void Report(double value) {
            lock (reports) {
                reports.Add(value);
            }
        }
    }
}
