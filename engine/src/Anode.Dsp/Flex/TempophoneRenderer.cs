namespace Anode.Dsp.Flex;

/// <summary>
///     Tempophone (docs/06-phase-5.md, "Flex algorithms"): granular, after the rotating-head tape machine it is named for.
///     60 ms grains start every 30 ms on a grid from the entry's start; each plays the file at original speed, centred
///     on the file frame the warp map gives its centre, under a Hann window, so neighbouring grains crossfade and sum to
///     unity. Grains repeat (slower) or skip (faster) material, which is the effect.
/// </summary>
internal sealed class TempophoneRenderer : WindowedFlexRenderer {
    readonly int hop;
    readonly float[] window;

    public TempophoneRenderer(int channels, int sampleRate, int maxFrames)
        : base(channels, Math.Max(maxFrames, 1_024) + 8_192 + sampleRate / 5) {
        hop = Math.Max(2, sampleRate * 3 / 100);
        window = new float[hop * 2];
        for (var i = 0; i < window.Length; i++) {
            window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / window.Length));
        }
    }

    protected override void RenderRun(in FlexRegion region, long offset, Span<float> output, IFlexSource source) {
        var warp = region.Warp;
        var channels = Channels;
        var data = Window.Data;
        var hint = 0;
        var grain = long.MinValue;
        long earlierStart = 0, laterStart = 0;
        for (var n = 0; n < output.Length / channels; n++) {
            var t = offset + n;

            // The two grains sounding at t: the one started at g·hop and the one before it.
            var g = Math.DivRem(t, hop, out var into);
            if (g != grain) {
                grain = g;
                laterStart = GrainFileStart(warp, g, ref hint);
                earlierStart = GrainFileStart(warp, g - 1, ref hint);
            }

            var later = window[into];
            var earlier = window[into + hop];
            // Both grains' frames in one window when they fit, so far-apart grains do not evict each other.
            var e = earlierStart + into + hop;
            var l = laterStart + into;
            var low = Math.Min(e, l);
            var span = Math.Max(e, l) - low + 1;
            int a, b;
            if (span <= Window.Capacity) {
                var start = Window.Locate(source, low, (int)span);
                a = start + (int)(e - low) * channels;
                b = start + (int)(l - low) * channels;
                for (var c = 0; c < channels; c++) {
                    output[n * channels + c] = earlier * data[a + c] + later * data[b + c];
                }

                continue;
            }

            a = Window.Locate(source, e, 1);
            for (var c = 0; c < channels; c++) {
                output[n * channels + c] = earlier * data[a + c];
            }

            b = Window.Locate(source, l, 1);
            for (var c = 0; c < channels; c++) {
                output[n * channels + c] += later * data[b + c];
            }
        }
    }

    /// <summary>The file frame grain <paramref name="g" /> starts at: its centre lands on the warp map.</summary>
    long GrainFileStart(ReadOnlySpan<FlexWarpPoint> warp, long g, ref int hint) {
        var centre = g * hop + hop;
        return (long)Math.Round(WarpMap.FileFrameAt(warp, centre, ref hint)) - hop;
    }
}
