using Anode.Dsp.Spatial;
using Anode.Dsp.Tests.Support;

namespace Anode.Dsp.Tests.Spatial;

/// <summary>The Surround Panner (docs/spatial/README.md): hand-worked gains, power, ramps, the LFE send.</summary>
public sealed class SurroundPannerTests {
    const float Rate = 48_000;
    static readonly float Half = MathF.Sqrt(0.5f);

    static float[] Gains(SpeakerFormat format, SurroundPannerSettings settings, int inputs = 1, int channel = 0) {
        var panner = new SurroundPanner();
        var layout = SpeakerLayout.Of(format);
        panner.Prepare(layout, inputs, Rate, 256);
        panner.Set(settings);
        var gains = new float[layout.ChannelCount];
        panner.Gains(channel, gains);
        return gains;
    }

    static void AssertGains(float[] expected, float[] actual) {
        Assert.Equal(expected.Length, actual.Length);
        for (var c = 0; c < expected.Length; c++) {
            Assert.True(Math.Abs(expected[c] - actual[c]) < 1e-5, $"channel {c}: {actual[c]}, expected {expected[c]}");
        }
    }

    [Fact]
    public void A_point_at_a_speaker_feeds_only_that_speaker() {
        // 5.1: L R C LFE Ls Rs.
        AssertGains([0, 0, 1, 0, 0, 0], Gains(SpeakerFormat.Surround51, SurroundPannerSettings.Default));
        AssertGains([0, 0, 0, 0, 1, 0], Gains(SpeakerFormat.Surround51, SurroundPannerSettings.Default with { Angle = 110 }));
        AssertGains([0, 1, 0, 0, 0, 0], Gains(SpeakerFormat.Surround51, SurroundPannerSettings.Default with { Angle = -30 }));
    }

    [Fact]
    public void Between_two_speakers_the_pan_is_constant_power() {
        // Halfway between L (30°) and Ls (110°): cos 45°, sin 45°.
        AssertGains([Half, 0, 0, 0, Half, 0], Gains(SpeakerFormat.Surround51, SurroundPannerSettings.Default with { Angle = 70 }));
        // A quarter of the way from C (0°) to L (30°): cos 22.5°, sin 22.5°.
        AssertGains(
            [MathF.Sin(MathF.PI / 8), 0, MathF.Cos(MathF.PI / 8), 0, 0, 0],
            Gains(SpeakerFormat.Surround51, SurroundPannerSettings.Default with { Angle = 7.5f })
        );
        // Behind, between Ls (110°) and Rs (−110°): straight back is halfway.
        AssertGains([0, 0, 0, 0, Half, Half], Gains(SpeakerFormat.Surround51, SurroundPannerSettings.Default with { Angle = 180 }));
    }

    [Fact]
    public void The_centre_level_moves_the_centre_into_a_phantom() {
        AssertGains(
            [Half, Half, 0, 0, 0, 0],
            Gains(SpeakerFormat.Surround51, SurroundPannerSettings.Default with { CentreGain = 0 })
        );
        // Half the centre's power: c² = 0.5, so C = √0.5 and L = R = √(0.5 · 0.5).
        AssertGains(
            [0.5f, 0.5f, Half, 0, 0, 0],
            Gains(SpeakerFormat.Surround51, SurroundPannerSettings.Default with { CentreGain = Half })
        );
    }

    [Fact]
    public void Diversity_zero_spreads_over_every_speaker() {
        var g = 1 / MathF.Sqrt(5);
        AssertGains([g, g, g, 0, g, g], Gains(SpeakerFormat.Surround51, SurroundPannerSettings.Default with { Diversity = 0 }));
        // Half way: power half the point's and half the spread's.
        var mixed = Gains(SpeakerFormat.Surround51, SurroundPannerSettings.Default with { Diversity = 0.5f });
        Assert.Equal(MathF.Sqrt(0.5f + 0.5f / 5), mixed[2], 5);
        Assert.Equal(MathF.Sqrt(0.5f / 5), mixed[0], 5);
    }

    [Fact]
    public void Elevation_moves_into_the_upper_layer() {
        // 7.1.4: L R C LFE Lss Rss Lrs Rrs Ltf Rtf Ltr Rtr.
        var at45 = SurroundPannerSettings.Default with { Angle = 45 };
        AssertGains([0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0], Gains(SpeakerFormat.Surround714, at45 with { Elevation = 30 }));
        // Half way up the layer: cos²/sin² of 45° between the ear layer's pair (L at 30°, Lss at 90°) and Ltf.
        var mid = Gains(SpeakerFormat.Surround714, at45 with { Elevation = 15 });
        Assert.Equal(Half, mid[8], 5);
        Assert.Equal(0.5f, mid[0] * mid[0] + mid[4] * mid[4], 5);
        // Straight up: every upper speaker equally.
        AssertGains(
            [0, 0, 0, 0, 0, 0, 0, 0, 0.5f, 0.5f, 0.5f, 0.5f],
            Gains(SpeakerFormat.Surround714, at45 with { Elevation = 90 })
        );
        // No upper layer: elevation is ignored.
        AssertGains(
            Gains(SpeakerFormat.Surround51, SurroundPannerSettings.Default),
            Gains(SpeakerFormat.Surround51, SurroundPannerSettings.Default with { Elevation = 60 })
        );
    }

    [Fact]
    public void A_stereo_input_spreads_its_channels_around_the_puck() {
        var settings = SurroundPannerSettings.Default with { Spread = 60 };
        AssertGains([1, 0, 0, 0, 0, 0], Gains(SpeakerFormat.Surround51, settings, 2, 0));
        AssertGains([0, 1, 0, 0, 0, 0], Gains(SpeakerFormat.Surround51, settings, 2, 1));
        var behind = settings with { Angle = 180, Spread = 140 };
        AssertGains([0, 0, 0, 0, 1, 0], Gains(SpeakerFormat.Surround51, behind, 2, 1));
        AssertGains([0, 0, 0, 0, 0, 1], Gains(SpeakerFormat.Surround51, behind, 2, 0));
    }

    [Fact]
    public void Every_setting_has_unit_power() {
        var random = new Random(7);
        foreach (var format in Enum.GetValues<SpeakerFormat>()) {
            var layout = SpeakerLayout.Of(format);
            for (var i = 0; i < 50; i++) {
                var settings = new SurroundPannerSettings(
                    random.NextSingle() * 360 - 180,
                    random.NextSingle(),
                    random.NextSingle() * 90,
                    random.NextSingle(),
                    random.NextSingle(),
                    random.NextSingle() * 180
                );
                foreach (var channel in new[] { 0, 1 }) {
                    var gains = Gains(format, settings, 2, channel);
                    var power = Enumerable.Range(0, gains.Length).Where(c => c != layout.LfeIndex).Sum(c => gains[c] * gains[c]);
                    Assert.Equal(1, power, 4);
                    Assert.All(gains, static g => Assert.True(g >= 0));
                }
            }
        }
    }

    [Fact]
    public void Changes_ramp_over_a_block_without_steps() {
        var layout = SpeakerLayout.Of(SpeakerFormat.Surround51);
        var panner = new SurroundPanner();
        panner.Prepare(layout, 1, Rate, 128);
        panner.Set(SurroundPannerSettings.Default);
        var input = new float[128];
        input.AsSpan().Fill(1);
        var output = new float[6 * 128];
        panner.Process(new(input, 1, 128), new(output, 6, 128));
        Assert.All(output.AsSpan(2 * 128, 128).ToArray(), static s => Assert.Equal(1, s, 6));

        panner.Set(SurroundPannerSettings.Default with { Angle = 30 });
        Array.Clear(output);
        panner.Process(new(input, 1, 128), new(output, 6, 128));
        var centre = output.AsSpan(2 * 128, 128);
        var left = output.AsSpan(0, 128);
        for (var n = 1; n < 128; n++) {
            Assert.True(Math.Abs(centre[n] - centre[n - 1]) <= 1.01f / 128, $"centre step at {n}");
            Assert.True(Math.Abs(left[n] - left[n - 1]) <= 1.01f / 128, $"left step at {n}");
        }

        Assert.Equal(0, centre[127], 6);
        Assert.Equal(1, left[127], 6);

        // The next block holds.
        Array.Clear(output);
        panner.Process(new(input, 1, 128), new(output, 6, 128));
        Assert.All(output.AsSpan(0, 128).ToArray(), static s => Assert.Equal(1, s, 6));

        // After a reset the settings apply at once.
        panner.Set(SurroundPannerSettings.Default);
        panner.Reset();
        Array.Clear(output);
        panner.Process(new(input, 1, 128), new(output, 6, 128));
        Assert.Equal(1, output[2 * 128], 6);
    }

    [Fact]
    public void A_puck_at_the_left_side_of_7_1_4_puts_the_track_there_and_the_lfe_send_low_passed()
    {
        // The golden path's case: everything in the left side speaker (M+090), every other at least 20 dB down.
        var layout = SpeakerLayout.Of(SpeakerFormat.Surround714);
        var panner = new SurroundPanner();
        const int block = 256;
        panner.Prepare(layout, 1, Rate, block);
        panner.Set(SurroundPannerSettings.Default with { Angle = 90, LfeGain = 1 });
        var frames = 48_000;
        var low = new float[frames];
        var high = new float[frames];
        for (var n = 0; n < frames; n++) {
            low[n] = MathF.Sin(2 * MathF.PI * 40 * n / Rate);
            high[n] = MathF.Sin(2 * MathF.PI * 2000 * n / Rate);
        }

        var lowOut = Render(panner, layout, low, block);
        panner.Reset();
        var highOut = Render(panner, layout, high, block);
        var side = layout.Label(4) == "M+090" ? 4 : -1;
        Assert.Equal(4, side);
        var sideRms = Support.Analysis.Rms(lowOut[side].AsSpan(4800));
        for (var c = 0; c < layout.ChannelCount; c++) {
            if (c == side || c == layout.LfeIndex) {
                continue;
            }

            Assert.True(Support.Analysis.Rms(lowOut[c].AsSpan(4800)) < sideRms * 0.1, $"channel {c}");
        }

        // The LFE: 40 Hz through (Butterworth at 120 Hz: −0.06 dB), 2 kHz down by 4 × 24 dB/octave.
        var lfe = layout.LfeIndex;
        Assert.Equal(sideRms, Support.Analysis.Rms(lowOut[lfe].AsSpan(4800)), 0.01);
        Assert.True(Support.Analysis.Rms(highOut[lfe].AsSpan(4800)) < Support.Analysis.Rms(highOut[side].AsSpan(4800)) * 1e-3);
    }

    static float[][] Render(SurroundPanner panner, SpeakerLayout layout, float[] signal, int block) {
        var channels = layout.ChannelCount;
        var result = new float[channels][];
        for (var c = 0; c < channels; c++) {
            result[c] = new float[signal.Length];
        }

        var output = new float[channels * block];
        for (var start = 0; start + block <= signal.Length; start += block) {
            Array.Clear(output);
            panner.Process(new(signal.AsSpan(start, block), 1, block), new(output, channels, block));
            for (var c = 0; c < channels; c++) {
                output.AsSpan(c * block, block).CopyTo(result[c].AsSpan(start));
            }
        }

        return result;
    }

    [Fact]
    public void Processing_allocates_nothing() {
        var layout = SpeakerLayout.Of(SpeakerFormat.Surround714);
        var panner = new SurroundPanner();
        panner.Prepare(layout, 2, Rate, 256);
        var input = new float[2 * 256];
        var output = new float[12 * 256];
        var angle = 0f;
        Assert.Equal(
            0,
            AllocationProbe.Measure(() => {
                panner.Set(SurroundPannerSettings.Default with { Angle = angle += 7, LfeGain = 0.5f, Elevation = 20 });
                panner.Process(new(input, 2, 256), new(output, 12, 256));
            })
        );
    }
}
