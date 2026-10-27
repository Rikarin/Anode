using System.Diagnostics;

namespace Anode.Spikes.BulkIpc;

/// <summary>
///     A simulated device callback: a thread paced to absolute deadlines every <c>frames</c> at <c>sampleRate</c> (a
///     Mach real-time thread on macOS, see <see cref="RealtimePacing" />; spinning elsewhere), which renders the active
///     model's events and playlist entries in
///     the buffer window of every track (binary search to the window start, then a scan).
/// </summary>
/// <remarks>
///     RCU: the control thread publishes a new <see cref="ModelRoot" /> with an atomic exchange, then waits until every
///     callback that could have loaded the old root has completed (<see cref="WaitForGrace" />) before unmapping what
///     only the old root used. The audio thread never waits, never frees and never allocates.
/// </remarks>
sealed unsafe class AudioThread {
    readonly int frames;
    readonly long periodTicks;
    readonly Thread thread;

    ModelRoot? active;
    long started;
    long completed;
    volatile bool running = true;
    volatile bool resetRequested;

    // Written by the audio thread only; read by the control thread for stats.
    long callbacks;
    long lateCallbacks;
    long slowCallbacks;
    readonly long[] slowByPhase = new long[4];
    long maxLatenessTicks;
    long maxCallbackTicks;
    long allocatedBytes;
    double sink;
    long position;

    public AudioThread(int frames, int sampleRate) {
        this.frames = frames;
        periodTicks = Stopwatch.Frequency * frames / sampleRate;
        thread = new(Run) { IsBackground = true, Name = "audio (simulated)", Priority = ThreadPriority.Highest };
    }

    public long Callbacks => Volatile.Read(ref callbacks);
    public long LateCallbacks => Volatile.Read(ref lateCallbacks);

    /// <summary>Callbacks that took more than a tenth of the period.</summary>
    public long SlowCallbacks => Volatile.Read(ref slowCallbacks);

    /// <summary>
    ///     What the host's control thread is doing, for attributing slow callbacks: 0 idle (the server is writing), 1
    ///     mapping and validating, 2 published and waiting for the first use, 3 unmapping the retired segment.
    /// </summary>
    public int Phase { get; set; }

    public string SlowByPhase => $"{slowByPhase[0]}/{slowByPhase[1]}/{slowByPhase[2]}/{slowByPhase[3]}";

    public double MaxLatenessMicros => Volatile.Read(ref maxLatenessTicks) * 1e6 / Stopwatch.Frequency;
    public double MaxCallbackMicros => Volatile.Read(ref maxCallbackTicks) * 1e6 / Stopwatch.Frequency;
    public long AllocatedBytes => Volatile.Read(ref allocatedBytes);
    public double Sink => Volatile.Read(ref sink);

    public void Start() => thread.Start();

    public void Stop() {
        running = false;
        thread.Join();
    }

    /// <summary>Asks the audio thread to zero its counters and its allocation baseline, and waits until it has.</summary>
    public void ResetStats() {
        resetRequested = true;
        while (resetRequested) {
            Thread.Sleep(1);
        }
    }

    /// <summary>Control thread: installs <paramref name="next" /> and returns the root it replaced plus the grace token.</summary>
    public (ModelRoot? Previous, long Grace) Publish(ModelRoot? next) {
        var previous = Interlocked.Exchange(ref active, next); // full fence: the read of `started` cannot move above it
        return (previous, Volatile.Read(ref started));
    }

    /// <summary>
    ///     Waits until every callback that had started when <see cref="Publish" /> returned <paramref name="grace" /> has
    ///     completed; after that no callback can still hold the previous root.
    /// </summary>
    public void WaitForGrace(long grace) {
        var spin = new SpinWait();
        while (Volatile.Read(ref completed) < grace) {
            spin.SpinOnce(-1);
        }
    }

    /// <summary>True when the thread runs as a Mach time-constraint thread (macOS), false when it spins.</summary>
    public bool Realtime { get; private set; }

    void Run() {
        Realtime = RealtimePacing.TryPromote(periodTicks);
        var baseline = GC.GetAllocatedBytesForCurrentThread();
        var deadline = Stopwatch.GetTimestamp() + periodTicks;
        while (running) {
            if (Realtime) {
                RealtimePacing.WaitUntil(deadline);
            }

            var now = Stopwatch.GetTimestamp();
            while (now < deadline) {
                Thread.SpinWait(20);
                now = Stopwatch.GetTimestamp();
            }

            if (resetRequested) {
                callbacks = lateCallbacks = slowCallbacks = maxLatenessTicks = maxCallbackTicks = 0;
                Array.Clear(slowByPhase);
                baseline = GC.GetAllocatedBytesForCurrentThread();
                Volatile.Write(ref allocatedBytes, 0);
                resetRequested = false;
            }

            var lateness = now - deadline;
            if (lateness > maxLatenessTicks) {
                maxLatenessTicks = lateness;
            }

            if (lateness > periodTicks) {
                lateCallbacks++;
                deadline = now; // a real device would have dropped out; realign rather than burst
            }

            deadline += periodTicks;
            var phase = Phase;
            Interlocked.Increment(ref started); // full fence before the root is read
            var root = Volatile.Read(ref active);
            if (root is not null) {
                var first = root.FirstUseTicks == 0;
                if (first) {
                    Volatile.Write(ref root.FirstUseTicks, now);
                }

                sink += Render(root);
                if (first) {
                    Volatile.Write(ref root.FirstRenderTicks, Math.Max(1, Stopwatch.GetTimestamp() - now));
                }
            }

            Volatile.Write(ref completed, completed + 1);
            var took = Stopwatch.GetTimestamp() - now;
            if (took > maxCallbackTicks) {
                maxCallbackTicks = took;
            }

            if (took * 10 > periodTicks) {
                slowCallbacks++;
                slowByPhase[phase & 3]++;
            }

            callbacks++;
            Volatile.Write(ref allocatedBytes, GC.GetAllocatedBytesForCurrentThread() - baseline);
        }
    }

    double Render(ModelRoot root) {
        var start = position % root.LengthSamples;
        var end = start + frames;
        position += frames;
        double acc = 0;
        foreach (var view in root.Tracks) {
            var events = view.Events;
            for (var i = LowerBound(events, view.EventCount, start);
                 i < view.EventCount && events[i].SamplePos < end;
                 i++) {
                acc += events[i].Value + events[i].Data1;
            }

            // The playlist entry playing at the window start: the last one starting at or before it.
            var playlist = view.Playlist;
            long lo = 0, hi = view.PlaylistCount;
            while (lo < hi) {
                var mid = lo + ((hi - lo) >> 1);
                if (playlist[mid].Start <= start) {
                    lo = mid + 1;
                } else {
                    hi = mid;
                }
            }

            if (lo > 0 && start < playlist[lo - 1].Start + playlist[lo - 1].Length) {
                acc += playlist[lo - 1].Gain;
            }
        }

        return acc;
    }

    static long LowerBound(PlaybackEvent* events, long count, long position) {
        long lo = 0, hi = count;
        while (lo < hi) {
            var mid = lo + ((hi - lo) >> 1);
            if (events[mid].SamplePos < position) {
                lo = mid + 1;
            } else {
                hi = mid;
            }
        }

        return lo;
    }
}
