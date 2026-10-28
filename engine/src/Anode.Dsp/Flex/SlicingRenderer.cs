namespace Anode.Dsp.Flex;

/// <summary>
///     Slicing and Rhythmic (docs/06-phase-5.md, "Flex algorithms"). The warp points cut the entry into slices; each
///     slice starts at its warped position and plays its file frames at original speed, up to the next slice's first
///     file frame or the next cut, whichever comes first.
///     <list type="bullet">
///         <item>
///             Slicing leaves a stretched slice's gap silent. A cut ends with a 2 ms fade, and a slice that does not
///             continue the previous one seamlessly starts with a 0.5 ms fade-in; a slice that plays exactly its own
///             length (rate 1) joins its neighbours untouched.
///         </item>
///         <item>
///             Rhythmic fills the gap by looping the slice's last 30 ms (less in a shorter slice), each pass
///             crossfaded into the next over 5 ms with an equal-power fade.
///         </item>
///     </list>
///     The compiler also puts warp points where the tempo ramps (as often as every 10 ms), and a slice per point would
///     chop such stretches into fragments, so a point cuts only when it is the first in a new 20 ms cell of the entry
///     (<see cref="IsCut" />): markers and beats, which lie further apart, always cut. The rule is local, so a render
///     from any offset finds the same slices.
/// </summary>
internal sealed class SlicingRenderer : WindowedFlexRenderer {
    readonly int cell;
    readonly int fadeIn;
    readonly int fadeOut;
    readonly int loop;
    readonly bool rhythmic;
    readonly int loopFade;

    public SlicingRenderer(bool rhythmic, int channels, int sampleRate, int maxFrames)
        : base(channels, Math.Max(maxFrames, 1_024) * 2 + 8_192) {
        this.rhythmic = rhythmic;
        cell = Math.Max(1, sampleRate / 50);
        fadeIn = Math.Max(1, sampleRate / 2_000);
        fadeOut = Math.Max(1, sampleRate / 500);
        loop = Math.Max(3, sampleRate * 3 / 100);
        loopFade = Math.Max(1, sampleRate / 200);
    }

    protected override void RenderRun(in FlexRegion region, long offset, Span<float> output, IFlexSource source) {
        var warp = region.Warp;
        var channels = Channels;
        var frames = output.Length / channels;
        var hint = 0;
        var n = 0;
        while (n < frames) {
            var t = offset + n;
            var s = WarpMap.Segment(warp, t, ref hint);
            var first = s;
            while (first > 0 && !IsCut(warp, first)) {
                first--;
            }

            var next = s + 1;
            while (next < warp.Length - 1 && !IsCut(warp, next)) {
                next++;
            }

            var sliceStart = warp[first].Offset;
            var sliceEnd = Math.Min(warp[next].Offset, region.Length);
            var count = (int)Math.Min(frames - n, sliceEnd - t);
            var slice = new Slice {
                Start = sliceStart,
                Length = sliceEnd - sliceStart,
                FileStart = (long)Math.Round(warp[first].FileFrame),
                Content = (long)Math.Round(warp[next].FileFrame) - (long)Math.Round(warp[first].FileFrame),
                FadesIn = first > 0 && !Seamless(warp, first)
            };
            RenderSlice(slice, t, output.Slice(n * channels, count * channels), source);
            n += count;
        }
    }

    /// <summary>Whether warp point <paramref name="i" /> starts a slice (see the class remarks).</summary>
    bool IsCut(ReadOnlySpan<FlexWarpPoint> warp, int i) =>
        i == 0 || i == warp.Length - 1 || warp[i].Offset / cell != warp[i - 1].Offset / cell;

    /// <summary>Whether the slice ending at cut <paramref name="i" /> played exactly its own file frames.</summary>
    bool Seamless(ReadOnlySpan<FlexWarpPoint> warp, int i) {
        var previous = i - 1;
        while (previous > 0 && !IsCut(warp, previous)) {
            previous--;
        }

        return (long)Math.Round(warp[previous].FileFrame) + (warp[i].Offset - warp[previous].Offset)
            == (long)Math.Round(warp[i].FileFrame);
    }

    void RenderSlice(in Slice slice, long offset, Span<float> output, IFlexSource source) {
        var channels = Channels;
        var data = Window.Data;
        var exact = slice.Content == slice.Length;
        var looped = rhythmic && slice.Content < slice.Length && slice.Content >= 3;
        var span = looped ? Math.Min(loop, slice.Content) : 0;
        var crossfade = looped ? Math.Min(loopFade, span / 3) : 0;
        var period = span - crossfade;
        var loopStart = slice.Content - crossfade; // where the first loop pass starts, within the slice
        var audible = looped ? slice.Length : Math.Min(slice.Content, slice.Length);
        for (var n = 0; n < output.Length / channels; n++) {
            var u = offset + n - slice.Start;
            if (u >= audible || slice.Content <= 0) {
                continue;
            }

            var gain = 1.0;
            if (slice.FadesIn) {
                gain = Math.Min(1.0, (u + 1.0) / (fadeIn + 1.0));
            }

            if (!exact) {
                gain *= Math.Min(1.0, (audible - u) / (double)fadeOut);
            }

            var target = output.Slice(n * channels, channels);
            if (!looped || u < loopStart) {
                Add(target, source, slice.FileStart + u, gain);
                continue;
            }

            // Pass 0 (the slice itself) fades out over its last frames while pass 1 fades in; pass m plays the
            // loop span from loopStart + (m - 1) * period, overlapping the next by the crossfade.
            var loopEnd = slice.FileStart + slice.Content;
            var m = (u - loopStart) / period + 1;
            var v = u - (loopStart + (m - 1) * period);
            Add(target, source, loopEnd - span + v, gain * FadeIn(v, crossfade));
            var w = v + period; // the previous pass, in its fade-out
            if (m == 1) {
                Add(target, source, slice.FileStart + u, gain * FadeOut(u - loopStart, crossfade));
            } else if (w < span) {
                Add(target, source, loopEnd - span + w, gain * FadeOut(w - period, crossfade));
            }
        }

        void Add(Span<float> target, IFlexSource source, long frame, double gain) {
            if (gain <= 0) {
                return;
            }

            var index = Window.Locate(source, frame, 1);
            for (var c = 0; c < channels; c++) {
                target[c] += (float)(data[index + c] * gain);
            }
        }
    }

    static double FadeIn(long v, long length) => v >= length ? 1 : Math.Sin(Math.PI / 2 * (v + 0.5) / length);

    static double FadeOut(long v, long length) =>
        v < 0 ? 1 : v >= length ? 0 : Math.Cos(Math.PI / 2 * (v + 0.5) / length);

    struct Slice {
        public long Start;
        public long Length;
        public long FileStart;
        public long Content;
        public bool FadesIn;
    }
}
