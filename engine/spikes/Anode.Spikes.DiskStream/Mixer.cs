using System.Diagnostics;

namespace Anode.Spikes.DiskStream;

/// <summary>
///     The audio-thread side: each callback mixes every track's next frames from its ring into a stereo bus (which is
///     discarded: the output stays silent), checks that the frame it plays is the right frame of the right file, and
///     records its own duration, underruns and allocation. Everything it touches is preallocated.
/// </summary>
sealed class Mixer {
    public const int MaxFrames = 4096; // CTH_MAX_FRAMES
    readonly StreamTrack[] tracks;
    readonly Cycle cycle;
    readonly float[] left = new float[MaxFrames];
    readonly float[] right = new float[MaxFrames];
    readonly float[] gainLeft;
    readonly float[] gainRight;
    long playhead;
    long allocatedBaseline;
    volatile bool resetRequested;
    volatile bool measuring;

    public Mixer(StreamTrack[] tracks, Cycle cycle, long startFrame) {
        this.tracks = tracks;
        this.cycle = cycle;
        playhead = startFrame;
        gainLeft = new float[tracks.Length];
        gainRight = new float[tracks.Length];
        for (var t = 0; t < tracks.Length; t++) {
            var pan = tracks.Length == 1 ? 0.5f : (float)t / (tracks.Length - 1);
            gainLeft[t] = (1 - pan) / tracks.Length;
            gainRight[t] = pan / tracks.Length;
        }
    }

    public Histogram CallbackTime { get; } = new(0.5, 40_000); // 0.5 µs bins up to 20 ms

    public long Callbacks { get; private set; }

    /// <summary>Track-callbacks whose ring held less than the callback needed.</summary>
    public long Underruns { get; private set; }

    public long UnderrunFrames { get; private set; }

    /// <summary>Played frames whose sample was not the file's sample for that timeline position.</summary>
    public long PositionErrors { get; private set; }

    public long CycleJumps { get; private set; }

    /// <summary>The least audio any ring held after a callback, in frames: the headroom left at worst.</summary>
    public int LowWaterFrames { get; private set; } = int.MaxValue;

    /// <summary>Bytes allocated on the callback thread since measuring started (GC.GetAllocatedBytesForCurrentThread).</summary>
    public long AllocatedBytes { get; private set; }

    /// <summary>Sum of callback durations, in Stopwatch ticks.</summary>
    public long BusyTicks { get; private set; }

    public float Checksum { get; private set; }

    /// <summary>Self-test of the allocation check: allocate a little in every callback.</summary>
    public bool AllocateForTest { get; init; }

    public object? AllocationSink { get; private set; }

    /// <summary>Clears the statistics at the next callback and starts measuring (called from the control thread).</summary>
    public void StartMeasuring() {
        resetRequested = true;
        measuring = true;
    }

    public void StopMeasuring() => measuring = false;

    public void Process(int frames) {
        var started = Stopwatch.GetTimestamp();
        if (resetRequested) {
            resetRequested = false;
            CallbackTime.Clear();
            Callbacks = Underruns = UnderrunFrames = PositionErrors = CycleJumps = BusyTicks = 0;
            LowWaterFrames = int.MaxValue;
            allocatedBaseline = GC.GetAllocatedBytesForCurrentThread();
        }

        var l = left.AsSpan(0, frames);
        var r = right.AsSpan(0, frames);
        l.Clear();
        r.Clear();
        var lowWater = int.MaxValue;
        for (var t = 0; t < tracks.Length; t++) {
            lowWater = Math.Min(lowWater, MixTrack(t, l, r, frames));
        }

        if (cycle.UntilJump(playhead) <= frames) {
            CycleJumps++;
        }

        playhead = cycle.Advance(playhead, frames);
        if (AllocateForTest) {
            AllocationSink = new float[4];
        }

        Checksum += l[0] + r[frames - 1];
        if (!measuring) {
            return;
        }

        Callbacks++;
        LowWaterFrames = Math.Min(LowWaterFrames, lowWater);
        AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBaseline;
        var elapsed = Stopwatch.GetTimestamp() - started;
        BusyTicks += elapsed;
        CallbackTime.RecordTicks(elapsed);
    }

    /// <summary>Mixes one track; returns what its ring holds afterwards.</summary>
    int MixTrack(int t, Span<float> l, Span<float> r, int frames) {
        var track = tracks[t];
        var ring = track.Ring;
        if (track.Debt > 0) {
            // Audio that arrived late for a callback that already played silence: drop it to stay in time.
            var drop = (int)Math.Min(track.Debt, ring.Available);
            ring.Skip(drop);
            track.Debt -= drop;
        }

        var available = ring.Available;
        if (track.Debt > 0 || available < frames) {
            // Underrun: play what is there as silence and owe the rest (with a debt left, the ring is empty).
            ring.Skip(available);
            track.Debt += frames - available;
            if (measuring) {
                Underruns++;
                UnderrunFrames += frames - available;
            }

            return 0;
        }

        var expected = playhead < track.Frames ? WavFile.Pattern(playhead, t) : 0;
        if ((int)MathF.Round(ring.Peek() * 8_388_608f) != expected && measuring) {
            PositionErrors++;
        }

        ring.MixInto(l, r, gainLeft[t], gainRight[t]);
        return available - frames;
    }
}
