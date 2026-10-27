using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace Anode.Spikes.DiskStream;

/// <summary>macOS page-cache control: <c>F_NOCACHE</c> reads and a residency probe (<c>mincore</c>).</summary>
static unsafe partial class Posix {
    const string Libc = "libc";
    const int FNoCache = 48; // F_NOCACHE (sys/fcntl.h)
    const int ProtRead = 1;
    const int MapShared = 1;
    const int MsInvalidate = 2;

    public static bool IsMacOS => OperatingSystem.IsMacOS();

    // fcntl is variadic. On Apple arm64 a variadic argument is passed on the stack, not in a register, so the value is
    // passed as a ninth argument (registers x0–x7 are full) and again in the third slot, where x64 reads it.
    [LibraryImport(Libc, EntryPoint = "fcntl", SetLastError = true)]
    private static partial int Fcntl(int fd, int cmd, nint a2, nint a3, nint a4, nint a5, nint a6, nint a7, nint arg);

    [LibraryImport(Libc, EntryPoint = "mmap", SetLastError = true)]
    private static partial nint Mmap(nint addr, nuint length, int prot, int flags, int fd, long offset);

    [LibraryImport(Libc, EntryPoint = "munmap")]
    private static partial int Munmap(nint addr, nuint length);

    [LibraryImport(Libc, EntryPoint = "mincore")]
    private static partial int Mincore(nint addr, nuint length, byte* vector);

    [LibraryImport(Libc, EntryPoint = "msync")]
    private static partial int Msync(nint addr, nuint length, int flags);

    /// <summary>Reads and writes on this descriptor bypass the unified buffer cache (macOS only; a no-op elsewhere).</summary>
    public static void SetNoCache(SafeFileHandle handle) {
        if (!IsMacOS) {
            return;
        }

        var added = false;
        try {
            handle.DangerousAddRef(ref added);
            if (Fcntl((int)handle.DangerousGetHandle(), FNoCache, 1, 0, 0, 0, 0, 0, 1) != 0) {
                throw new IOException($"fcntl(F_NOCACHE) failed: errno {Marshal.GetLastPInvokeError()}");
            }
        } finally {
            if (added) {
                handle.DangerousRelease();
            }
        }
    }

    /// <summary>Fraction of the file's pages resident in the page cache, optionally after asking the kernel to drop them.</summary>
    public static double Residency(string path, bool evict) {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var length = (nuint)RandomAccess.GetLength(handle);
        if (length == 0) {
            return 0;
        }

        // The mapping keeps the file referenced on its own, so the descriptor is only needed for the call.
        var added = false;
        nint map;
        try {
            handle.DangerousAddRef(ref added);
            map = Mmap(0, length, ProtRead, MapShared, (int)handle.DangerousGetHandle(), 0);
        } finally {
            if (added) {
                handle.DangerousRelease();
            }
        }

        if (map == -1) {
            throw new IOException($"mmap failed: errno {Marshal.GetLastPInvokeError()}");
        }

        try {
            if (evict) {
                _ = Msync(map, length, MsInvalidate);
            }

            var pages = (int)((length + (nuint)Environment.SystemPageSize - 1) / (nuint)Environment.SystemPageSize);
            var vector = new byte[pages];
            fixed (byte* v = vector) {
                _ = Mincore(map, length, v);
            }

            var resident = 0;
            foreach (var b in vector) {
                resident += b & 1;
            }

            return (double)resident / pages;
        } finally {
            _ = Munmap(map, length);
        }
    }
}
