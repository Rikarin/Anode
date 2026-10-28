namespace Anode.Engine.Streaming;

/// <summary>
///     The disk thread (spike S2's design, docs/spikes/S2-disk-streaming.md): one thread for every track, each pass
///     refilling the stream with the least audio queued ahead of the playhead (a track with crossfades has two, one
///     per playlist lane, and the preview channel one), once it is below two thirds of its 2 s
///     target, with one page-aligned read of about 96 KiB (32,768 mono frames) through <c>F_NOCACHE</c>. It renders in
///     play order, following the cycle, so a cycle jump needs nothing of the audio thread; and it starts a new fill
///     whenever the play order (the anchor) or a track's compiled playlist changes.
/// </summary>
public sealed class DiskStreamer : IDisposable {
    /// <summary>Frames of one read for a mono track (96 KiB of 24-bit audio); a stereo track reads half as many.</summary>
    public const int ReadFrames = 32_768;

    readonly AudioEngine engine;
    readonly PlaylistRenderer renderer = new(ReadFrames);
    readonly float[] chunk = GC.AllocateArray<float>(ReadFrames, true);
    readonly AutoResetEvent wake = new(false);
    Thread? thread;
    volatile bool running;
    long started;
    long completed;

    /// <param name="engine">The engine whose model and transport the streamer follows.</param>
    /// <param name="leadFrames">
    ///     How far ahead of the playhead a new fill starts while rolling, so it is on disk before the playhead gets
    ///     there and the old audio plays seamlessly until then.
    /// </param>
    public DiskStreamer(AudioEngine engine, int leadFrames) {
        this.engine = engine;
        LeadFrames = leadFrames;
    }

    public int LeadFrames { get; }

    /// <summary>Whether the disk thread runs (otherwise whoever drives the engine calls <see cref="Pass" />).</summary>
    public bool IsRunning => running;

    /// <summary>Bytes read from audio files so far.</summary>
    public long BytesRead => renderer.BytesRead;

    public long Reads => renderer.Reads;

    /// <summary>Frames one read covers for a track of <paramref name="channels" /> channels.</summary>
    public static int ChunkFrames(int channels) => ReadFrames / channels;

    /// <summary>Runs the disk thread (tests and offline renders call <see cref="Pass" /> themselves instead).</summary>
    public void Start() {
        running = true;
        thread = new(Run) { IsBackground = true, Name = "anode disk", Priority = ThreadPriority.Highest };
        thread.Start();
    }

    /// <summary>Control thread: asks for a pass now (after a locate or a model swap) instead of at the next poll.</summary>
    public void Wake() => wake.Set();

    /// <summary>A grace token: the passes in progress now (see <see cref="IsGraceOver" />).</summary>
    public long Grace => Interlocked.Read(ref started);

    /// <summary>True once every pass that had started when <paramref name="grace" /> was taken has finished.</summary>
    public bool IsGraceOver(long grace) => Interlocked.Read(ref completed) >= grace;

    /// <summary>
    ///     One pass: refills the most urgent stream (a track's, its crossfade stream's, or the preview's) by one read.
    ///     Returns false when no stream needs anything.
    /// </summary>
    public bool Pass() {
        Interlocked.Increment(ref started);
        try {
            var model = engine.Root;
            var snapshot = engine.Snapshot;
            var anchor = snapshot.Anchor;
            var rolling = snapshot.State != TransportState.Stopped && !snapshot.CountIn;
            var best = MostUrgent(model, snapshot, rolling);
            if (engine.Preview is { } preview && preview.Ahead < best.Ahead) {
                return preview.Fill() || best.Track >= 0;
            }

            if (best.Track < 0) {
                return false;
            }

            var track = model.Tracks[best.Track];
            Fill(best.Lane == 0 ? track.Stream : track.Crossfade!, model, best.Track, best.Lane, anchor, best.Frames);
            return true;
        } finally {
            Interlocked.Increment(ref completed);
        }
    }

    /// <summary>
    ///     Brings every track's streams in line with the play order and its compile, and picks the stream with the least
    ///     audio ahead that needs a read (track −1: none).
    /// </summary>
    (int Track, int Lane, long Ahead, int Frames) MostUrgent(
        ModelRoot model,
        in EngineSnapshot snapshot,
        bool rolling
    ) {
        var tracks = model.Tracks;
        (int Track, int Lane, long Ahead, int Frames) best = (-1, 0, long.MaxValue, 0);
        for (var i = 0; i < tracks.Length; i++) {
            var main = tracks[i].Stream;
            var second = model.Lanes[i] is null ? null : tracks[i].Crossfade;
            var identity = TrackFeed.Identity(snapshot.Anchor.Id, model.Generations[i]);
            Sync(main, second, identity, snapshot, rolling);
            for (var lane = 0; lane < (second is null ? 1 : 2); lane++) {
                var (ahead, frames) = Plan(lane == 0 ? main : second!, identity, snapshot, rolling);
                if (frames > 0 && ahead < best.Ahead) {
                    best = (i, lane, ahead, frames);
                }
            }
        }

        return best;
    }

    /// <summary>
    ///     Starts a new fill when the play order or the track's compile changed: the track's streams start together, at
    ///     the same clock, so a crossfade's two halves splice in on the same frame.
    /// </summary>
    void Sync(TrackFeed main, TrackFeed? second, long identity, in EngineSnapshot snapshot, bool rolling) {
        var anchor = snapshot.Anchor;
        if (main.FillIdentity != identity) {
            // A locate restarts at once (the old audio is dropped); anything else starts a little ahead, so the old
            // audio plays on until the new fill begins.
            var hard = anchor.Hard && TrackFeed.AnchorOf(main.FillIdentity) != anchor.Id;
            var lead = rolling && !hard ? LeadFrames : 0;
            var start = Math.Max(anchor.Clock, snapshot.Clock + lead);
            main.BeginFill(identity, start);
            second?.BeginFill(identity, start);
        } else if (second is not null && second.FillIdentity != identity) {
            second.BeginFill(identity, Math.Max(main.FillStart, snapshot.Clock + (rolling ? LeadFrames : 0)));
        }
    }

    /// <summary>
    ///     How much audio the stream holds ahead of the playhead, and how many frames it should read now (0: none, it
    ///     is above two thirds of its target or has no room for a useful read).
    /// </summary>
    (long Ahead, int Frames) Plan(TrackFeed stream, long identity, in EngineSnapshot snapshot, bool rolling) {
        var ahead = stream.FillCursor - snapshot.Clock;
        if (ahead < 0) {
            // The playhead overtook the fill (the disk stalled): skip ahead rather than read audio that is already
            // late; the gap plays as silence.
            stream.BeginFill(identity, snapshot.Clock + (rolling ? LeadFrames : 0));
            ahead = stream.FillCursor - snapshot.Clock;
        }

        if (ahead >= stream.TargetFrames * 2L / 3) {
            return (ahead, 0);
        }

        var chunk = ChunkFrames(stream.Channels);
        var frames = (int)Math.Min(Math.Min(chunk, stream.FreeFrames), stream.TargetFrames - ahead);
        return (ahead, frames < Math.Min(1_024, chunk) ? 0 : frames);
    }

    /// <summary>
    ///     Renders the next <paramref name="frames" /> of the stream's fill (the track's entries in
    ///     <paramref name="lane" />), in play order, and queues them.
    /// </summary>
    void Fill(TrackFeed stream, ModelRoot model, int index, int lane, Anchor anchor, int frames) {
        var channels = stream.Channels;
        var buffer = chunk.AsSpan(0, frames * channels);
        var clock = stream.FillCursor;
        var view = model.Views[index];
        var lanes = model.Lanes[index];
        var done = 0;
        while (done < frames) {
            var position = anchor.PositionAt(clock + done);
            var count = (int)Math.Min(frames - done, anchor.Cycle.UntilJump(position));
            renderer.Render(
                view,
                lanes,
                lane,
                model.FilesFor(index),
                position,
                count,
                channels,
                buffer[(done * channels)..]
            );
            done += count;
        }

        stream.Append(buffer);
    }

    void Run() {
        while (running) {
            if (!Pass()) {
                wake.WaitOne(1);
            }
        }
    }

    public void Dispose() {
        running = false;
        thread?.Join();
        renderer.Dispose();
        wake.Dispose();
    }
}
