namespace Anode.Dsp.Flex;

/// <summary>
///     The shape the C# algorithms share: each output frame is a function of the entry's maps and the file alone, so
///     they need no priming and any fill start renders the same samples. The base clears what lies outside
///     0…<c>Length</c>, renders the rest through <see cref="RenderRun" />, then applies the pitch points' gain at the file
///     frame each output frame plays. Allocation-free after construction.
/// </summary>
internal abstract class WindowedFlexRenderer : IFlexRenderer {
    protected WindowedFlexRenderer(int channels, int windowFrames) {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        Channels = channels;
        Window = new(channels, windowFrames);
    }

    protected int Channels { get; }

    protected SourceWindow Window { get; }

    public int PrimingFrames => 0;

    public void Render(in FlexRegion region, long offset, Span<float> interleaved, int channels, IFlexSource source) {
        if (channels != Channels || source.Channels != channels) {
            throw new ArgumentException("The renderer, the output and the source must have the same channel count.");
        }

        var frames = interleaved.Length / channels;
        interleaved.Clear();
        var warp = region.Warp;
        if (warp.Length < 2) {
            return;
        }

        var first = Math.Max(offset, 0);
        var end = Math.Min(offset + frames, region.Length);
        if (first >= end) {
            return;
        }

        var output = interleaved.Slice((int)(first - offset) * channels, (int)(end - first) * channels);
        RenderRun(region, first, output, source);
        if (region.Pitch.IsEmpty) {
            return;
        }

        int warpHint = 0, pitchHint = 0;
        for (var n = 0; n < end - first; n++) {
            var gain = PitchCurve.GainAt(
                region.Pitch,
                WarpMap.FileFrameAt(warp, first + n, ref warpHint),
                ref pitchHint
            );
            if (gain != 1) {
                foreach (ref var sample in output.Slice(n * channels, channels)) {
                    sample *= gain;
                }
            }
        }
    }

    public void Reset() => Window.Invalidate();

    public void Dispose() { }

    /// <summary>
    ///     Renders <c>output.Length / Channels</c> frames from entry offset <paramref name="offset" />, all within
    ///     0…<c>Length</c>, into the cleared <paramref name="output" />.
    /// </summary>
    protected abstract void RenderRun(in FlexRegion region, long offset, Span<float> output, IFlexSource source);
}
