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
/// <param name="StreamSeconds">Audio each track's stream keeps queued ahead of the playhead (spike S2: 2 s).</param>
/// <param name="VerifyBytes">
///     Segments up to this size get the checksum and sort-order check on load (ADR-0006: on for per-track segments,
///     off for whole models over a few MB). Layout validation and the prefault always run.
/// </param>
public sealed record EngineOptions(
    int SampleRate,
    int BufferFrames,
    int RoundTripLatency = 0,
    double StreamSeconds = 2,
    long VerifyBytes = 4 << 20
);

/// <summary>
///     The engine's control plane, called from the host's control thread (the IPC connection): loads compiled models
///     from shared-memory segments and publishes them to the audio and disk threads (RCU; docs/adr/0006-bulk-ipc.md),
///     sets strips and track IO, drives the transport through the audio thread's command queue and runs recording
///     passes. Every method is serialised by one lock; the audio thread never waits for it.
/// </summary>
public sealed class EngineController : IDisposable {
    /// <summary>Whole-model segments kept mapped for the server to reuse (its pool of two slots, ADR-0006).</summary>
    const int PooledSegments = 2;

    static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(2);
    static readonly TimeSpan PrimeTimeout = TimeSpan.FromSeconds(2);

    readonly Lock gate = new();
    readonly Dictionary<string, MappedSegment> pool = new(StringComparer.Ordinal);
    readonly List<string> poolOrder = [];
    readonly Dictionary<int, StreamFile?> files = [];
    readonly float[] scratch = new float[64 * 1024];
    readonly int streamFrames;
    long segmentIds;
    int generations;
    bool countIn;
    RecordTake[] takes = [];

    public EngineController(EngineOptions options) {
        Options = options;
        Engine = new(options.SampleRate);
        streamFrames = (int)(options.StreamSeconds * options.SampleRate);
        Streamer = new(Engine, Math.Max(4 * options.BufferFrames, options.SampleRate * 15 / 100));
        Writer = new();
    }

    public EngineOptions Options { get; }

    /// <summary>The audio callback, to hand to a device.</summary>
    public AudioEngine Engine { get; }

    public DiskStreamer Streamer { get; }

    public RecordWriter Writer { get; }

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

    /// <summary>Starts the disk thread (offline renders call <see cref="DiskStreamer.Pass" /> instead).</summary>
    public void Start() => Streamer.Start();

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
            var tracks = new List<TrackState>();
            var views = new List<TrackView>();
            var gens = new List<int>();
            var owners = new List<MappedSegment?>();
            var metronome = default(TrackView);
            MappedSegment? metronomeOwner = null;
            if (!message.Replace) {
                tracks.AddRange(current.Tracks);
                views.AddRange(current.Views);
                gens.AddRange(current.Generations);
                owners.AddRange(current.Owners);
                metronome = current.Metronome;
                metronomeOwner = current.MetronomeOwner;
            }

            unsafe {
                for (var k = 0; k < segment.Header->TrackCount; k++) {
                    var id = segment.TrackId(k);
                    if (id == TrackEntry.MetronomeTrack) {
                        metronome = segment.Track(k);
                        metronomeOwner = segment;
                        continue;
                    }

                    var index = tracks.FindIndex(t => t.Id == id);
                    var existing = index >= 0 ? tracks[index] : Find(current, id);
                    var stereo = settings.TryGetValue(id, out var s) ? s.Stereo : existing?.Stereo ?? false;
                    var state = existing is not null && existing.Stereo == stereo ? existing : NewTrack(id, stereo, existing);
                    if (index < 0) {
                        tracks.Add(state);
                        views.Add(segment.Track(k));
                        gens.Add(++generations);
                        owners.Add(segment);
                    } else {
                        tracks[index] = state;
                        views[index] = segment.Track(k);
                        gens[index] = ++generations;
                        owners[index] = segment;
                    }
                }
            }

            for (var i = 0; i < tracks.Count; i++) {
                if (!settings.TryGetValue(tracks[i].Id, out var s)) {
                    continue;
                }

                if (s.Stereo != tracks[i].Stereo) {
                    tracks[i] = NewTrack(s.Id, s.Stereo, tracks[i]);
                    gens[i] = ++generations;
                }

                tracks[i].Apply(s);
            }

            Engine.Master.Set(message.Master);
            RecordDirectory = message.RecordDirectory;
            long length;
            unsafe {
                length = message.Replace
                    ? segment.Header->LengthSamples
                    : Math.Max(current.LengthSamples, segment.Header->LengthSamples);
            }

            Swap(
                new(
                    message.Segment.Version,
                    length,
                    [.. tracks],
                    [.. views],
                    [.. gens],
                    [.. owners],
                    metronome,
                    metronomeOwner,
                    FileTable()
                )
            );
            return message.Segment.Version;
        }
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
                        current.Files
                    )
                );
            }

            track.SetIo(inputChannel, armed, monitoring);
            return true;
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
            var (frames, beats) = stopped && countIn
                ? Metronome.CountInFor(root.Metronome, from, Options.SampleRate)
                : (0L, 0);
            Send(new(EngineCommandKind.Record, frames, beats));
        }
    }

    /// <summary>Stops; returns the takes of the recording pass that ended (empty when none ran).</summary>
    public IReadOnlyList<RecordedTake> Stop() {
        lock (gate) {
            var sent = Send(new(EngineCommandKind.Stop));
            WaitApplied(sent);
            if (takes.Length == 0) {
                return [];
            }

            Writer.Retire();
            var finished = new List<RecordedTake>(takes.Length);
            foreach (var take in takes) {
                foreach (var track in Engine.Root.Tracks) {
                    if (track.Take == take) {
                        track.Take = null;
                    }
                }

                finished.Add(take.Complete(scratch));
                if (take.Dropped > 0) {
                    Log?.Invoke($"take {take.Path}: {take.Dropped} frames dropped (the writer fell behind)");
                }

                take.Dispose();
            }

            takes = [];
            var failure = Writer.TakeFailure();
            return failure is not null ? throw new IOException($"Recording failed: {failure.Message}", failure) : finished;
        }
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

    long Send(in EngineCommand command) {
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

    void BeginTakes(ModelRoot root) {
        Directory.CreateDirectory(RecordDirectory);
        var started = new List<RecordTake>();
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        foreach (var track in root.Tracks) {
            if (!track.Armed) {
                continue;
            }

            var path = Path.Combine(RecordDirectory, $"{track.Id}-{stamp}.wav");
            for (var n = 1; File.Exists(path); n++) {
                path = Path.Combine(RecordDirectory, $"{track.Id}-{stamp}-{n}.wav");
            }

            var take = new RecordTake(
                track.Id,
                path,
                track.Stereo ? 2 : 1,
                Options.SampleRate,
                Options.RoundTripLatency
            );
            started.Add(take);
            track.Take = take;
        }

        takes = [.. started];
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

        foreach (var track in previous.Tracks) {
            if (Array.IndexOf(next.Tracks, track) < 0) {
                track.Dispose();
            }
        }

        foreach (var file in previous.Files) {
            if (file is not null && Array.IndexOf(next.Files, file) < 0) {
                file.Dispose();
            }
        }

        TrimPool();
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
        MappedSegment? segment = null;
        if (replace && pool.TryGetValue(reference.Name, out var pooled)) {
            unsafe {
                if (pooled.Length == reference.Length && pooled.Header->ModelVersion == reference.Version) {
                    segment = pooled;
                }
            }

            if (segment is null) {
                // Not the server's slot any more (resized, or a new object under the old name): map it afresh.
                pool.Remove(reference.Name);
                poolOrder.Remove(reference.Name);
                pooled.Pooled = false;
                if (pooled.RootRefs == 0) {
                    pooled.Dispose();
                }
            }
        }

        var fresh = segment is null;
        segment ??= MappedSegment.Map(++segmentIds, reference.Name, reference.Length);
        try {
            segment.ValidateLayout();
            segment.Prefault();
            if (segment.Length <= Options.VerifyBytes) {
                segment.Verify();
            }

            unsafe {
                if (segment.Header->ModelVersion != reference.Version) {
                    throw new InvalidDataException(
                        $"Segment holds model version {segment.Header->ModelVersion}, expected {reference.Version}."
                    );
                }
            }
        } catch {
            if (fresh) {
                segment.Dispose();
            }

            throw;
        }

        if (replace && fresh) {
            segment.Pooled = true;
            pool[reference.Name] = segment;
            poolOrder.Add(reference.Name);
        } else if (replace) {
            poolOrder.Remove(reference.Name);
            poolOrder.Add(reference.Name);
        }

        return segment;
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

            try {
                var file = new StreamFile(f.Id, f.Path);
                if (file.Channels > PlaylistRenderer.MaxFileChannels) {
                    file.Dispose();
                    throw new InvalidDataException($"{file.Channels} channels");
                }

                if (file.Format.SampleRate != Options.SampleRate) {
                    Log?.Invoke($"file {f.Path}: {file.Format.SampleRate} Hz on a {Options.SampleRate} Hz engine");
                }

                files[f.Id] = file;
            } catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) {
                files[f.Id] = null;
                Log?.Invoke($"file {f.Path}: {e.Message}; its regions play silence");
            }
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
        var state = new TrackState(id, stereo, streamFrames);
        if (previous is not null) {
            state.Strip.Set(previous.Strip.Settings);
            state.SetIo(previous.InputChannel, previous.Armed, previous.Monitoring);
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

        Writer.Dispose();
        Streamer.Dispose();
        var root = Engine.Root;
        Engine.Publish(ModelRoot.Empty);
        foreach (var track in root.Tracks) {
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
