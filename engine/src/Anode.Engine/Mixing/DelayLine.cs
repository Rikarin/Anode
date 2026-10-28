namespace Anode.Engine.Mixing;

/// <summary>
///     A stereo delay line with any number of read taps, for plugin delay compensation: each block a channel writes its
///     post-insert signal once, and each of its outputs and sends reads it back delayed by what its summing point needs.
///     Allocated by the control thread; written and read by the audio thread only.
/// </summary>
public sealed class DelayLine {
    readonly float[] left;
    readonly float[] right;
    readonly int mask;
    int write;

    /// <param name="maxDelay">The longest delay a tap may ask for, in frames.</param>
    public DelayLine(int maxDelay) {
        var size = 1;
        while (size < maxDelay + AudioEngine.MaxFrames) {
            size <<= 1;
        }

        left = GC.AllocateArray<float>(size, true);
        right = GC.AllocateArray<float>(size, true);
        mask = size - 1;
        MaxDelay = size - AudioEngine.MaxFrames;
    }

    /// <summary>The longest delay a tap gets; longer requests are clamped to it.</summary>
    public int MaxDelay { get; }

    /// <summary>Audio thread: appends a block (a mono signal passes the same span twice).</summary>
    public void Write(ReadOnlySpan<float> l, ReadOnlySpan<float> r) {
        var w = write;
        for (var i = 0; i < l.Length; i++) {
            var at = (w + i) & mask;
            left[at] = l[i];
            right[at] = r[i];
        }

        write = (w + l.Length) & mask;
    }

    /// <summary>
    ///     Audio thread: the block just written, <paramref name="delay" /> frames late, into <paramref name="l" /> and
    ///     <paramref name="r" />.
    /// </summary>
    public void Read(int delay, Span<float> l, Span<float> r) {
        delay = Math.Clamp(delay, 0, MaxDelay);
        var start = write - l.Length - delay;
        for (var i = 0; i < l.Length; i++) {
            var at = (start + i) & mask;
            l[i] = left[at];
            r[i] = right[at];
        }
    }

    /// <summary>Audio thread: silence (a render starts from a clean state).</summary>
    public void Clear() {
        Array.Clear(left);
        Array.Clear(right);
    }
}
