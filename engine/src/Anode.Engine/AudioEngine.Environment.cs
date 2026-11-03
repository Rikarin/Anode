using Anode.Engine.Environment;
using Anode.Engine.Midi;
using Anode.Ipc;

namespace Anode.Engine;

// The Environment on the MIDI input path (docs/12-phase-11.md, docs/adr/0015-environment.md; workstream A). While the
// project has one, the MIDI thread and the server's live MIDI queue their input here instead of on the tracks; each
// callback takes it at its sample (as far into the callback as it arrived into the previous one, as live MIDI on a
// track is), and each block runs the graph on it before the mix. What leaves the graph at the sequencer input goes
// where the default path sends live MIDI (the armed tracks whose input matches, else the MIDI-thru track; the server's
// live MIDI to its own track), at a track input to that track (a Drum Machine Designer's pads as on the default path),
// at a MIDI port to the MIDI thread (timestamped as an external track's), and at a control target to the control
// thread, which moves the parameter. The tracks take what reached them in their block's live input (recorded as live
// MIDI is). Without an Environment none of this runs: the default path is Phase 10's exactly, at its cost.
public sealed unsafe partial class AudioEngine {
    EnvironmentGraph environmentNext = EnvironmentGraph.Empty;
    EnvironmentGraph environment = EnvironmentGraph.Empty;
    EnvironmentRouting? routing;
    ModelRoot environmentModel = ModelRoot.Empty;
    long environmentClock;
    long environmentPanics;
    long environmentDroppedBefore;
    long environmentDropped;
    long environmentBlockStart;
    int thruTrack = -1;
    bool environmentRouted;

    /// <summary>
    ///     MIDI for the Environment (producers: the MIDI thread and the control thread, under its gate): a device's
    ///     message, the server's live MIDI (with its track), or live MIDI into an on-screen object.
    /// </summary>
    public SpscChannel<EnvironmentInput> EnvironmentIn { get; } = new(8192);

    /// <summary>What the Environment's MIDI port objects send (consumer: the MIDI thread).</summary>
    public SpscChannel<OutgoingPortMidi> EnvironmentOut { get; } = new(8192);

    /// <summary>What the Environment's control targets set (consumer: the control thread).</summary>
    public SpscChannel<ControlMove> ControlOut { get; } = new(4096);

    /// <summary>
    ///     The Environment the callback runs (<see cref="EnvironmentGraph.Empty" />: the default input path). The
    ///     control thread sets a compiled one; the callback swaps it in at its next start, resetting the old one and
    ///     ending the notes its objects left sounding on the tracks.
    /// </summary>
    public EnvironmentGraph Environment {
        get => Volatile.Read(ref environmentNext);
        set => Volatile.Write(ref environmentNext, value ?? EnvironmentGraph.Empty);
    }

    /// <summary>Whether live input goes through an Environment (the MIDI thread asks before routing each message).</summary>
    public bool EnvironmentActive => !Volatile.Read(ref environmentNext).IsEmpty;

    /// <summary>Events the Environment dropped since the engine started (its queues were full).</summary>
    public long EnvironmentDropped => Volatile.Read(ref environmentDropped);

    /// <summary>The track that hears live input when no armed track takes it (−1: none); the MIDI hub's.</summary>
    public int ThruTrack {
        get => Volatile.Read(ref thruTrack);
        set => Volatile.Write(ref thruTrack, value);
    }

    /// <summary>Producers (never the audio thread): queues <paramref name="e" /> for the Environment.</summary>
    public void EnterEnvironment(in EnvironmentInput e) {
        lock (EnvironmentIn.ProducerGate) {
            EnvironmentIn.TryEnqueue(e);
        }
    }

    /// <summary>
    ///     The callback's start: swaps in a new graph (resetting the old one), resets it on stop and locate, and takes
    ///     the queued input at its samples in this callback.
    /// </summary>
    void BeginEnvironment(ModelRoot model) {
        var next = Volatile.Read(ref environmentNext);
        environmentModel = model;
        if (!ReferenceEquals(next, environment)) {
            environmentDroppedBefore += environment.Dropped;
            environment.Reset();
            ReleaseEnvironmentNotes(model); // what the old graph's objects left sounding
            environment = next;
            environmentPanics = panics;
        }

        if (environmentPanics != panics) {
            environmentPanics = panics;
            environment.Reset();
        }

        if (next.IsEmpty) {
            EnvironmentIn.Clear(); // what was queued as the graph went
            return;
        }

        routing ??= new(this);
        var period = callbackNs - previousCallbackNs;
        while (EnvironmentIn.TryDequeue(out var input)) {
            var e = new EnvironmentEvent(
                environmentClock + Offset(input.TimeNs, period),
                input.Status,
                input.Data1,
                input.Data2,
                input.Port,
                input.Track
            );
            if (input.Object >= 0) {
                environment.InputAt(input.Object, e);
            } else {
                environment.Input(e);
            }
        }
    }

    /// <summary>A block: the graph over its frames, before the mix takes what reached the tracks.</summary>
    void EnvironmentBlock(int frames) {
        if (!environment.IsEmpty) {
            environmentBlockStart = environmentClock;
            environment.Process(environmentClock, frames, blockTransport, routing!);
            Volatile.Write(ref environmentDropped, environmentDroppedBefore + environment.Dropped);
        }

        environmentClock += frames;
    }

    /// <summary>After the block's mix: what reached tracks that did not take it (not MIDI tracks) goes.</summary>
    void EndEnvironmentBlock() {
        if (!environmentRouted) {
            return;
        }

        environmentRouted = false;
        foreach (var track in environmentModel.Tracks) {
            track.Midi.RoutedCount = 0;
        }
    }

    /// <summary>Note-offs, at the next block's start, for every note the Environment left sounding on a track.</summary>
    void ReleaseEnvironmentNotes(ModelRoot model) {
        foreach (var track in model.Tracks) {
            var midi = track.Midi;
            if (midi.RoutedHeld.IsEmpty) {
                continue;
            }

            var count = midi.RoutedHeld.Release(releases);
            for (var i = 0; i < count; i++) {
                midi.Route(0, releases[i].Status, releases[i].Note, 0, -1);
            }

            environmentRouted = true;
        }
    }

    /// <summary>The block's events the Environment sent the track, into its live input (captured as live MIDI is).</summary>
    void RoutedInput(TrackState track, MidiTrackState midi, int frames, long clock, bool recording, bool countIn) {
        var flashback = flashbackActive;
        for (var i = 0; i < midi.RoutedCount; i++) {
            ref readonly var e = ref midi.Routed[i];
            var frame = Math.Clamp(e.Frame, 0, frames - 1);
            midi.Add(frame, e.Status, e.Data1, e.Data2);
            var (owner, armed) = e.Origin >= 0 ? Origin(e.Origin, countIn) : (track.Id, recording);
            if (armed || flashback) {
                var played = clock + frame - (e.Generated ? 0 : MidiRecordShift); // an arpeggio where it sounds
                Capture(new(0, e.Status, e.Data1, e.Data2), owner, armed, flashback, played);
            }
        }

        midi.RoutedCount = 0;
    }

    /// <summary>Where the graph's events leave it, on the audio thread (see the file's head).</summary>
    sealed class EnvironmentRouting(AudioEngine engine) : IEnvironmentSink {
        public void SequencerInput(in EnvironmentEvent e) {
            var root = engine.environmentModel;
            if (e.Track >= 0) {
                Deliver(root, e.Track, e);
                return;
            }

            var routed = false;
            foreach (var track in root.Tracks) {
                if (track.Armed
                    && (track.IsMidi || track.NoteRoutes is not null)
                    && MidiHub.Matches(track.MidiIo, e.Port, e.Status)) {
                    Deliver(root, track, e);
                    routed = true;
                }
            }

            if (!routed && engine.ThruTrack is var thru and >= 0) {
                Deliver(root, thru, e);
            }
        }

        public void TrackInput(int trackId, in EnvironmentEvent e) => Deliver(engine.environmentModel, trackId, e);

        public void MidiPort(string port, in EnvironmentEvent e) {
            var frame = e.Sample - engine.environmentBlockStart;
            var time = engine.callbackNs
                + (long)((engine.OutputLatency + engine.blockOffset + frame) * (1e9 / engine.SampleRate));
            engine.EnvironmentOut.TryEnqueue(new(port, time, e.Status, e.Data1, e.Data2));
        }

        public void Control(EngineAutomationTarget target, float value, long sample) =>
            engine.ControlOut.TryEnqueue(new(target, value));

        void Deliver(ModelRoot root, int trackId, in EnvironmentEvent e) {
            if (root.IndexOf(trackId) is var index and >= 0) {
                Deliver(root, root.Tracks[index], e);
            }
        }

        /// <summary>
        ///     To <paramref name="track" />'s live input in this block; a Drum Machine Designer's notes to the pad its note
        ///     routes to, its other messages to every pad (as <see cref="MidiHub" /> routes live MIDI).
        /// </summary>
        void Deliver(ModelRoot root, TrackState track, in EnvironmentEvent e) {
            var frame = (int)(e.Sample - engine.environmentBlockStart);
            if (track.NoteRoutes is not { } routes) {
                Add(track, frame, e, -1);
                return;
            }

            if ((e.Status & 0xF0) is 0x80 or 0x90 or 0xA0) {
                if (routes[e.Data1 & 0x7F] is var target and >= 0 && root.IndexOf(target) is var i and >= 0) {
                    Add(root.Tracks[i], frame, e, track.Id);
                }

                return;
            }

            for (var note = 0; note < routes.Length; note++) {
                var target = routes[note];
                if (target >= 0 && Array.IndexOf(routes, target) == note && root.IndexOf(target) is var i and >= 0) {
                    Add(root.Tracks[i], frame, e, track.Id);
                }
            }
        }

        void Add(TrackState track, int frame, in EnvironmentEvent e, int origin) {
            var midi = track.Midi;
            midi.Route(frame, e.Status, (byte)(e.Data1 & 0x7F), (byte)(e.Data2 & 0x7F), origin, e.Generated);
            engine.environmentRouted = true;
        }
    }
}

/// <summary>
///     MIDI on its way to the Environment: when it arrived (ns on the monotonic clock; 0: at the next callback's start),
///     its bytes, the device it came from (null: live MIDI), the track the server's live MIDI was played on (−1: none),
///     and the on-screen object it is played into (−1: the physical inputs).
/// </summary>
public readonly record struct EnvironmentInput(
    long TimeNs,
    byte Status,
    byte Data1,
    byte Data2,
    string? Port = null,
    int Track = -1,
    int Object = -1
);

/// <summary>A message an Environment MIDI port object sends to <paramref name="Port" />, due at its time.</summary>
public readonly record struct OutgoingPortMidi(string Port, long TimeNs, byte Status, byte Data1, byte Data2);

/// <summary>An Environment control target's move: the target and its value (0…1 of its range).</summary>
public readonly record struct ControlMove(EngineAutomationTarget Target, float Value);
