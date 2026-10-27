namespace Anode.Spikes.SandboxWake;

/// <summary>
///     The sandbox role: a stand-in for one <c>anode-plugin</c> process. Its audio thread waits on Go, runs the stub
///     DSP of every instance it owns in turn, and signals Done.
/// </summary>
static unsafe class Sandbox {
    public static int Run(string shmName, int index) {
        if (IpcWake.ShmOpen(shmName, out var shm) != 0) {
            Console.Error.WriteLine($"sandbox {index}: {IpcWake.LastErrorMessage}");
            return 1;
        }

        var segment = IpcWake.ShmData(shm);
        var header = Layout.HeaderOf(segment);
        if (header->Magic != Header.MagicValue) {
            Console.Error.WriteLine($"sandbox {index}: bad segment");
            return 1;
        }

        var control = Layout.Control(segment, index);
        var kind = header->Kind;
        if (IpcWake.Create(kind, &control->Go, Layout.SemName(header->HostPid, index, true), 0, out var go) != 0
            || IpcWake.Create(kind, &control->Done, Layout.SemName(header->HostPid, index, false), 0, out var done)
            != 0) {
            Console.Error.WriteLine($"sandbox {index}: {IpcWake.LastErrorMessage}");
            return 1;
        }

        var hostPid = header->HostPid;
        var watchdog = new Thread(() => {
                // Lifeline: a sandbox never outlives its host. Stop the audio thread as the host would.
                while (IpcWake.ProcessAlive(hostPid) == 1) {
                    Thread.Sleep(200);
                }

                Volatile.Write(ref header->Stop, 1);
                IpcWake.Signal(go);
            }
        ) { IsBackground = true, Name = "watchdog" };
        watchdog.Start();

        var dsp = new StubDsp(header, segment, index);
        var audio = new Thread(() => AudioLoop(header, control, go, done, dsp)) { Name = "sandbox audio" };
        audio.Start();
        audio.Join();
        IpcWake.Destroy(go, 0);
        IpcWake.Destroy(done, 0);
        IpcWake.ShmClose(shm, 0);
        return 0;
    }

    static void AudioLoop(Header* header, SandboxControl* control, nint go, nint done, StubDsp dsp) {
        Anode.Audio.Native.NativeAudio.FlushDenormalsOnCurrentThread();
        var period = (ulong)header->PeriodNs;
        control->RtStatus = header->RealTime != 0 ? IpcWake.RtPromote(period, period / 2, period) : 1;
        control->Pid = Environment.ProcessId;
        nint member = 0;
        control->WorkgroupStatus = 1;
        if (header->Workgroup != 0) {
            control->WorkgroupStatus = IpcWake.WorkgroupJoin(Layout.WorkgroupService(header->HostPid), out member);
            if (control->WorkgroupStatus != 0) {
                Console.Error.WriteLine($"sandbox workgroup: {IpcWake.WorkgroupLastErrorMessage}");
            }
        }

        var spin = (ulong)header->SandboxSpinNs;
        // A spinning sandbox polls Stop every 250 ms; a blocking one is woken by the host for it.
        var timeout = header->Kind == IpcWake.Spin ? 250_000_000UL : IpcWake.Forever;
        var seen = IpcWake.Load(go);
        Volatile.Write(ref control->Ready, 1);
        while (true) {
            var r = IpcWake.Wait(go, seen, timeout, spin);
            if (Volatile.Read(ref header->Stop) != 0) {
                if (member != 0) {
                    IpcWake.WorkgroupLeave(member);
                }

                return;
            }

            if (r != 0) {
                if (r < 0) {
                    Console.Error.WriteLine($"sandbox wait: {IpcWake.LastErrorMessage}");
                    return;
                }

                continue;
            }

            var wake = IpcWake.Now();
            seen = IpcWake.Load(go);
            control->WakeNs = wake;
            var cycle = Volatile.Read(ref control->Cycle);
            dsp.Process(control->Frames);
            control->DoneNs = IpcWake.Now();
            Volatile.Write(ref control->DoneCycle, cycle);
            IpcWake.Signal(done);
        }
    }
}

/// <summary>
///     The stand-in plugin: per instance a chain of biquads per channel, a gain of 0.5, and optionally a busy-wait of a
///     fixed number of nanoseconds to model a heavier plugin. All state is allocated up front.
/// </summary>
sealed unsafe class StubDsp {
    // A gentle low-pass (RBJ, fc = 8 kHz at 48 kHz, Q = 0.707).
    const float B0 = 0.155_051f;
    const float B1 = 0.310_102f;
    const float B2 = 0.155_051f;
    const float A1 = -0.620_204f;
    const float A2 = 0.240_408f;
    readonly float[] state;
    readonly float*[] buffers;
    readonly int biquads;
    readonly long burnNs;

    public StubDsp(Header* header, byte* segment, int index) {
        var mine = new List<int>();
        for (var i = 0; i < header->Instances; i++) {
            if (Layout.SandboxOf(i, header->Sandboxes) == index) {
                mine.Add(i);
            }
        }

        biquads = header->Biquads;
        burnNs = header->BurnNs;
        buffers = new float*[mine.Count * Layout.Channels];
        for (var n = 0; n < mine.Count; n++) {
            for (var c = 0; c < Layout.Channels; c++) {
                buffers[n * Layout.Channels + c] = Layout.Buffer(segment, header->Sandboxes, mine[n], c);
            }
        }

        state = new float[buffers.Length * Math.Max(1, biquads) * 2];
    }

    public void Process(int frames) {
        var instances = buffers.Length / Layout.Channels;
        for (var n = 0; n < instances; n++) {
            var start = burnNs > 0 ? IpcWake.Now() : 0;
            for (var c = 0; c < Layout.Channels; c++) {
                var slot = n * Layout.Channels + c;
                var x = buffers[slot];
                for (var b = 0; b < biquads; b++) {
                    Biquad(x, frames, ref state[(slot * biquads + b) * 2], ref state[(slot * biquads + b) * 2 + 1]);
                }

                for (var i = 0; i < frames; i++) {
                    x[i] *= 0.5f;
                }
            }

            if (burnNs > 0) {
                while (IpcWake.Now() - start < burnNs) {
                    Thread.SpinWait(8);
                }
            }
        }
    }

    static void Biquad(float* x, int frames, ref float z1, ref float z2) {
        for (var i = 0; i < frames; i++) {
            var input = x[i];
            var y = B0 * input + z1;
            z1 = B1 * input - A1 * y + z2;
            z2 = B2 * input - A2 * y;
            x[i] = y;
        }
    }
}
