using System.Runtime.InteropServices;

namespace Anode.Spikes.SandboxWake;

/// <summary>P/Invoke surface of <c>an_ipcwake</c> (engine/native/an_ipcwake/include/an_ipcwake.h).</summary>
static unsafe partial class IpcWake {
    public const int Spin = 0;
    public const int Address = 1;
    public const int Semaphore = 2;
    public const ulong Forever = ulong.MaxValue;
    const string Library = "an_ipcwake";

    [LibraryImport(Library, EntryPoint = "an_shm_create", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int ShmCreate(string name, ulong size, out nint shm);

    [LibraryImport(Library, EntryPoint = "an_shm_open", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int ShmOpen(string name, out nint shm);

    [LibraryImport(Library, EntryPoint = "an_shm_data")]
    public static partial byte* ShmData(nint shm);

    [LibraryImport(Library, EntryPoint = "an_shm_close")]
    public static partial void ShmClose(nint shm, int unlink);

    [LibraryImport(Library, EntryPoint = "an_wake_supported")]
    public static partial int Supported(int kind);

    [LibraryImport(Library, EntryPoint = "an_wake_create", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Create(int kind, uint* word, string? name, int create, out nint wake);

    [LibraryImport(Library, EntryPoint = "an_wake_destroy")]
    public static partial void Destroy(nint wake, int unlink);

    [LibraryImport(Library, EntryPoint = "an_wake_load")]
    public static partial uint Load(nint wake);

    [LibraryImport(Library, EntryPoint = "an_wake_signal")]
    public static partial void Signal(nint wake);

    [LibraryImport(Library, EntryPoint = "an_wake_wait")]
    public static partial int Wait(nint wake, uint seen, ulong timeoutNs, ulong spinNs);

    [LibraryImport(Library, EntryPoint = "an_now_ns")]
    public static partial long Now();

    [LibraryImport(Library, EntryPoint = "an_sleep_until_ns")]
    public static partial void SleepUntil(long t);

    [LibraryImport(Library, EntryPoint = "an_rt_promote")]
    public static partial int RtPromote(ulong periodNs, ulong computationNs, ulong constraintNs);

    [LibraryImport(Library, EntryPoint = "an_process_cpu_ns")]
    public static partial long ProcessCpuNs(int pid);

    [LibraryImport(Library, EntryPoint = "an_process_alive")]
    public static partial int ProcessAlive(int pid);

    [LibraryImport("libc", EntryPoint = "getloadavg")]
    private static partial int GetLoadAverage(double* loads, int count);

    /// <summary>The one-minute load average, or -1 when unavailable.</summary>
    public static double LoadAverage {
        get {
            double load;
            return GetLoadAverage(&load, 1) == 1 ? load : -1;
        }
    }

    [LibraryImport(Library, EntryPoint = "an_wg_publish_device", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int WorkgroupPublish(string? deviceUid, string service, out nint server);

    [LibraryImport(Library, EntryPoint = "an_wg_unpublish")]
    public static partial void WorkgroupUnpublish(nint server);

    [LibraryImport(Library, EntryPoint = "an_wg_join", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int WorkgroupJoin(string service, out nint member);

    [LibraryImport(Library, EntryPoint = "an_wg_leave")]
    public static partial void WorkgroupLeave(nint member);

    [LibraryImport(Library, EntryPoint = "an_wg_last_error")]
    private static partial byte* WorkgroupLastError();

    public static string WorkgroupLastErrorMessage =>
        Marshal.PtrToStringUTF8((nint)WorkgroupLastError()) ?? "unknown error";

    [LibraryImport(Library, EntryPoint = "an_ipcwake_last_error")]
    private static partial byte* LastError();

    public static string LastErrorMessage => Marshal.PtrToStringUTF8((nint)LastError()) ?? "unknown error";

    public static int ParseKind(string name) =>
        name switch {
            "spin" => Spin,
            "address" => Address,
            "semaphore" => Semaphore,
            _ => throw new ArgumentException($"unknown wake kind '{name}' (spin, address, semaphore)")
        };

    public static string KindName(int kind) =>
        kind switch {
            Spin => "spin",
            Address => "address",
            Semaphore => "semaphore",
            _ => "?"
        };
}

/// <summary>The few <c>cth_audio</c> entry points the spike needs that <c>Anode.Audio</c> keeps internal.</summary>
static unsafe partial class CthAudio {
    const string Library = "cth_audio";

    [LibraryImport(Library, EntryPoint = "cth_audio_open", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Open(
        string? deviceUid,
        ref StreamConfig config,
        delegate* unmanaged<void*, float**, int, float**, int, int, void> process,
        void* user,
        out nint stream
    );

    [LibraryImport(Library, EntryPoint = "cth_audio_start")]
    public static partial int Start(nint stream);

    [LibraryImport(Library, EntryPoint = "cth_audio_stop")]
    public static partial int Stop(nint stream);

    [LibraryImport(Library, EntryPoint = "cth_audio_close")]
    public static partial void Close(nint stream);

    [LibraryImport(Library, EntryPoint = "cth_audio_stats")]
    public static partial void Stats(nint stream, out StreamStats stats);

    [LibraryImport(Library, EntryPoint = "cth_audio_timing")]
    public static partial void GetTiming(nint stream, TimingData* timing);

    [LibraryImport(Library, EntryPoint = "cth_audio_reset_timing")]
    public static partial void ResetTiming(nint stream);

    [LibraryImport(Library, EntryPoint = "cth_audio_last_error")]
    private static partial byte* LastError();

    public static string LastErrorMessage => Marshal.PtrToStringUTF8((nint)LastError()) ?? "unknown error";

    [StructLayout(LayoutKind.Sequential)]
    public struct StreamConfig {
        public double SampleRate;
        public int BufferFrames;
        public int InputChannels;
        public int OutputChannels;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct StreamStats {
        public ulong Callbacks;
        public ulong Overloads;
        public int InputLatencyFrames;
        public int OutputLatencyFrames;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TimingData {
        public fixed ulong Bins[201];
        public ulong OverBudget;
        public double MaxPercent;
    }
}
