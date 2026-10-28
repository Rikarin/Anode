using Anode.Dsp.Instruments;
using Anode.Plugins.Native;
using Anode.Plugins.Sandbox;

namespace Anode.Plugins.Hosting;

/// <summary>Where one plugin instance runs: in a sandbox slot, or in this process.</summary>
internal abstract class PluginBackend : IDisposable {
    /// <summary>Loads and activates the plugin (control thread), setting parameters <paramref name="indices" /> to <paramref name="values" />.</summary>
    public abstract bool Load(
        double sampleRate,
        int maxFrames,
        bool sidechain,
        int[] indices,
        double[] values,
        out PluginDescription? description,
        out int latency,
        out string? error
    );

    public abstract void Unload();

    /// <summary>One block (audio thread); false on a miss (outputs untouched).</summary>
    public abstract bool Process(
        ReadOnlySpan<float> inL,
        ReadOnlySpan<float> inR,
        ReadOnlySpan<float> scL,
        ReadOnlySpan<float> scR,
        ReadOnlySpan<MidiEvent> events,
        Span<float> outL,
        Span<float> outR,
        long deadlineNs
    );

    public abstract void SetParameter(int index, double value);

    public abstract void Reset();

    public abstract byte[]? SaveState(out string? error);

    public abstract bool LoadState(ReadOnlySpan<byte> state, out double[]? values, out int latency, out string? error);

    /// <summary>Opens the editor window; its window number, or negative.</summary>
    public abstract int OpenEditor(string title, EditorPlacement placement, out string? error);

    public abstract void CloseEditor();

    /// <summary>True when the backend can no longer process (its sandbox died).</summary>
    public abstract bool IsFailed { get; }

    public abstract void Dispose();

    public virtual long Misses => 0;
    public virtual long BusySkips => 0;
    public virtual long Completed => 0;
    public virtual long LastRoundTripNs => 0;
    public virtual int SandboxPid => 0;
}

/// <summary>An instance in a sandbox slot.</summary>
internal sealed class SandboxBackend(SandboxProcess box, int slot, PluginEntry entry, TimeSpan loadTimeout)
    : PluginBackend {
    static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    readonly SandboxSlot audio = new(box, slot, entry.Description.Parameters.Count);
    bool loaded;

    public SandboxProcess Box => box;
    public int Slot => slot;

    public override bool Load(
        double sampleRate,
        int maxFrames,
        bool sidechain,
        int[] indices,
        double[] values,
        out PluginDescription? description,
        out int latency,
        out string? error
    ) {
        var reply = box.Request(
            new() {
                Op = "load",
                Slot = slot,
                Format = (int)entry.Format,
                Path = entry.Path.Length > 0 ? entry.Path : null,
                Plugin = entry.Description.Id,
                SampleRate = sampleRate,
                MaxFrames = maxFrames,
                Sidechain = sidechain,
                Indices = indices,
                Values = values
            },
            loadTimeout
        );
        description = reply.Description is { } d ? SandboxJson.ReadDescription(d) : null;
        latency = reply.Latency;
        error = reply.Ok ? null : reply.Error;
        loaded = reply.Ok;
        return reply.Ok;
    }

    public override void Unload() {
        if (!loaded) {
            return;
        }

        loaded = false;
        box.Request(new() { Op = "unload", Slot = slot }, RequestTimeout);
    }

    public override bool Process(
        ReadOnlySpan<float> inL,
        ReadOnlySpan<float> inR,
        ReadOnlySpan<float> scL,
        ReadOnlySpan<float> scR,
        ReadOnlySpan<MidiEvent> events,
        Span<float> outL,
        Span<float> outR,
        long deadlineNs
    ) =>
        loaded && audio.Run(inL, inR, scL, scR, events, outL, outR, deadlineNs);

    public override void SetParameter(int index, double value) => audio.SetParameter(index, value);

    public override void Reset() => audio.RequestReset();

    public override byte[]? SaveState(out string? error) {
        var reply = box.Request(new() { Op = "saveState", Slot = slot }, RequestTimeout);
        error = reply.Ok ? null : reply.Error;
        return reply.Ok && reply.State is { } s ? Convert.FromBase64String(s) : null;
    }

    public override bool LoadState(ReadOnlySpan<byte> state, out double[]? values, out int latency, out string? error) {
        var reply = box.Request(
            new() { Op = "loadState", Slot = slot, State = Convert.ToBase64String(state) },
            RequestTimeout
        );
        values = reply.Values;
        latency = reply.Latency;
        error = reply.Ok ? null : reply.Error;
        return reply.Ok;
    }

    public override int OpenEditor(string title, EditorPlacement placement, out string? error) {
        var reply = box.Request(
            new() {
                Op = "editor",
                Slot = slot,
                Open = true,
                Title = title,
                Parent = placement.ParentWindow,
                X = placement.X,
                Y = placement.Y,
                Floating = placement.Floating
            },
            RequestTimeout
        );
        error = reply.Ok ? null : reply.Error;
        return reply.Ok ? reply.Window : -1;
    }

    public override void CloseEditor() => box.Request(new() { Op = "editor", Slot = slot, Open = false }, RequestTimeout);

    public override bool IsFailed => box.IsDead;

    public override long Misses => audio.Misses;
    public override long BusySkips => audio.BusySkips;
    public override long Completed => audio.Completed;
    public override long LastRoundTripNs => audio.LastRoundTripNs;
    public override int SandboxPid => box.Pid;

    /// <summary>True while the sandbox still owes a late block.</summary>
    public bool Busy => audio.Busy;

    public override void Dispose() {
        if (!box.IsDead) {
            // A late block still running in the sandbox: give it a moment before the slot is reused.
            for (var i = 0; i < 200 && audio.Busy && !box.IsDead; i++) {
                Thread.Sleep(5);
            }

            Unload();
        }
    }
}

/// <summary>An instance in this process (<see cref="SandboxMode.InHost" />): no isolation, no deadline.</summary>
internal sealed unsafe class InHostBackend(PluginEntry entry) : PluginBackend {
    nint plug;
    float[] scratchLeft = [];
    float[] scratchRight = [];

    public override bool Load(
        double sampleRate,
        int maxFrames,
        bool sidechain,
        int[] indices,
        double[] values,
        out PluginDescription? description,
        out int latency,
        out string? error
    ) {
        description = null;
        latency = 0;
        if (PlugNative.Open((int)entry.Format, entry.Path.Length > 0 ? entry.Path : null, entry.Description.Id, out plug) != 0) {
            error = PlugNative.LastError;
            plug = 0;
            return false;
        }

        if (PlugNative.Activate(plug, sampleRate, maxFrames, sidechain ? 1 : 0) != 0) {
            error = PlugNative.LastError;
            PlugNative.Close(plug);
            plug = 0;
            return false;
        }

        for (var i = 0; i < indices.Length; i++) {
            PlugNative.ParamSet(plug, indices[i], values[i]);
        }

        scratchLeft = new float[maxFrames];
        scratchRight = new float[maxFrames];
        description = SandboxJson.ReadDescription(PlugNative.Describe(plug));
        latency = PlugNative.Latency(plug);
        error = null;
        return true;
    }

    public override void Unload() {
        if (plug != 0) {
            PlugNative.Close(plug);
            plug = 0;
        }
    }

    public override bool Process(
        ReadOnlySpan<float> inL,
        ReadOnlySpan<float> inR,
        ReadOnlySpan<float> scL,
        ReadOnlySpan<float> scR,
        ReadOnlySpan<MidiEvent> events,
        Span<float> outL,
        Span<float> outR,
        long deadlineNs
    ) {
        if (plug == 0) {
            return false;
        }

        var frames = Math.Min(outL.Length, scratchLeft.Length);
        if (!inL.IsEmpty) {
            // Plugins are not required to process in place: hand them a copy of the input.
            inL[..frames].CopyTo(scratchLeft);
            inR[..frames].CopyTo(scratchRight);
            inL = scratchLeft.AsSpan(0, frames);
            inR = scratchRight.AsSpan(0, frames);
        }

        PlugMidi* midi = stackalloc PlugMidi[Math.Min(events.Length, 256)];
        var count = 0;
        foreach (var e in events) {
            if (count == 256) {
                break;
            }

            midi[count++] = new() { Frame = Math.Clamp(e.Frame, 0, frames - 1), Status = e.Status, Data1 = e.Data1, Data2 = e.Data2 };
        }

        fixed (float* il = inL, ir = inR, sl = scL, sr = scR, ol = outL, or = outR) {
            float** input = stackalloc float*[2];
            float** sc = stackalloc float*[2];
            float** output = stackalloc float*[2];
            input[0] = il;
            input[1] = ir;
            sc[0] = sl;
            sc[1] = sr;
            output[0] = ol;
            output[1] = or;
            return PlugNative.Process(plug, inL.IsEmpty ? null : input, scL.IsEmpty ? null : sc, output, frames, midi, count) == 0;
        }
    }

    public override void SetParameter(int index, double value) {
        if (plug != 0) {
            PlugNative.ParamSet(plug, index, value);
        }
    }

    public override void Reset() {
        if (plug != 0) {
            PlugNative.Reset(plug);
        }
    }

    public override byte[]? SaveState(out string? error) {
        error = "not loaded";
        return plug == 0 ? null : PlugNative.SaveState(plug, out error);
    }

    public override bool LoadState(ReadOnlySpan<byte> state, out double[]? values, out int latency, out string? error) {
        values = null;
        latency = 0;
        if (plug == 0) {
            error = "not loaded";
            return false;
        }

        if (!PlugNative.LoadState(plug, state, out error)) {
            return false;
        }

        values = new double[PlugNative.ParamCount(plug)];
        for (var i = 0; i < values.Length; i++) {
            values[i] = PlugNative.ParamGet(plug, i);
        }

        latency = PlugNative.Latency(plug);
        return true;
    }

    public override int OpenEditor(string title, EditorPlacement placement, out string? error) {
        error = "plugins hosted in the engine's own process have no editor window; use the generic editor";
        return -1;
    }

    public override void CloseEditor() { }

    public override bool IsFailed => false;

    public override void Dispose() => Unload();
}
