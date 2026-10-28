using System.Runtime.InteropServices;

namespace Anode.Engine.Recording;

/// <summary>
///     Flashback Capture's memory of one armed audio track (docs/04-phase-3.md): the last minutes of its input while
///     the transport plays (not while it records), in a circular buffer of native memory indexed by the transport's
///     clock. The audio thread writes; the control thread copies a window out on capture, checking afterwards that
///     the audio thread did not overwrite what it copied.
/// </summary>
/// <remarks>
///     The buffer is allocated untouched, so its pages cost nothing until written; the writer thread faults them in a
///     little ahead of the audio thread (<see cref="Touch" />), so the callback does not take page faults on the first
///     lap either.
/// </remarks>
public sealed unsafe class FlashbackBuffer : IDisposable {
    const int TouchAheadSeconds = 2;
    static readonly int PageFloats = Math.Max(4096, Environment.SystemPageSize) / sizeof(float);

    float* data;
    long runStart = -1; // the clock the current run of writes started at (−1: none yet)
    long end; // the clock after the last frame written
    long touched; // writer thread: samples faulted in so far (first lap only)

    public FlashbackBuffer(int channels, long capacityFrames, int sampleRate) {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityFrames, 1);
        Channels = channels;
        Capacity = capacityFrames;
        SampleRate = sampleRate;
        data = (float*)NativeMemory.Alloc((nuint)(capacityFrames * channels), sizeof(float));
    }

    public int Channels { get; }

    /// <summary>Frames it holds at most.</summary>
    public long Capacity { get; }

    public int SampleRate { get; }

    /// <summary>The clock the current run of writes started at (−1: nothing written yet).</summary>
    public long RunStart => Volatile.Read(ref runStart);

    /// <summary>The clock after the last frame written.</summary>
    public long End => Volatile.Read(ref end);

    /// <summary>
    ///     Audio thread: stores <paramref name="frames" /> frames of input (null channels: silence) at clocks
    ///     [<paramref name="clock" />, + <paramref name="frames" />). <paramref name="newRun" /> (playback started),
    ///     or a clock that does not continue the last write, starts a new run, forgetting the old one.
    /// </summary>
    public void Write(float* left, float* right, int frames, long clock, bool newRun) {
        if (newRun || clock != end || runStart < 0) {
            Volatile.Write(ref runStart, clock);
        }

        var at = clock % Capacity;
        for (var f = 0; f < frames; f++) {
            var frame = data + at * Channels;
            frame[0] = left is null ? 0f : left[f];
            if (Channels == 2) {
                frame[1] = right is null ? frame[0] : right[f];
            }

            if (++at == Capacity) {
                at = 0;
            }
        }

        Volatile.Write(ref end, clock + frames);
    }

    /// <summary>
    ///     Control thread: copies frames [<paramref name="clock" />, + <paramref name="frames" />) (interleaved) into
    ///     <paramref name="destination" />. False when the audio thread may have overwritten any of them meanwhile.
    /// </summary>
    public bool Read(long clock, int frames, Span<float> destination) {
        if (clock < End - Capacity) {
            return false;
        }

        var at = clock % Capacity;
        for (var f = 0; f < frames; f++) {
            var frame = data + at * Channels;
            for (var c = 0; c < Channels; c++) {
                destination[f * Channels + c] = frame[c];
            }

            if (++at == Capacity) {
                at = 0;
            }
        }

        Interlocked.MemoryBarrier();
        return clock >= End - Capacity; // still in the window once copied
    }

    /// <summary>
    ///     Writer thread: faults in the pages up to two seconds ahead of the audio thread on the buffer's first lap, with
    ///     atomic adds of zero, which leave whatever the audio thread writes beside them intact.
    /// </summary>
    public void Touch() {
        var total = Capacity * Channels;
        if (touched >= total) {
            return;
        }

        var target = Math.Min(total, (End % Capacity + (long)TouchAheadSeconds * SampleRate) * Channels);
        if (End >= Capacity) {
            target = total;
        }

        while (touched < target) {
            Interlocked.Add(ref *(int*)(data + touched), 0);
            touched += PageFloats;
        }
    }

    public void Dispose() {
        if (data is not null) {
            NativeMemory.Free(data);
            data = null;
        }
    }
}

/// <summary>
///     Where the playhead was at every clock while the transport rolled: breakpoints (clock C, position P) meaning
///     "from clock C on, the position is P + (clock − C)", added by the audio thread wherever play order jumps (a cycle
///     wrap, a locate, a start). Flashback Capture maps its frames' clocks to positions through it, so material played
///     across cycle wraps lands where it was heard.
/// </summary>
public sealed class PositionHistory {
    const int Capacity = 1 << 16; // power of two: over an hour of one-second cycles

    readonly Breakpoint[] entries = new Breakpoint[Capacity];
    long count; // audio thread; published with release
    long nextClock = -1;
    long nextPosition = -1;

    /// <summary>
    ///     Audio thread: frames [<paramref name="clock" />, + <paramref name="frames" />) play from
    ///     <paramref name="position" /> on in <paramref name="order" />.
    /// </summary>
    public void Played(long clock, long position, int frames, Cycle order) {
        var done = 0;
        while (done < frames) {
            if (clock + done != nextClock || position != nextPosition) {
                entries[count & (Capacity - 1)] = new(clock + done, position);
                Volatile.Write(ref count, count + 1);
            }

            var run = (int)Math.Min(frames - done, order.UntilJump(position));
            done += run;
            nextClock = clock + done;
            nextPosition = position + run;
            position = order.Advance(position, run);
        }
    }

    /// <summary>
    ///     Control thread: the breakpoints that cover clocks from <paramref name="from" /> on, oldest first (the first
    ///     moved to <paramref name="from" /> when it started before), or an empty list when the history no longer
    ///     reaches back that far.
    /// </summary>
    public List<Breakpoint> Since(long from) {
        var list = new List<Breakpoint>();
        while (true) {
            list.Clear();
            var n = Volatile.Read(ref count);
            var oldest = Math.Max(0, n - Capacity + 64);
            for (var i = oldest; i < n; i++) {
                list.Add(entries[i & (Capacity - 1)]);
            }

            Interlocked.MemoryBarrier();
            if (Volatile.Read(ref count) <= oldest + Capacity) {
                break;
            }

            // The oldest was overwritten while copied: read again.
        }

        // Keep the last breakpoint at or before `from` (rebased to it) and everything after.
        var first = list.FindLastIndex(b => b.Clock <= from);
        if (first < 0) {
            return list; // from before the oldest: positions before it run on linearly back from it
        }

        var start = list[first];
        list.RemoveRange(0, first);
        list[0] = new(from, start.Position + (from - start.Clock));
        // Several breakpoints at one clock (a locate while stopped, then play): the last one holds.
        for (var i = list.Count - 1; i > 0; i--) {
            if (list[i - 1].Clock == list[i].Clock) {
                list.RemoveAt(i - 1);
            }
        }

        return list;
    }

    /// <summary>The position at <paramref name="clock" /> under <paramref name="breakpoints" /> (from <see cref="Since" />).</summary>
    public static long PositionAt(List<Breakpoint> breakpoints, long clock) {
        var i = breakpoints.FindLastIndex(b => b.Clock <= clock);
        var b = breakpoints[Math.Max(0, i)];
        return b.Position + (clock - b.Clock);
    }

    public readonly record struct Breakpoint(long Clock, long Position);
}
