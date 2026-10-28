using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Anode.Plugins.Native;
using Microsoft.Win32.SafeHandles;

namespace Anode.Plugins.Sandbox;

/// <summary>
///     The sandbox side of <c>anode-plugin</c> (<c>anode-plugin host --shm NAME</c>): hosts the plugins of one group in
///     the slots of a shared segment. Its threads:
///     <list type="bullet">
///         <item>the main thread runs the platform's main loop (an_plug_main_run): every plugin call except processing;</item>
///         <item>the control thread reads requests from stdin and runs them on the main thread;</item>
///         <item>
///             the audio thread (real time, joined to the device's workgroup when the host publishes one) waits on the
///             go word, processes every slot with a pending request and signals each slot's done word;
///         </item>
///         <item>the watchdog stops the sandbox when the host dies; the idle pump keeps editors in sync.</item>
///     </list>
/// </summary>
internal sealed unsafe class SandboxServer {
    readonly nint shm;
    readonly byte* segment;
    readonly SandboxHeader* header;
    readonly nint go;
    readonly Slot[] slots;
    readonly Stream protocol;
    readonly Lock writeGate = new();
    volatile bool stopping;
    volatile int processingSlot = -1;

    sealed class Slot {
        public nint Plug;
        public nint Done;
        public bool Sidechain;
        public bool Instrument;
        public float** In;
        public float** Sidechains;
        public float** Out;
        public GCHandle Self;
        public int Index;
    }

    SandboxServer(nint shm, Stream protocol) {
        this.shm = shm;
        this.protocol = protocol;
        segment = IpcWake.ShmData(shm);
        header = SharedLayout.Header(segment);
        go = IpcWake.CreateWord(SharedLayout.Go(segment));
        slots = new Slot[header->MaxSlots];
        for (var s = 0; s < slots.Length; s++) {
            var slot = new Slot { Index = s, Done = IpcWake.CreateWord(&SharedLayout.Control(segment, s)->Done) };
            slot.In = (float**)NativeMemory.AllocZeroed(2, (nuint)sizeof(float*));
            slot.Sidechains = (float**)NativeMemory.AllocZeroed(2, (nuint)sizeof(float*));
            slot.Out = (float**)NativeMemory.AllocZeroed(2, (nuint)sizeof(float*));
            for (var c = 0; c < 2; c++) {
                slot.In[c] = SharedLayout.Channel(segment, s, SharedLayout.InLeft + c);
                slot.Sidechains[c] = SharedLayout.Channel(segment, s, SharedLayout.SidechainLeft + c);
                slot.Out[c] = SharedLayout.Channel(segment, s, SharedLayout.OutLeft + c);
            }

            slots[s] = slot;
        }
    }

    /// <summary>Runs the sandbox until the host asks it to quit, closes its stdin or dies. Main thread.</summary>
    public static int Run(string shmName) {
        var fd = PlugNative.RedirectStdout();
        if (fd < 0) {
            Console.Error.WriteLine("anode-plugin: " + PlugNative.LastError);
            return 1;
        }

        using var protocol = new FileStream(new SafeFileHandle(fd, true), FileAccess.Write, 1);
        if (IpcWake.ShmOpen(shmName, out var shm) != 0) {
            Console.Error.WriteLine("anode-plugin: " + IpcWake.LastError);
            return 1;
        }

        var h = SharedLayout.Header(IpcWake.ShmData(shm));
        if (h->Magic != SandboxHeader.MagicValue || h->Version != SandboxHeader.CurrentVersion) {
            Console.Error.WriteLine("anode-plugin: not a sandbox segment");
            return 1;
        }

        var server = new SandboxServer(shm, protocol);
        return server.Serve();
    }

    int Serve() {
        current = this;
        header->SandboxPid = Environment.ProcessId;
        var audio = new Thread(AudioLoop) { Name = "anode-plugin audio", IsBackground = true };
        audio.Start();
        var control = new Thread(ControlLoop) { Name = "anode-plugin control", IsBackground = true };
        control.Start();
        var watchdog = new Thread(Watchdog) { Name = "anode-plugin watchdog", IsBackground = true };
        watchdog.Start();
        var idle = new Thread(IdlePump) { Name = "anode-plugin idle", IsBackground = true };
        idle.Start();
        PlugNative.MainRun();
        Stop();
        audio.Join(1000);
        foreach (var slot in slots) {
            if (slot.Plug != 0) {
                PlugNative.Close(slot.Plug);
                slot.Plug = 0;
            }
        }

        IpcWake.ShmClose(shm, 0);
        return 0;
    }

    void Stop() {
        if (stopping) {
            return;
        }

        stopping = true;
        Volatile.Write(ref header->Stop, 1);
        IpcWake.Signal(go);
        PlugNative.MainQuit();
    }

    // ---- Control ---------------------------------------------------------------------------------------------------

    void ControlLoop() {
        using var input = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        while (!stopping) {
            string? line;
            try {
                line = input.ReadLine();
            } catch (IOException) {
                line = null;
            }

            if (line is null) {
                break; // the host closed our stdin: it is gone or done with us
            }

            if (line.Length == 0) {
                continue;
            }

            var request = SandboxJson.ReadRequest(line);
            if (request is null) {
                continue;
            }

            if (request.Op == "quit") {
                Reply(new() { Id = request.Id, Ok = true });
                break;
            }

            SandboxReply reply;
            try {
                reply = OnMain(() => Handle(request));
            }
#pragma warning disable CA1031 // any failure goes back to the host as the reply's error
            catch (Exception e) {
#pragma warning restore CA1031
                reply = new() { Ok = false, Error = e.Message };
            }

            reply.Id = request.Id;
            Reply(reply);
        }

        Stop();
    }

    SandboxReply Handle(SandboxRequest r) {
        switch (r.Op) {
            case "ping":
                return new() { Ok = true };
            case "configure":
                return new() { Ok = true }; // the audio thread re-reads the header's period and workgroup itself
            case "load":
                return Load(r);
            case "unload":
                Unload(r.Slot);
                return new() { Ok = true };
            case "saveState": {
                var plug = PlugOf(r.Slot);
                var state = PlugNative.SaveState(plug, out var error);
                return state is null ? Fail(error) : new() { Ok = true, State = Convert.ToBase64String(state) };
            }
            case "loadState": {
                var plug = PlugOf(r.Slot);
                if (!PlugNative.LoadState(plug, Convert.FromBase64String(r.State ?? ""), out var error)) {
                    return Fail(error);
                }

                return new() { Ok = true, Values = Values(plug), Latency = PlugNative.Latency(plug) };
            }
            case "values": {
                var plug = PlugOf(r.Slot);
                return new() { Ok = true, Values = Values(plug), Latency = PlugNative.Latency(plug) };
            }
            case "editor": {
                var plug = PlugOf(r.Slot);
                if (!r.Open) {
                    PlugNative.EditorClose(plug);
                    return new() { Ok = true };
                }

                var window = PlugNative.EditorOpen(
                    plug,
                    r.Title ?? "Plugin",
                    r.Parent,
                    r.X,
                    r.Y,
                    r.Floating ? PlugNative.EditorFloating : 0
                );
                return window < 0 ? Fail(PlugNative.LastError) : new() { Ok = true, Window = window };
            }
            case "crash":
                // Tests: the whole sandbox goes down as if a plugin crashed it.
                PlugNative.Crash();
                return new() { Ok = false };
            default:
                return Fail("unknown op " + r.Op);
        }
    }

    static SandboxReply Fail(string? error) => new() { Ok = false, Error = error ?? "failed" };

    nint PlugOf(int slot) =>
        slot >= 0 && slot < slots.Length && slots[slot].Plug != 0
            ? slots[slot].Plug
            : throw new InvalidOperationException($"slot {slot} is empty");

    static double[] Values(nint plug) {
        var n = PlugNative.ParamCount(plug);
        var values = new double[n];
        for (var i = 0; i < n; i++) {
            values[i] = PlugNative.ParamGet(plug, i);
        }

        return values;
    }

    SandboxReply Load(SandboxRequest r) {
        if (r.Slot < 0 || r.Slot >= slots.Length) {
            return Fail("no such slot");
        }

        var slot = slots[r.Slot];
        if (slot.Plug != 0) {
            Unload(r.Slot);
        }

        if (PlugNative.Open(r.Format, r.Path, r.Plugin ?? "", out var plug) != 0) {
            return Fail(PlugNative.LastError);
        }

        var maxFrames = Math.Min(r.MaxFrames > 0 ? r.MaxFrames : header->MaxFrames, header->MaxFrames);
        if (PlugNative.Activate(plug, r.SampleRate, maxFrames, r.Sidechain ? 1 : 0) != 0) {
            var error = PlugNative.LastError;
            PlugNative.Close(plug);
            return Fail(error);
        }

        slot.Self = GCHandle.Alloc(slot);
        PlugNative.SetEventCallback(plug, &OnPluginEvent, GCHandle.ToIntPtr(slot.Self));
        slot.Sidechain = r.Sidechain;
        slot.Instrument = PlugNative.IsInstrument(plug) != 0;
        if (r.Values is { } values && r.Indices is { } indices) {
            for (var i = 0; i < values.Length && i < indices.Length; i++) {
                PlugNative.ParamSet(plug, indices[i], values[i]);
            }
        }

        // A fresh slot: nothing pending, nothing completed.
        var c = SharedLayout.Control(segment, r.Slot);
        Volatile.Write(ref c->Completed, Volatile.Read(ref c->Requested));
        Volatile.Write(ref slot.Plug, plug);
        return new() {
            Ok = true,
            Description = PlugNative.Describe(plug),
            Latency = PlugNative.Latency(plug),
            Values = Values(plug)
        };
    }

    void Unload(int index) {
        var slot = slots[index];
        var plug = slot.Plug;
        if (plug == 0) {
            return;
        }

        Volatile.Write(ref slot.Plug, 0);
        // Let the audio thread leave the slot if it is processing it.
        var spins = 0;
        while (processingSlot == index && spins++ < 5_000) {
            Thread.Sleep(1);
        }

        PlugNative.Close(plug);
        if (slot.Self.IsAllocated) {
            slot.Self.Free();
        }
    }

    [UnmanagedCallersOnly]
    static void OnPluginEvent(nint user, int kind, int index, double value) {
        var slot = (Slot)GCHandle.FromIntPtr(user).Target!;
        var server = current!;
        switch (kind) {
            case 0:
                server.Reply(new() { Event = "param", Slot = slot.Index, Index = index, Value = value });
                break;
            case 1:
                server.Reply(new() { Event = "latency", Slot = slot.Index, Latency = (int)value });
                break;
            case 2:
                server.Reply(new() { Event = "editorClosed", Slot = slot.Index });
                break;
        }
    }

    static SandboxServer? current;

    void Reply(SandboxReply reply) {
        var bytes = Encoding.UTF8.GetBytes(SandboxJson.Write(reply) + "\n");
        lock (writeGate) {
            try {
                protocol.Write(bytes);
                protocol.Flush();
            } catch (IOException) {
                // The host is gone; the watchdog stops us.
            }
        }
    }

    // ---- Main thread -------------------------------------------------------------------------------------------------

    static T OnMain<T>(Func<T> work) {
        T? result = default;
        var call = new MainCall(() => result = work());
        var handle = GCHandle.Alloc(call);
        try {
            PlugNative.MainCall(&MainCall.Run, GCHandle.ToIntPtr(handle));
        } finally {
            handle.Free();
        }

        call.Failure?.Throw();
        return result!;
    }

    sealed class MainCall(Action work) {
        public readonly Action Work = work;
        public System.Runtime.ExceptionServices.ExceptionDispatchInfo? Failure;

        [UnmanagedCallersOnly]
        public static void Run(nint ctx) {
            var call = (MainCall)GCHandle.FromIntPtr(ctx).Target!;
            try {
                call.Work();
            }
#pragma warning disable CA1031 // rethrown on the calling thread
            catch (Exception e) {
#pragma warning restore CA1031
                call.Failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e);
            }
        }
    }

    void IdlePump() {
        while (!stopping) {
            Thread.Sleep(33);
            try {
                OnMain(() => {
                        foreach (var slot in slots) {
                            if (slot.Plug != 0) {
                                PlugNative.Idle(slot.Plug);
                            }
                        }

                        return 0;
                    }
                );
            } catch (InvalidOperationException) {
                // shutting down
            }
        }
    }

    void Watchdog() {
        var host = header->HostPid;
        while (!stopping) {
            if (IpcWake.ProcessAlive(host) != 1) {
                // A sandbox never outlives its host.
                Stop();
                Environment.Exit(0);
            }

            Thread.Sleep(200);
        }
    }

    // ---- Audio -------------------------------------------------------------------------------------------------------

    void AudioLoop() {
        PlugNative.FlushDenormals();
        var generation = -1;
        nint member = 0;
        header->WorkgroupStatus = 1;
        var seen = IpcWake.Load(go);
        Volatile.Write(ref header->AudioReady, 1);
        while (true) {
            if (Volatile.Read(ref header->ConfigGeneration) != generation) {
                generation = Volatile.Read(ref header->ConfigGeneration);
                member = Configure(member);
            }

            var r = IpcWake.Wait(go, seen, IpcWake.Forever, 0);
            if (Volatile.Read(ref header->Stop) != 0 || stopping) {
                break;
            }

            if (r < 0) {
                Thread.Sleep(1);
                continue;
            }

            seen = IpcWake.Load(go);
            header->Wakes++;
            var wake = IpcWake.Now();
            for (var s = 0; s < slots.Length; s++) {
                ProcessSlot(s, wake);
            }
        }

        if (member != 0) {
            IpcWake.WorkgroupLeave(member);
        }
    }

    nint Configure(nint member) {
        var period = Volatile.Read(ref header->PeriodNs);
        if (period <= 0) {
            period = 1_333_333; // 64 frames at 48 kHz
        }

        header->RtStatus = IpcWake.RtPromote((ulong)period, (ulong)period / 2, (ulong)period);
        if (member != 0) {
            IpcWake.WorkgroupLeave(member);
            member = 0;
        }

        var wg = Volatile.Read(ref header->Workgroup);
        if (wg > 0) {
            header->WorkgroupStatus = IpcWake.WorkgroupJoin(
                SharedLayout.WorkgroupService(header->HostPid, wg),
                out member
            );
            if (header->WorkgroupStatus != 0) {
                member = 0;
            }
        } else {
            header->WorkgroupStatus = 1;
        }

        return member;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    void ProcessSlot(int s, long wake) {
        var c = SharedLayout.Control(segment, s);
        var requested = Volatile.Read(ref c->Requested);
        if (requested == Volatile.Read(ref c->Completed)) {
            return;
        }

        var slot = slots[s];
        processingSlot = s;
        var plug = Volatile.Read(ref slot.Plug);
        if (plug != 0) {
            c->WakeNs = wake;
            var frames = Math.Min(c->Frames, header->MaxFrames);
            var p = SharedLayout.Params(segment, s);
            for (var i = 0; i < c->ParamCount && i < SharedLayout.MaxParams; i++) {
                PlugNative.ParamSet(plug, p[i].Index, p[i].Value);
            }

            if ((c->Flags & SlotControl.FlagReset) != 0) {
                PlugNative.Reset(plug);
            }

            var sidechain = (c->Flags & SlotControl.FlagSidechain) != 0 ? slot.Sidechains : null;
            var midiCount = Math.Min(c->MidiCount, SharedLayout.MaxMidi);
            c->Status = PlugNative.Process(
                plug,
                slot.In,
                sidechain,
                slot.Out,
                frames,
                midiCount > 0 ? SharedLayout.Midi(segment, s) : null,
                midiCount
            );
        } else {
            c->Status = -1;
        }

        processingSlot = -1;
        c->DoneNs = IpcWake.Now();
        Volatile.Write(ref c->Completed, requested);
        IpcWake.Signal(slot.Done);
    }

    // ---- Scanning ----------------------------------------------------------------------------------------------------

    /// <summary>
    ///     <c>anode-plugin scan ITEM...</c>: describes each item (<c>vst3:PATH</c>, <c>au:ID</c>, <c>au:*</c> for the
    ///     component list, <c>clap:PATH</c>) on the protocol descriptor: a <c>begin</c> event, then a <c>scanned</c>
    ///     event with the plugins' JSON or an error. A crash leaves the last <c>begin</c> without its result.
    /// </summary>
    public static int Scan(IReadOnlyList<string> items) {
        var fd = PlugNative.RedirectStdout();
        using var protocol = new FileStream(new SafeFileHandle(fd, true), FileAccess.Write, 1);
        void Write(SandboxReply reply) {
            protocol.Write(Encoding.UTF8.GetBytes(SandboxJson.Write(reply) + "\n"));
            protocol.Flush();
        }

        foreach (var item in items) {
            Write(new() { Event = "begin", Item = item });
            var colon = item.IndexOf(':', StringComparison.Ordinal);
            var kind = colon > 0 ? item[..colon] : "";
            var target = colon > 0 ? item[(colon + 1)..] : item;
            var format = kind switch {
                "vst3" => PlugNative.Vst3,
                "au" => PlugNative.Au,
                "clap" => PlugNative.Clap,
                _ => 0
            };
            string? error = "unknown plugin format";
            var json = format == 0 ? null : PlugNative.Scan(format, target == "*" ? null : target, target != "*", out error);
            Write(
                json is null
                    ? new() { Event = "scanned", Item = item, Ok = false, Error = error }
                    : new() { Event = "scanned", Item = item, Ok = true, Plugins = json }
            );
        }

        return 0;
    }
}
