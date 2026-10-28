using Anode.Dsp.Effects;
using Anode.Dsp.Instruments;

namespace Anode.Plugins.Hosting;

/// <summary>
///     The state of one hosted instance shared by the effect and instrument adapters: its backend, parameter values,
///     latency, a state stashed until it is prepared, and its failure.
/// </summary>
internal sealed class PluginInstance {
    readonly PluginHost host;
    readonly double[] values;
    readonly bool[] set;
    PluginBackend? backend;
    byte[]? stashedState;
    double sampleRate;
    int maxFrames;
    volatile string? failure;
    volatile int latency;

    public PluginInstance(PluginHost host, PluginEntry entry, PluginOwner owner, PluginBackend? backend, string? failure) {
        this.host = host;
        Entry = entry;
        Owner = owner;
        this.backend = backend;
        this.failure = failure;
        latency = entry.Description.Latency;
        values = [..entry.Description.Parameters.Select(p => p.Default)];
        set = new bool[values.Length];
    }

    public PluginEntry Entry { get; }
    public PluginOwner Owner { get; }
    public PluginBackend? Backend => backend;
    public bool Prepared { get; private set; }
    public double SampleRate => sampleRate;
    public int EditorWindow { get; set; }

    public bool IsFailed => failure is not null || backend is null || backend.IsFailed;

    public string? Failure => failure ?? (backend is { IsFailed: true } ? "the plugin's sandbox stopped" : null);

    public int Latency => latency;

    public HostedPluginStats Stats =>
        backend is { } b ? new(b.Completed, b.Misses, b.BusySkips, b.LastRoundTripNs, b.SandboxPid) : default;

    public void Prepare(float rate, int frames) {
        if (backend is null || failure is not null) {
            return;
        }

        if (Prepared && Math.Abs(rate - sampleRate) < 1e-6 && frames <= maxFrames) {
            return;
        }

        if (Prepared) {
            // New settings: keep the state across the reload.
            stashedState = backend.SaveState(out _) ?? stashedState;
            backend.Unload();
            Prepared = false;
        }

        sampleRate = rate;
        maxFrames = Math.Min(frames, host.MaxFrames);
        // Values the engine set before this first load go with it; on a reload the state carries them.
        var indices = stashedState is null ? Enumerable.Range(0, set.Length).Where(i => set[i]).ToArray() : [];
        var initial = indices.Select(i => values[i]).ToArray();
        if (!backend.Load(rate, maxFrames, Entry.Description.HasSidechain, indices, initial, out _, out var l, out var error)) {
            Fail("could not be loaded: " + error);
            return;
        }

        latency = l;
        Prepared = true;
        if (stashedState is { } state) {
            stashedState = null;
            LoadState(state);
        }
    }

    public void SetParameter(int index, double value) {
        if ((uint)index >= (uint)values.Length) {
            return;
        }

        var p = Entry.Description.Parameters[index];
        value = Math.Clamp(value, p.Min, p.Max);
        values[index] = value;
        if (!Prepared) {
            set[index] = true;
        }

        backend?.SetParameter(index, value);
    }

    public double GetParameter(int index) => (uint)index < (uint)values.Length ? values[index] : 0;

    public bool Process(
        ReadOnlySpan<float> inL,
        ReadOnlySpan<float> inR,
        ReadOnlySpan<float> scL,
        ReadOnlySpan<float> scR,
        ReadOnlySpan<MidiEvent> events,
        Span<float> outL,
        Span<float> outR
    ) {
        var b = backend;
        if (b is null || !Prepared || failure is not null) {
            return false;
        }

        return b.Process(inL, inR, scL, scR, events, outL, outR, host.Deadline(outL.Length, sampleRate));
    }

    public void Reset() => backend?.Reset();

    public byte[]? SaveState() {
        if (backend is null || !Prepared || IsFailed) {
            return stashedState;
        }

        return backend.SaveState(out _);
    }

    public bool LoadState(ReadOnlySpan<byte> state) {
        if (backend is null || failure is not null) {
            return false;
        }

        if (!Prepared) {
            stashedState = state.ToArray();
            return true;
        }

        if (!backend.LoadState(state, out var loaded, out var l, out _)) {
            return false;
        }

        if (loaded is not null) {
            for (var i = 0; i < loaded.Length && i < values.Length; i++) {
                values[i] = loaded[i];
            }
        }

        latency = l;
        return true;
    }

    /// <summary>The plugin's own editor changed a parameter (a sandbox event).</summary>
    public void OnEdited(int index, double value) {
        if ((uint)index >= (uint)values.Length) {
            return;
        }

        values[index] = value;
        host.RaiseParameterChanged(Owner, Entry.Description.Parameters[index].Id, value);
    }

    public void OnLatency(int samples) => latency = samples;

    public void Fail(string message) {
        if (failure is not null) {
            return;
        }

        failure = message;
        host.RaiseProblem(new(Owner, Entry.PluginId, $"{Entry.Description.Name} {message}"));
    }

    public int OpenEditor(bool open, EditorPlacement? placement) {
        if (backend is null || IsFailed || !Prepared) {
            return -1;
        }

        if (!open) {
            if (EditorWindow != 0) {
                backend.CloseEditor();
                EditorWindow = 0;
            }

            return 0;
        }

        var window = backend.OpenEditor(Entry.Description.Name, placement ?? new(), host.Options.AppProcessId, out var error);
        if (window < 0) {
            host.Log?.Invoke($"{Entry.Description.Name}: no editor window ({error})");
            return -1;
        }

        EditorWindow = window;
        return window;
    }

    public void Dispose() {
        var b = backend;
        backend = null;
        if (b is null) {
            host.Release(this);
            return;
        }

        if (EditorWindow != 0 && !b.IsFailed) {
            b.CloseEditor();
        }

        b.Dispose();
        host.Release(this, b);
    }
}

/// <summary>A hosted plugin effect, as the engine sees it.</summary>
internal sealed class HostedEffect(PluginInstance instance) : IAudioEffect, IHostedPlugin {
    readonly EffectDescriptor descriptor = instance.Entry.EffectDescriptor();

    public PluginInstance Instance => instance;
    public EffectDescriptor Descriptor => descriptor;
    public PluginOwner Owner => instance.Owner;
    public string PluginId => instance.Entry.PluginId;
    public bool HasEditor => instance.Entry.Description.HasEditor;
    public bool IsFailed => instance.IsFailed;
    public string? Failure => instance.Failure;
    public HostedPluginStats Stats => instance.Stats;
    public int EditorWindow => instance.EditorWindow;
    public int LatencySamples => instance.Latency;
    public float Reading => float.NaN;

    public void Prepare(float sampleRate, int maxFrames) => instance.Prepare(sampleRate, maxFrames);

    public void SetParameter(int index, float value) => instance.SetParameter(index, value);

    public float GetParameter(int index) => (float)instance.GetParameter(index);

    /// <summary>In place; on a miss or a failure the block passes through dry.</summary>
    public void Process(Span<float> left, Span<float> right, ReadOnlySpan<float> sidechainLeft, ReadOnlySpan<float> sidechainRight) =>
        instance.Process(left, right, sidechainLeft, sidechainRight, [], left, right);

    public void Reset() => instance.Reset();

    public byte[]? SaveState() => instance.SaveState();

    public bool LoadState(ReadOnlySpan<byte> state) => instance.LoadState(state);

    public int OpenEditor(bool open, EditorPlacement? placement = null) => instance.OpenEditor(open, placement);

    public void Dispose() => instance.Dispose();
}

/// <summary>A hosted plugin instrument, as the engine sees it.</summary>
internal sealed class HostedInstrument(PluginInstance instance) : IInstrument, IHostedPlugin {
    readonly InstrumentDescriptor descriptor = instance.Entry.InstrumentDescriptor();

    public PluginInstance Instance => instance;
    public InstrumentDescriptor Descriptor => descriptor;
    public PluginOwner Owner => instance.Owner;
    public string PluginId => instance.Entry.PluginId;
    public bool HasEditor => instance.Entry.Description.HasEditor;
    public bool IsFailed => instance.IsFailed;
    public string? Failure => instance.Failure;
    public HostedPluginStats Stats => instance.Stats;
    public int EditorWindow => instance.EditorWindow;

    /// <summary>The instrument's latency, in samples (for delay compensation).</summary>
    public int LatencySamples => instance.Latency;

    public void Prepare(float sampleRate, int maxFrames) => instance.Prepare(sampleRate, maxFrames);

    public void SetParameter(int index, float value) => instance.SetParameter(index, value);

    public float GetParameter(int index) => (float)instance.GetParameter(index);

    /// <summary>On a miss or a failure the block is silent.</summary>
    public void Process(ReadOnlySpan<MidiEvent> events, Span<float> left, Span<float> right) {
        if (!instance.Process([], [], [], [], events, left, right)) {
            left.Clear();
            right.Clear();
        }
    }

    public void Reset() => instance.Reset();

    public bool LoadFile(string path) => false;

    public byte[]? SaveState() => instance.SaveState();

    public bool LoadState(ReadOnlySpan<byte> state) => instance.LoadState(state);

    public int OpenEditor(bool open, EditorPlacement? placement = null) => instance.OpenEditor(open, placement);

    public void Dispose() => instance.Dispose();
}
