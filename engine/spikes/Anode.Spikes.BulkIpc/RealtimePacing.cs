using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Anode.Spikes.BulkIpc;

/// <summary>
///     Makes the simulated audio thread behave like a CoreAudio IOProc on macOS: a Mach time-constraint (real-time)
///     thread that sleeps to an absolute deadline with <c>mach_wait_until</c>. Elsewhere the thread spins to its
///     deadline at the highest normal priority (the spike's simulation; the real backends are cth_audio's).
/// </summary>
static unsafe partial class RealtimePacing {
    const string LibSystem = "/usr/lib/libSystem.dylib";
    const int ThreadTimeConstraintPolicy = 2;
    const int ThreadTimeConstraintPolicyCount = 4;

    static double machPerStopwatchTick;

    /// <summary>
    ///     Promotes the calling thread to a time-constraint thread with the given period; false where that is not
    ///     available, and the caller spins instead.
    /// </summary>
    public static bool TryPromote(long periodTicks) {
        if (!OperatingSystem.IsMacOS()) {
            return false;
        }

        MachTimebase timebase;
        if (MachTimebaseInfo(&timebase) != 0 || timebase.Numer == 0) {
            return false;
        }

        // Mach absolute time = nanoseconds * denom / numer.
        machPerStopwatchTick = 1e9 / Stopwatch.Frequency * timebase.Denom / timebase.Numer;
        var period = (uint)(periodTicks * machPerStopwatchTick);
        var policy = stackalloc uint[ThreadTimeConstraintPolicyCount];
        policy[0] = period; // period
        policy[1] = period / 4; // computation: a quarter of the buffer, as a light engine would ask for
        policy[2] = period; // constraint: done within the period
        policy[3] = 1; // preemptible
        return ThreadPolicySet(MachThreadSelf(), ThreadTimeConstraintPolicy, policy, ThreadTimeConstraintPolicyCount)
            == 0;
    }

    /// <summary>Sleeps until the <see cref="Stopwatch" /> timestamp <paramref name="deadline" />.</summary>
    public static void WaitUntil(long deadline) {
        var remaining = deadline - Stopwatch.GetTimestamp();
        if (remaining > 0) {
            _ = MachWaitUntil(MachAbsoluteTime() + (ulong)(remaining * machPerStopwatchTick));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MachTimebase {
        public uint Numer;
        public uint Denom;
    }

    [LibraryImport(LibSystem, EntryPoint = "mach_timebase_info")]
    private static partial int MachTimebaseInfo(MachTimebase* info);

    [LibraryImport(LibSystem, EntryPoint = "mach_absolute_time")]
    private static partial ulong MachAbsoluteTime();

    [LibraryImport(LibSystem, EntryPoint = "mach_wait_until")]
    private static partial int MachWaitUntil(ulong deadline);

    [LibraryImport(LibSystem, EntryPoint = "mach_thread_self")]
    private static partial uint MachThreadSelf();

    [LibraryImport(LibSystem, EntryPoint = "thread_policy_set")]
    private static partial int ThreadPolicySet(uint thread, int flavor, uint* policy, uint count);
}
