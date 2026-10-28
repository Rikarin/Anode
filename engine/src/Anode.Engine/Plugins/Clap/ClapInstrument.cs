using Anode.Dsp.Instruments;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Anode.Engine.Plugins.Clap;

/// <summary>
///     A CLAP plugin instance hosted in the engine process (ADR-0003's in-host mode), wrapped as an
///     <see cref="IInstrument" />. Main-thread calls (create, init, activate, state, parameter info, destroy) run on the
///     <see cref="PluginThread" />; <see cref="Process" />, <see cref="SetParameter" /> and <see cref="Reset" /> run on
///     the audio thread and neither allocate nor lock: events, parameter changes and buffers live in native memory
///     allocated up front.
/// </summary>
/// <remarks>
///     Lifecycle: <see cref="Create" /> (init) → <see cref="Prepare" /> (remembers the format) →
///     <see cref="LoadState" /> and parameter values (main thread, inactive; values are flushed at activation) →
///     <see cref="Activate" /> → the engine swaps it in; its first <see cref="Process" /> starts processing.
///     <see cref="Dispose" /> stops processing, deactivates and destroys it once the engine no longer uses it
///     (<c>stop_processing</c> runs on the plugin thread marked as the audio thread then, as nothing processes it).
/// </remarks>
public sealed unsafe class ClapInstrument : IInstrument, IHostedInstrument {
    const int EventCapacity = 2048;
    const int EventSlot = 64; // bytes, enough for the largest event sent (param value: 56)
    const int ParameterCapacity = 1024;

    readonly ClapModule module;
    readonly GCHandle self;
    readonly ClapHost* host;
    readonly ClapPlugin* plugin;
    readonly ClapPluginParams* parameters;
    readonly ClapPluginState* stateExtension;
    readonly uint* parameterIds;
    readonly float* values;
    readonly byte* eventMemory;
    readonly ClapEventHeader** eventList;
    readonly EventContext* inputContext;
    readonly ClapInputEvents* inputEvents;
    readonly ClapOutputEvents* outputEvents;
    readonly ClapInputEvents* flushEvents;
    readonly ClapProcess* process;
    readonly ClapAudioBuffer* output;
    readonly float** channels;
    readonly (int Index, float Value)[] pending = new (int, float)[ParameterCapacity];
    readonly List<(int Index, float Value)> preActivation = [];
    readonly bool noteEvents;
    readonly bool midiEvents;
    readonly int outputChannels;
    int pendingCount;
    int requests;
    double sampleRate = 48_000;
    int maxFrames = 4096;
    bool active;
    bool processing;
    bool disposed;
    long steadyTime;

    ClapInstrument(ClapModule module, ClapDescriptor descriptor, Action<string>? log) {
        this.module = module;
        Log = log;
        self = GCHandle.Alloc(this);
        host = ClapHostCallbacks.Create(self);
        var id = ClapModule.Utf8(descriptor.Id);
        try {
            plugin = module.Factory->CreatePlugin(module.Factory, host, id);
        } finally {
            NativeMemory.Free(id);
        }

        if (plugin is null) {
            Free();
            throw new IOException($"{descriptor.Id}: the factory did not create it.");
        }

        if (plugin->Init(plugin) == 0) {
            plugin->Destroy(plugin);
            plugin = null;
            Free();
            throw new IOException($"{descriptor.Id}: init failed.");
        }

        parameters = (ClapPluginParams*)Extension("clap.params"u8);
        stateExtension = (ClapPluginState*)Extension("clap.state"u8);
        (noteEvents, midiEvents) = Dialects((ClapPluginNotePorts*)Extension("clap.note-ports"u8));
        outputChannels = OutputChannels((ClapPluginAudioPorts*)Extension("clap.audio-ports"u8));
        var infos = ReadParameters(out var ids);
        parameterIds = (uint*)NativeMemory.Alloc((nuint)Math.Max(1, ids.Count), sizeof(uint));
        values = (float*)NativeMemory.AllocZeroed((nuint)Math.Max(1, ids.Count), sizeof(float));
        for (var i = 0; i < ids.Count; i++) {
            parameterIds[i] = ids[i];
            values[i] = infos[i].Default;
        }

        ParameterCount = ids.Count;
        Descriptor = new(descriptor.Id, descriptor.Name, descriptor.Vendor, descriptor.Version, infos, []);
        ReadValues();

        eventMemory = (byte*)NativeMemory.AlignedAlloc(EventCapacity * EventSlot, 64);
        eventList = (ClapEventHeader**)NativeMemory.Alloc(EventCapacity, (nuint)sizeof(void*));
        for (var i = 0; i < EventCapacity; i++) {
            eventList[i] = (ClapEventHeader*)(eventMemory + i * EventSlot);
        }

        inputContext = (EventContext*)NativeMemory.AllocZeroed((nuint)sizeof(EventContext));
        inputContext->Events = eventList;
        inputContext->Values = values;
        inputContext->Ids = parameterIds;
        inputContext->Count = ParameterCount;
        inputEvents = (ClapInputEvents*)NativeMemory.AllocZeroed((nuint)sizeof(ClapInputEvents));
        inputEvents->Ctx = inputContext;
        inputEvents->Size = &EventsSize;
        inputEvents->Get = &EventsGet;
        flushEvents = (ClapInputEvents*)NativeMemory.AllocZeroed((nuint)sizeof(ClapInputEvents));
        *flushEvents = *inputEvents;
        outputEvents = (ClapOutputEvents*)NativeMemory.AllocZeroed((nuint)sizeof(ClapOutputEvents));
        outputEvents->Ctx = inputContext;
        outputEvents->TryPush = &OutputPush;
        channels = (float**)NativeMemory.AllocZeroed(2, (nuint)sizeof(float*));
        output = (ClapAudioBuffer*)NativeMemory.AllocZeroed((nuint)sizeof(ClapAudioBuffer));
        output->Data32 = channels;
        output->ChannelCount = (uint)outputChannels;
        process = (ClapProcess*)NativeMemory.AllocZeroed((nuint)sizeof(ClapProcess));
        process->AudioOutputs = output;
        process->AudioOutputsCount = 1;
        process->InEvents = inputEvents;
        process->OutEvents = outputEvents;
        PluginThread.Shared.AddPoller(Poll);
    }

    /// <summary>What a plugin asked of the host (a bit set, raised from any thread).</summary>
    [Flags]
    internal enum Requests {
        Callback = 1,
        Restart = 2,
        Rescan = 4,
        Dirty = 8,
        Latency = 16,
        AudioThreadLog = 32
    }

    public InstrumentDescriptor Descriptor { get; }

    public int ParameterCount { get; }

    /// <summary>The file the plugin came from.</summary>
    public string Path => module.Path;

    /// <summary>The latency the plugin reported at activation, in frames (not compensated yet).</summary>
    public int LatencyFrames { get; private set; }

    /// <summary>Messages the plugin logged (from the plugin thread or a control thread).</summary>
    public Action<string>? Log { get; set; }

    /// <summary>Messages the plugin logged on the audio thread (counted, not formatted: that would allocate).</summary>
    public int AudioThreadLogs { get; private set; }

    /// <summary>Creates and initialises an instance of plugin <paramref name="pluginId" /> from <paramref name="path" />.</summary>
    public static ClapInstrument Create(string path, string pluginId, Action<string>? log = null) =>
        PluginThread.Shared.Invoke(() => {
                var module = ClapModule.Load(path);
                var descriptor = module.Descriptors.FirstOrDefault(d => d.Id == pluginId)
                    ?? throw new IOException($"{path} has no plugin {pluginId}.");
                return new ClapInstrument(module, descriptor, log);
            }
        );

    public void Prepare(float sampleRate, int maxFrames) {
        this.sampleRate = sampleRate;
        this.maxFrames = Math.Max(1, maxFrames);
    }

    /// <summary>Flushes the parameter values set so far and activates the plugin (the plugin thread).</summary>
    public void Activate() =>
        PluginThread.Shared.Invoke(() => {
                if (active || disposed) {
                    return;
                }

                FlushPreActivation();
                if (plugin->Activate(plugin, sampleRate, 1, (uint)maxFrames) == 0) {
                    throw new IOException($"{Descriptor.Id}: activate failed.");
                }

                active = true;
                var latency = (ClapPluginLatency*)Extension("clap.latency"u8);
                LatencyFrames = latency is null ? 0 : (int)latency->Get(plugin);
                if (LatencyFrames > 0) {
                    Log?.Invoke($"{Descriptor.Name}: {LatencyFrames} frames of latency (not compensated)");
                }
            }
        );

    /// <summary>Plugin thread, inactive: sends the parameter values set while the instance was being built.</summary>
    void FlushPreActivation() {
        if (preActivation.Count == 0 || parameters is null) {
            return;
        }

        var count = 0;
        foreach (var (index, value) in preActivation.Take(EventCapacity)) {
            WriteParam(count++, 0, index, value);
        }

        inputContext->Size = (uint)count;
        parameters->Flush(plugin, flushEvents, outputEvents);
        inputContext->Size = 0;
        preActivation.Clear();
    }

    public void SetParameter(int index, float value) {
        if ((uint)index >= (uint)ParameterCount) {
            return;
        }

        var info = Descriptor.Parameters[index];
        value = Math.Clamp(value, info.Min, info.Max);
        values[index] = value;
        if (!active && !AudioThread.IsCurrent) {
            preActivation.Add((index, value)); // the instance is still being built: flushed by Activate
            return;
        }

        for (var i = 0; i < pendingCount; i++) {
            if (pending[i].Index == index) {
                pending[i].Value = value;
                return;
            }
        }

        if (pendingCount < pending.Length) {
            pending[pendingCount++] = (index, value);
        }
    }

    public float GetParameter(int index) => (uint)index < (uint)ParameterCount ? values[index] : 0f;

    public void Process(ReadOnlySpan<MidiEvent> events, Span<float> left, Span<float> right) {
        var frames = left.Length;
        if (!active || frames == 0) {
            left.Clear();
            right.Clear();
            return;
        }

        if (!processing) {
            processing = plugin->StartProcessing(plugin) != 0;
            if (!processing) {
                left.Clear();
                right.Clear();
                return;
            }
        }

        var count = 0;
        for (var i = 0; i < pendingCount && count < EventCapacity; i++) {
            WriteParam(count++, 0, pending[i].Index, pending[i].Value);
        }

        pendingCount = 0;
        foreach (var e in events) {
            if (count < EventCapacity && WriteMidi(count, e, frames)) {
                count++;
            }
        }

        inputContext->Size = (uint)count;
        fixed (float* l = left) {
            fixed (float* r = right) {
                channels[0] = l;
                channels[1] = r;
                process->SteadyTime = steadyTime;
                process->FramesCount = (uint)frames;
                output->ConstantMask = 0;
                if (plugin->Process(plugin, process) == 0) {
                    left.Clear();
                    right.Clear();
                } else if (outputChannels == 1) {
                    left.CopyTo(right);
                }

                channels[0] = channels[1] = null;
            }
        }

        inputContext->Size = 0;
        steadyTime += frames;
    }

    public void Reset() {
        if (active && processing) {
            plugin->Reset(plugin);
        }
    }

    public bool LoadFile(string path) => false;

    public byte[]? SaveState() {
        if (stateExtension is null) {
            return null;
        }

        return PluginThread.Shared.Invoke(() => {
                var sink = new MemoryStream();
                var handle = GCHandle.Alloc(sink);
                try {
                    var stream = new ClapOStream { Ctx = (void*)GCHandle.ToIntPtr(handle), Write = &StreamWrite };
                    return stateExtension->Save(plugin, &stream) != 0 ? sink.ToArray() : null;
                } finally {
                    handle.Free();
                }
            }
        );
    }

    public bool LoadState(ReadOnlySpan<byte> state) {
        if (stateExtension is null) {
            return false;
        }

        var copy = state.ToArray();
        return PluginThread.Shared.Invoke(() => {
                var source = new MemoryStream(copy, false);
                var handle = GCHandle.Alloc(source);
                try {
                    var stream = new ClapIStream { Ctx = (void*)GCHandle.ToIntPtr(handle), Read = &StreamRead };
                    var loaded = stateExtension->Load(plugin, &stream) != 0;
                    ReadValues();
                    return loaded;
                } finally {
                    handle.Free();
                }
            }
        );
    }

    internal void Flag(Requests request) {
        int seen, next;
        do {
            seen = Volatile.Read(ref requests);
            next = seen | (int)request;
        } while (Interlocked.CompareExchange(ref requests, next, seen) != seen);
    }

    internal void Report(int severity, string message) {
        var level = severity switch {
            0 => "debug",
            1 => "info",
            2 => "warning",
            3 => "error",
            4 => "fatal",
            5 => "host misbehaving",
            6 => "plugin misbehaving",
            _ => "log"
        };
        Log?.Invoke($"{Descriptor?.Name ?? "clap"} [{level}]: {message}");
    }

    /// <summary>Plugin thread: serves what the plugin requested (on_main_thread, rescans) about every 10 ms.</summary>
    void Poll() {
        var raised = (Requests)Interlocked.Exchange(ref requests, 0);
        if (raised == 0 || disposed) {
            return;
        }

        if ((raised & Requests.Callback) != 0) {
            plugin->OnMainThread(plugin);
        }

        if ((raised & Requests.Rescan) != 0) {
            ReadValues();
        }

        if ((raised & Requests.AudioThreadLog) != 0) {
            AudioThreadLogs++;
            Log?.Invoke($"{Descriptor.Name}: logged on the audio thread (not shown: it would allocate)");
        }

        if ((raised & Requests.Restart) != 0) {
            Log?.Invoke($"{Descriptor.Name}: asked for a restart (not supported yet)");
        }
    }

    void* Extension(ReadOnlySpan<byte> id) {
        Span<byte> terminated = stackalloc byte[id.Length + 1];
        id.CopyTo(terminated);
        terminated[id.Length] = 0;
        fixed (byte* text = terminated) {
            return plugin->GetExtension(plugin, text);
        }
    }

    (bool Notes, bool Midi) Dialects(ClapPluginNotePorts* ports) {
        if (ports is null || ports->Count(plugin, 1) == 0) {
            return (true, true);
        }

        ClapNotePortInfo info;
        if (ports->Get(plugin, 0, 1, &info) == 0) {
            return (true, true);
        }

        return ((info.SupportedDialects & ClapNoteDialect.Clap) != 0,
            (info.SupportedDialects & ClapNoteDialect.Midi) != 0);
    }

    int OutputChannels(ClapPluginAudioPorts* ports) {
        if (ports is null || ports->Count(plugin, 0) == 0) {
            return 2;
        }

        ClapAudioPortInfo info;
        return ports->Get(plugin, 0, 0, &info) != 0 ? (int)Math.Clamp(info.ChannelCount, 1, 2) : 2;
    }

    List<InstrumentParameter> ReadParameters(out List<uint> ids) {
        var list = new List<InstrumentParameter>();
        ids = [];
        if (parameters is null) {
            return list;
        }

        var count = parameters->Count(plugin);
        var buffer = stackalloc byte[256];
        for (var i = 0u; i < count; i++) {
            ClapParamInfo info;
            if (parameters->GetInfo(plugin, i, &info) == 0 || (info.Flags & ClapParamFlags.IsHidden) != 0) {
                continue;
            }

            var min = (float)info.MinValue;
            var max = (float)info.MaxValue;
            var stepped = (info.Flags & ClapParamFlags.IsStepped) != 0;
            var steps = stepped ? (int)Math.Clamp(Math.Round(info.MaxValue - info.MinValue), 0, int.MaxValue) : 0;
            var names = stepped && (info.Flags & ClapParamFlags.IsEnum) != 0 && steps is > 0 and <= 128
                ? ValueNames(info.Id, info.MinValue, steps, buffer)
                : null;

            list.Add(
                new(
                    list.Count,
                    info.Id.ToString(CultureInfo.InvariantCulture),
                    ClapModule.Text(info.Name),
                    min,
                    max,
                    (float)info.DefaultValue,
                    string.Empty,
                    steps,
                    names
                )
            );
            ids.Add(info.Id);
        }

        return list;
    }

    /// <summary>A choice parameter's value names, as the plugin prints its values.</summary>
    List<string> ValueNames(uint id, double min, int steps, byte* buffer) {
        var names = new List<string>(steps + 1);
        for (var s = 0; s <= steps; s++) {
            names.Add(
                parameters->ValueToText(plugin, id, min + s, buffer, 256) != 0
                    ? ClapModule.Text(buffer)
                    : (min + s).ToString(CultureInfo.InvariantCulture)
            );
        }

        return names;
    }

    /// <summary>Main thread: reads every parameter's current value from the plugin.</summary>
    void ReadValues() {
        if (parameters is null) {
            return;
        }

        for (var i = 0; i < ParameterCount; i++) {
            double value;
            if (parameters->GetValue(plugin, parameterIds[i], &value) != 0) {
                values[i] = (float)value;
            }
        }
    }

    void WriteParam(int slot, uint time, int index, float value) {
        var e = (ClapEventParamValue*)eventList[slot];
        *e = default;
        e->Header = new() { Size = (uint)sizeof(ClapEventParamValue), Time = time, Type = ClapEventType.ParamValue };
        e->ParamId = parameterIds[index];
        e->NoteId = -1;
        e->PortIndex = -1;
        e->Channel = -1;
        e->Key = -1;
        e->Value = value;
    }

    bool WriteMidi(int slot, in MidiEvent m, int frames) {
        var time = (uint)Math.Clamp(m.Frame, 0, frames - 1);
        var kind = m.Status & 0xF0;
        var isNote = kind is 0x80 or 0x90;
        if (isNote && noteEvents) {
            var on = kind == 0x90 && m.Data2 > 0;
            var e = (ClapEventNote*)eventList[slot];
            e->Header = new() {
                Size = (uint)sizeof(ClapEventNote),
                Time = time,
                Type = on ? ClapEventType.NoteOn : ClapEventType.NoteOff
            };
            e->NoteId = -1;
            e->PortIndex = 0;
            e->Channel = (short)(m.Status & 0x0F);
            e->Key = (short)(m.Data1 & 0x7F);
            e->Velocity = on || kind == 0x80 ? m.Data2 / 127.0 : 0;
            return true;
        }

        if (!midiEvents) {
            return false;
        }

        var midi = (ClapEventMidi*)eventList[slot];
        midi->Header = new() { Size = (uint)sizeof(ClapEventMidi), Time = time, Type = ClapEventType.Midi };
        midi->PortIndex = 0;
        midi->Data[0] = m.Status;
        midi->Data[1] = m.Data1;
        midi->Data[2] = m.Data2;
        return true;
    }

    void Free() {
        if (host is not null) {
            NativeMemory.Free(host);
        }

        if (self.IsAllocated) {
            self.Free();
        }
    }

    public void Dispose() {
        if (disposed) {
            return;
        }

        PluginThread.Shared.Invoke(() => {
                if (processing) {
                    // Nothing processes the instance any more: the plugin thread stands in for the audio thread.
                    AudioThread.Mark();
                    try {
                        plugin->StopProcessing(plugin);
                    } finally {
                        AudioThread.Mark(false);
                    }

                    processing = false;
                }

                if (active) {
                    plugin->Deactivate(plugin);
                    active = false;
                }

                plugin->Destroy(plugin);
                disposed = true;
            }
        );
        PluginThread.Shared.RemovePoller(Poll);
        NativeMemory.Free(parameterIds);
        NativeMemory.Free(values);
        NativeMemory.AlignedFree(eventMemory);
        NativeMemory.Free(eventList);
        NativeMemory.Free(inputContext);
        NativeMemory.Free(inputEvents);
        NativeMemory.Free(flushEvents);
        NativeMemory.Free(outputEvents);
        NativeMemory.Free(channels);
        NativeMemory.Free(output);
        NativeMemory.Free(process);
        Free();
    }

    // ---- Native callbacks: event lists and streams --------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    struct EventContext {
        public ClapEventHeader** Events;
        public uint Size;
        public float* Values;
        public uint* Ids;
        public int Count;
    }

    [UnmanagedCallersOnly]
    static uint EventsSize(ClapInputEvents* list) => ((EventContext*)list->Ctx)->Size;

    [UnmanagedCallersOnly]
    static ClapEventHeader* EventsGet(ClapInputEvents* list, uint index) {
        var context = (EventContext*)list->Ctx;
        return index < context->Size ? context->Events[index] : null;
    }

    /// <summary>The plugin's output events: parameter values it changed itself update the cached values.</summary>
    [UnmanagedCallersOnly]
    static byte OutputPush(ClapOutputEvents* list, ClapEventHeader* header) {
        if (header is null || header->SpaceId != 0 || header->Type != ClapEventType.ParamValue) {
            return 1;
        }

        var context = (EventContext*)list->Ctx;
        var e = (ClapEventParamValue*)header;
        for (var i = 0; i < context->Count; i++) {
            if (context->Ids[i] == e->ParamId) {
                context->Values[i] = (float)e->Value;
                break;
            }
        }

        return 1;
    }

    [UnmanagedCallersOnly]
    static long StreamWrite(ClapOStream* stream, void* buffer, ulong size) {
        if (GCHandle.FromIntPtr((nint)stream->Ctx).Target is not MemoryStream sink) {
            return -1;
        }

        var count = (int)Math.Min(size, int.MaxValue);
        sink.Write(new(buffer, count));
        return count;
    }

    [UnmanagedCallersOnly]
    static long StreamRead(ClapIStream* stream, void* buffer, ulong size) {
        if (GCHandle.FromIntPtr((nint)stream->Ctx).Target is not MemoryStream source) {
            return -1;
        }

        return source.Read(new(buffer, (int)Math.Min(size, int.MaxValue)));
    }
}
