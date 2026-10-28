namespace Anode.Dsp.Flex;

/// <summary>
///     The reference renderer: each output frame plays the file at the fractional frame the warp map gives it, linearly
///     interpolated between file frames; no pitch shifting (pitch points are ignored), so its pitch follows the warp's
///     rate, as Speed's does. Stateless apart from a read cache, so it needs no priming and any fill start renders the
///     same samples. Allocation-free after construction.
/// </summary>
public sealed class ReferenceFlexRenderer : IFlexRenderer {
    readonly int channels;
    readonly float[] window;
    readonly int windowFrames;
    long windowStart = long.MinValue;

    public ReferenceFlexRenderer(int channels, int maxFrames) {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFrames, 1);
        this.channels = channels;
        windowFrames = Math.Max(maxFrames, 1024) + 1;
        window = new float[windowFrames * channels];
    }

    public int PrimingFrames => 0;

    public void Render(in FlexRegion region, long offset, Span<float> interleaved, int channels, IFlexSource source) {
        if (channels != this.channels || source.Channels != channels) {
            throw new ArgumentException("The renderer, the output and the source must have the same channel count.");
        }

        var frames = interleaved.Length / channels;
        var warp = region.Warp;
        if (warp.Length < 2) {
            interleaved.Clear();
            return;
        }

        var segment = 0;
        for (var n = 0; n < frames; n++) {
            var o = offset + n;
            var output = interleaved.Slice(n * channels, channels);
            if (o < 0 || o >= region.Length) {
                output.Clear();
                continue;
            }

            while (segment < warp.Length - 2 && warp[segment + 1].Offset <= o) {
                segment++;
            }

            while (segment > 0 && warp[segment].Offset > o) {
                segment--;
            }

            var a = warp[segment];
            var b = warp[segment + 1];
            var position = a.FileFrame + (o - a.Offset) * (b.FileFrame - a.FileFrame) / (b.Offset - a.Offset);
            var frame = (long)Math.Floor(position);
            var fraction = (float)(position - frame);
            var first = Pair(frame, source);
            var second = first + channels;
            for (var c = 0; c < channels; c++) {
                output[c] = window[first + c] + (window[second + c] - window[first + c]) * fraction;
            }
        }
    }

    public void Reset() => windowStart = long.MinValue;

    public void Dispose() { }

    /// <summary>
    ///     The index in <see cref="window" /> of file frame <paramref name="frame" />, followed by the next frame, reading
    ///     them in if need be.
    /// </summary>
    int Pair(long frame, IFlexSource source) {
        if (frame < windowStart || frame + 1 >= windowStart + windowFrames) {
            windowStart = frame;
            source.Read(frame, window);
        }

        return (int)(frame - windowStart) * channels;
    }
}
