using System.Runtime.InteropServices;

namespace Anode.Plugins.Native;

/// <summary>
///     P/Invoke surface of <c>an_ipcwake</c> (engine/native/an_ipcwake/include/an_ipcwake.h), promoted from spike S4
///     (docs/spikes/S4-sandbox-wake.md): shared memory, the cross-process wake words, the clock, real-time promotion and
///     the device workgroup. The product uses only the address wait (<see cref="Address" />): a shared
///     <c>os_sync_wait_on_address</c> on macOS 14.4+, a shared futex on Linux.
/// </summary>
internal static unsafe partial class IpcWake {
    public const int Address = 1;
    public const ulong Forever = ulong.MaxValue;
    const string Library = "an_ipcwake";

    [LibraryImport(Library, EntryPoint = "an_shm_create", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int ShmCreate(string name, ulong size, out nint shm);

    [LibraryImport(Library, EntryPoint = "an_shm_open", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int ShmOpen(string name, out nint shm);

    [LibraryImport(Library, EntryPoint = "an_shm_data")]
    public static partial byte* ShmData(nint shm);

    [LibraryImport(Library, EntryPoint = "an_shm_size")]
    public static partial ulong ShmSize(nint shm);

    [LibraryImport(Library, EntryPoint = "an_shm_close")]
    public static partial void ShmClose(nint shm, int unlink);

    [LibraryImport(Library, EntryPoint = "an_wake_supported")]
    public static partial int Supported(int kind);

    [LibraryImport(Library, EntryPoint = "an_wake_create")]
    public static partial int Create(int kind, uint* word, byte* name, int create, out nint wake);

    [LibraryImport(Library, EntryPoint = "an_wake_destroy")]
    public static partial void Destroy(nint wake, int unlink);

    [LibraryImport(Library, EntryPoint = "an_wake_load")]
    [SuppressGCTransition]
    public static partial uint Load(nint wake);

    [LibraryImport(Library, EntryPoint = "an_wake_signal")]
    public static partial void Signal(nint wake);

    /// <summary>0 when the word changed, 1 on timeout, negative on error.</summary>
    [LibraryImport(Library, EntryPoint = "an_wake_wait")]
    public static partial int Wait(nint wake, uint seen, ulong timeoutNs, ulong spinNs);

    [LibraryImport(Library, EntryPoint = "an_now_ns")]
    [SuppressGCTransition]
    public static partial long Now();

    [LibraryImport(Library, EntryPoint = "an_rt_promote")]
    public static partial int RtPromote(ulong periodNs, ulong computationNs, ulong constraintNs);

    [LibraryImport(Library, EntryPoint = "an_process_alive")]
    public static partial int ProcessAlive(int pid);

    [LibraryImport(Library, EntryPoint = "an_process_cpu_ns")]
    public static partial long ProcessCpuNs(int pid);

    [LibraryImport(Library, EntryPoint = "an_ipcwake_last_error")]
    private static partial byte* LastErrorRaw();

    public static string LastError => Marshal.PtrToStringUTF8((nint)LastErrorRaw()) ?? "unknown error";

    [LibraryImport(Library, EntryPoint = "an_wg_publish_device", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int WorkgroupPublish(string? deviceUid, string service, out nint server);

    [LibraryImport(Library, EntryPoint = "an_wg_unpublish")]
    public static partial void WorkgroupUnpublish(nint server);

    [LibraryImport(Library, EntryPoint = "an_wg_join", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int WorkgroupJoin(string service, out nint member);

    [LibraryImport(Library, EntryPoint = "an_wg_leave")]
    public static partial void WorkgroupLeave(nint member);

    [LibraryImport(Library, EntryPoint = "an_wg_last_error")]
    private static partial byte* WorkgroupLastErrorRaw();

    public static string WorkgroupLastError => Marshal.PtrToStringUTF8((nint)WorkgroupLastErrorRaw()) ?? "unknown error";

    /// <summary>A wake word over <paramref name="word" /> (inside a shared segment), or throws.</summary>
    public static nint CreateWord(uint* word) {
        if (Create(Address, word, null, 0, out var wake) != 0) {
            throw new InvalidOperationException("an_ipcwake: " + LastError);
        }

        return wake;
    }
}
