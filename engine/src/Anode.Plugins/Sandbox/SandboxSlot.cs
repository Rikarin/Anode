using Anode.Dsp.Instruments;
using Anode.Plugins.Native;
using System.Runtime.CompilerServices;

namespace Anode.Plugins.Sandbox;

/// <summary>
///     The audio-thread side of one plugin instance in a sandbox: hands a block to the sandbox through the slot's
///     shared buffers, wakes it and waits for it with a deadline (S4's protocol, per slot). A slot the sandbox has not
///     finished by the deadline is a miss: the caller keeps its dry signal, and the slot is not signalled again until
///     the sandbox has caught up. No allocation, no lock.
/// </summary>
internal sealed unsafe class SandboxSlot {
    readonly SandboxProcess box;
    readonly SlotControl* control;
    readonly nint done;
    readonly float* inLeft, inRight, scLeft, scRight, outLeft, outRight;
    readonly ParamChange* paramArea;
    readonly PlugMidi* midiArea;
    readonly int maxFrames;
    readonly double[] pendingValues;
    readonly bool[] pendingDirty;
    readonly int[] dirty;
    int dirtyCount;
    bool pendingReset;
    long lastRequested;

    public SandboxSlot(SandboxProcess box, int slot, int parameters) {
        this.box = box;
        Index = slot;
        var s = box.Segment;
        control = SharedLayout.Control(s, slot);
        done = box.DoneWords[slot];
        inLeft = SharedLayout.Channel(s, slot, SharedLayout.InLeft);
        inRight = SharedLayout.Channel(s, slot, SharedLayout.InLeft + 1);
        scLeft = SharedLayout.Channel(s, slot, SharedLayout.SidechainLeft);
        scRight = SharedLayout.Channel(s, slot, SharedLayout.SidechainLeft + 1);
        outLeft = SharedLayout.Channel(s, slot, SharedLayout.OutLeft);
        outRight = SharedLayout.Channel(s, slot, SharedLayout.OutLeft + 1);
        paramArea = SharedLayout.Params(s, slot);
        midiArea = SharedLayout.Midi(s, slot);
        maxFrames = box.Header->MaxFrames;
        pendingValues = new double[Math.Max(parameters, 1)];
        pendingDirty = new bool[pendingValues.Length];
        dirty = new int[pendingValues.Length];
        lastRequested = Volatile.Read(ref control->Requested);
    }

    public SandboxProcess Box => box;
    public int Index { get; }

    /// <summary>Blocks handed over and finished in time.</summary>
    public long Completed;

    /// <summary>Blocks bypassed: late, still busy with an earlier late block, or the sandbox is gone.</summary>
    public long Misses;

    /// <summary>Of <see cref="Misses" />, blocks not even handed over because the sandbox was still busy.</summary>
    public long BusySkips;

    /// <summary>The last round trip (hand-over to seen done), in nanoseconds.</summary>
    public long LastRoundTripNs;

    /// <summary>Queues a parameter value for the next block (audio thread).</summary>
    public void SetParameter(int index, double value) {
        if ((uint)index >= (uint)pendingValues.Length) {
            return;
        }

        pendingValues[index] = value;
        if (!pendingDirty[index]) {
            pendingDirty[index] = true;
            dirty[dirtyCount++] = index;
        }
    }

    public void RequestReset() => pendingReset = true;

    /// <summary>
    ///     Runs one block through the plugin: input (effects) or MIDI (instruments) in, output into
    ///     <paramref name="outL" /> and <paramref name="outR" />. False on a miss, leaving the outputs untouched.
    /// </summary>
    public bool Run(
        ReadOnlySpan<float> inL,
        ReadOnlySpan<float> inR,
        ReadOnlySpan<float> scL,
        ReadOnlySpan<float> scR,
        ReadOnlySpan<MidiEvent> events,
        Span<float> outL,
        Span<float> outR,
        long deadlineNs
    ) =>
        Submit(inL, inR, scL, scR, events, outL.Length) && Complete(outL, outR, deadlineNs);

    long submitted;
    long submitStart;
    int submittedFrames;

    /// <summary>
    ///     The first half of <see cref="Run" />: hands the block to the sandbox and wakes it, without waiting. False (a
    ///     miss) when the sandbox is gone or still busy with a late block.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public bool Submit(
        ReadOnlySpan<float> inL,
        ReadOnlySpan<float> inR,
        ReadOnlySpan<float> scL,
        ReadOnlySpan<float> scR,
        ReadOnlySpan<MidiEvent> events,
        int length
    ) {
        submitted = 0;
        var frames = Math.Min(length, maxFrames);
        if (box.IsDead) {
            Misses++;
            return false;
        }

        if (Volatile.Read(ref control->Completed) != lastRequested) {
            // Still busy with a block it missed: leave it alone.
            Misses++;
            BusySkips++;
            return false;
        }

        if (!inL.IsEmpty) {
            inL[..frames].CopyTo(new(inLeft, frames));
            inR[..frames].CopyTo(new(inRight, frames));
        }

        var flags = 0;
        if (!scL.IsEmpty) {
            scL[..frames].CopyTo(new(scLeft, frames));
            scR[..frames].CopyTo(new(scRight, frames));
            flags |= SlotControl.FlagSidechain;
        }

        if (pendingReset) {
            flags |= SlotControl.FlagReset;
            pendingReset = false;
        }

        var n = Math.Min(dirtyCount, SharedLayout.MaxParams);
        for (var i = 0; i < n; i++) {
            var index = dirty[i];
            paramArea[i].Index = index;
            paramArea[i].Value = pendingValues[index];
            pendingDirty[index] = false;
        }

        // Any beyond the area's capacity wait for the next block.
        for (var i = n; i < dirtyCount; i++) {
            dirty[i - n] = dirty[i];
        }

        dirtyCount -= n;
        var midi = 0;
        foreach (var e in events) {
            if (midi == SharedLayout.MaxMidi) {
                break;
            }

            midiArea[midi++] = new() {
                Frame = Math.Clamp(e.Frame, 0, frames - 1), Status = e.Status, Data1 = e.Data1, Data2 = e.Data2
            };
        }

        control->Frames = frames;
        control->Flags = flags;
        control->ParamCount = n;
        control->MidiCount = midi;
        var start = IpcWake.Now();
        control->SignalNs = start;
        var request = lastRequested + 1;
        Volatile.Write(ref control->Requested, request);
        lastRequested = request;
        IpcWake.Signal(box.Go);
        submitted = request;
        submitStart = start;
        submittedFrames = frames;
        return true;
    }

    /// <summary>
    ///     The second half of <see cref="Run" />: waits for the submitted block until the deadline and copies its output.
    ///     False (a miss, outputs untouched) when nothing was submitted or it came too late.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public bool Complete(Span<float> outL, Span<float> outR, long deadlineNs) {
        var request = submitted;
        submitted = 0;
        if (request == 0) {
            return false; // Submit already counted the miss
        }

        var frames = Math.Min(submittedFrames, outL.Length);
        if (box.IsDead || !WaitDone(request, deadlineNs)) {
            Misses++;
            return false;
        }

        var seen = IpcWake.Now();
        LastRoundTripNs = seen - submitStart;
        if (seen > deadlineNs || box.IsDead) {
            // Finished, but the host only saw it after the deadline: as good as a miss.
            Misses++;
            return false;
        }

        new ReadOnlySpan<float>(outLeft, frames).CopyTo(outL);
        new ReadOnlySpan<float>(outRight, frames).CopyTo(outR);
        Completed++;
        return true;
    }

    bool WaitDone(long request, long deadlineNs) {
        while (true) {
            // A dead sandbox's segment may be unmapped already: never read it.
            if (box.IsDead) {
                return false;
            }

            if (Volatile.Read(ref control->Completed) == request) {
                return true;
            }

            var seen = IpcWake.Load(done);
            if (Volatile.Read(ref control->Completed) == request) {
                return true;
            }

            if (box.IsDead) {
                return false;
            }

            var now = IpcWake.Now();
            if (now >= deadlineNs) {
                return false;
            }

            if (IpcWake.Wait(done, seen, (ulong)(deadlineNs - now), 0) < 0) {
                return false;
            }
        }
    }

    /// <summary>True while the sandbox still owes the last block (control thread: wait before unloading).</summary>
    public bool Busy => !box.IsDead && Volatile.Read(ref control->Completed) != lastRequested;
}
