using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Anode.Spikes.DiskStream;

/// <summary>What drives the callback: a real device or a thread ticking at the buffer period.</summary>
interface IClock : IDisposable {
    string Name { get; }

    int BufferFrames { get; }

    /// <summary>Callbacks that started more than a period late (null clock) or driver overloads (device).</summary>
    long Late { get; }

    /// <summary>The worst callback the device shim measured natively, in % of the period (device only).</summary>
    double NativeMaxPercent { get; }

    void Start();

    void ResetStats();

    void Stop();
}

/// <summary>
///     A thread that calls the mixer at the exact buffer period, with absolute deadlines (sleep to ~150 µs before, then
///     spin). On macOS it takes the time-constraint (real-time) policy CoreAudio gives its I/O threads; without it the
///     scheduler may park the thread on an efficiency core or preempt it for milliseconds. A late tick is run at once,
///     as a device would ask for the next buffer.
/// </summary>
sealed unsafe partial class NullClock : IClock {
    readonly Mixer mixer;
    readonly Thread thread;
    readonly bool realTime;
    volatile bool running = true;
    long late;

    public NullClock(Mixer mixer, int bufferFrames, bool realTime) {
        this.mixer = mixer;
        this.realTime = realTime;
        BufferFrames = bufferFrames;
        thread = new(Run) { IsBackground = true, Name = "null clock", Priority = ThreadPriority.Highest };
    }

    public string Name => RealTime ? "null-rt" : "null";

    public int BufferFrames { get; }

    public long Late => Interlocked.Read(ref late);

    public double NativeMaxPercent => 0;

    [LibraryImport("libc", EntryPoint = "usleep")]
    private static partial int Usleep(uint microseconds);

    [LibraryImport("libc", EntryPoint = "mach_thread_self")]
    private static partial uint MachThreadSelf();

    [LibraryImport("libc", EntryPoint = "mach_timebase_info")]
    private static partial int MachTimebaseInfo(uint* info);

    [LibraryImport("libc", EntryPoint = "thread_policy_set")]
    private static partial int ThreadPolicySet(uint thread, uint flavor, uint* policy, uint count);

    /// <summary>True when the calling thread got THREAD_TIME_CONSTRAINT_POLICY (macOS).</summary>
    public bool RealTime { get; private set; }

    void MakeRealTime() {
        if (!OperatingSystem.IsMacOS()) {
            return;
        }

        var timebase = stackalloc uint[2];
        _ = MachTimebaseInfo(timebase);
        var periodNs = BufferFrames * 1e9 / WavFile.SampleRate;
        var period = (uint)(periodNs * timebase[1] / timebase[0]);
        var policy = stackalloc uint[4];
        policy[0] = period; // period
        policy[1] = period / 2; // computation
        policy[2] = period; // constraint
        policy[3] = 1; // preemptible
        RealTime = ThreadPolicySet(MachThreadSelf(), 2, policy, 4) == 0; // THREAD_TIME_CONSTRAINT_POLICY
    }

    public void Start() => thread.Start();

    public void ResetStats() => Interlocked.Exchange(ref late, 0);

    void Run() {
        if (realTime) {
            MakeRealTime();
        }

        var period = (double)BufferFrames / WavFile.SampleRate * Stopwatch.Frequency;
        var spin = 150e-6 * Stopwatch.Frequency;
        var ticksPerMicrosecond = Stopwatch.Frequency / 1e6;
        double next = Stopwatch.GetTimestamp();
        while (running) {
            next += period;
            var remaining = next - Stopwatch.GetTimestamp();
            if (remaining > spin) {
                _ = Usleep((uint)((remaining - spin) / ticksPerMicrosecond));
            }

            while (Stopwatch.GetTimestamp() < next) {
                Thread.SpinWait(20);
            }

            if (Stopwatch.GetTimestamp() - next > period) {
                Interlocked.Increment(ref late);
            }

            mixer.Process(BufferFrames);
        }
    }

    public void Stop() {
        running = false;
        thread.Join();
    }

    public void Dispose() {
        if (running) {
            Stop();
        }
    }
}

/// <summary>
///     A real device through the cth_audio shim (engine/native). Anode.Audio has no public stream API yet (only its
///     device list), so the stream entry points are bound here. The output is left silent.
/// </summary>
sealed unsafe partial class DeviceClock : IClock {
    const string Library = "cth_audio";
    readonly Mixer mixer;
    readonly nint stream;
    GCHandle self;

    public DeviceClock(Mixer mixer, string? deviceUid, int bufferFrames) {
        this.mixer = mixer;
        self = GCHandle.Alloc(this);
        var config = new StreamConfig { SampleRate = WavFile.SampleRate, BufferFrames = bufferFrames };
        if (Open(deviceUid, ref config, &Callback, (void*)GCHandle.ToIntPtr(self), out stream) != 0) {
            self.Free();
            throw new InvalidOperationException(
                $"cth_audio_open: {Marshal.PtrToStringUTF8((nint)LastError()) ?? "unknown error"}"
            );
        }

        if ((int)config.SampleRate != WavFile.SampleRate) {
            Close(stream);
            self.Free();
            throw new InvalidOperationException($"device runs at {config.SampleRate} Hz; this spike needs 48 kHz");
        }

        BufferFrames = config.BufferFrames;
        OutputChannels = config.OutputChannels;
    }

    public string Name => "device";

    public int BufferFrames { get; }

    public int OutputChannels { get; }

    public long Late {
        get {
            Stats(stream, out var s);
            return (long)(s.Overloads - overloadBaseline);
        }
    }

    ulong overloadBaseline;

    public double NativeMaxPercent {
        get {
            Timing timing;
            TimingOf(stream, &timing);
            return timing.MaxPercent;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void Callback(
        void* user,
        float** inputs,
        int inputChannels,
        float** outputs,
        int outputChannels,
        int frames
    ) {
        var clock = (DeviceClock)GCHandle.FromIntPtr((nint)user).Target!;
        clock.mixer.Process(frames); // outputs arrive zeroed and stay so: nobody needs to hear this
    }

    public void Start() {
        if (StartStream(stream) != 0) {
            throw new InvalidOperationException("cth_audio_start failed");
        }
    }

    public void ResetStats() {
        ResetTiming(stream);
        Stats(stream, out var s);
        overloadBaseline = s.Overloads;
    }

    public void Stop() => _ = StopStream(stream);

    public void Dispose() {
        Close(stream);
        if (self.IsAllocated) {
            self.Free();
        }
    }

    [LibraryImport(Library, EntryPoint = "cth_audio_open", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(
        string? deviceUid,
        ref StreamConfig config,
        delegate* unmanaged[Cdecl]<void*, float**, int, float**, int, int, void> process,
        void* user,
        out nint stream
    );

    [LibraryImport(Library, EntryPoint = "cth_audio_start")]
    private static partial int StartStream(nint stream);

    [LibraryImport(Library, EntryPoint = "cth_audio_stop")]
    private static partial int StopStream(nint stream);

    [LibraryImport(Library, EntryPoint = "cth_audio_close")]
    private static partial void Close(nint stream);

    [LibraryImport(Library, EntryPoint = "cth_audio_stats")]
    private static partial void Stats(nint stream, out StreamStats stats);

    [LibraryImport(Library, EntryPoint = "cth_audio_timing")]
    private static partial void TimingOf(nint stream, Timing* timing);

    [LibraryImport(Library, EntryPoint = "cth_audio_reset_timing")]
    private static partial void ResetTiming(nint stream);

    [LibraryImport(Library, EntryPoint = "cth_audio_last_error")]
    private static partial byte* LastError();

    [StructLayout(LayoutKind.Sequential)]
    struct StreamConfig {
        public double SampleRate;
        public int BufferFrames;
        public int InputChannels;
        public int OutputChannels;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct StreamStats {
        public ulong Callbacks;
        public ulong Overloads;
        public int InputLatencyFrames;
        public int OutputLatencyFrames;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Timing {
        public fixed ulong Bins[201];
        public ulong OverBudget;
        public double MaxPercent;
    }
}
