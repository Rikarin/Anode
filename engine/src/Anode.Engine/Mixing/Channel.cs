using Anode.Dsp.Effects;
using Anode.Engine.Midi;

namespace Anode.Engine.Mixing;

/// <summary>
///     An insert's effect instance, kept across structure changes by insert id while its plugin stays the same: the
///     prepared effect (null when it failed to load: the slot passes audio through), the parameter values the control
///     thread last set and the queue that carries changes to the audio thread.
/// </summary>
public sealed class InsertSlot : IDisposable {
    readonly Dictionary<string, int> indexById = new(StringComparer.Ordinal);
    int failed;

    public InsertSlot(string id, string pluginId, IAudioEffect? effect) {
        Id = id;
        PluginId = pluginId;
        Effect = effect;
        if (effect is not null) {
            foreach (var p in effect.Descriptor.Parameters) {
                indexById[p.Id] = p.Index;
                Values[p.Id] = p.Default;
            }
        }

        Parameters = new(Math.Max(256, 2 * indexById.Count));
    }

    public string Id { get; }

    public string PluginId { get; }

    /// <summary>The instance; null when the plugin could not be loaded.</summary>
    public IAudioEffect? Effect { get; }

    /// <summary>The plugin reported a problem (a crash): it is bypassed from now on.</summary>
    public bool Failed => Volatile.Read(ref failed) != 0;

    /// <summary>Control thread: each parameter's value as last set, by id.</summary>
    public Dictionary<string, double> Values { get; } = new(StringComparer.Ordinal);

    /// <summary>Parameter changes on their way to the audio thread.</summary>
    public SpscChannel<ParameterChange> Parameters { get; }

    public void MarkFailed() => Volatile.Write(ref failed, 1);

    /// <summary>The parameter's index, or −1.</summary>
    public int IndexOf(string parameterId) => indexById.GetValueOrDefault(parameterId, -1);

    /// <summary>Control thread, before the slot is published: sets the parameter directly. False for an unknown id.</summary>
    public bool Initialise(string parameterId, double value) {
        var index = IndexOf(parameterId);
        if (index < 0 || !double.IsFinite(value) || Effect is null) {
            return false;
        }

        Values[parameterId] = value;
        Effect.SetParameter(index, (float)value);
        return true;
    }

    /// <summary>Control thread, once published: queues the change. False for an unknown id.</summary>
    public bool Set(string parameterId, double value) {
        var index = IndexOf(parameterId);
        if (index < 0 || !double.IsFinite(value)) {
            return false;
        }

        Values[parameterId] = value;
        return Parameters.TryEnqueue(new(index, (float)value));
    }

    /// <summary>Audio thread: applies the queued changes.</summary>
    public void ApplyParameters() {
        if (Effect is null) {
            while (Parameters.TryDequeue(out _)) { }

            return;
        }

        while (Parameters.TryDequeue(out var change)) {
            Effect.SetParameter(change.Index, change.Value);
        }
    }

    public void Dispose() => Effect?.Dispose();
}

/// <summary>An insert as a channel's configuration places it: the instance, bypass and sidechain.</summary>
public sealed record ChannelInsert(InsertSlot Slot, bool Bypassed, int SidechainTrack, int SidechainBus);

/// <summary>A send kept across structure changes by id: its level and pan (set any time), and its gain ramp.</summary>
public sealed class SendSlot(string id) {
    float gain;
    float pan;

    public string Id { get; } = id;

    public float Gain => Volatile.Read(ref gain);

    public float Pan => Volatile.Read(ref pan);

    /// <summary>Audio thread: the gains it last applied.</summary>
    internal EdgeGain Edge;

    /// <summary>Control thread.</summary>
    public void Set(float level, float panning) {
        Volatile.Write(ref gain, float.IsFinite(level) ? Math.Max(0f, level) : 0f);
        Volatile.Write(ref pan, float.IsFinite(panning) ? Math.Clamp(panning, -1f, 1f) : 0f);
    }
}

/// <summary>A send as a channel's configuration places it.</summary>
public sealed record ChannelSend(SendSlot Slot, int Bus, bool PreFader);

/// <summary>
///     A channel's structure (<c>setChannel</c>): inserts in order, sends and output (0: the master, 1…64: a bus, −N:
///     the device's output pair N). Immutable once published; a change makes a new one that keeps the instances.
/// </summary>
public sealed record ChannelConfig(ChannelInsert[] Inserts, ChannelSend[] Sends, int Output) {
    public static readonly ChannelConfig Empty = new([], [], 0);
}

/// <summary>The left and right gains an edge (an output or a send) last applied, so a change ramps over a block.</summary>
internal struct EdgeGain {
    internal float Left;
    internal float Right;
    internal bool Set;
}

/// <summary>
///     A channel strip's state that outlives structure changes (one per engine track, and the master): the fader
///     (<see cref="Strip" />), the latest configuration (control thread), and the audio thread's delay line, gain ramp
///     and post-fader buffer (for tracks other inserts take as a sidechain).
/// </summary>
public sealed class ChannelState {
    DelayLine? delay;
    float[]? postLeft;
    float[]? postRight;

    public ChannelState(Strip strip) {
        Strip = strip;
    }

    public Strip Strip { get; }

    /// <summary>Control thread: the configuration the next plan publishes.</summary>
    public ChannelConfig Config { get; set; } = ChannelConfig.Empty;

    /// <summary>The delay line for compensation (null: none needed), replaced by the control thread when it grows.</summary>
    public DelayLine? Delay {
        get => Volatile.Read(ref delay);
        set => Volatile.Write(ref delay, value);
    }

    /// <summary>The post-fader output of the last block processed (when another insert takes it as a sidechain).</summary>
    public float[]? PostLeft => Volatile.Read(ref postLeft);

    public float[]? PostRight => Volatile.Read(ref postRight);

    /// <summary>Audio thread: frames in the post-fader buffers.</summary>
    internal int PostFrames;

    /// <summary>Audio thread: the output's gain ramp.</summary>
    internal EdgeGain Output;

    // Audio thread, per block: the latency the delay compensation aligns on, whether it is on an armed or monitored
    // path, and whether it sounds under the solos.
    internal int Latency;
    internal int ChainLatency;
    internal bool Live;
    internal bool Audible;
    internal bool ImpliedSolo;

    /// <summary>Control thread: allocates the post-fader buffers (before a plan that writes them is published).</summary>
    public void EnsurePost() {
        if (postLeft is null) {
            Volatile.Write(ref postRight, GC.AllocateArray<float>(AudioEngine.MaxFrames, true));
            Volatile.Write(ref postLeft, GC.AllocateArray<float>(AudioEngine.MaxFrames, true));
        }
    }
}
