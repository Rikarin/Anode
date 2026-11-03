namespace Anode.Engine.Mixing;

/// <summary>
///     A stereo delay line with any number of read taps, for plugin delay compensation: each block a channel writes its
///     post-insert signal once, and each of its outputs and sends reads it back delayed by what its summing point needs.
///     Allocated by the control thread; written and read by the audio thread only. Phase 10: a multichannel strip's
///     line also carries its channels after the first two (<see cref="ExtraChannels" />).
/// </summary>
public sealed class DelayLine {
    readonly float[] left;
    readonly float[] right;
    readonly float[][] extra;
    readonly int mask;
    int write;

    /// <param name="maxDelay">The longest delay a tap may ask for, in frames.</param>
    /// <param name="extraChannels">Channels beyond the left and right (a surround strip's).</param>
    public DelayLine(int maxDelay, int extraChannels = 0) {
        var size = 1;
        while (size < maxDelay + AudioEngine.MaxFrames) {
            size <<= 1;
        }

        left = GC.AllocateArray<float>(size, true);
        right = GC.AllocateArray<float>(size, true);
        extra = new float[Math.Max(0, extraChannels)][];
        for (var c = 0; c < extra.Length; c++) {
            extra[c] = GC.AllocateArray<float>(size, true);
        }

        mask = size - 1;
        MaxDelay = size - AudioEngine.MaxFrames;
    }

    /// <summary>Channels the line keeps beyond the left and right.</summary>
    public int ExtraChannels => extra.Length;

    /// <summary>
    ///     Audio thread: appends a block of extra channel <paramref name="channel" /> (0: the strip's third), before
    ///     <see cref="Write" /> appends the left and right and moves on.
    /// </summary>
    public void WriteExtra(int channel, ReadOnlySpan<float> block) {
        var line = extra[channel];
        var w = write;
        for (var i = 0; i < block.Length; i++) {
            line[(w + i) & mask] = block[i];
        }
    }

    /// <summary>Audio thread, after <see cref="Write" />: extra channel <paramref name="channel" />'s block, delayed.</summary>
    public void ReadExtra(int channel, int delay, Span<float> block) {
        delay = Math.Clamp(delay, 0, MaxDelay);
        var line = extra[channel];
        var start = write - block.Length - delay;
        for (var i = 0; i < block.Length; i++) {
            block[i] = line[(start + i) & mask];
        }
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
        foreach (var line in extra) {
            Array.Clear(line);
        }
    }
}
