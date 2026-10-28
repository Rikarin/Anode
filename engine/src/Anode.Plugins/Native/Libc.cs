using System.Runtime.InteropServices;

namespace Anode.Plugins.Native;

/// <summary>The few libc calls the plugin host needs (macOS and Linux).</summary>
internal static unsafe partial class Libc {
    [LibraryImport("libc", EntryPoint = "getppid")]
    private static partial int GetParentPidRaw();

    [LibraryImport("libc", EntryPoint = "sysctlbyname", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SysctlByName(string name, void* value, nuint* size, void* newValue, nuint newSize);

    public static int GetParentPid() {
        try {
            return GetParentPidRaw();
        } catch (DllNotFoundException) {
            return 0;
        } catch (EntryPointNotFoundException) {
            return 0;
        }
    }

    /// <summary>The number of performance cores (macOS <c>hw.perflevel0.physicalcpu</c>), else the processor count.</summary>
    public static int PerformanceCores() {
        if (OperatingSystem.IsMacOS()) {
            int cores;
            nuint size = sizeof(int);
            if (SysctlByName("hw.perflevel0.physicalcpu", &cores, &size, null, 0) == 0 && cores > 0) {
                return cores;
            }
        }

        return Environment.ProcessorCount;
    }
}
