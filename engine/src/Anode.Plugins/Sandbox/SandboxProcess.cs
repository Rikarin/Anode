using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Anode.Plugins.Native;

namespace Anode.Plugins.Sandbox;

/// <summary>
///     The host's side of one <c>anode-plugin</c> process: its shared segment and wake words, its control pipe
///     (requests and replies, events), its slots, and the watchdog that notices it crashed or hung.
/// </summary>
internal sealed unsafe class SandboxProcess : IDisposable {
    readonly nint shm;
    readonly Process process;
    readonly ConcurrentDictionary<int, TaskCompletionSource<SandboxReply>> pending = new();
    readonly bool[] used;
    readonly Lock gate = new();
    readonly Queue<string> stderrTail = new();
    readonly Action<string>? log;
    readonly TimeSpan hangTimeout;
    readonly Thread monitor;
    int nextId;
    int dying;
    volatile bool dead;

    public SandboxProcess(
        string executable,
        string group,
        int index,
        int maxSlots,
        int maxFrames,
        long periodNs,
        int workgroup,
        TimeSpan hangTimeout,
        Action<string>? log
    ) {
        Group = group;
        this.log = log;
        this.hangTimeout = hangTimeout;
        var pid = Environment.ProcessId;
        Name = SharedLayout.ShmName(pid, index);
        if (IpcWake.ShmCreate(Name, (ulong)SharedLayout.Size(maxSlots, maxFrames), out shm) != 0) {
            throw new InvalidOperationException("sandbox segment: " + IpcWake.LastError);
        }

        Segment = IpcWake.ShmData(shm);
        var h = SharedLayout.Header(Segment);
        h->Version = SandboxHeader.CurrentVersion;
        h->MaxSlots = maxSlots;
        h->MaxFrames = maxFrames;
        h->HostPid = pid;
        h->PeriodNs = periodNs;
        h->Workgroup = workgroup;
        h->Magic = SandboxHeader.MagicValue;
        Go = IpcWake.CreateWord(SharedLayout.Go(Segment));
        DoneWords = new nint[maxSlots];
        for (var s = 0; s < maxSlots; s++) {
            DoneWords[s] = IpcWake.CreateWord(&SharedLayout.Control(Segment, s)->Done);
        }

        used = new bool[maxSlots];
        var dll = executable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(dll ? "dotnet" : executable) {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8
        };
        if (dll) {
            start.ArgumentList.Add("exec");
            start.ArgumentList.Add(executable);
        }

        start.ArgumentList.Add("host");
        start.ArgumentList.Add("--shm");
        start.ArgumentList.Add(Name);
        process = Process.Start(start) ?? throw new InvalidOperationException("could not start " + executable);
        Pid = process.Id;
        process.StandardInput.AutoFlush = true;
        new Thread(ReadReplies) { IsBackground = true, Name = "anode-plugin replies " + Pid }.Start();
        new Thread(ReadErrors) { IsBackground = true, Name = "anode-plugin stderr " + Pid }.Start();
        monitor = new(Monitor) { IsBackground = true, Name = "anode-plugin watchdog " + Pid };
        monitor.Start();

        var until = Stopwatch.StartNew();
        while (Volatile.Read(ref h->AudioReady) == 0) {
            if (dead || until.Elapsed > TimeSpan.FromSeconds(30)) {
                var why = DeathReason ?? "did not start";
                Kill("did not start");
                throw new InvalidOperationException($"anode-plugin {why}");
            }

            Thread.Sleep(2);
        }
    }

    public string Group { get; }
    public string Name { get; }
    public int Pid { get; }
    public byte* Segment { get; }
    public nint Go { get; }
    public nint[] DoneWords { get; }
    public SandboxHeader* Header => SharedLayout.Header(Segment);

    /// <summary>True once the process has exited or was killed: every slot is bypassed.</summary>
    public bool IsDead => dead;

    public string? DeathReason { get; private set; }

    /// <summary>Raised (on a background thread) once when the sandbox dies.</summary>
    public event Action<SandboxProcess>? Died;

    /// <summary>Events from the sandbox: (slot, event).</summary>
    public event Action<int, SandboxReply>? EventReceived;

    public int SlotsInUse {
        get {
            lock (gate) {
                return used.Count(u => u);
            }
        }
    }

    public int AllocateSlot() {
        lock (gate) {
            for (var s = 0; s < used.Length; s++) {
                if (!used[s]) {
                    used[s] = true;
                    return s;
                }
            }
        }

        return -1;
    }

    public void FreeSlot(int slot) {
        lock (gate) {
            used[slot] = false;
        }
    }

    /// <summary>Sends a request and waits for its reply (control thread). A sandbox that does not answer is killed.</summary>
    public SandboxReply Request(SandboxRequest request, TimeSpan timeout) {
        if (dead) {
            return new() { Ok = false, Error = DeathReason ?? "the sandbox is gone" };
        }

        request.Id = Interlocked.Increment(ref nextId);
        var reply = new TaskCompletionSource<SandboxReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[request.Id] = reply;
        try {
            lock (gate) {
                process.StandardInput.WriteLine(SandboxJson.Write(request));
            }
        } catch (IOException) {
            pending.TryRemove(request.Id, out _);
            return new() { Ok = false, Error = DeathReason ?? "the sandbox is gone" };
        }

        if (!reply.Task.Wait(timeout)) {
            pending.TryRemove(request.Id, out _);
            Kill($"did not answer '{request.Op}' within {timeout.TotalSeconds:0.#} s");
            return new() { Ok = false, Error = DeathReason };
        }

        return reply.Task.Result;
    }

    void ReadReplies() {
        try {
            while (process.StandardOutput.ReadLine() is { } line) {
                SandboxReply? reply;
                try {
                    reply = SandboxJson.ReadReply(line);
                } catch (System.Text.Json.JsonException) {
                    log?.Invoke($"anode-plugin {Pid}: {line}");
                    continue;
                }

                if (reply is null) {
                    continue;
                }

                if (reply.Event is not null) {
                    EventReceived?.Invoke(reply.Slot, reply);
                } else if (pending.TryRemove(reply.Id, out var waiter)) {
                    waiter.TrySetResult(reply);
                }
            }
        } catch (IOException) {
            // exited
        }
    }

    void ReadErrors() {
        try {
            while (process.StandardError.ReadLine() is { } line) {
                lock (stderrTail) {
                    stderrTail.Enqueue(line);
                    if (stderrTail.Count > 20) {
                        stderrTail.Dequeue();
                    }
                }

                log?.Invoke($"anode-plugin {Pid}: {line}");
            }
        } catch (IOException) {
            // exited
        }
    }

    /// <summary>Watches the process: its exit, and slots stuck on one request longer than the hang timeout.</summary>
    void Monitor() {
        var stuckSince = new long[used.Length];
        var stuckOn = new long[used.Length];
        while (!dead) {
            if (process.WaitForExit(100)) {
                var code = process.ExitCode;
                Die(code > 128 ? $"crashed (signal {code - 128})" : $"exited ({code})");
                return;
            }

            var now = IpcWake.Now();
            for (var s = 0; s < used.Length; s++) {
                var c = SharedLayout.Control(Segment, s);
                var requested = Volatile.Read(ref c->Requested);
                if (requested == Volatile.Read(ref c->Completed)) {
                    stuckSince[s] = 0;
                    continue;
                }

                if (stuckOn[s] != requested || stuckSince[s] == 0) {
                    stuckOn[s] = requested;
                    stuckSince[s] = now;
                } else if (now - stuckSince[s] > hangTimeout.Ticks * 100) {
                    Kill($"hung (a plugin has not returned for {hangTimeout.TotalSeconds:0.#} s)");
                    return;
                }
            }
        }
    }

    /// <summary>Ends the process (a hang, a protocol failure, disposal) and treats it as dead.</summary>
    public void Kill(string reason) {
        try {
            if (!process.HasExited) {
                process.Kill();
            }
        } catch (InvalidOperationException) {
            // already gone
        }

        Die(reason);
    }

    void Die(string reason) {
        if (Interlocked.Exchange(ref dying, 1) != 0) {
            return;
        }

        string tail;
        lock (stderrTail) {
            tail = string.Join(" | ", stderrTail.TakeLast(3));
        }

        DeathReason = tail.Length > 0 ? $"{reason}: {tail}" : reason;
        dead = true;
        // Wake any host thread waiting on a slot: it sees the sandbox dead and bypasses.
        foreach (var d in DoneWords) {
            IpcWake.Signal(d);
        }

        foreach (var waiter in pending.Values) {
            waiter.TrySetResult(new() { Ok = false, Error = DeathReason });
        }

        Died?.Invoke(this);
    }

    /// <summary>Asks the sandbox to quit, then kills it if it does not.</summary>
    public void Dispose() {
        if (!dead) {
            Request(new() { Op = "quit" }, TimeSpan.FromSeconds(2));
            try {
                process.StandardInput.Close();
            } catch (IOException) {
                // gone
            }

            if (!process.WaitForExit(2000)) {
                Kill("did not quit");
            } else {
                Die("quit");
            }
        }

        monitor.Join(1000);
        Volatile.Write(ref Header->Stop, 1);
        foreach (var d in DoneWords) {
            IpcWake.Destroy(d, 0);
        }

        IpcWake.Destroy(Go, 0);
        IpcWake.ShmClose(shm, 1);
        process.Dispose();
    }
}
