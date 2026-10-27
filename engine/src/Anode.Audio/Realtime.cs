using Anode.Audio.Native;
using Microsoft.Win32.SafeHandles;
using System.Diagnostics;

namespace Anode.Audio;

/// <summary>
///     Thread scheduling, the clock and page-cache control, through the native shim <c>an_sys</c> where it is built
///     (engine/native/build.sh). Without it the clock falls back to <see cref="Stopwatch" />, sleeps to
///     <see cref="Thread.Sleep(int)" /> and the rest reports failure.
/// </summary>
public static class Realtime {
    static readonly bool Native = Probe();

    /// <summary>True when the native shim is loaded: real-time promotion and <c>F_NOCACHE</c> can work.</summary>
    public static bool IsAvailable => Native;

    /// <summary>
    ///     Gives the calling thread real-time scheduling for a periodic workload of <paramref name="periodFrames" /> at
    ///     <paramref name="sampleRate" />, expecting to compute for <paramref name="computation" /> of each period
    ///     (macOS: the Mach time-constraint policy CoreAudio gives its I/O threads; Linux: SCHED_FIFO). Without it,
    ///     spike S2 measured preemptions of 6.5 ms on a thread ticking at 1.3 ms. False when the system refused.
    /// </summary>
    public static bool TryPromoteCurrentThread(int periodFrames, int sampleRate, double computation = 0.5) {
        if (!Native) {
            return false;
        }

        var period = (ulong)(periodFrames * 1e9 / sampleRate);
        return SystemMethods.RtPromote(period, (ulong)(period * computation), period) == 0;
    }

    /// <summary>Monotonic nanoseconds, the clock <see cref="Stopwatch" /> reads (CLOCK_UPTIME_RAW on macOS).</summary>
    public static long NowNanoseconds() =>
        Native ? (long)SystemMethods.NowNs() : (long)(Stopwatch.GetTimestamp() * (1e9 / Stopwatch.Frequency));

    /// <summary>Sleeps the calling thread until <see cref="NowNanoseconds" /> reaches <paramref name="deadline" />.</summary>
    public static void SleepUntil(long deadline) {
        if (Native) {
            SystemMethods.SleepUntilNs((ulong)Math.Max(0, deadline));
            return;
        }

        var remaining = deadline - NowNanoseconds();
        if (remaining > 0) {
            Thread.Sleep(TimeSpan.FromTicks(Math.Max(1, remaining / 100)));
        }
    }

    /// <summary>
    ///     Reads through <paramref name="handle" /> bypass the page cache (macOS <c>F_NOCACHE</c>; Linux drops the
    ///     file's cached pages). Streams are read once per pass, so caching them only evicts other things (spike S2).
    ///     False when it could not be set.
    /// </summary>
    public static bool BypassPageCache(SafeFileHandle handle) {
        if (!Native || OperatingSystem.IsWindows()) {
            return false;
        }

        var added = false;
        try {
            handle.DangerousAddRef(ref added);
            return SystemMethods.NoCache((int)handle.DangerousGetHandle(), 1) == 0;
        } finally {
            if (added) {
                handle.DangerousRelease();
            }
        }
    }

    static bool Probe() {
        try {
            _ = SystemMethods.NowNs();
            return true;
        } catch (DllNotFoundException) {
            return false;
        } catch (EntryPointNotFoundException) {
            return false;
        }
    }
}
