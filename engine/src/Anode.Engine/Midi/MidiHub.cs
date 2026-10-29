using Anode.Audio.Midi;
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
    readonly Dictionary<int, List<RecordedMidiEvent>> captured = [];
    readonly Dictionary<int, List<(long Clock, RecordedMidiEvent Event)>> history = [];
    readonly byte[] message = new byte[3];
    List<MidiActivityEntry> activity = [];
    IReadOnlyList<MidiPortInfo> ports = [];
    IReadOnlyList<EngineMidiPort> enginePorts = [];
    Thread? thread;
    volatile bool running;
    int knownPorts = -1;
    long lastScan;
    long lastActivity;
    int thru = -1;

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
        get => Volatile.Read(ref thru);
        set => Volatile.Write(ref thru, value);
    }

    /// <summary>Opens the devices, lists the ports and starts the MIDI thread.</summary>
    public void Start() {
        if (thread is not null) {
            return;
        }

        lock (pumpGate) {
            Rescan(true);
        }

        running = true;
        thread = new(Run) { IsBackground = true, Name = "anode midi" };
        thread.Start();
    }

    /// <summary>
    ///     Control thread: plays <paramref name="events" /> on track <paramref name="trackId" /> now (the server's live
    ///     MIDI). False when the model has no such track.
    /// </summary>
    public bool Live(int trackId, IReadOnlyList<EngineMidiEvent> events) {
        var root = engine.Root;
        var index = root.IndexOf(trackId);
        if (index < 0) {
            return false;
        }

        var track = root.Tracks[index];
        var now = Now();
        foreach (var e in events) {
            if (e.Status is < 0x80 or >= 0xF0) {
                continue;
            }

            Push(root, track, new(now, (byte)e.Status, (byte)(e.Data1 & 0x7F), (byte)(e.Data2 & 0x7F)));
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

    /// <summary>One pass: input, output, capture, activity and (when due) the port check.</summary>
    public void Pump() {
        lock (pumpGate) {
            var now = Stopwatch.GetTimestamp();
            if (backend is not null && now - lastScan >= ScanInterval.TotalSeconds * Stopwatch.Frequency) {
                lastScan = now;
                Rescan(false);
            }

            ReadInput();
            SendOutput();
            DrainCaptured();
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

    /// <summary>Sends a message to every armed MIDI track whose input matches, else to the thru track.</summary>
    void Route(ModelRoot root, IReadOnlyList<MidiPortInfo> known, in MidiInputMessage m) {
        if (m.Status is < 0x80 or >= 0xF0) {
            return; // system messages (clock, active sensing) are not played
        }

        var device = (uint)m.Source < (uint)known.Count ? known[m.Source].Uid : null;
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

    static bool Matches(EngineMidiIo? io, string? device, byte status) =>
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

    /// <summary>External MIDI tracks' messages to their ports: at once when the OS schedules them, else when due.</summary>
    void SendOutput() {
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
