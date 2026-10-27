using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace Anode.Spikes.BulkIpc;

/// <summary>How a segment is backed; chosen by the server, carried in the segment's name.</summary>
enum SegmentBackend {
    /// <summary>
    ///     <see cref="MemoryMappedFile" /> only (no P/Invoke). .NET supports named mappings only on Windows, so: Windows,
    ///     a named pagefile-backed mapping (<c>Local\anode-…</c>); Linux, a file in <c>/dev/shm</c> (tmpfs); macOS, a
    ///     file in <c>$TMPDIR</c> (APFS, there is no tmpfs).
    /// </summary>
    File,

    /// <summary>macOS: a POSIX shared-memory object (<c>shm_open</c> + <c>mmap</c>), named <c>shm:/anode-…</c>.</summary>
    PosixShm
}

/// <summary>
///     One shared-memory segment. The writer (server) owns the name: it keeps it until the host acknowledges the
///     mapping, then removes it. The host's mapping keeps the memory alive after that, until the host unmaps it.
/// </summary>
sealed unsafe partial class SharedSegment : IDisposable {
    const string ShmPrefix = "shm:";
    const string LibSystem = "/usr/lib/libSystem.dylib";

    static int unlinkFailures;

    readonly MemoryMappedFile? file;
    readonly string? fileToDelete;
    readonly string? shmToUnlink;
    MemoryMappedViewAccessor? view;
    bool rawMapped;

    public string Name { get; }
    public long Length { get; }
    public byte* Pointer { get; private set; }
    public bool Locked { get; private set; }

    /// <summary>Shared-memory objects the writer failed to remove (leaked names), in this process.</summary>
    public static int UnlinkFailures => Volatile.Read(ref unlinkFailures);

    SharedSegment(
        string name,
        long length,
        MemoryMappedFile mapping,
        MemoryMappedViewAccessor view,
        string? fileToDelete
    ) {
        Name = name;
        Length = length;
        file = mapping;
        this.view = view;
        this.fileToDelete = fileToDelete;
        byte* pointer = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        Pointer = pointer + view.PointerOffset;
    }

    SharedSegment(string name, long length, byte* pointer, string? shmToUnlink) {
        Name = name;
        Length = length;
        Pointer = pointer;
        rawMapped = true;
        this.shmToUnlink = shmToUnlink;
    }

    /// <summary>Where segment files go on this machine (the <see cref="SegmentBackend.File" /> backend, not Windows).</summary>
    public static string DefaultDirectory() =>
        OperatingSystem.IsLinux() && Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath();

    /// <summary>Server: creates and maps a segment for writing.</summary>
    public static SharedSegment Create(SegmentBackend backend, string directory, string prefix, long id, long length) {
        if (backend == SegmentBackend.PosixShm) {
            return CreateShm($"/{prefix}-{id}", length);
        }

        if (OperatingSystem.IsWindows()) {
            var name = $@"Local\{prefix}-{id}";
            var mapping = MemoryMappedFile.CreateNew(name, length, MemoryMappedFileAccess.ReadWrite);
            return new(name, length, mapping, mapping.CreateViewAccessor(0, length), null);
        }

        var path = Path.Combine(directory, $"{prefix}-{id}.seg");
        var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete
        );
        stream.SetLength(length);
        var file = MemoryMappedFile.CreateFromFile(
            stream,
            null,
            length,
            MemoryMappedFileAccess.ReadWrite,
            HandleInheritability.None,
            false
        );
        return new(path, length, file, file.CreateViewAccessor(0, length, MemoryMappedFileAccess.ReadWrite), path);
    }

    /// <summary>Host: maps an existing segment read-only.</summary>
    public static SharedSegment Open(string name, long length) {
        if (name.StartsWith(ShmPrefix, StringComparison.Ordinal)) {
            return OpenShm(name, length);
        }

        if (OperatingSystem.IsWindows()) {
            var mapping = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
            return new(name, length, mapping, mapping.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read), null);
        }

        var stream = new FileStream(name, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length != length) {
            stream.Dispose();
            throw new InvalidDataException($"'{name}' is {stream.Length} bytes, expected {length}.");
        }

        var file = MemoryMappedFile.CreateFromFile(
            stream,
            null,
            0,
            MemoryMappedFileAccess.Read,
            HandleInheritability.None,
            false
        );
        return new(name, length, file, file.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read), null);
    }

    /// <summary>Host: pins the mapped pages in RAM (POSIX mlock), so they cannot be evicted and re-faulted.</summary>
    public bool TryLock() {
        var result = OperatingSystem.IsMacOS() ? MlockMac(Pointer, (nuint)Length)
            : OperatingSystem.IsLinux() ? MlockLinux(Pointer, (nuint)Length)
            : -1;
        Locked = result == 0;
        return Locked;
    }

    public void Dispose() {
        if (rawMapped) {
            Munmap(Pointer, (nuint)Length);
            rawMapped = false;
            if (shmToUnlink is not null && ShmUnlink(shmToUnlink) != 0) {
                Interlocked.Increment(ref unlinkFailures);
            }
        } else if (view is not null) {
            view.SafeMemoryMappedViewHandle.ReleasePointer();
            view.Dispose();
            view = null;
        }

        Pointer = null;
        file?.Dispose();
        if (fileToDelete is not null) {
            File.Delete(fileToDelete);
        }
    }

    static SharedSegment CreateShm(string name, long length) {
        RequireMac();
        var fd = ShmOpen(name, OCreat | OExcl | ORdWr, 0x180); // 0600
        if (fd < 0) {
            throw new IOException($"shm_open({name}) failed: errno {Marshal.GetLastPInvokeError()}.");
        }

        try {
            if (Ftruncate(fd, length) != 0) {
                var errno = Marshal.GetLastPInvokeError();
                ShmUnlink(name);
                throw new IOException($"ftruncate({name}) failed: errno {errno}.");
            }

            var pointer = Mmap(null, (nuint)length, ProtRead | ProtWrite, MapShared, fd, 0);
            if (pointer == (void*)-1) {
                var errno = Marshal.GetLastPInvokeError();
                ShmUnlink(name);
                throw new IOException($"mmap({name}) failed: errno {errno}.");
            }

            return new(ShmPrefix + name, length, (byte*)pointer, name);
        } finally {
            Close(fd);
        }
    }

    static SharedSegment OpenShm(string qualified, long length) {
        RequireMac();
        var name = qualified[ShmPrefix.Length..];
        var fd = ShmOpen(name, ORdOnly, 0);
        if (fd < 0) {
            throw new IOException($"shm_open({name}) failed: errno {Marshal.GetLastPInvokeError()}.");
        }

        try {
            // macOS rounds a shared-memory object up to whole pages, so its size is not checked against the length.
            var pointer = Mmap(null, (nuint)length, ProtRead, MapShared, fd, 0);
            if (pointer == (void*)-1) {
                throw new IOException($"mmap({name}) failed: errno {Marshal.GetLastPInvokeError()}.");
            }

            return new(qualified, length, (byte*)pointer, null);
        } finally {
            Close(fd);
        }
    }

    static void RequireMac() {
        if (!OperatingSystem.IsMacOS()) {
            throw new PlatformNotSupportedException("The shm backend is implemented for macOS only.");
        }
    }

    // macOS values.
    const int ORdOnly = 0;
    const int ORdWr = 2;
    const int OCreat = 0x200;
    const int OExcl = 0x800;
    const int ProtRead = 1;
    const int ProtWrite = 2;
    const int MapShared = 1;

    /// <summary>
    ///     <c>shm_open</c> is variadic (the mode). On Apple arm64 variadic arguments go on the stack, not in registers,
    ///     so the mode is passed as a ninth argument after six unused register arguments; on x64 it is the third.
    /// </summary>
    static int ShmOpen(string name, int flags, int mode) =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? ShmOpenArm64(name, flags, 0, 0, 0, 0, 0, 0, mode)
            : ShmOpenX64(name, flags, mode);

    [LibraryImport(LibSystem, EntryPoint = "shm_open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int ShmOpenArm64(
        string name,
        int flags,
        nint x2,
        nint x3,
        nint x4,
        nint x5,
        nint x6,
        nint x7,
        nint mode
    );

    [LibraryImport(LibSystem, EntryPoint = "shm_open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int ShmOpenX64(string name, int flags, int mode);

    [LibraryImport(
        LibSystem,
        EntryPoint = "shm_unlink",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8
    )]
    private static partial int ShmUnlink(string name);

    [LibraryImport(LibSystem, EntryPoint = "ftruncate", SetLastError = true)]
    private static partial int Ftruncate(int fd, long length);

    [LibraryImport(LibSystem, EntryPoint = "mmap", SetLastError = true)]
    private static partial void* Mmap(void* address, nuint length, int protection, int flags, int fd, long offset);

    [LibraryImport(LibSystem, EntryPoint = "munmap", SetLastError = true)]
    private static partial int Munmap(void* address, nuint length);

    [LibraryImport(LibSystem, EntryPoint = "close", SetLastError = true)]
    private static partial int Close(int fd);

    [LibraryImport(LibSystem, EntryPoint = "mlock")]
    private static partial int MlockMac(void* address, nuint length);

    [LibraryImport("libc.so.6", EntryPoint = "mlock")]
    private static partial int MlockLinux(void* address, nuint length);
}
