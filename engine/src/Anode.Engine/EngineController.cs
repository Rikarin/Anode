using Anode.Audio.Midi;
using Anode.Engine.Midi;
using Anode.Engine.Plugins;
using Anode.Engine.Recording;
using Anode.Engine.Streaming;
using Anode.Ipc;
using Anode.Ipc.Segments;
using System.Globalization;

namespace Anode.Engine;

/// <summary>How the engine is set up: the device's format and the streaming parameters.</summary>
/// <param name="SampleRate">The device's sample rate.</param>
/// <param name="BufferFrames">The device's buffer size, in frames.</param>
/// <param name="RoundTripLatency">Input plus output latency in frames, by which recorded takes are shifted back.</param>
/// <param name="OutputLatency">
///     Output latency in frames: external MIDI is timestamped this far ahead, and recorded live MIDI is shifted back by
///     it plus one buffer.
/// </param>
/// <param name="StreamSeconds">Audio each track's stream keeps queued ahead of the playhead (spike S2: 2 s).</param>
/// <param name="VerifyBytes">
///     Segments up to this size get the checksum and sort-order check on load (ADR-0006: on for per-track segments,
///     off for whole models over a few MB). Layout validation and the prefault always run.
/// </param>
/// <param name="FlashbackSeconds">
///     How much of each armed audio track's input, and of live MIDI, Flashback Capture keeps (docs/04-phase-3.md: 10
///     minutes).
/// </param>
public sealed record EngineOptions(
    int SampleRate,
    int BufferFrames,
    int RoundTripLatency = 0,
    int OutputLatency = 0,
    double StreamSeconds = 2,
    long VerifyBytes = 4 << 20,
    double FlashbackSeconds = 600
);

/// <summary>The takes a recording pass produced: audio files, and the live MIDI armed MIDI tracks received.</summary>
public sealed record RecordingResult(IReadOnlyList<RecordedTake> Takes, IReadOnlyList<RecordedMidiTake> MidiTakes);

/// <summary>
///     The engine's control plane, called from the host's control thread (the IPC connection): loads compiled models
///     from shared-memory segments and publishes them to the audio and disk threads (RCU; docs/adr/0006-bulk-ipc.md),
///     sets strips and track IO, builds instruments and swaps them in, queues parameter changes and live MIDI, drives
///     the transport through the audio thread's command queue and runs recording passes. Every method is serialised by
///     one lock; the audio thread never waits for it.
/// </summary>
public sealed partial class EngineController : IDisposable {
    /// <summary>Whole-model segments kept mapped for the server to reuse (its pool of two slots, ADR-0006).</summary>
    const int PooledSegments = 2;

    static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(2);
    static readonly TimeSpan PrimeTimeout = TimeSpan.FromSeconds(2);

    readonly Lock gate = new();
    readonly Lock sendGate = new();
    readonly Dictionary<string, MappedSegment> pool = new(StringComparer.Ordinal);
    readonly List<string> poolOrder = [];
    readonly Dictionary<int, StreamFile?> files = [];
    readonly float[] scratch = new float[64 * 1024];
    readonly int streamFrames;
    readonly long flashbackFrames;
    long segmentIds;
    int generations;
    bool countIn;
    RecordTake[] takes = [];
    bool recordingPass;

    /// <param name="options">The device's format and the streaming parameters.</param>
    /// <param name="plugins">The instruments it can load (default: the built-ins only).</param>
    /// <param name="midi">The MIDI devices (null: none; live MIDI from the server still plays).</param>
    public EngineController(EngineOptions options, PluginRegistry? plugins = null, IMidiBackend? midi = null) {
        Options = options;
        Engine = new(options.SampleRate) {
            OutputLatency = options.OutputLatency,
            MidiRecordShift = options.BufferFrames + options.OutputLatency,
            RecordLatency = Math.Max(0, options.RoundTripLatency)
        };
        flashbackFrames = Math.Max(1, (long)(options.FlashbackSeconds * options.SampleRate));
        streamFrames = (int)(options.StreamSeconds * options.SampleRate);
        Streamer = new(Engine, Math.Max(4 * options.BufferFrames, options.SampleRate * 15 / 100));
        Writer = new();
        Plugins = plugins ?? new();
        Midi = new(Engine, midi) { Log = message => Log?.Invoke(message), HistoryFrames = flashbackFrames };
    }

    public EngineOptions Options { get; }

    /// <summary>The audio callback, to hand to a device.</summary>
    public AudioEngine Engine { get; }

    public DiskStreamer Streamer { get; }

    public RecordWriter Writer { get; }

    /// <summary>The instruments it can load.</summary>
    public PluginRegistry Plugins { get; }

    /// <summary>The MIDI thread: device input and output, live MIDI, capture and activity.</summary>
    public MidiHub Midi { get; }

    /// <summary>Where recorded takes go, from the last model load (empty before one).</summary>
    public string RecordDirectory { get; private set; } = string.Empty;

    /// <summary>Whether a recording pass is running.</summary>
    public bool IsRecording {
        get {
            lock (gate) {
                return takes.Length > 0;
            }
        }
    }

    /// <summary>Segments mapped now (pooled ones included), for leak checks.</summary>
    public int MappedSegments {
        get {
            lock (gate) {
                var set = new HashSet<MappedSegment>(pool.Values);
                AddOwners(Engine.Root, set);
                return set.Count;
            }
        }
    }

    /// <summary>
    ///     Starts the disk thread and the MIDI thread (offline renders call <see cref="DiskStreamer.Pass" /> and
    ///     <see cref="MidiHub.Pump" /> instead).
    /// </summary>
    public void Start() {
        Streamer.Start();
        Midi.Start();
    }

    /// <summary>Where diagnostics go (missing files and the like).</summary>
    public Action<string>? Log { get; set; }

    // ---- Model -------------------------------------------------------------------------------------------------------

    /// <summary>
    ///     Maps, validates and prefaults the segment, opens new files, builds the next model and publishes it; returns
    ///     once the model it replaces can no longer be in use (the server then disposes the segment). Throws
    ///     <see cref="IOException" /> or <see cref="InvalidDataException" /> when the segment cannot be used, and
    ///     leaves the current model playing.
    /// </summary>
    public long LoadModel(LoadModelMessage message) {
        lock (gate) {
            var segment = MapSegment(message.Segment, message.Replace);
            OpenFiles(message.Files);
            var settings = new Dictionary<int, EngineTrack>();
            foreach (var t in message.Tracks) {
                settings[t.Id] = t;
            }

            var current = Engine.Root;
            var draft = new ModelDraft(this, message.Replace ? null : current);
            unsafe {
                for (var k = 0; k < segment.Header->TrackCount; k++) {
                    draft.Patch(segment, k, settings, current);
                }
            }

            draft.Apply(settings);
            var replaced = draft.ApplyInstruments(settings);
            draft.PrepareStreams();
            Engine.Master.Set(message.Master);
            RecordDirectory = message.RecordDirectory;
            long length;
            unsafe {
                length = message.Replace
                    ? segment.Header->LengthSamples
                    : Math.Max(current.LengthSamples, segment.Header->LengthSamples);
            }

            Swap(draft.Build(message.Segment.Version, length, FileTable()));
            foreach (var slot in replaced) {
                slot.Dispose(); // the swap's grace period covered the instruments set before it
            }

            SyncFlashback();

            return message.Segment.Version;
        }
    }

    /// <summary>The next model's tracks while a load assembles them: the current ones (a patch) or none (a replace).</summary>
    sealed class ModelDraft {
        readonly EngineController owner;
        readonly List<TrackState> tracks = [];
        readonly List<TrackView> views = [];
        readonly List<int> generations = [];
        readonly List<MappedSegment?> owners = [];
        readonly List<byte[]?> lanes = [];
        TrackView metronome;
        MappedSegment? metronomeOwner;

        public ModelDraft(EngineController owner, ModelRoot? from) {
            this.owner = owner;
            if (from is null) {
                return;
            }

            tracks.AddRange(from.Tracks);
            views.AddRange(from.Views);
            generations.AddRange(from.Generations);
            owners.AddRange(from.Owners);
            lanes.AddRange(from.Lanes);
            metronome = from.Metronome;
            metronomeOwner = from.MetronomeOwner;
        }

        /// <summary>Takes track <paramref name="k" /> of <paramref name="segment" />: a new compile of it.</summary>
        public void Patch(MappedSegment segment, int k, Dictionary<int, EngineTrack> settings, ModelRoot current) {
            var id = segment.TrackId(k);
            if (id == TrackEntry.MetronomeTrack) {
                metronome = segment.Track(k);
                metronomeOwner = segment;
                return;
            }

            var index = tracks.FindIndex(t => t.Id == id);
            var existing = index >= 0 ? tracks[index] : Find(current, id);
            var stereo = settings.TryGetValue(id, out var s) ? s.Stereo : existing?.Stereo ?? false;
            var state = existing is not null && existing.Stereo == stereo
                ? existing
                : owner.NewTrack(id, stereo, existing);
            var view = segment.Track(k);
            if (index < 0) {
                tracks.Add(state);
                views.Add(view);
                generations.Add(++owner.generations);
                owners.Add(segment);
                lanes.Add(PlaylistLanes.Of(view));
            } else {
                tracks[index] = state;
                views[index] = view;
                generations[index] = ++owner.generations;
                owners[index] = segment;
                lanes[index] = PlaylistLanes.Of(view);
            }
        }

        /// <summary>Applies the listed tracks' settings; a changed channel count gets a new stream.</summary>
        public void Apply(Dictionary<int, EngineTrack> settings) {
            for (var i = 0; i < tracks.Count; i++) {
                if (!settings.TryGetValue(tracks[i].Id, out var s)) {
                    continue;
                }

                if (s.Stereo != tracks[i].Stereo) {
                    tracks[i] = owner.NewTrack(s.Id, s.Stereo, tracks[i]);
                    generations[i] = ++owner.generations;
                }

                tracks[i].Apply(s);
            }
        }

        /// <summary>
        ///     Builds or updates the listed tracks' instruments; returns the instruments they replaced, to dispose once
        ///     the model is swapped in. A track whose instrument cannot be built is left without one (and logged).
        /// </summary>
        public List<InstrumentSlot> ApplyInstruments(Dictionary<int, EngineTrack> settings) {
            var replaced = new List<InstrumentSlot>();
            foreach (var track in tracks) {
                if (!settings.TryGetValue(track.Id, out var s)) {
                    continue;
                }

                try {
                    if (owner.ApplyInstrument(track, s.Instrument) is { } old) {
                        replaced.Add(old);
                    }
                } catch (Exception e) when (IsInstrumentFailure(e)) {
                    owner.Log?.Invoke($"track {track.Id}: instrument {s.Instrument?.PluginId}: {e.Message}");
                    if (track.Instrument is { } old) {
                        track.Instrument = null;
                        replaced.Add(old);
                    }
                }
            }

            return replaced;
        }

        /// <summary>Gives every track whose playlist has crossfades its second stream, before the model is published.</summary>
        public void PrepareStreams() {
            for (var i = 0; i < tracks.Count; i++) {
                if (lanes[i] is not null) {
                    tracks[i].EnsureCrossfade();
                }
            }
        }

        public ModelRoot Build(long version, long length, StreamFile?[] files) =>
            new(
                version,
                length,
                [..tracks],
                [..views],
                [..generations],
                [..owners],
                metronome,
                metronomeOwner,
                files,
                [..lanes]
            );
    }

    /// <summary>Control thread: a strip changed; <paramref name="trackId" /> −1 is the master. False for an unknown track.</summary>
    public bool SetStrip(int trackId, EngineStrip strip) {
        lock (gate) {
            if (trackId == -1) {
                Engine.Master.Set(strip);
                return true;
            }

            var track = Find(Engine.Root, trackId);
            track?.Strip.Set(strip);
            return track is not null;
        }
    }

    /// <summary>A track's input, arm or monitoring changed. A change of channel count swaps in a new stream.</summary>
    public bool SetTrackIo(int trackId, int inputChannel, bool stereo, bool armed, bool monitoring) {
        lock (gate) {
            var current = Engine.Root;
            var index = current.IndexOf(trackId);
            if (index < 0) {
                return false;
            }

            var track = current.Tracks[index];
            if (track.Stereo != stereo) {
                var tracks = (TrackState[])current.Tracks.Clone();
                var gens = (int[])current.Generations.Clone();
                tracks[index] = track = NewTrack(trackId, stereo, track);
                gens[index] = ++generations;
                Swap(
                    new(
                        current.Version,
                        current.LengthSamples,
                        tracks,
                        current.Views,
                        gens,
                        current.Owners,
                        current.Metronome,
                        current.MetronomeOwner,
                        current.Files,
                        current.Lanes
                    )
                );
            }

            track.SetIo(inputChannel, armed, monitoring);
            SyncFlashback();
            return true;
        }
    }

    // ---- Instruments and MIDI ----------------------------------------------------------------------------------------

    /// <summary>
    ///     Gives a track an instrument (null removes it), building it here and swapping it in. Throws
    ///     <see cref="InvalidOperationException" /> for an unknown track or plugin, and the instrument's own failures.
    /// </summary>
    public void SetInstrument(int trackId, EngineInstrument? instrument) {
        lock (gate) {
            var track = Find(Engine.Root, trackId)
                ?? throw new InvalidOperationException($"No track {trackId}.");
            if (ApplyInstrument(track, instrument) is { } old) {
                WaitGrace();
                old.Dispose();
            }

            SyncFlashback();
        }
    }

    /// <summary>Queues a parameter change for the track's instrument; false for an unknown track or parameter.</summary>
    public bool SetParameter(int trackId, string parameterId, double value) {
        lock (gate) {
            return Find(Engine.Root, trackId)?.Instrument is { } slot && slot.Set(parameterId, value);
        }
    }

    /// <summary>A track's MIDI input and output; false for an unknown track.</summary>
    public bool SetMidiIo(int trackId, EngineMidiIo midi) {
        lock (gate) {
            var track = Find(Engine.Root, trackId);
            track?.MidiIo = midi;
            SyncFlashback();
            return track is not null;
        }
    }

    /// <summary>The track that hears live MIDI when no armed track takes it (−1: none).</summary>
    public void SetMidiThru(int trackId) => Midi.ThruTrack = trackId;

    /// <summary>Plays live MIDI (the server's on-screen keyboard) on a track; false for an unknown track.</summary>
    public bool LiveMidi(int trackId, IReadOnlyList<EngineMidiEvent> events) => Midi.Live(trackId, events);

    /// <summary>Every instrument's opaque state (base64, null when it keeps none), by track.</summary>
    public IReadOnlyList<InstrumentState> InstrumentStates() {
        lock (gate) {
            var states = new List<InstrumentState>();
            foreach (var track in Engine.Root.Tracks) {
                if (track.Instrument is not { } slot) {
                    continue;
                }

                string? state = null;
                try {
                    state = slot.Instrument.SaveState() is { } bytes ? Convert.ToBase64String(bytes) : null;
                } catch (Exception e) when (IsInstrumentFailure(e)) {
                    Log?.Invoke($"track {track.Id}: saving the instrument's state failed: {e.Message}");
                }

                states.Add(new(track.Id, state));
            }

            return states;
        }
    }

    /// <summary>
    ///     Brings <paramref name="track" />'s instrument in line with <paramref name="spec" />. The same plugin and
    ///     file only update the parameters it lists (the server resends a track's settings without the state, and a
    ///     saved state matters only to a new instance); another plugin or file builds a new instance (prepared, state
    ///     and file loaded, parameters set, the plugin's defaults for any not listed, activated) and swaps it in.
    ///     Returns the instrument it replaced, which the caller disposes after a grace period.
    /// </summary>
    InstrumentSlot? ApplyInstrument(TrackState track, EngineInstrument? spec) {
        var current = track.Instrument;
        if (spec is null) {
            track.Instrument = null;
            return current;
        }

        if (current is not null
            && current.PluginId == spec.PluginId
            && current.File == spec.File) {
            foreach (var (id, value) in spec.Parameters) {
                if (!current.Values.TryGetValue(id, out var known) || Math.Abs(known - value) > 1e-6) {
                    current.Set(id, value);
                }
            }

            return null;
        }

        // A new file for the same plugin keeps what the instance holds beyond its parameters.
        var state = spec.State;
        if (state is null && current is not null && current.PluginId == spec.PluginId) {
            state = current.Instrument.SaveState() is { } bytes ? Convert.ToBase64String(bytes) : null;
        }

        track.Instrument = Build(spec, state);
        return current;
    }

    InstrumentSlot Build(EngineInstrument spec, string? state) {
        var instrument = Plugins.Create(spec.PluginId)
            ?? throw new InvalidOperationException($"No plugin '{spec.PluginId}'.");
        try {
            instrument.Prepare(Options.SampleRate, AudioEngine.MaxFrames);
            if (state is not null && !instrument.LoadState(Convert.FromBase64String(state))) {
                Log?.Invoke($"{spec.PluginId}: the saved state did not load; it starts from its defaults");
            }

            if (spec.File is not null && !instrument.LoadFile(spec.File)) {
                Log?.Invoke($"{spec.PluginId}: cannot load {spec.File}");
            }

            var slot = new InstrumentSlot(instrument, spec.PluginId, spec.File, spec.State);
            foreach (var (id, value) in spec.Parameters) {
                slot.Initialise(id, value);
            }

            (instrument as IHostedInstrument)?.Activate();
            return slot;
        } catch {
            instrument.Dispose();
            throw;
        }
    }

    static bool IsInstrumentFailure(Exception e) =>
        e is IOException
            or InvalidOperationException
            or FormatException
            or BadImageFormatException
            or DllNotFoundException
            or EntryPointNotFoundException
            or ArgumentException;

    /// <summary>Waits until no callback that may have read what was just replaced is still running.</summary>
    void WaitGrace() {
        var grace = Engine.Grace();
        var spin = new SpinWait();
        var deadline = DateTime.UtcNow + CommandTimeout;
        while (!Engine.IsGraceOver(grace) && DateTime.UtcNow < deadline) {
            spin.SpinOnce(-1);
        }
    }

    // ---- Transport ---------------------------------------------------------------------------------------------------

    public void SetCycle(bool enabled, long start, long end) =>
        Send(new(EngineCommandKind.SetCycle, Math.Max(0, start), Math.Max(0, end), enabled));

    /// <summary>The click's level and whether it sounds; <paramref name="countInEnabled" /> adds a bar before recording.</summary>
    public void SetMetronome(bool enabled, float gain, bool countInEnabled) {
        lock (gate) {
            Engine.SetMetronome(enabled, gain);
            countIn = countInEnabled;
        }
    }

    /// <summary>Moves the playhead; while rolling, playback resumes there once the disk has caught up (a short gap).</summary>
    public void Locate(long position) {
        Send(new(EngineCommandKind.Locate, Math.Max(0, position)));
        Streamer.Wake();
    }

    /// <summary>
    ///     Plays from <paramref name="position" /> (negative: from where the playhead is). From a stop it first waits
    ///     (up to 2 s) until every track's stream holds audio there, so the start is not cut.
    /// </summary>
    public async Task PlayAsync(long position, CancellationToken cancellationToken = default) {
        var snapshot = Engine.Snapshot;
        if (snapshot.State != TransportState.Stopped) {
            if (position >= 0) {
                Locate(position);
            }

            return;
        }

        await PrepareAsync(position, cancellationToken).ConfigureAwait(false);
        Send(new(EngineCommandKind.Play));
    }

    /// <summary>
    ///     Records the armed tracks from <paramref name="position" /> (negative: the playhead), after a one-bar count-in
    ///     when it is on and the transport is stopped; while playing, punches in at once.
    /// </summary>
    public async Task RecordAsync(long position, CancellationToken cancellationToken = default) {
        if (string.IsNullOrEmpty(RecordDirectory)) {
            throw new InvalidOperationException("No model loaded, so there is no directory to record into.");
        }

        var snapshot = Engine.Snapshot;
        if (snapshot.State == TransportState.Recording) {
            return;
        }

        var stopped = snapshot.State == TransportState.Stopped;
        if (stopped) {
            await PrepareAsync(position, cancellationToken).ConfigureAwait(false);
        }

        lock (gate) {
            var root = Engine.Root;
            var from = stopped ? Engine.Snapshot.Position : snapshot.Position;
            BeginTakes(root);
            Midi.BeginCapture();
            recordingPass = true;
            var (frames, beats) = stopped && countIn
                ? Metronome.CountInFor(root.Metronome, from, Options.SampleRate)
                : (0L, 0);
            Send(new(EngineCommandKind.Record, frames, beats));
        }
    }

    /// <summary>
    ///     Stops; returns the takes of the recording pass that ended (empty when no track was armed), or null when no
    ///     recording pass ran.
    /// </summary>
    public RecordingResult? Stop() {
        lock (gate) {
            var sent = Send(new(EngineCommandKind.Stop));
            WaitApplied(sent);
            var pass = recordingPass;
            recordingPass = false;
            if (!pass) {
                return null;
            }

            var midiTakes = MidiTakes();
            if (takes.Length == 0) {
                return new([], midiTakes);
            }

            Writer.Retire();
            var finished = new List<RecordedTake>(takes.Length);
            foreach (var take in takes) {
                foreach (var track in Engine.Root.Tracks) {
                    if (track.Take == take) {
                        track.Take = null;
                    }
                }

                var result = take.Complete(scratch);
                if (take.Dropped > 0) {
                    Log?.Invoke($"take {take.Path}: {take.Dropped} frames dropped (the writer fell behind)");
                }

                take.Dispose();
                if (result.Frames > 0) {
                    finished.Add(result);
                } else {
                    TryDelete(result.Path); // nothing was kept (a punch range the pass never reached)
                }
            }

            takes = [];
            var failure = Writer.TakeFailure();
            return failure is not null
                ? throw new IOException($"Recording failed: {failure.Message}", failure)
                : new(finished, midiTakes);
        }
    }

    /// <summary>
    ///     The live MIDI each armed MIDI track received during the pass, in project samples over what the pass covered
    ///     (through the cycle, the passes merged; inside the punch range with autopunch); notes still held at the stop
    ///     end there. Tracks that received nothing have no take.
    /// </summary>
    List<RecordedMidiTake> MidiTakes() {
        var captured = Midi.EndCapture();
        var (low, high) = Engine.RecordRange;
        if (Engine.RecordStart < 0 || low > high) {
            return [];
        }

        var punch = Engine.Punch;
        var start = punch.Active ? Math.Max(low, punch.In) : low;
        var end = Math.Max(start, punch.Active ? Math.Min(high, punch.Out) : high);
        return BuildMidiTakes(captured, start, end, Math.Clamp(Engine.RecordEnd, start, end));
    }

    /// <summary>
    ///     MIDI takes over [<paramref name="start" />, <paramref name="end" />] from each track's events in the order
    ///     they were played. A note whose end was played after a cycle wrap (so lies before its start) ends at
    ///     <paramref name="end" />; notes still held end at <paramref name="close" />.
    /// </summary>
    static List<RecordedMidiTake> BuildMidiTakes(
        Dictionary<int, List<RecordedMidiEvent>> captured,
        long start,
        long end,
        long close
    ) {
        var result = new List<RecordedMidiTake>();
        foreach (var (trackId, events) in captured.OrderBy(static c => c.Key)) {
            var list = MidiTake(events, start, end, close);
            if (list.Count > 0) {
                result.Add(new(trackId, start, end, list));
            }
        }

        return result;
    }

    /// <summary>One track's events for <see cref="BuildMidiTakes" />, sorted by sample.</summary>
    static List<RecordedMidiEvent> MidiTake(List<RecordedMidiEvent> events, long start, long end, long close) {
        var held = new NoteSet();
        var onsets = new Dictionary<int, long>();
        var list = new List<RecordedMidiEvent>(events.Count);
        foreach (var played in events) {
            var e = played with { Sample = Math.Clamp(played.Sample, start, end) };
            var key = ((e.Status & 0x0F) << 7) | (e.Data1 & 0x7F);
            var kind = e.Status & 0xF0;
            if (kind == 0x90 && e.Data2 > 0) {
                onsets[key] = e.Sample;
            } else if (kind is 0x80 or 0x90 && onsets.Remove(key, out var onset) && e.Sample < onset) {
                e = e with { Sample = end }; // ended after a wrap
            }

            held.Track((byte)e.Status, (byte)e.Data1, (byte)e.Data2);
            list.Add(e);
        }

        Span<(byte Status, byte Note)> open = stackalloc (byte, byte)[128];
        int count;
        while ((count = held.Release(open)) > 0) {
            for (var i = 0; i < count; i++) {
                var key = ((open[i].Status & 0x0F) << 7) | open[i].Note;
                var at = onsets.TryGetValue(key, out var onset) && close < onset ? end : close;
                list.Add(new(at, open[i].Status, open[i].Note, 0));
            }
        }

        return [..list.OrderBy(static e => e.Sample)];
    }

    /// <summary>Waits until the audio thread has applied the command numbered <paramref name="sequence" />.</summary>
    public bool WaitApplied(long sequence) {
        var deadline = DateTime.UtcNow + CommandTimeout;
        while (Engine.Snapshot.CommandsApplied < sequence) {
            if (DateTime.UtcNow > deadline) {
                return false;
            }

            Thread.Sleep(1);
        }

        return true;
    }

    /// <summary>True when every track's stream holds <paramref name="frames" /> of audio for the current play order.</summary>
    public bool IsPrimed(int frames) {
        var root = Engine.Root;
        var snapshot = Engine.Snapshot;
        for (var i = 0; i < root.Tracks.Length; i++) {
            var stream = root.Tracks[i].Stream;
            if (stream.FillIdentity != TrackFeed.Identity(snapshot.Anchor.Id, root.Generations[i])
                || stream.FillCursor - snapshot.Clock < Math.Min(frames, stream.TargetFrames)) {
                return false;
            }
        }

        return true;
    }

    async Task PrepareAsync(long position, CancellationToken cancellationToken) {
        long sent;
        if (position >= 0 && position != Engine.Snapshot.Position) {
            sent = Send(new(EngineCommandKind.Locate, position));
        } else {
            sent = Engine.Commands.Enqueued;
        }

        Streamer.Wake();
        var deadline = DateTime.UtcNow + PrimeTimeout;
        var prime = Options.SampleRate / 4;
        while (DateTime.UtcNow < deadline
               && (Engine.Snapshot.CommandsApplied < sent || !IsPrimed(prime))) {
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Enqueues a command for the audio thread; returns its sequence number (see <see cref="WaitApplied" />).</summary>
    long Send(in EngineCommand command) {
        lock (sendGate) { // the queue has one producer: whichever control thread holds this
            var spin = new SpinWait();
            var deadline = DateTime.UtcNow + CommandTimeout;
            while (!Engine.Commands.TryEnqueue(command)) {
                if (DateTime.UtcNow > deadline) {
                    throw new InvalidOperationException("The audio thread is not taking commands.");
                }

                spin.SpinOnce();
            }

            return Engine.Commands.Enqueued;
        }
    }

    void BeginTakes(ModelRoot root) {
        Directory.CreateDirectory(RecordDirectory);
        var started = new List<RecordTake>();
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        foreach (var track in root.Tracks) {
            if (!track.Armed || track.IsMidi) {
                continue; // MIDI tracks record MIDI (MidiHub.BeginCapture)
            }

            var path = Path.Combine(RecordDirectory, $"{track.Id}-{stamp}.wav");
            for (var n = 1; File.Exists(path); n++) {
                path = Path.Combine(RecordDirectory, $"{track.Id}-{stamp}-{n}.wav");
            }

            var take = new RecordTake(track.Id, path, track.Stereo ? 2 : 1, Options.SampleRate);
            started.Add(take);
            track.Take = take;
        }

        takes = [..started];
        Writer.Activate(takes);
    }

    // ---- Publication (RCU) -------------------------------------------------------------------------------------------

    /// <summary>Publishes <paramref name="next" />, waits until nothing can use the model it replaced, and releases it.</summary>
    void Swap(ModelRoot next) {
        var added = new HashSet<MappedSegment>();
        AddOwners(next, added);
        foreach (var owner in added) {
            owner.RootRefs++;
        }

        var (previous, grace) = Engine.Publish(next);
        var diskGrace = Streamer.Grace;
        Streamer.Wake();
        var spin = new SpinWait();
        var deadline = DateTime.UtcNow + CommandTimeout;
        while ((!Engine.IsGraceOver(grace) || !Streamer.IsGraceOver(diskGrace)) && DateTime.UtcNow < deadline) {
            spin.SpinOnce(-1);
        }

        Retire(previous, next);
    }

    void Retire(ModelRoot previous, ModelRoot next) {
        var released = new HashSet<MappedSegment>();
        AddOwners(previous, released);
        foreach (var owner in released) {
            if (--owner.RootRefs == 0 && !owner.Pooled) {
                owner.Dispose();
            }
        }

        var memories = new List<FlashbackBuffer>();
        foreach (var track in previous.Tracks) {
            if (Array.IndexOf(next.Tracks, track) < 0) {
                RetireTrack(track, next, memories);
            }
        }

        ReleaseFlashback(memories);

        foreach (var file in previous.Files) {
            if (file is not null && Array.IndexOf(next.Files, file) < 0) {
                file.Dispose();
            }
        }

        TrimPool();
    }

    /// <summary>Releases a track the model no longer has; its Flashback buffer goes to <paramref name="memories" />.</summary>
    static void RetireTrack(TrackState track, ModelRoot next, List<FlashbackBuffer> memories) {
        if (track.Instrument is { } slot && !next.Tracks.Any(t => t.Instrument == slot)) {
            slot.Dispose(); // the track went away
        }

        if (track.Flashback is { } memory) {
            track.Flashback = null;
            memories.Add(memory);
        }

        track.Dispose();
    }

    static void AddOwners(ModelRoot root, HashSet<MappedSegment> set) {
        foreach (var owner in root.Owners) {
            if (owner is not null) {
                set.Add(owner);
            }
        }

        if (root.MetronomeOwner is not null) {
            set.Add(root.MetronomeOwner);
        }
    }

    MappedSegment MapSegment(SegmentRef reference, bool replace) {
        var segment = replace ? Pooled(reference) : null;
        var fresh = segment is null;
        segment ??= MappedSegment.Map(++segmentIds, reference.Name, reference.Length);
        try {
            Check(segment, reference.Version);
        } catch {
            if (fresh) {
                segment.Dispose();
            }

            throw;
        }

        if (replace) {
            segment.Pooled = true;
            pool[reference.Name] = segment;
            poolOrder.Remove(reference.Name);
            poolOrder.Add(reference.Name);
        }

        return segment;
    }

    /// <summary>
    ///     The whole-model segment of this name the host still has mapped, when it is the server's pooled slot holding
    ///     this version; otherwise forgets it (resized, or a new object under the old name) and returns null.
    /// </summary>
    unsafe MappedSegment? Pooled(SegmentRef reference) {
        if (!pool.TryGetValue(reference.Name, out var pooled)) {
            return null;
        }

        if (pooled.Length == reference.Length && pooled.Header->ModelVersion == reference.Version) {
            return pooled;
        }

        pool.Remove(reference.Name);
        poolOrder.Remove(reference.Name);
        pooled.Pooled = false;
        if (pooled.RootRefs == 0) {
            pooled.Dispose();
        }

        return null;
    }

    /// <summary>
    ///     Validates the layout, prefaults every page (ADR-0006), verifies the checksum and sort order of segments up to
    ///     <see cref="EngineOptions.VerifyBytes" />, and checks the model version.
    /// </summary>
    unsafe void Check(MappedSegment segment, long version) {
        segment.ValidateLayout();
        segment.Prefault();
        if (segment.Length <= Options.VerifyBytes) {
            segment.Verify();
        }

        if (segment.Header->ModelVersion != version) {
            throw new InvalidDataException(
                $"Segment holds model version {segment.Header->ModelVersion}, expected {version}."
            );
        }
    }

    /// <summary>Unmaps pooled segments beyond the newest few that no model uses.</summary>
    void TrimPool() {
        for (var i = 0; poolOrder.Count > PooledSegments && i < poolOrder.Count - PooledSegments;) {
            var name = poolOrder[i];
            var segment = pool[name];
            if (segment.RootRefs > 0) {
                i++;
                continue;
            }

            pool.Remove(name);
            poolOrder.RemoveAt(i);
            segment.Dispose();
        }
    }

    void OpenFiles(IReadOnlyList<EngineFile> list) {
        foreach (var f in list) {
            if (files.TryGetValue(f.Id, out var known) && known is not null && known.Path == f.Path) {
                continue;
            }

            if (f.Id is < 0 or > 1 << 20) {
                Log?.Invoke($"file {f.Id}: id out of range, ignored");
                continue;
            }

            files[f.Id] = Open(f);
        }
    }

    /// <summary>Opens a file for streaming; null (its regions play silence) when it cannot be read.</summary>
    StreamFile? Open(EngineFile f) {
        try {
            var file = new StreamFile(f.Id, f.Path);
            if (file.Channels > PlaylistRenderer.MaxFileChannels) {
                file.Dispose();
                throw new InvalidDataException($"{file.Channels} channels");
            }

            if (file.Format.SampleRate != Options.SampleRate) {
                Log?.Invoke($"file {f.Path}: {file.Format.SampleRate} Hz on a {Options.SampleRate} Hz engine");
            }

            return file;
        } catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) {
            Log?.Invoke($"file {f.Path}: {e.Message}; its regions play silence");
            return null;
        }
    }

    StreamFile?[] FileTable() {
        var size = 0;
        foreach (var id in files.Keys) {
            size = Math.Max(size, id + 1);
        }

        var table = new StreamFile?[size];
        foreach (var (id, file) in files) {
            table[id] = file;
        }

        return table;
    }

    TrackState NewTrack(int id, bool stereo, TrackState? previous) {
        var state = new TrackState(id, stereo, streamFrames, previous?.Midi);
        if (previous is not null) {
            state.Strip.Set(previous.Strip.Settings);
            state.SetIo(previous.InputChannel, previous.Armed, previous.Monitoring);
            state.Instrument = previous.Instrument;
            state.MidiIo = previous.MidiIo;
            if (previous.Crossfade is not null) {
                state.EnsureCrossfade();
            }
        }

        return state;
    }

    static TrackState? Find(ModelRoot root, int id) {
        var index = root.IndexOf(id);
        return index < 0 ? null : root.Tracks[index];
    }

    public void Dispose() {
        lock (gate) {
            if (takes.Length > 0) {
                Writer.Retire();
                foreach (var take in takes) {
                    take.Complete(scratch);
                    take.Dispose();
                }

                takes = [];
            }
        }

        Midi.Dispose();
        Writer.Dispose();
        Streamer.Dispose();
        Engine.SetPreview(null)?.Dispose(); // no thread reads it any more
        var root = Engine.Root;
        Engine.Publish(ModelRoot.Empty);
        WaitGrace();
        foreach (var track in root.Tracks) {
            track.Instrument?.Dispose();
            track.Instrument = null;
            track.Dispose();
        }

        var segments = new HashSet<MappedSegment>(pool.Values);
        AddOwners(root, segments);
        foreach (var segment in segments) {
            segment.Dispose();
        }

        foreach (var file in files.Values) {
            file?.Dispose();
        }
    }
}
