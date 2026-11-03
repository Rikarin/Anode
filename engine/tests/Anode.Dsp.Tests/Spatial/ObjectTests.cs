using Anode.Dsp.Spatial;
using Anode.Dsp.Tests.Support;

namespace Anode.Dsp.Tests.Spatial;

/// <summary>VBAP and the object renderer (docs/spatial/README.md).</summary>
public sealed class ObjectTests {
    const float Rate = 48_000;

    [Fact]
    public void An_object_at_a_speaker_plays_from_that_speaker_only() {
        foreach (var format in Enum.GetValues<SpeakerFormat>()) {
            var layout = SpeakerLayout.Of(format);
            var vbap = new Vbap(layout);
            var gains = new float[layout.ChannelCount];
            for (var c = 0; c < layout.ChannelCount; c++) {
                if (c == layout.LfeIndex || format is SpeakerFormat.Stereo or SpeakerFormat.Lcr) {
                    continue;
                }

                vbap.Gains(new(layout.Azimuths[c], layout.Elevations[c], 1, 0), gains);
                for (var k = 0; k < gains.Length; k++) {
                    Assert.Equal(k == c ? 1 : 0, gains[k], 5);
                }
            }
        }
    }

    [Fact]
    public void Gains_have_unit_power_everywhere_with_any_size_and_none_in_the_lfe() {
        var random = new Random(11);
        foreach (var format in Enum.GetValues<SpeakerFormat>()) {
            var layout = SpeakerLayout.Of(format);
            var vbap = new Vbap(layout);
            var gains = new float[layout.ChannelCount];
            for (var i = 0; i < 200; i++) {
                var position = new ObjectPosition(
                    random.NextSingle() * 360 - 180,
                    random.NextSingle() * 180 - 90,
                    1,
                    i % 3 == 0 ? random.NextSingle() : 0
                );
                vbap.Gains(position, gains);
                var power = gains.Sum(static g => g * g);
                // BS.2127's stereo (and Anode's LCR) lowers the level behind by up to 3 dB.
                if (format is SpeakerFormat.Stereo or SpeakerFormat.Lcr) {
                    Assert.InRange(power, 0.5 - 1e-4, 1 + 1e-4);
                } else {
                    Assert.Equal(1, power, 4);
                }

                if (layout.LfeIndex >= 0) {
                    Assert.Equal(0, gains[layout.LfeIndex]);
                }
            }
        }
    }

    [Fact]
    public void Size_spreads_over_more_speakers() {
        var layout = SpeakerLayout.Of(SpeakerFormat.Surround714);
        var vbap = new Vbap(layout);
        var point = new float[12];
        var wide = new float[12];
        var everywhere = new float[12];
        vbap.Gains(new(0, 0, 1, 0), point);
        vbap.Gains(new(0, 0, 1, 0.3f), wide);
        vbap.Gains(new(0, 0, 1, 1), everywhere);
        Assert.Equal(1, point[2], 5);
        Assert.True(wide[2] < 0.9f && wide[0] > 0.2f && wide[1] > 0.2f);
        Assert.True(wide[6] < wide[0]);
        Assert.All(everywhere.Where((_, c) => c != layout.LfeIndex), static g => Assert.True(g > 0.1f));
    }

    [Fact]
    public void Distance_changes_only_the_level() {
        Assert.Equal(1, ObjectPosition.DistanceGain(1));
        Assert.Equal(1.25f, ObjectPosition.DistanceGain(0.8f), 5);
        Assert.Equal(2, ObjectPosition.DistanceGain(0));
    }

    [Fact]
    public void An_object_moving_from_front_left_to_rear_right_overhead_moves_its_energy_from_l_to_the_top_rear_right() {
        // The golden path's object: linear in azimuth and elevation, 4 s, per-block positions.
        var layout = SpeakerLayout.Of(SpeakerFormat.Surround714);
        var renderer = new ObjectRenderer();
        const int block = 480;
        renderer.Prepare(layout, 1, Rate, block);
        var seconds = 4;
        var frames = (int)Rate * seconds;
        var signal = new float[block];
        var noise = new Random(3);
        var energy = new double[seconds * 4, 12];
        var output = new float[12 * block];
        for (var start = 0; start < frames; start += block) {
            var t = (start + block) / (double)frames;
            renderer.SetPosition(0, new((float)(30 + t * (-135 - 30)), (float)(t * 30), 1, 0));
            for (var n = 0; n < block; n++) {
                signal[n] = noise.NextSingle() * 2 - 1;
            }

            Array.Clear(output);
            renderer.Render(0, new(signal, 1, block), new(output, 12, block));
            for (var c = 0; c < 12; c++) {
                foreach (var s in output.AsSpan(c * block, block)) {
                    energy[start / ((int)Rate / 4), c] += s * s;
                }
            }
        }

        // By quarter seconds: L first, U-135 last.
        int Loudest(int quarter) => Enumerable.Range(0, 12).MaxBy(c => energy[quarter, c]);
        Assert.Equal(0, Loudest(0));
        Assert.Equal(11, Loudest(seconds * 4 - 1));
    }

    [Fact]
    public void Positions_ramp_over_the_block_or_to_their_frame() {
        var layout = SpeakerLayout.Of(SpeakerFormat.Surround51);
        var renderer = new ObjectRenderer();
        renderer.Prepare(layout, 2, Rate, 100);
        var input = new float[100];
        input.AsSpan().Fill(1);
        var output = new float[6 * 100];

        // The first position applies at once.
        renderer.SetPosition(0, new(0, 0, 1, 0));
        renderer.Render(0, new(input, 1, 100), new(output, 6, 100));
        Assert.All(output.AsSpan(200, 100).ToArray(), static s => Assert.Equal(1, s, 6));

        // A per-block position (frame 0) glides over the whole block.
        Array.Clear(output);
        renderer.SetPosition(0, new(30, 0, 1, 0));
        renderer.Render(0, new(input, 1, 100), new(output, 6, 100));
        Assert.Equal(0.99f, output[200], 5);
        Assert.Equal(0.5f, output[249], 5);
        Assert.Equal(0, output[299], 5);
        Assert.Equal(1, output[99], 5);

        // A position at frame 20 is reached there and held.
        Array.Clear(output);
        renderer.SetPosition(0, new(0, 0, 1, 0), 20);
        renderer.Render(0, new(input, 1, 100), new(output, 6, 100));
        Assert.Equal(0.95f, output[0], 5);
        Assert.Equal(0, output[19], 5);
        Assert.All(output.AsSpan(220, 80).ToArray(), static s => Assert.Equal(1, s, 6));

        // No position: hold. Reset: the next position applies at once.
        Array.Clear(output);
        renderer.Render(0, new(input, 1, 100), new(output, 6, 100));
        Assert.All(output.AsSpan(200, 100).ToArray(), static s => Assert.Equal(1, s, 6));
        renderer.Reset(0);
        Array.Clear(output);
        renderer.SetPosition(0, new(-30, 0, 0.5f, 0));
        renderer.Render(0, new(input, 1, 100), new(output, 6, 100));
        Assert.All(output.AsSpan(100, 100).ToArray(), static s => Assert.Equal(2, s, 6));
    }

    [Fact]
    public void Rendering_118_objects_allocates_nothing() {
        var layout = SpeakerLayout.Of(SpeakerFormat.Surround714);
        var renderer = new ObjectRenderer();
        renderer.Prepare(layout, ObjectRenderer.MaxObjects, Rate, 256);
        var input = new float[256];
        var output = new float[12 * 256];
        var step = 0f;
        Assert.Equal(
            0,
            AllocationProbe.Measure(() => {
                step += 3;
                for (var o = 0; o < ObjectRenderer.MaxObjects; o++) {
                    renderer.SetPosition(o, new(o + step, o % 60, 1, o % 4 == 0 ? 0.2f : 0));
                    renderer.Render(o, new(input, 1, 256), new(output, 12, 256));
                }
            })
        );
    }
}
