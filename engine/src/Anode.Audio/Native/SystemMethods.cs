using System.Runtime.InteropServices;

namespace Anode.Audio.Native;

/// <summary>P/Invoke surface of <c>an_sys</c> (engine/native/an_sys/include/an_sys.h).</summary>
static partial class SystemMethods {
    const string Library = "an_sys";

    [LibraryImport(Library, EntryPoint = "an_sys_nocache", SetLastError = true)]
    internal static partial int NoCache(int fd, int enable);

    [LibraryImport(Library, EntryPoint = "an_sys_now_ns")]
    internal static partial ulong NowNs();

    [LibraryImport(Library, EntryPoint = "an_sys_sleep_until_ns")]
    internal static partial void SleepUntilNs(ulong t);

    [LibraryImport(Library, EntryPoint = "an_sys_rt_promote")]
    internal static partial int RtPromote(ulong periodNs, ulong computationNs, ulong constraintNs);
}
