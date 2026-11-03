using Anode.Dsp.Transport;
using Anode.Ipc;
using Anode.Ipc.Telemetry;

namespace Anode.Engine.Environment;

/// <summary>
///     A MIDI event in the Environment: its absolute engine sample (where it happens in the block), its bytes, and the
///     input it came from (a device's port id; null for live MIDI and on-screen objects). <paramref name="Track" /> is
///     the track the server's live MIDI was played on (−1: a device, or an on-screen object): the sequencer input plays
///     it there, as the default path does. <paramref name="Generated" /> marks an event the graph timed itself (an
///     arpeggio's step, on the project's grid): a recording takes it where it sounds, where played input is taken
///     where it was played (the callback it waited for and the output latency earlier).
/// </summary>
public readonly record struct EnvironmentEvent(
    long Sample,
    byte Status,
    byte Data1,
    byte Data2,
    string? Port = null,
    int Track = -1,
    bool Generated = false
);

/// <summary>
///     Where the graph's events leave it (the engine's MIDI input path implements it): the sequencer input (the
///     record-enabled or selected track, as the default path), a track's input, an external port, or a control target
///     (a strip parameter, by the IPC's automation addressing), each at the event's sample. On the audio thread.
/// </summary>
public interface IEnvironmentSink {
    void SequencerInput(in EnvironmentEvent e);

    void TrackInput(int trackId, in EnvironmentEvent e);

    void MidiPort(string port, in EnvironmentEvent e);

    /// <summary>Sets control target <paramref name="target" /> to <paramref name="value" /> (0…1 of its range).</summary>
    void Control(EngineAutomationTarget target, float value, long sample);
}

/// <summary>
///     What an object emits into: the graph's routes from its outlets, at once or (delays, the arpeggiator) scheduled
///     later in the graph's bounded queue. <see cref="Schedule" /> is false when the queue is full; the event is then
///     dropped and counted. It also tells the object the block it runs in (<see cref="BlockStart" />,
///     <see cref="Frames" />, <see cref="Transport" />) and where the graph's events leave it (<see cref="Sink" />).
/// </summary>
public interface IEnvironmentEmitter {
    /// <summary>The engine sample of the block's first frame.</summary>
    long BlockStart { get; }

    int Frames { get; }

    /// <summary>The block's transport (its project sample, tempo and bar grid).</summary>
    ref readonly TransportContext Transport { get; }

    IEnvironmentSink Sink { get; }

    void Emit(int outlet, in EnvironmentEvent e);

    bool Schedule(int outlet, in EnvironmentEvent e);
}

/// <summary>
///     The Environment as the host runs it (docs/adr/0015-environment.md; workstream A): the compiled objects of an
///     <see cref="EngineEnvironment" /> in route order, on the MIDI input path. Built and prepared on the control thread
///     (<see cref="Compile" />, which allocates every queue), swapped in whole; <see cref="Input" />,
///     <see cref="InputAt" /> and <see cref="Process" /> run on the audio thread without allocating. The empty graph
///     (<see cref="Empty" />) costs nothing: the engine passes input straight to the sequencer input without it.
/// </summary>
/// <remarks>
///     A block runs as a data flow: every route goes to a later object, so the objects run once each, in index order,
///     each on what reached its inlets in the block (sorted by sample, stably): what entered at physical inputs (or at
///     an on-screen object), what earlier objects emitted, and what was scheduled earlier and falls due. An object then
///     <see cref="EnvironmentNode.Advance" />s (an arpeggiator plays its block). What an object emits within the block
///     reaches the next objects in the same block, at its sample; what it schedules past the block waits in the queue.
/// </remarks>
public sealed class EnvironmentGraph {
    /// <summary>The most events the graph's queue holds (scheduled delays and arpeggios included).</summary>
    public const int QueueCapacity = 4096;

    /// <summary>The most events that reach one object's inlets in a block, and that enter the graph in one callback.</summary>
    public const int InboxCapacity = 1024;

    /// <summary>The furthest ahead an event can be scheduled, in seconds (further is dropped).</summary>
    const double ScheduleBoundSeconds = 60;

    readonly EnvironmentNode[] nodes;
    readonly Inbox[] inboxes;
    readonly Pending[] pending;
    readonly Scheduled[] queue;
    readonly Emitter emitter;
    readonly long bound;
    int pendingCount;
    int queued;
    long sequence;
    long dropped;

    EnvironmentGraph() {
        nodes = [];
        inboxes = [];
        pending = [];
        queue = [];
        emitter = new(this);
    }

    EnvironmentGraph(EnvironmentNode[] nodes, float sampleRate) {
        this.nodes = nodes;
        inboxes = new Inbox[nodes.Length];
        for (var i = 0; i < inboxes.Length; i++) {
            inboxes[i] = new();
        }

        pending = new Pending[InboxCapacity];
        queue = new Scheduled[QueueCapacity];
        emitter = new(this);
        bound = (long)(ScheduleBoundSeconds * sampleRate);
    }

    /// <summary>The default input path: physical input to sequencer input.</summary>
    public static EnvironmentGraph Empty { get; } = new();

    public bool IsEmpty => this == Empty;

    /// <summary>The objects, by index.</summary>
    public IReadOnlyList<EnvironmentNode> Nodes => nodes;

    /// <summary>Events dropped since it was compiled (the queue was full, or a schedule fell past its bound).</summary>
    public long Dropped => Volatile.Read(ref dropped);

    /// <summary>On the control thread: the graph of <paramref name="environment" /> (null: <see cref="Empty" />).</summary>
    public static EnvironmentGraph Compile(EngineEnvironment? environment, float sampleRate, int maxFrames) {
        if (environment is null || environment.Objects.Count == 0) {
            return Empty;
        }

        // Indices are positions; a route that does not go forward (or anywhere) is left out: the server orders them.
        var objects = environment.Objects.OrderBy(static o => o.Index).ToArray();
        var nodes = new EnvironmentNode[objects.Length];
        for (var i = 0; i < objects.Length; i++) {
            var o = objects[i];
            var routes = o.Routes.Where(r => r.To > i && r.To < objects.Length && r.Outlet >= 0).ToArray();
            nodes[i] = EnvironmentNode.Create(o with { Index = i, Routes = routes });
            nodes[i].Prepare(sampleRate, maxFrames);
        }

        return new(nodes, sampleRate);
    }

    /// <summary>On the audio thread: an event from a physical input (a device, live MIDI, a control surface).</summary>
    public void Input(in EnvironmentEvent e) => Enter(-1, e);

    /// <summary>On the audio thread: live MIDI into object <paramref name="objectIndex" /> (an on-screen keyboard or fader).</summary>
    public void InputAt(int objectIndex, in EnvironmentEvent e) {
        if ((uint)objectIndex < (uint)nodes.Length) {
            Enter(objectIndex, e);
        }
    }

    void Enter(int target, in EnvironmentEvent e) {
        if (nodes.Length == 0) {
            return;
        }

        if (pendingCount == pending.Length) {
            dropped++;
            return;
        }

        pending[pendingCount++] = new(target, e);
    }

    /// <summary>
    ///     On the audio thread, once per block: runs what is due in [<paramref name="blockStart" />,
    ///     <paramref name="blockStart" /> + <paramref name="frames" />) through the objects, in sample order, into
    ///     <paramref name="sink" />.
    /// </summary>
    public void Process(long blockStart, int frames, in TransportContext transport, IEnvironmentSink sink) {
        if (nodes.Length == 0) {
            return;
        }

        var end = blockStart + frames;
        emitter.Begin(blockStart, frames, transport, sink);
        TakeInput(blockStart, end);
        TakeScheduled(blockStart, end);
        for (var i = 0; i < nodes.Length; i++) {
            var node = nodes[i];
            var inbox = inboxes[i];
            emitter.Current = i;
            inbox.Sort();
            foreach (ref readonly var item in inbox.Items) { // what it emits goes to later objects' inboxes only
                node.Receive(item.Inlet, item.Event, emitter);
            }

            inbox.Count = 0;
            node.Advance(blockStart, frames, emitter);
        }
    }

    /// <summary>What entered the graph and falls in the block (late ones at its start) into their objects' inboxes.</summary>
    void TakeInput(long start, long end) {
        var kept = 0;
        for (var p = 0; p < pendingCount; p++) {
            var (target, e) = pending[p];
            if (e.Sample >= end) {
                pending[kept++] = pending[p];
                continue;
            }

            var at = e with { Sample = Math.Max(start, e.Sample) };
            if (target >= 0) {
                Deliver(target, -1, at);
                continue;
            }

            for (var i = 0; i < nodes.Length; i++) {
                if (nodes[i].Settings.Kind == EngineEnvironmentKind.PhysicalInput) {
                    Deliver(i, 0, at);
                }
            }
        }

        pendingCount = kept;
    }

    /// <summary>The queue's events due in the block, along their routes.</summary>
    void TakeScheduled(long start, long end) {
        while (queued > 0 && queue[0].Event.Sample < end) {
            var s = queue[0];
            Pop();
            var e = s.Event.Sample < start ? s.Event with { Sample = start } : s.Event;
            Route(s.Node, s.Outlet, e);
        }
    }

    /// <summary>An event from object <paramref name="from" />'s outlet <paramref name="outlet" /> to every route's inlet.</summary>
    void Route(int from, int outlet, in EnvironmentEvent e) {
        var routes = nodes[from].Settings.Routes;
        for (var r = 0; r < routes.Count; r++) {
            var route = routes[r];
            if (route.Outlet == outlet) {
                Deliver(route.To, route.Inlet, e);
            }
        }
    }

    void Deliver(int to, int inlet, in EnvironmentEvent e) {
        if (!inboxes[to].Add(inlet, e)) {
            dropped++;
        }
    }

    bool Push(int from, int outlet, in EnvironmentEvent e) {
        if (queued == queue.Length || e.Sample - emitter.BlockStart > bound) {
            dropped++;
            return false;
        }

        // A binary heap by sample, then by when it was scheduled (events at one sample keep their order).
        var item = new Scheduled(e, from, outlet, sequence++);
        var at = queued++;
        while (at > 0) {
            var parent = (at - 1) >> 1;
            if (!Before(item, queue[parent])) {
                break;
            }

            queue[at] = queue[parent];
            at = parent;
        }

        queue[at] = item;
        return true;
    }

    void Pop() {
        var last = queue[--queued];
        var at = 0;
        while (true) {
            var child = 2 * at + 1;
            if (child >= queued) {
                break;
            }

            if (child + 1 < queued && Before(queue[child + 1], queue[child])) {
                child++;
            }

            if (!Before(queue[child], last)) {
                break;
            }

            queue[at] = queue[child];
            at = child;
        }

        if (queued > 0) {
            queue[at] = last;
        }
    }

    static bool Before(in Scheduled a, in Scheduled b) =>
        a.Event.Sample < b.Event.Sample || (a.Event.Sample == b.Event.Sample && a.Sequence < b.Sequence);

    /// <summary>
    ///     On the control (or telemetry) thread: writes the monitors' and faders' telemetry slots; the slots no object
    ///     of this graph has are freed.
    /// </summary>
    public void Publish(Span<EnvironmentMonitorEntry> monitors, Span<EnvironmentFaderEntry> faders) {
        Span<bool> monitorUsed = stackalloc bool[monitors.Length];
        Span<bool> faderUsed = stackalloc bool[faders.Length];
        foreach (var node in nodes) {
            var slot = node.Settings.TelemetrySlot;
            switch (node) {
                case MonitorNode monitor when (uint)slot < (uint)monitors.Length:
                    monitor.Publish(ref monitors[slot]);
                    monitorUsed[slot] = true;
                    break;
                case FaderNode fader when (uint)slot < (uint)faders.Length:
                    faders[slot] = new() { Object = node.Settings.Index + 1, Value = fader.Value };
                    faderUsed[slot] = true;
                    break;
            }
        }

        for (var m = 0; m < monitors.Length; m++) {
            if (!monitorUsed[m] && monitors[m].Object != 0) {
                monitors[m] = default;
            }
        }

        for (var f = 0; f < faders.Length; f++) {
            if (!faderUsed[f] && faders[f].Object != 0) {
                faders[f] = default;
            }
        }
    }

    /// <summary>Releases held notes and clears every queue (stop, locate, a graph swapped out).</summary>
    public void Reset() {
        pendingCount = 0;
        queued = 0;
        foreach (var inbox in inboxes) {
            inbox.Count = 0;
        }

        foreach (var node in nodes) {
            node.Reset();
        }
    }

    readonly record struct Pending(int Target, EnvironmentEvent Event);

    readonly record struct Scheduled(EnvironmentEvent Event, int Node, int Outlet, long Sequence);

    /// <summary>An event at an inlet (−1: live MIDI into the object itself).</summary>
    readonly record struct Item(int Inlet, EnvironmentEvent Event);

    /// <summary>What reached an object in the block, preallocated.</summary>
    sealed class Inbox {
        readonly Item[] items = new Item[InboxCapacity];
        public int Count;

        public ReadOnlySpan<Item> Items => items.AsSpan(0, Count);

        public bool Add(int inlet, in EnvironmentEvent e) {
            if (Count == items.Length) {
                return false;
            }

            items[Count++] = new(inlet, e);
            return true;
        }

        /// <summary>A stable insertion sort by sample (mostly in order already).</summary>
        public void Sort() {
            for (var i = 1; i < Count; i++) {
                var item = items[i];
                var j = i - 1;
                while (j >= 0 && items[j].Event.Sample > item.Event.Sample) {
                    items[j + 1] = items[j];
                    j--;
                }

                items[j + 1] = item;
            }
        }
    }

    /// <summary>The graph's one emitter: routes the current object's events, or schedules them.</summary>
    sealed class Emitter(EnvironmentGraph graph) : IEnvironmentEmitter {
        TransportContext transport;

        public int Current;

        public long BlockStart { get; private set; }

        public int Frames { get; private set; }

        public ref readonly TransportContext Transport => ref transport;

        public IEnvironmentSink Sink { get; private set; } = NullSink.Instance;

        public void Begin(long blockStart, int frames, in TransportContext context, IEnvironmentSink sink) {
            BlockStart = blockStart;
            Frames = frames;
            transport = context;
            Sink = sink;
        }

        public void Emit(int outlet, in EnvironmentEvent e) {
            var end = BlockStart + Frames;
            if (e.Sample >= end) {
                Schedule(outlet, e);
                return;
            }

            graph.Route(Current, outlet, e.Sample < BlockStart ? e with { Sample = BlockStart } : e);
        }

        public bool Schedule(int outlet, in EnvironmentEvent e) {
            if (e.Sample < BlockStart + Frames) {
                graph.Route(Current, outlet, e.Sample < BlockStart ? e with { Sample = BlockStart } : e);
                return true;
            }

            return graph.Push(Current, outlet, e);
        }
    }

    sealed class NullSink : IEnvironmentSink {
        public static readonly NullSink Instance = new();

        public void SequencerInput(in EnvironmentEvent e) { }

        public void TrackInput(int trackId, in EnvironmentEvent e) { }

        public void MidiPort(string port, in EnvironmentEvent e) { }

        public void Control(EngineAutomationTarget target, float value, long sample) { }
    }
}
