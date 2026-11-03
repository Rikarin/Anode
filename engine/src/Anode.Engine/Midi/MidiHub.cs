using Anode.Audio.Midi;
using Anode.Engine.Sync;
using Anode.Ipc;
using System.Diagnostics;

namespace Anode.Engine.Midi;

/// <summary>
///     The engine's MIDI thread (after Cathode's <c>MidiInput</c>): polls the device input and routes each message to
///     the tracks that hear it (every record-armed MIDI track whose input matches, else the MIDI-thru track); sends
///     what external MIDI tracks play to their ports at its timestamp; collects what armed tracks capture while
///     recording; batches live MIDI activity (at most 30 batches a second); and watches the ports, reopening them and
///     reporting the list when devices come or go. <see cref="Pump" /> is one pass (tests call it directly).
/// </summary>
/// <remarks>
///     Phase 8 (docs/09-phase-8.md, "Routing", "Sync"): it publishes Anode's virtual ports
///     (<see cref="VirtualPortPrefix" />);
///     decides per message from the server's <see cref="ControlRouting" /> whether it goes to the server
///     (<see cref="ControlMidi" />, batched every pass) or plays; hands the clock or MTC input's system messages and MTC
///     full frames to the <see cref="Sync" /> follower; sends the audio callback's MIDI Clock and MTC to their
///     destinations, each with its delay; and sends a control surface's feedback on request (<see cref="SendNow" />).
/// </remarks>
public sealed class MidiHub : IDisposable {
    const int ReadBatch = 256;
    static readonly double NanosecondsPerTick = 1e9 / Stopwatch.Frequency;
    static readonly long ActivityInterval = Stopwatch.Frequency / 30 + 1;

    readonly AudioEngine engine;
    readonly IMidiBackend? backend;
    readonly Lock pumpGate = new();
    readonly Lock activityGate = new();
    readonly MidiInputMessage[] input = new MidiInputMessage[ReadBatch];
    readonly List<OutgoingMidi> due = [];
    readonly List<OutgoingPortMidi> duePorts = [];
    readonly Dictionary<int, List<RecordedMidiEvent>> captured = [];
    readonly Dictionary<int, List<(long Clock, RecordedMidiEvent Event)>> history = [];
    readonly byte[] message = new byte[3];
    readonly byte[] sysex = new byte[16384]; // CTH_MIDI_SYSEX_MAX
    readonly byte[] syncBytes = new byte[10];
    readonly List<PendingSync> pendingSync = [];
    ControlRouting routing = ControlRouting.None;
    List<EngineControlEvent> control = [];
    IReadOnlyList<EngineSyncDestination> syncDestinations = [];
    List<MidiActivityEntry> activity = [];
    IReadOnlyList<MidiPortInfo> ports = [];
    IReadOnlyList<EngineMidiPort> enginePorts = [];
    Thread? thread;
    volatile bool running;
    int knownPorts = -1;
    long lastScan;
    long lastActivity;
    readonly Lock stepGate = new();
    StepChord? stepChord;

    /// <param name="engine">The audio callback whose tracks it feeds and whose queues it drains.</param>
    /// <param name="backend">The MIDI devices, or null for none (live MIDI from the server still plays).</param>
    public MidiHub(AudioEngine engine, IMidiBackend? backend) {
        this.engine = engine;
        this.backend = backend;
    }

    /// <summary>How often the port count is checked for devices plugged in or out.</summary>
    public TimeSpan ScanInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The ports changed (called on the MIDI thread, and once by <see cref="Start" />).</summary>
    public Action<IReadOnlyList<EngineMidiPort>>? PortsChanged { get; set; }

    /// <summary>A batch of live MIDI activity is ready (called on the MIDI thread or by <see cref="Pump" />).</summary>
    public Action<MidiActivityMessage>? Activity { get; set; }

    public Action<string>? Log { get; set; }

    /// <summary>The ports as last listed.</summary>
    public IReadOnlyList<EngineMidiPort> Ports => Volatile.Read(ref enginePorts);

    /// <summary>The track that hears live input when no armed track takes it (−1: none).</summary>
    public int ThruTrack {
        get => engine.ThruTrack;
        set => engine.ThruTrack = value;
    }

    /// <summary>Anode's virtual input's uid (the contract's <c>VirtualMidiPorts.InputId</c>).</summary>
    public const string VirtualInputId = "anode:in";

    /// <summary>Anode's virtual output's uid (the contract's <c>VirtualMidiPorts.OutputId</c>).</summary>
    public const string VirtualOutputId = "anode:out";

    /// <summary>
    ///     When set, <see cref="Start" /> publishes Anode's virtual ports where the backend has them, with uids
    ///     <c>&lt;prefix&gt;:in</c> and <c>&lt;prefix&gt;:out</c>: anode-host's prefix is <c>anode</c> (the ports
    ///     <see cref="VirtualInputId" /> "Anode In" and <see cref="VirtualOutputId" /> "Anode Out"); another prefix
    ///     names them "Anode In (prefix)", for tests that run hosts side by side.
    /// </summary>
    public string? VirtualPortPrefix { get; init; }

    /// <summary>A batch of messages consumed for control is ready (on the MIDI thread, at most once a pass).</summary>
    public Action<ControlMidiMessage>? ControlMidi { get; set; }

    /// <summary>The MIDI Clock and MTC follower the clock and MTC inputs feed (set before <see cref="Start" />).</summary>
    public SyncFollower? Sync { get; set; }

    /// <summary>The control routing in effect.</summary>
    public ControlRouting Routing => Volatile.Read(ref routing);

    /// <summary>Control thread: the server's control routing, replacing the previous one.</summary>
    public void SetRouting(ControlRouting next) => Volatile.Write(ref routing, next);

    /// <summary>Control thread: where the callback's MIDI Clock and MTC go, and each destination's delay.</summary>
    public void SetSyncDestinations(IReadOnlyList<EngineSyncDestination> destinations) =>
        Volatile.Write(ref syncDestinations, destinations);

    /// <summary>
    ///     Control thread: sends <paramref name="data" /> (whole messages, SysEx included) to output
    ///     <paramref name="port" /> now: a control surface's feedback. False when it could not.
    /// </summary>
    public bool SendNow(string port, ReadOnlySpan<byte> data) {
        if (backend is null || data.IsEmpty) {
            return false;
        }

        lock (pumpGate) { // the backend is the MIDI thread's; this waits for the pass in progress
            return backend.Send(port, data, 0);
        }
    }

    /// <summary>Opens the devices, lists the ports and starts the MIDI thread.</summary>
    public void Start() {
        if (thread is not null) {
            return;
        }

        lock (pumpGate) {
            if (VirtualPortPrefix is { Length: > 0 } prefix && backend is not null) {
                var suffix = prefix == "anode" ? string.Empty : $" ({prefix})";
                OpenVirtual($"Anode In{suffix}", $"{prefix}:in", true);
                OpenVirtual($"Anode Out{suffix}", $"{prefix}:out", false);
            }

            Rescan(true);
        }

        running = true;
        thread = new(Run) { IsBackground = true, Name = "anode midi" };
        thread.Start();
    }

    /// <summary>
    ///     A step of MIDI step input is ready (Phase 7, while <see cref="StepInputForward" />): on the MIDI thread, or on
    ///     the control thread for the server's live MIDI.
    /// </summary>
    public Action<StepInputMidiMessage>? StepInput { get; set; }

    /// <summary>
    ///     Phase 7's MIDI step input (<c>setStepInputForward</c>): while on, live note input (devices, and the server's
    ///     live MIDI) is also made into steps for <see cref="StepInput" />; it still plays as it would otherwise (as in
    ///     Logic, step-entered notes sound). Turning it on or off forgets the keys held so far.
    /// </summary>
    public bool StepInputForward {
        get {
            lock (stepGate) {
                return stepChord is not null;
            }
        }
        set {
            lock (stepGate) {
                stepChord = value ? new() : null;
            }
        }
    }

    /// <summary>Makes a note message a step while forwarding is on (any thread).</summary>
    void Forward(int status, int data1, int data2) {
        StepInputMidiMessage? step;
        lock (stepGate) {
            step = stepChord?.Take(status, data1, data2);
        }

        if (step is not null) {
            StepInput?.Invoke(step);
        }
    }

    /// <summary>
    ///     Control thread: plays <paramref name="events" /> on track <paramref name="trackId" /> now (the server's live
    ///     MIDI); with an Environment (Phase 11), through it, entering at its physical inputs. False when the model has
    ///     no such track.
    /// </summary>
    public bool Live(int trackId, IReadOnlyList<EngineMidiEvent> events) {
        foreach (var e in events) {
            Forward(e.Status, e.Data1, e.Data2);
        }

        var root = engine.Root;
        var index = root.IndexOf(trackId);
        if (index < 0) {
            return false;
        }

        var track = root.Tracks[index];
        var now = Now();
        var environment = engine.EnvironmentActive;
        foreach (var e in events) {
            if (e.Status is < 0x80 or >= 0xF0) {
                continue;
            }

            var (status, data1, data2) = ((byte)e.Status, (byte)(e.Data1 & 0x7F), (byte)(e.Data2 & 0x7F));
            if (environment) {
                engine.EnterEnvironment(new(now, status, data1, data2, null, trackId)); // Phase 11: through it
            } else {
                Push(root, track, new(now, status, data1, data2));
            }
        }

        lock (activityGate) {
            foreach (var e in events) {
                activity.Add(new(trackId, null, e.Status, e.Data1, e.Data2));
            }
        }

        return true;
    }

    /// <summary>Control thread, as a recording pass starts: forgets what earlier passes captured.</summary>
    public void BeginCapture() {
        lock (pumpGate) {
            DrainCaptured();
            captured.Clear();
        }
    }

    /// <summary>
    ///     Control thread, once the audio thread has stopped recording: what each armed track captured, by track id, in
    ///     order.
    /// </summary>
    public Dictionary<int, List<RecordedMidiEvent>> EndCapture() {
        lock (pumpGate) {
            DrainCaptured();
            var result = new Dictionary<int, List<RecordedMidiEvent>>(captured);
            captured.Clear();
            return result;
        }
    }

    /// <summary>How far back, in transport clocks, Flashback Capture's MIDI history reaches (default 10 minutes).</summary>
    public long HistoryFrames { get; set; } = 600L * 48_000;

    /// <summary>
    ///     Control thread, for a Flashback Capture: the live MIDI each track received while playing at clocks
    ///     [<paramref name="from" />, <paramref name="to" />), by track id, in the order played.
    /// </summary>
    public Dictionary<int, List<RecordedMidiEvent>> History(long from, long to) {
        lock (pumpGate) {
            DrainCaptured();
            var result = new Dictionary<int, List<RecordedMidiEvent>>();
            foreach (var (trackId, events) in history) {
                var list = events.Where(e => e.Clock >= from && e.Clock < to).Select(static e => e.Event).ToList();
                if (list.Count > 0) {
                    result[trackId] = list;
                }
            }

            return result;
        }
    }

    void OpenVirtual(string name, string uid, bool input) {
        var result = backend!.OpenVirtual(name, uid, input);
        if (result != 0 && result != IMidiBackend.VirtualUnsupported) {
            Log?.Invoke($"MIDI: the virtual port '{name}' could not be opened ({result})");
        }
    }

    /// <summary>One pass: input, output, capture, control, activity and (when due) the port check.</summary>
    public void Pump() {
        lock (pumpGate) {
            var now = Stopwatch.GetTimestamp();
            if (backend is not null && now - lastScan >= ScanInterval.TotalSeconds * Stopwatch.Frequency) {
                lastScan = now;
                Rescan(false);
            }

            ReadInput();
            ReadSystemExclusive();
            SendOutput();
            SendSync();
            Sync?.Poll(Now());
            DrainCaptured();
            FlushControl();
            FlushActivity(now);
        }
    }

    void Run() {
        while (running) {
            try {
                Pump();
            }
#pragma warning disable CA1031 // the MIDI thread keeps running; a failing device must not end it
            catch (Exception e) {
#pragma warning restore CA1031
                Log?.Invoke($"MIDI: {e.Message}");
                Thread.Sleep(100);
            }

            Thread.Sleep(1);
        }
    }

    static long Now() => (long)(Stopwatch.GetTimestamp() * NanosecondsPerTick);

    /// <summary>Reopens and relists the ports when their count changed (or when <paramref name="force" />).</summary>
    void Rescan(bool force) {
        if (backend is null) {
            if (force) {
                PortsChanged?.Invoke([]);
            }

            return;
        }

        var count = backend.PortCount();
        if (!force && count == knownPorts) {
            return;
        }

        knownPorts = count;
        backend.Open();
        var listed = backend.Ports();
        Volatile.Write(ref ports, listed);
        var mapped = listed.Select(static p => new EngineMidiPort(p.Uid, p.Name, p.IsInput, !p.IsInput)).ToArray();
        Volatile.Write(ref enginePorts, mapped);
        PortsChanged?.Invoke(mapped);
    }

    void ReadInput() {
        if (backend is null) {
            return;
        }

        int read;
        while ((read = backend.Read(input)) > 0) {
            var root = engine.Root;
            var known = Volatile.Read(ref ports);
            for (var i = 0; i < read; i++) {
                Route(root, known, input[i]);
            }

            if (read < input.Length) {
                break;
            }
        }
    }

    /// <summary>
    ///     A message from a device: to the server when the control routing takes it (and, then, nowhere else), a
    ///     system message to the sync follower, the rest to every armed MIDI track whose input matches, else to the thru
    ///     track; with an Environment (Phase 11), the rest into it instead, which routes it.
    /// </summary>
    void Route(ModelRoot root, IReadOnlyList<MidiPortInfo> known, in MidiInputMessage m) {
        if (m.Status < 0x80) {
            return;
        }

        var device = (uint)m.Source < (uint)known.Count ? known[m.Source].Uid : null;
        if (!Play(device, m)) {
            return;
        }

        Forward(m.Status, m.Data1, m.Data2);
        if (engine.EnvironmentActive) {
            engine.EnterEnvironment(new(m.TimeNs, m.Status, m.Data1, m.Data2, device));
            Note(-1, device, m);
            return;
        }

        var live = new LiveMidi(m.TimeNs, m.Status, m.Data1, m.Data2);
        var routed = false;
        foreach (var track in root.Tracks) {
            if (track.Armed
                && (track.IsMidi || track.NoteRoutes is not null)
                && Matches(track.MidiIo, device, m.Status)) {
                Push(root, track, live);
                Note(track.Id, device, m);
                routed = true;
            }
        }

        if (!routed && ThruTrack is var thruId and >= 0 && root.IndexOf(thruId) is var index and >= 0) {
            Push(root, root.Tracks[index], live);
            Note(thruId, device, m);
            routed = true;
        }

        if (!routed) {
            Note(-1, device, m);
        }
    }

    /// <summary>
    ///     The control routing's and the sync follower's say: a control port's messages and matched ones go to the
    ///     server (with every channel message while learning), the clock or MTC input's system messages to the follower;
    ///     true when the message still plays the tracks.
    /// </summary>
    bool Play(string? device, in MidiInputMessage m) {
        var current = Volatile.Read(ref routing);
        if (m.Status >= 0xF0) {
            // The sync follower first: a port chosen as the clock or MTC input syncs even when a surface also uses it
            // (surfaces send no clock); other system messages on a control port are the surface's.
            if (Sync is { } sync && sync.Wants(device, m.Status)) {
                Span<byte> bytes = [m.Status, m.Data1, m.Data2];
                sync.Receive(device, bytes[..Length(m.Status)], m.TimeNs);
            } else if (current.IsControlPort(device)) {
                Control(device, m);
            }

            return false; // system messages (clock, active sensing) are not played
        }

        var (forward, play) = current.Decide(device, m.Status, m.Data1);
        if (forward) {
            Control(device, m);
        }

        return play;
    }

    /// <summary>The length of a message by its status (1–3 bytes).</summary>
    static int Length(byte status) =>
        status switch {
            < 0xF0 => (status & 0xF0) is 0xC0 or 0xD0 ? 2 : 3,
            0xF1 or 0xF3 => 2,
            0xF2 => 3,
            _ => 1
        };

    void Control(string? device, in MidiInputMessage m) {
        var data = new byte[Length(m.Status)];
        data[0] = m.Status;
        if (data.Length > 1) {
            data[1] = m.Data1;
        }

        if (data.Length > 2) {
            data[2] = m.Data2;
        }

        control.Add(new(device ?? string.Empty, m.TimeNs, data));
    }

    /// <summary>Drains the SysEx: a control port's to the server, MTC full frames to the follower, the rest dropped.</summary>
    void ReadSystemExclusive() {
        if (backend is null) {
            return;
        }

        int length;
        while ((length = backend.ReadSystemExclusive(sysex, out var timeNs, out var source)) != 0) {
            if (length < 0) {
                break; // longer than any the backend queues
            }

            var known = Volatile.Read(ref ports);
            var device = (uint)source < (uint)known.Count ? known[source].Uid : null;
            var data = sysex.AsSpan(0, length);
            // MTC's full frame (F0 7F dev 01 01 …) syncs on the MTC input even when a surface uses the port too.
            var fullFrame = data is [0xF0, 0x7F, _, 0x01, 0x01, ..];
            if (fullFrame && Sync is { } mtc && mtc.Wants(device, 0xF0)) {
                mtc.Receive(device, data, timeNs);
            } else if (Volatile.Read(ref routing).IsControlPort(device)) {
                control.Add(new(device ?? string.Empty, timeNs, [..data]));
            } else if (Sync is { } sync && sync.Wants(device, 0xF0)) {
                sync.Receive(device, data, timeNs);
            }
        }
    }

    void FlushControl() {
        if (control.Count == 0) {
            return;
        }

        var batch = control;
        control = [];
        ControlMidi?.Invoke(new(batch));
    }

    /// <summary>
    ///     The callback's MIDI Clock and MTC to each destination that takes them, at their time plus its delay: queued
    ///     in time order and sent when due (CoreMIDI: a little ahead, timestamped, so it schedules them).
    /// </summary>
    void SendSync() {
        var destinations = Volatile.Read(ref syncDestinations);
        while (engine.SyncOut.TryDequeue(out var e)) {
            if (backend is not null) {
                QueueSync(destinations, e);
            }
        }

        if (pendingSync.Count == 0) {
            return;
        }

        var due = Now() + (backend!.SchedulesOutput ? SyncLeadNs : 0);
        var sent = 0;
        while (sent < pendingSync.Count && pendingSync[sent].TimeNs <= due) {
            var p = pendingSync[sent++];
            backend.Send(p.Port, SyncBytes(p.Event), Math.Max(0, p.TimeNs));
        }

        pendingSync.RemoveRange(0, sent);
    }

    /// <summary>A sync message for each destination that takes it, at its time plus the destination's delay, in time order.</summary>
    void QueueSync(IReadOnlyList<EngineSyncDestination> destinations, in OutgoingSync e) {
        foreach (var destination in destinations) {
            if (e.Kind == SyncMessageKind.Clock ? !destination.Clock : !destination.Mtc) {
                continue;
            }

            var pending = new PendingSync(e.TimeNs + (long)(destination.DelayMs * 1e6), destination.Port, e);
            var at = pendingSync.Count;
            while (at > 0 && pendingSync[at - 1].TimeNs > pending.TimeNs) {
                at--;
            }

            pendingSync.Insert(at, pending);
        }
    }

    /// <summary>How far ahead of its time a sync message goes to a backend that schedules (CoreMIDI).</summary>
    const long SyncLeadNs = 2_000_000;

    ReadOnlySpan<byte> SyncBytes(in OutgoingSync e) {
        if (e.Length == 10) {
            syncBytes[0] = 0xF0;
            syncBytes[1] = 0x7F;
            syncBytes[2] = 0x7F;
            syncBytes[3] = 0x01;
            syncBytes[4] = 0x01;
            syncBytes[5] = e.B0;
            syncBytes[6] = e.B1;
            syncBytes[7] = e.B2;
            syncBytes[8] = e.B3;
            syncBytes[9] = 0xF7;
            return syncBytes;
        }

        syncBytes[0] = e.B0;
        syncBytes[1] = e.B1;
        syncBytes[2] = e.B2;
        return syncBytes.AsSpan(0, Math.Clamp((int)e.Length, 1, 3));
    }

    readonly record struct PendingSync(long TimeNs, string Port, OutgoingSync Event);

    /// <summary>Whether a track's MIDI input (device and channel; null: any) takes a message from <paramref name="device" />.</summary>
    internal static bool Matches(EngineMidiIo? io, string? device, byte status) =>
        io is null
        || ((io.InputDevice is null || io.InputDevice == device)
            && (io.InputChannel == 0 || io.InputChannel == (status & 0x0F) + 1));

    /// <summary>
    ///     Queues live MIDI for <paramref name="track" />; a Drum Machine Designer track (Phase 6) routes it to its
    ///     pads instead: a note message (on, off, poly pressure) to the pad track its note routes to, unchanged (dropped
    ///     when no route has its note); other channel messages to every pad. Routed messages carry the DMD track as
    ///     their origin, so recording it captures them as its own.
    /// </summary>
    static void Push(ModelRoot root, TrackState track, in LiveMidi e) {
        if (track.NoteRoutes is not { } routes) {
            Enqueue(track, e);
            return;
        }

        var routed = e with { Origin = track.Id };
        if ((e.Status & 0xF0) is 0x80 or 0x90 or 0xA0) {
            if (routes[e.Data1 & 0x7F] is var target and >= 0 && root.IndexOf(target) is var i and >= 0) {
                Enqueue(root.Tracks[i], routed);
            }

            return;
        }

        for (var note = 0; note < routes.Length; note++) {
            var target = routes[note];
            if (target < 0 || Array.IndexOf(routes, target) != note) {
                continue; // each pad once
            }

            if (root.IndexOf(target) is var i and >= 0) {
                Enqueue(root.Tracks[i], routed);
            }
        }
    }

    static void Enqueue(TrackState track, in LiveMidi e) {
        lock (track.Midi.Live.ProducerGate) {
            track.Midi.Live.TryEnqueue(e);
        }
    }

    void Note(int trackId, string? device, in MidiInputMessage m) {
        lock (activityGate) {
            activity.Add(new(trackId, device, m.Status, m.Data1, m.Data2));
        }
    }

    /// <summary>
    ///     External MIDI tracks' messages, and the Environment's MIDI port objects' (Phase 11), to their ports: at once
    ///     when the OS schedules them, else when due.
    /// </summary>
    void SendOutput() {
        SendPorts();
        var root = engine.Root;
        while (engine.MidiOut.TryDequeue(out var e)) {
            if (backend is null) {
                continue;
            }

            if (backend.SchedulesOutput) {
                Send(root, e);
            } else {
                var at = due.Count;
                while (at > 0 && due[at - 1].TimeNs > e.TimeNs) {
                    at--;
                }

                due.Insert(at, e);
            }
        }

        if (due.Count == 0) {
            return;
        }

        var now = Now();
        var sent = 0;
        while (sent < due.Count && due[sent].TimeNs <= now) {
            Send(root, due[sent++]);
        }

        due.RemoveRange(0, sent);
    }

    /// <summary>The Environment's MIDI port objects' messages (Phase 11), as external tracks' are sent.</summary>
    void SendPorts() {
        while (engine.EnvironmentOut.TryDequeue(out var e)) {
            if (backend is null) {
                continue;
            }

            if (backend.SchedulesOutput) {
                SendPort(e);
            } else {
                var at = duePorts.Count;
                while (at > 0 && duePorts[at - 1].TimeNs > e.TimeNs) {
                    at--;
                }

                duePorts.Insert(at, e);
            }
        }

        if (duePorts.Count == 0) {
            return;
        }

        var now = Now();
        var sent = 0;
        while (sent < duePorts.Count && duePorts[sent].TimeNs <= now) {
            SendPort(duePorts[sent++]);
        }

        duePorts.RemoveRange(0, sent);
    }

    void SendPort(in OutgoingPortMidi e) {
        message[0] = e.Status;
        message[1] = e.Data1;
        message[2] = e.Data2;
        backend!.Send(e.Port, message.AsSpan(0, Length(e.Status)), e.TimeNs);
    }

    void Send(ModelRoot root, in OutgoingMidi e) {
        var index = root.IndexOf(e.TrackId);
        if (index < 0 || root.Tracks[index].MidiIo?.OutputDevice is not { } port) {
            return;
        }

        message[0] = e.Status;
        message[1] = e.Data1;
        message[2] = e.Data2;
        var length = (e.Status & 0xF0) is 0xC0 or 0xD0 ? 2 : 3;
        backend!.Send(port, message.AsSpan(0, length), e.TimeNs);
    }

    void DrainCaptured() {
        var newest = long.MinValue;
        while (engine.CapturedMidi.TryDequeue(out var e)) {
            if (e.Flashback) {
                if (!history.TryGetValue(e.TrackId, out var past)) {
                    history[e.TrackId] = past = [];
                }

                past.Add((e.Clock, new(e.Sample, e.Status, e.Data1, e.Data2)));
                newest = Math.Max(newest, e.Clock);
                continue;
            }

            if (!captured.TryGetValue(e.TrackId, out var list)) {
                captured[e.TrackId] = list = [];
            }

            list.Add(new(e.Sample, e.Status, e.Data1, e.Data2));
        }

        if (newest == long.MinValue) {
            return;
        }

        // Forget what is older than the history reaches.
        foreach (var past in history.Values) {
            var old = 0;
            while (old < past.Count && past[old].Clock < newest - HistoryFrames) {
                old++;
            }

            past.RemoveRange(0, old);
        }
    }

    void FlushActivity(long now) {
        if (now - lastActivity < ActivityInterval) {
            return;
        }

        List<MidiActivityEntry> batch;
        lock (activityGate) {
            if (activity.Count == 0) {
                return;
            }

            batch = activity;
            activity = [];
        }

        lastActivity = now;
        Activity?.Invoke(new(batch));
    }

    public void Dispose() {
        running = false;
        thread?.Join();
        thread = null;
        backend?.Dispose();
    }
}
