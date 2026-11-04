using Anode.Dsp.Effects;
using Anode.Dsp.Instruments;
using Anode.Dsp.MidiEffects;
using Anode.Dsp.Spatial;
using Anode.Engine.Midi;
using Anode.Engine.Spatial;
using Anode.Ipc;

namespace Anode.Engine.Mixing;

/// <summary>
///     An insert's effect instance, kept across structure changes by insert id while its plugin stays the same: the
///     prepared effect (null when it failed to load: the slot passes audio through), the parameter values the control
///     thread last set and the queue that carries changes to the audio thread.
///     <para>
///         Phase 11: an insert that changes its strip's format (<see cref="IFormatEffect" />, <see cref="IsFormat" />)
///         has no <see cref="Effect" />; its instance is its <see cref="Format" /> stage, prepared for the format at its
///         slot and replaced whole when that format or its parameters change it (structure).
///     </para>
/// </summary>
public sealed class InsertSlot : IDisposable {
    readonly Dictionary<string, int> indexById = new(StringComparer.Ordinal);
    int failed;
    FormatStage? format;

    public InsertSlot(string id, string pluginId, IAudioEffect? effect) : this(id, pluginId, effect?.Descriptor) {
        Effect = effect;
    }

    /// <summary>A slot for <paramref name="descriptor" />'s plugin (an <see cref="IFormatEffect" />'s, or none).</summary>
    InsertSlot(string id, string pluginId, EffectDescriptor? descriptor) {
        Id = id;
        PluginId = pluginId;
        if (descriptor is not null) {
            foreach (var p in descriptor.Parameters) {
                indexById[p.Id] = p.Index;
                Values[p.Id] = p.Default;
            }
        }

        Parameters = new(Math.Max(256, 2 * indexById.Count));
    }

    /// <summary>A slot of an insert that changes its strip's format (Phase 11), its stage still to be prepared.</summary>
    public static InsertSlot ForFormat(string id, string pluginId, EffectDescriptor descriptor, string? file) =>
        new(id, pluginId, descriptor) { IsFormat = true, File = file };

    /// <summary>The insert changes its strip's format (Phase 11): its instance is <see cref="Format" />.</summary>
    public bool IsFormat { get; private init; }

    /// <summary>
    ///     The format insert's prepared instance (null: none to be had; the slot then passes the signal as it is).
    ///     Swapped whole by the control thread before a plan that needs it is published.
    /// </summary>
    public FormatStage? Format {
        get => Volatile.Read(ref format);
        set => Volatile.Write(ref format, value);
    }

    /// <summary>Control thread: a parameter of a format insert changed since its stage was prepared.</summary>
    internal bool FormatStale { get; set; }

    /// <summary>Control thread: a format insert's saved state (base64), which each new stage loads.</summary>
    internal string? FormatState { get; set; }

    /// <summary>A Mastering Assistant's meter (Phase 11; null on every other insert), which the telemetry reads.</summary>
    public MasteringMeter? Mastering { get; init; }

    EngineMasteringChain? chain;

    /// <summary>
    ///     Control thread: a Mastering Assistant's chain from its analysis (null: none yet), handed to the assistant when
    ///     it differs from the one it has.
    /// </summary>
    public void SetChain(EngineMasteringChain? next) {
        if (Mastering is not { } meter || Same(chain, next)) {
            return;
        }

        chain = next;
        meter.Chain.SetChain(
            next is null
                ? null
                : new(
                    [..next.EqGainsDb],
                    next.CompressorThresholdDb,
                    next.CompressorRatio,
                    next.LoudnessLufs,
                    next.ChainGainDb
                )
        );
    }

    static bool Same(EngineMasteringChain? a, EngineMasteringChain? b) =>
        ReferenceEquals(a, b)
        || (a is not null
            && b is not null
            && a.EqGainsDb.SequenceEqual(b.EqGainsDb)
            && a.CompressorThresholdDb == b.CompressorThresholdDb
            && a.CompressorRatio == b.CompressorRatio
            && a.LoudnessLufs == b.LoudnessLufs
            && a.ChainGainDb == b.ChainGainDb);

    public string Id { get; }

    public string PluginId { get; }

    /// <summary>The file the effect loaded (an impulse response; <c>IFileEffect</c>), or null.</summary>
    public string? File { get; init; }

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

    /// <summary>
    ///     Control thread, before the slot is published: sets the parameter directly (a format insert's when its stage
    ///     is prepared). False for an unknown id.
    /// </summary>
    public bool Initialise(string parameterId, double value) {
        var index = IndexOf(parameterId);
        if (index < 0 || !double.IsFinite(value) || (Effect is null && !IsFormat)) {
            return false;
        }

        Values[parameterId] = value;
        Effect?.SetParameter(index, (float)value);
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

    /// <summary>
    ///     The extra instances and delays the insert needs on a multichannel strip (Phase 10), or null on a mono or
    ///     stereo one. Swapped whole by the control thread before a plan that needs it is published.
    /// </summary>
    public MultiInsert? Multi {
        get => Volatile.Read(ref multi);
        set => Volatile.Write(ref multi, value);
    }

    MultiInsert? multi;

    /// <summary>Audio thread: applies the queued changes (to the extra instances of a multichannel strip too).</summary>
    public void ApplyParameters() {
        if (Effect is null) {
            var stage = format?.Effect;
            while (Parameters.TryDequeue(out var queued)) {
                stage?.SetParameter(queued.Index, queued.Value); // a format insert's (Phase 11)
            }

            return;
        }

        var extras = multi?.Extras;
        while (Parameters.TryDequeue(out var change)) {
            Effect.SetParameter(change.Index, change.Value);
            if (extras is not null) {
                foreach (var extra in extras) {
                    extra?.SetParameter(change.Index, change.Value);
                }
            }
        }
    }

    public void Dispose() {
        Effect?.Dispose();
        multi?.Dispose();
        format?.Dispose();
    }
}

/// <summary>
///     A format insert's instance as the plan uses it (Phase 11): the effect prepared from <see cref="In" /> to
///     <see cref="Out" /> (what it outputs at its parameters), and the gains a bypassed one mixes the channels with
///     (each to its label's speaker, or a down-mix), output-major. Immutable in shape; built on the control thread.
/// </summary>
public sealed class FormatStage(IFormatEffect effect, SpeakerFormat input, SpeakerFormat output, float[] bypass)
    : IDisposable {
    public IFormatEffect Effect { get; } = effect;

    public SpeakerFormat In { get; } = input;

    public SpeakerFormat Out { get; } = output;

    public int InChannels { get; } = SpeakerLayout.Of(input).ChannelCount;

    public int OutChannels { get; } = SpeakerLayout.Of(output).ChannelCount;

    /// <summary>The gains of the bypassed insert, <c>[o × InChannels + i]</c>.</summary>
    public float[] Bypass { get; } = bypass;

    public void Dispose() => Effect.Dispose();
}

/// <summary>
///     A MIDI FX slot's instance (Phase 6, docs/07-phase-6.md), kept across structure changes by slot id while its
///     plugin stays the same, as an insert's is: the prepared effect (null when it failed to load: the slot passes MIDI
///     through), the parameter values the control thread last set and the queue that carries changes to the audio
///     thread.
///     <para>
///         Phase 7: an effect with a run-time parameter list (<see cref="IRuntimeParameters" />) is addressed by the
///         ids of that list, re-read by <see cref="RefreshParameters" />; a script (<see cref="IScriptHost" />) keeps
///         its <see cref="Source" />, a failed flag that bypasses it, the notes it left sounding (released when a new
///         instance replaces it or it fails) and its log's rate limit.
///     </para>
/// </summary>
public sealed class MidiEffectSlot : IDisposable {
    readonly Dictionary<string, int> indexById = new(StringComparer.Ordinal);
    MidiEffectSlot? predecessor;
    int failed;

    public MidiEffectSlot(string id, string pluginId, IMidiEffect? effect) {
        Id = id;
        PluginId = pluginId;
        Effect = effect;
        Runtime = effect as IRuntimeParameters;
        Script = effect as IScriptHost;
        foreach (var p in ParameterList) {
            indexById[p.Id] = p.Index;
            Values[p.Id] = p.Default;
        }

        Parameters = new(Math.Max(256, 2 * indexById.Count));
    }

    public string Id { get; }

    public string PluginId { get; }

    /// <summary>The instance; null when the plugin could not be loaded.</summary>
    public IMidiEffect? Effect { get; }

    /// <summary>The instance when its parameter list is defined at run time (Phase 7), else null.</summary>
    public IRuntimeParameters? Runtime { get; }

    /// <summary>The instance when it runs a script (Phase 7), else null.</summary>
    public IScriptHost? Script { get; }

    /// <summary>The script the instance compiled (null: none was given).</summary>
    public string? Source { get; init; }

    /// <summary>Control thread: the parameters as the slot addresses them now: the run-time list, or the descriptor's.</summary>
    public IReadOnlyList<InstrumentParameter> ParameterList =>
        Runtime?.RuntimeParameters ?? Effect?.Descriptor.Parameters ?? [];

    /// <summary>Control thread: each parameter's value as last set, by id.</summary>
    public Dictionary<string, double> Values { get; } = new(StringComparer.Ordinal);

    /// <summary>Parameter changes on their way to the audio thread.</summary>
    public SpscChannel<ParameterChange> Parameters { get; }

    /// <summary>The script failed (to compile, or while running): the slot passes MIDI through from now on.</summary>
    public bool Failed => Volatile.Read(ref failed) != 0;

    /// <summary>Control thread: the script's log on its way to the server, kept across the slot's instances.</summary>
    public ScriptLog Log { get; init; } = new();

    /// <summary>Audio thread: the engine's stop/locate count the effect was last reset for.</summary>
    internal long Panic;

    /// <summary>Audio thread: the notes the effect's output left sounding (released on a swap or a fault).</summary>
    internal NoteSet Held { get; } = new();

    /// <summary>Audio thread: the slot was bypassed for its failure (its notes released).</summary>
    internal bool Bypassing;

    /// <summary>
    ///     The instance this one replaced (a new script): set by the control thread before the slot is published; the
    ///     audio thread releases the notes it left sounding on this slot's first block, then forgets it.
    /// </summary>
    internal MidiEffectSlot? Predecessor {
        get => Volatile.Read(ref predecessor);
        set => Volatile.Write(ref predecessor, value);
    }

    public void MarkFailed() => Volatile.Write(ref failed, 1);

    /// <summary>The parameter's index, or −1.</summary>
    public int IndexOf(string parameterId) => indexById.GetValueOrDefault(parameterId, -1);

    /// <summary>
    ///     Control thread: re-reads a run-time parameter list. Parameters that stay (by id) keep their values, new ones
    ///     start at their defaults, and those gone are forgotten.
    /// </summary>
    public void RefreshParameters() {
        var kept = new Dictionary<string, double>(Values, StringComparer.Ordinal);
        indexById.Clear();
        Values.Clear();
        foreach (var p in ParameterList) {
            indexById[p.Id] = p.Index;
            Values[p.Id] = kept.TryGetValue(p.Id, out var value) ? value : p.Default;
        }
    }

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

/// <summary>
///     A script slot's log on its way to the server (Phase 7, <c>scriptLog</c>): at most <see cref="MaxPerSecond" />
///     lines go in any one second (a sliding window over the send times of the last ones); the rest are counted as
///     dropped and reported with the next message. Control thread.
/// </summary>
public sealed class ScriptLog {
    /// <summary>The most lines a slot sends in any one second.</summary>
    public const int MaxPerSecond = 50;

    readonly long[] sent = new long[MaxPerSecond];
    readonly bool[] used = new bool[MaxPerSecond];
    int next;

    /// <summary>Lines left out since the last message.</summary>
    public int Dropped { get; private set; }

    /// <summary>Lines the engine itself adds (errors), sent before the next trace lines.</summary>
    public List<string> Pending { get; } = [];

    /// <summary>
    ///     Takes <paramref name="lines" />, and <paramref name="dropped" /> lines the trace ring already left out, at
    ///     <paramref name="timestamp" /> (<see cref="System.Diagnostics.Stopwatch" /> ticks): returns those to send now,
    ///     and counts the rest as dropped.
    /// </summary>
    public List<string> Admit(IEnumerable<string> lines, int dropped, long timestamp) {
        Dropped += Math.Max(0, dropped);
        var admitted = new List<string>();
        foreach (var line in lines) {
            // A line goes when the oldest of the last 50 sent went at least a second ago.
            if (used[next] && timestamp - sent[next] < System.Diagnostics.Stopwatch.Frequency) {
                Dropped++;
                continue;
            }

            sent[next] = timestamp;
            used[next] = true;
            next = (next + 1) % MaxPerSecond;
            admitted.Add(line);
        }

        return admitted;
    }

    /// <summary>Takes the dropped count for a message.</summary>
    public int TakeDropped() {
        var dropped = Dropped;
        Dropped = 0;
        return dropped;
    }
}

/// <summary>A MIDI FX slot as a channel's configuration places it: the instance and whether it is bypassed.</summary>
public sealed record ChannelMidiEffect(MidiEffectSlot Slot, bool Bypassed);

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

    /// <summary>
    ///     Control thread (Phase 10): the Surround Panner a send to a surround bus pans through (the strip's surround
    ///     settings), kept while its bus's format and its input width stay.
    /// </summary>
    internal PannerCache Panner;

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
///     the device's output pair N), and its MIDI FX slots in order (Phase 6). Immutable once published; a change makes
///     a new one that keeps the instances.
///     <para>
///         Phase 10 (docs/11-phase-10.md): the strip's input and output formats and its panner's mode. An aux's input
///         format is its bus's (every summing point carries the format of the strip that reads it: the master's input,
///         an aux's); a track's own signal is its source's channels (mono or stereo) whatever its input format says.
///     </para>
/// </summary>
public sealed record ChannelConfig(ChannelInsert[] Inserts, ChannelSend[] Sends, int Output) {
    public static readonly ChannelConfig Empty = new([], [], 0);

    /// <summary>The MIDI FX slots, run in order on the track's MIDI before its instrument.</summary>
    public ChannelMidiEffect[] MidiEffects { get; init; } = [];

    public SpeakerFormat InputFormat { get; init; } = SpeakerFormat.Stereo;

    /// <summary>As the server keeps it (its destination's); the engine follows the destination's own format.</summary>
    public SpeakerFormat OutputFormat { get; init; } = SpeakerFormat.Stereo;

    public EnginePannerMode PannerMode { get; init; } = EnginePannerMode.Stereo;
}

/// <summary>The left and right gains an edge (an output or a send) last applied, so a change ramps over a block.</summary>
internal struct EdgeGain {
    internal float Left;
    internal float Right;
    internal bool Set;
}

/// <summary>
///     The gains of a dual pan last applied (each channel's to the left and right), so a change ramps over a block.
/// </summary>
internal struct DualGain {
    internal float LeftToLeft;
    internal float LeftToRight;
    internal float RightToLeft;
    internal float RightToRight;
    internal bool Set;
}

/// <summary>A prepared Surround Panner kept for an edge while its output format and input width stay (control thread).</summary>
internal struct PannerCache {
    ISurroundPanner? panner;
    SpeakerFormat format;
    int inputs;

    /// <summary>
    ///     The panner for <paramref name="output" /> and <paramref name="inputChannels" />: the kept one, or a new one
    ///     prepared.
    /// </summary>
    internal ISurroundPanner Get(SpeakerFormat output, int inputChannels, SpatialContext spatial) {
        if (panner is null || format != output || inputs != inputChannels) {
            var next = spatial.Factory.CreateSurroundPanner();
            next.Prepare(SpeakerLayout.Of(output), inputChannels, spatial.SampleRate, AudioEngine.MaxFrames);
            panner = next;
            format = output;
            inputs = inputChannels;
        }

        return panner;
    }
}

/// <summary>
///     A channel strip's state that outlives structure changes (one per engine track, and the master): the fader
///     (<see cref="Strip" />), the latest configuration (control thread), and the audio thread's delay line, gain ramp
///     and post-fader buffer (for tracks other inserts take as a sidechain).
/// </summary>
public sealed class ChannelState(Strip strip) {
    DelayLine? delay;
    float[]? postLeft;
    float[]? postRight;
    float[]? workLeft;
    float[]? workRight;

    public Strip Strip { get; } = strip;

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

    /// <summary>Audio thread: the trim last applied, so a change ramps over a block.</summary>
    internal float AppliedTrim;

    internal bool TrimApplied;

    /// <summary>Audio thread: the dual pan's ramp.</summary>
    internal DualGain Dual;

    /// <summary>Audio thread (Phase 10): the fader's gain ramp of an output with no stereo pan (surround, object, mono).</summary>
    internal EdgeGain Fader;

    /// <summary>Control thread (Phase 10): the Surround Panner of a surround output.</summary>
    internal PannerCache OutputPanner;

    // Audio thread, per block: the latency the delay compensation aligns on, whether it is on an armed or monitored
    // path, and whether it sounds under the solos.
    internal int Latency;
    internal int ChainLatency;
    internal bool Live;
    internal bool Audible;
    internal bool ImpliedSolo;

    /// <summary>Its own work buffers, for a track whose sandboxed inserts begin before the mix reaches it.</summary>
    public float[]? WorkLeft => Volatile.Read(ref workLeft);

    public float[]? WorkRight => Volatile.Read(ref workRight);

    // Audio thread, a prefetched block: where its inserts resume, whether a sandboxed block was handed over (the
    // insert before Resume), and whether the signal is stereo by then.
    internal bool Prefetched;
    internal bool Begun;
    internal int Resume;
    internal bool PrefetchStereo;

    /// <summary>Control thread: allocates the work buffers (before a plan that prefetches the channel is published).</summary>
    public void EnsureWork() {
        if (workLeft is null) {
            Volatile.Write(ref workRight, GC.AllocateArray<float>(AudioEngine.MaxFrames, true));
            Volatile.Write(ref workLeft, GC.AllocateArray<float>(AudioEngine.MaxFrames, true));
        }
    }

    /// <summary>Control thread: allocates the post-fader buffers (before a plan that writes them is published).</summary>
    public void EnsurePost() {
        if (postLeft is null) {
            Volatile.Write(ref postRight, GC.AllocateArray<float>(AudioEngine.MaxFrames, true));
            Volatile.Write(ref postLeft, GC.AllocateArray<float>(AudioEngine.MaxFrames, true));
        }
    }
}
