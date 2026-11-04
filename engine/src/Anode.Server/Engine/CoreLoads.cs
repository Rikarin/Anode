using System.Globalization;
using System.Runtime.InteropServices;

namespace Anode.Server.Engine;

/// <summary>What a core has done since the system started, in the system's ticks.</summary>
public readonly record struct CoreTicks(ulong Busy, ulong Total);

/// <summary>
///     How busy each of the machine's cores was between two calls of <see cref="Sample" /> (0…1): the system's whole load,
///     not only the engine's, as a CPU meter shows it. macOS through <c>host_processor_info</c>, Linux through
///     <c>/proc/stat</c>; elsewhere no cores (the meter then shows the audio thread alone).
/// </summary>
public sealed partial class CoreLoads {
    CoreTicks[] previous = [];

    /// <summary>Each core's load since the previous call; empty on the first call and where there is no way to tell.</summary>
    public IReadOnlyList<double> Sample() {
        var now = Read();
        var loads = Between(previous, now);
        previous = now;
        return loads;
    }

    /// <summary>The loads between two readings of the same cores; empty when they are not.</summary>
    public static double[] Between(IReadOnlyList<CoreTicks> before, IReadOnlyList<CoreTicks> after) {
        if (before.Count != after.Count) {
            return [];
        }

        var loads = new double[after.Count];
        for (var c = 0; c < loads.Length; c++) {
            var total = after[c].Total - before[c].Total;
            var busy = after[c].Busy - before[c].Busy;
            loads[c] = total == 0 ? 0 : Math.Clamp((double)busy / total, 0, 1);
        }

        return loads;
    }

    /// <summary>
    ///     The cores of a <c>/proc/stat</c>: each <c>cpuN</c> line's user, nice, system, idle, iowait, irq, softirq and
    ///     steal (idle and iowait are what is not busy; the total line <c>cpu</c> is left out).
    /// </summary>
    public static CoreTicks[] ParseProcStat(string text) {
        var cores = new List<CoreTicks>();
        foreach (var line in text.Split('\n')) {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 5
                || !fields[0].StartsWith("cpu", StringComparison.Ordinal)
                || fields[0].Length == 3) {
                continue;
            }

            ulong busy = 0;
            ulong total = 0;
            for (var f = 1; f < Math.Min(fields.Length, 9); f++) {
                var ticks = ulong.Parse(fields[f], CultureInfo.InvariantCulture);
                total += ticks;
                busy += f is 4 or 5 ? 0 : ticks;
            }

            cores.Add(new(busy, total));
        }

        return [..cores];
    }

    static CoreTicks[] Read() {
        try {
            if (OperatingSystem.IsMacOS()) {
                return Mach.Read();
            }

            return OperatingSystem.IsLinux() ? ParseProcStat(File.ReadAllText("/proc/stat")) : [];
        } catch (Exception e) when (e is IOException
                                        or UnauthorizedAccessException
                                        or FormatException
                                        or OverflowException
                                        or DllNotFoundException
                                        or EntryPointNotFoundException) {
            return [];
        }
    }

    /// <summary>macOS's per-processor tick counters.</summary>
    static unsafe partial class Mach {
        const string Library = "/usr/lib/libSystem.B.dylib";
        const int ProcessorCpuLoadInfo = 2;
        const int StatesPerCpu = 4; // user, system, idle, nice
        const int IdleState = 2;

        static readonly uint Host = HostSelf();

        static readonly uint Task = (uint)Marshal.ReadInt32(
            NativeLibrary.GetExport(NativeLibrary.Load(Library), "mach_task_self_")
        );

        public static CoreTicks[] Read() {
            if (HostProcessorInfo(Host, ProcessorCpuLoadInfo, out var count, out var info, out var length) != 0) {
                return [];
            }

            try {
                var cores = new CoreTicks[count];
                var ticks = (uint*)info;
                for (var c = 0; c < count; c++) {
                    ulong busy = 0;
                    ulong total = 0;
                    for (var s = 0; s < StatesPerCpu; s++) {
                        var value = ticks[c * StatesPerCpu + s];
                        total += value;
                        busy += s == IdleState ? 0 : value;
                    }

                    cores[c] = new(busy, total);
                }

                return cores;
            } finally {
                _ = VmDeallocate(Task, info, length * sizeof(int));
            }
        }

        [LibraryImport(Library, EntryPoint = "mach_host_self")]
        private static partial uint HostSelf();

        [LibraryImport(Library, EntryPoint = "host_processor_info")]
        private static partial int HostProcessorInfo(
            uint host,
            int flavor,
            out uint count,
            out nint info,
            out uint length
        );

        [LibraryImport(Library, EntryPoint = "vm_deallocate")]
        private static partial int VmDeallocate(uint task, nint address, nuint size);
    }
}
