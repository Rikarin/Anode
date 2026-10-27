using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Anode.Spikes.SandboxWake;

/// <summary>What one host run measures with.</summary>
sealed record HostOptions(
    string Clock,
    string? Device,
    int Frames,
    double SampleRate,
    int Kind,
    int Sandboxes,
    int Instances,
    int Biquads,
    int BurnNs,
    long HostSpinNs,
    long SandboxSpinNs,
    double DeadlinePercent,
    bool RealTime,
    bool Workgroup,
    double Warmup,
    double Seconds,
    string Label,
    string Load,
    string? Results
);

/// <summary>
///     The host role: the engine side of the sandbox handshake. Each callback it writes every instance's input, signals
///     every sandbox (they run in parallel), then waits for each with a deadline and bypasses any that miss it.
/// </summary>
sealed unsafe class Host : IDisposable {
    readonly HostOptions options;
    readonly nint shm;
    readonly byte* segment;
    readonly SandboxControl*[] controls;
    readonly nint[] go;
    readonly nint[] done;
    readonly long[] lastSignaled;
    readonly bool[] signaled;
    readonly Process[] processes;
    readonly float[] mix = new float[Layout.MaxFrames * Layout.Channels];
    readonly long periodNs;
    readonly long deadlineNs;
    readonly nint workgroup;
    GCHandle self;

    // Written by the audio thread only, read by the control thread after it stops.
    public readonly Histogram Wake = new();
    public readonly Histogram SandboxDone = new();
    public readonly Histogram AllDone = new();
    public readonly Histogram Callback = new();
    public long Callbacks;
    public long Misses;
    public long BusySkips;
    public long Late;
    public long MissedCallbacks;
    public long Corrupt;
    public long ClockOverruns;
    long cycle;
    double phase;
    volatile bool measuring;
    volatile bool stopClock;

    public Host(HostOptions options) {
        this.options = options;
        periodNs = (long)(options.Frames * 1e9 / options.SampleRate);
        deadlineNs = (long)(periodNs * options.DeadlinePercent / 100);
        var k = options.Sandboxes;
        var pid = Environment.ProcessId;
        if (IpcWake.ShmCreate(Layout.ShmName(pid), (ulong)Layout.Size(k, options.Instances), out shm) != 0) {
            throw new InvalidOperationException(IpcWake.LastErrorMessage);
        }

        segment = IpcWake.ShmData(shm);
        var header = Layout.HeaderOf(segment);
        header->Sandboxes = k;
        header->Instances = options.Instances;
        header->MaxFrames = Layout.MaxFrames;
        header->Channels = Layout.Channels;
        header->Kind = options.Kind;
        header->Biquads = options.Biquads;
        header->BurnNs = options.BurnNs;
        header->SandboxSpinNs = options.SandboxSpinNs;
        header->PeriodNs = periodNs;
        header->RealTime = options.RealTime ? 1 : 0;
        header->HostPid = pid;
        if (options.Workgroup) {
            if (IpcWake.WorkgroupPublish(options.Device, Layout.WorkgroupService(pid), out workgroup) != 0) {
                throw new InvalidOperationException(IpcWake.WorkgroupLastErrorMessage);
            }

            header->Workgroup = 1;
        }

        header->Magic = Header.MagicValue;

        controls = new SandboxControl*[k];
        go = new nint[k];
        done = new nint[k];
        lastSignaled = new long[k];
        signaled = new bool[k];
        processes = new Process[k];
        for (var s = 0; s < k; s++) {
            controls[s] = Layout.Control(segment, s);
            if (IpcWake.Create(options.Kind, &controls[s]->Go, Layout.SemName(pid, s, true), 1, out go[s]) != 0
                || IpcWake.Create(options.Kind, &controls[s]->Done, Layout.SemName(pid, s, false), 1, out done[s])
                != 0) {
                throw new InvalidOperationException(IpcWake.LastErrorMessage);
            }
        }

        for (var s = 0; s < k; s++) {
            processes[s] = Process.Start(
                new ProcessStartInfo(Environment.ProcessPath!) {
                    ArgumentList = {
                        "sandbox",
                        "--shm",
                        Layout.ShmName(pid),
                        "--index",
                        s.ToString()
                    },
                    UseShellExecute = false
                }
            )!;
        }

        var until = Stopwatch.StartNew();
        for (var s = 0; s < k; s++) {
            while (Volatile.Read(ref controls[s]->Ready) == 0) {
                if (until.Elapsed.TotalSeconds > 10 || processes[s].HasExited) {
                    throw new InvalidOperationException($"sandbox {s} did not start");
                }

                Thread.Sleep(5);
            }
        }
    }

    public long PeriodNs => periodNs;

    public long DeadlineNs => deadlineNs;

    /// <summary>The sandboxes' real-time promotion: 0 when every one got it, else the first failure.</summary>
    public int SandboxRtStatus {
        get {
            foreach (var c in controls) {
                if (c->RtStatus != 0) {
                    return c->RtStatus;
                }
            }

            return 0;
        }
    }

    /// <summary>The sandboxes' workgroup join: 0 when every one joined, 1 when not asked to, else the failure.</summary>
    public int SandboxWorkgroupStatus {
        get {
            foreach (var c in controls) {
                if (c->WorkgroupStatus != 0) {
                    return c->WorkgroupStatus;
                }
            }

            return 0;
        }
    }

    public IEnumerable<int> SandboxPids {
        get {
            foreach (var p in processes) {
                yield return p.Id;
            }
        }
    }

    public void StartMeasuring() => measuring = true;

    public void StopMeasuring() => measuring = false;

    /// <summary>One engine callback. Real-time path: no allocation, no locks.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Cycle(int frames) {
        var start = IpcWake.Now();
        frames = Math.Min(frames, Layout.MaxFrames);
        var record = measuring;
        cycle++;
        var deadline = start + deadlineNs;
        var k = controls.Length;
        var missed = false;

        // Hand every sandbox its inputs and wake them all, so the sandboxes run in parallel.
        for (var s = 0; s < k; s++) {
            var c = controls[s];
            if (Volatile.Read(ref c->DoneCycle) != lastSignaled[s]) {
                // Still busy with an earlier cycle it missed: leave it alone and bypass it again.
                signaled[s] = false;
                if (record) {
                    BusySkips++;
                    Misses++;
                }

                missed = true;
                continue;
            }

            WriteInputs(s, frames);
            c->Cycle = cycle;
            c->Frames = frames;
            c->SignalNs = IpcWake.Now();
            IpcWake.Signal(go[s]);
            lastSignaled[s] = cycle;
            signaled[s] = true;
        }

        new Span<float>(mix).Clear();
        long lastDone = 0;
        for (var s = 0; s < k; s++) {
            if (!signaled[s]) {
                // Bypassed: a real host mixes its own copy of the dry input here. The spike only counts it.
                continue;
            }

            var c = controls[s];
            var ok = WaitDone(s, c, deadline);
            if (ok) {
                var observed = IpcWake.Now();
                lastDone = observed;
                if (record) {
                    Wake.Add(c->WakeNs - c->SignalNs);
                    SandboxDone.Add(c->DoneNs - c->SignalNs);
                }

                if (observed <= deadline) {
                    MixOutputs(s, frames, record);
                } else {
                    // Done, but the host only saw it after the deadline (it was descheduled): as good as a miss.
                    if (record) {
                        Late++;
                        Misses++;
                    }

                    missed = true;
                }
            } else {
                if (record) {
                    Misses++;
                }

                missed = true;
            }
        }

        if (record) {
            Callbacks++;
            if (missed) {
                MissedCallbacks++;
            }

            if (lastDone != 0) {
                AllDone.Add(lastDone - start);
            }

            Callback.Add(IpcWake.Now() - start);
        }

        phase = (phase + 2 * Math.PI * 440 / options.SampleRate * frames) % (2 * Math.PI);
    }

    bool WaitDone(int s, SandboxControl* c, long deadline) {
        while (true) {
            if (Volatile.Read(ref c->DoneCycle) == cycle) {
                return true;
            }

            var seen = IpcWake.Load(done[s]);
            if (Volatile.Read(ref c->DoneCycle) == cycle) {
                return true;
            }

            var now = IpcWake.Now();
            if (now >= deadline) {
                return false;
            }

            if (IpcWake.Wait(done[s], seen, (ulong)(deadline - now), (ulong)options.HostSpinNs) < 0) {
                return false;
            }
        }
    }

    void WriteInputs(int sandbox, int frames) {
        var k = controls.Length;
        var step = 2 * Math.PI * 440 / options.SampleRate;
        for (var i = sandbox; i < options.Instances; i += k) {
            var left = Layout.Buffer(segment, k, i, 0);
            var right = Layout.Buffer(segment, k, i, 1);
            var p = phase;
            for (var f = 0; f < frames; f++) {
                var v = (float)Math.Sin(p) * 0.25f;
                left[f] = v;
                right[f] = -v;
                p += step;
            }
        }
    }

    void MixOutputs(int sandbox, int frames, bool record) {
        var k = controls.Length;
        for (var i = sandbox; i < options.Instances; i += k) {
            for (var ch = 0; ch < Layout.Channels; ch++) {
                var x = Layout.Buffer(segment, k, i, ch);
                var m = ch * Layout.MaxFrames;
                for (var f = 0; f < frames; f++) {
                    mix[m + f] += x[f];
                }
            }

            if (record && options.Biquads == 0) {
                // Without filters the stub is a pure gain of 0.5 on the host's sine: check that the round trip
                // carried this cycle's data.
                var expected = (float)Math.Sin(phase) * 0.25f * 0.5f;
                if (Math.Abs(Layout.Buffer(segment, k, i, 0)[0] - expected) > 1e-6f) {
                    Corrupt++;
                }
            }
        }
    }

    /// <summary>Drives <see cref="Cycle" /> from a real-time thread on a timer, as a device would.</summary>
    public Thread StartNullClock() {
        var thread = new Thread(() => {
                var p = (ulong)periodNs;
                _ = IpcWake.RtPromote(p, p / 2, p);
                Anode.Audio.Native.NativeAudio.FlushDenormalsOnCurrentThread();
                var next = IpcWake.Now() + periodNs;
                while (!stopClock) {
                    IpcWake.SleepUntil(next);
                    Cycle(options.Frames);
                    next += periodNs;
                    var now = IpcWake.Now();
                    if (now > next) {
                        if (measuring) {
                            ClockOverruns++;
                        }

                        next = now + periodNs;
                    }
                }
            }
        ) { Name = "null clock" };
        thread.Start();
        return thread;
    }

    public void StopNullClock(Thread thread) {
        stopClock = true;
        thread.Join();
    }

    /// <summary>Opens the device with this host as its callback; output stays silent.</summary>
    public nint OpenDevice(out CthAudio.StreamConfig config) {
        if (!self.IsAllocated) {
            self = GCHandle.Alloc(this);
        }

        config = new() { SampleRate = options.SampleRate, BufferFrames = options.Frames, InputChannels = 0 };
        var user = (void*)GCHandle.ToIntPtr(self);
        if (CthAudio.Open(options.Device, ref config, &DeviceCallback, user, out var stream) != 0) {
            throw new InvalidOperationException(CthAudio.LastErrorMessage);
        }

        return stream;
    }

    [UnmanagedCallersOnly]
    static void DeviceCallback(
        void* user,
        float** inputs,
        int inputChannels,
        float** outputs,
        int outputChannels,
        int frames
    ) =>
        ((Host)GCHandle.FromIntPtr((nint)user).Target!).Cycle(frames);

    public void Dispose() {
        Volatile.Write(ref Layout.HeaderOf(segment)->Stop, 1);
        for (var s = 0; s < controls.Length; s++) {
            IpcWake.Signal(go[s]);
        }

        foreach (var p in processes) {
            if (!p.WaitForExit(2000)) {
                p.Kill();
            }

            p.Dispose();
        }

        for (var s = 0; s < controls.Length; s++) {
            IpcWake.Destroy(go[s], 1);
            IpcWake.Destroy(done[s], 1);
        }

        IpcWake.ShmClose(shm, 1);
        if (workgroup != 0) {
            IpcWake.WorkgroupUnpublish(workgroup);
        }

        if (self.IsAllocated) {
            self.Free();
        }
    }
}
