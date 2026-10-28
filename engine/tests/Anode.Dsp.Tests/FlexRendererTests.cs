using Anode.Dsp.Flex;

namespace Anode.Dsp.Tests;

/// <summary>
///     The reference flex renderer (docs/06-phase-5.md; the fallback without an_stretch): onsets land where the warp
///     map says; seeking is continuous.
/// </summary>
public sealed class FlexRendererTests {
    /// <summary>A stereo file of silence with a click (left 1, right −1) at each of <paramref name="clicks" />.</summary>
    sealed class ClickSource(long frames, params long[] clicks) : IFlexSource {
        public int Channels => 2;
        public long Frames => frames;

        public int Read(long fileFrame, Span<float> interleaved) {
            interleaved.Clear();
            var inside = 0;
            for (var n = 0; n < interleaved.Length / 2; n++) {
                var frame = fileFrame + n;
                if (frame < 0 || frame >= frames) {
                    continue;
                }

                inside++;
                if (Array.IndexOf(clicks, frame) >= 0) {
                    interleaved[n * 2] = 1;
                    interleaved[n * 2 + 1] = -1;
                }
            }

            return inside;
        }
    }

    // Twice as slow over the first 2,000 file frames, then 1:1 for the next 2,000.
    static readonly FlexWarpPoint[] Warp = [
        new() { Offset = 0, FileFrame = 0 },
        new() { Offset = 4_000, FileFrame = 2_000 },
        new() { Offset = 6_000, FileFrame = 4_000 }
    ];

    static float[] Render(IFlexRenderer renderer, IFlexSource source, long offset, int frames, int block) {
        var output = new float[frames * 2];
        for (var done = 0; done < frames; done += block) {
            var count = Math.Min(block, frames - done);
            renderer.Render(
                new(FlexKind.Polyphonic, Warp, [], 6_000),
                offset + done,
                output.AsSpan(done * 2, count * 2),
                2,
                source
            );
        }

        return output;
    }

    [Fact]
    public void Onsets_land_where_the_warp_map_puts_them() {
        var source = new ClickSource(10_000, 1_000, 3_000);
        using var renderer = new ReferenceFlexRenderer(2, 512);
        var output = Render(renderer, source, 0, 6_000, 512);

        // File frame 1000 at offset 2000 (stretched ×2), file frame 3000 at offset 5000 (1:1 after 4000).
        Assert.Equal(1f, output[2_000 * 2]);
        Assert.Equal(-1f, output[2_000 * 2 + 1]);
        Assert.Equal(0.5f, output[1_999 * 2]); // file frame 999.5: halfway to the click
        Assert.Equal(1f, output[5_000 * 2]);
        Assert.Equal(0f, output[4_999 * 2]);
        Assert.Equal(0f, output[5_001 * 2]);
        var loud = Enumerable.Range(0, 6_000).Count(n => output[n * 2] != 0);
        Assert.Equal(4, loud); // 1999, 2000, 2001 around the stretched click; 5000
    }

    [Fact]
    public void A_render_from_any_offset_equals_the_continuous_render() {
        var source = new ClickSource(10_000, 100, 1_000, 2_500, 3_999);
        using var continuous = new ReferenceFlexRenderer(2, 256);
        var whole = Render(continuous, source, 0, 6_000, 256);

        using var seeking = new ReferenceFlexRenderer(2, 256);
        foreach (var start in new long[] { 1_234, 17, 4_000, 3_999 }) {
            seeking.Reset();
            var part = Render(seeking, source, start, 1_500, 100);
            Assert.Equal(whole.AsSpan((int)start * 2, part.Length).ToArray(), part);
        }
    }

    [Fact]
    public void Outside_the_entry_is_silent() {
        var source = new ClickSource(10_000, 0, 5_999);
        using var renderer = new ReferenceFlexRenderer(2, 64);
        var before = Render(renderer, source, -64, 64, 64);
        var after = Render(renderer, source, 6_000, 64, 64);
        Assert.All(before, static s => Assert.Equal(0f, s));
        Assert.All(after, static s => Assert.Equal(0f, s));
    }
}
