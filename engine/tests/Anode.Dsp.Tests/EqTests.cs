using Anode.Dsp.Effects.Eq;
using Anode.Dsp.Filters;
using Anode.Dsp.Tests.Support;
using static Anode.Dsp.Tests.Support.Analysis;

namespace Anode.Dsp.Tests;

/// <summary>The Channel EQ's responses, measured from impulse responses against the textbook designs.</summary>
public sealed class EqTests {
    static readonly double[] Probes = [20, 50, 100, 200, 400, 800, 1_000, 1_600, 3_000, 5_000, 8_000, 12_000, 16_000];

    static float[] ImpulseResponse(params (int, float)[] parameters) {
        using var eq = Fx.Create(ChannelEq.Id, parameters);
        return Fx.Run(eq, Fx.Impulse(32_768)).Left;
    }

    public static TheoryData<int, double, double, double> Bands => new() {
        { 0, 80, 6, 0.71 }, // low shelf
        { 0, 300, -9, 1 },
        { 1, 250, 6, 1 }, // peaks
        { 2, 1_000, -12, 4 },
        { 3, 3_000, 9, 0.5 },
        { 4, 10_000, -6, 2 },
        { 5, 8_000, 4, 0.71 }, // high shelf
        { 5, 2_000, -10, 1 }
    };

    [Theory]
    [MemberData(nameof(Bands))]
    public void Bells_and_shelves_match_the_cookbook(int band, double frequency, double gainDb, double q) {
        var (on, f, g, quality, type) = band switch {
            0 => (ChannelEq.LowShelfOn, ChannelEq.LowShelfFrequency, ChannelEq.LowShelfGain, ChannelEq.LowShelfQ,
                BiquadType.LowShelf),
            1 => (ChannelEq.Peak1On, ChannelEq.Peak1Frequency, ChannelEq.Peak1Gain, ChannelEq.Peak1Q, BiquadType.Peak),
            2 => (ChannelEq.Peak2On, ChannelEq.Peak2Frequency, ChannelEq.Peak2Gain, ChannelEq.Peak2Q, BiquadType.Peak),
            3 => (ChannelEq.Peak3On, ChannelEq.Peak3Frequency, ChannelEq.Peak3Gain, ChannelEq.Peak3Q, BiquadType.Peak),
            4 => (ChannelEq.Peak4On, ChannelEq.Peak4Frequency, ChannelEq.Peak4Gain, ChannelEq.Peak4Q, BiquadType.Peak),
            _ => (ChannelEq.HighShelfOn, ChannelEq.HighShelfFrequency, ChannelEq.HighShelfGain, ChannelEq.HighShelfQ,
                BiquadType.HighShelf)
        };
        var ir = ImpulseResponse((on, 1), (f, (float)frequency), (g, (float)gainDb), (quality, (float)q));
        var reference = BiquadCoefficients.Design(type, Fx.Rate, (float)frequency, (float)q, (float)gainDb);
        foreach (var probe in Probes.Append(frequency)) {
            var measured = ToDb(MagnitudeAt(ir, probe, Fx.Rate));
            Assert.Equal(ToDb(reference.MagnitudeAt(probe, Fx.Rate)), measured, 0.02);
        }

        // At the centre of a bell, and far past a shelf, the gain is the setting.
        var at = type switch {
            BiquadType.Peak => frequency,
            BiquadType.LowShelf => 10,
            _ => 23_000
        };
        Assert.Equal(gainDb, ToDb(MagnitudeAt(ir, at, Fx.Rate)), type == BiquadType.Peak ? 0.01 : 0.3);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 4)]
    [InlineData(2, 6)]
    [InlineData(3, 8)]
    public void Cuts_are_butterworth_of_the_slope(int slope, int order) {
        const double cutoff = 200;
        var low = ImpulseResponse(
            (ChannelEq.HighPassOn, 1),
            (ChannelEq.HighPassFrequency, (float)cutoff),
            (ChannelEq.HighPassSlope, slope)
        );
        const double top = 4_000;
        var high = ImpulseResponse(
            (ChannelEq.LowPassOn, 1),
            (ChannelEq.LowPassFrequency, (float)top),
            (ChannelEq.LowPassSlope, slope)
        );
        foreach (var probe in Probes) {
            // The bilinear transform of an analog Butterworth, prewarped at the cutoff.
            var w = Math.Tan(Math.PI * probe / Fx.Rate);
            var highPass = 1 / Math.Sqrt(1 + Math.Pow(Math.Tan(Math.PI * cutoff / Fx.Rate) / w, 2 * order));
            var lowPass = 1 / Math.Sqrt(1 + Math.Pow(w / Math.Tan(Math.PI * top / Fx.Rate), 2 * order));
            Assert.Equal(Math.Max(ToDb(highPass), -150), Math.Max(ToDb(MagnitudeAt(low, probe, Fx.Rate)), -150), 0.05);
            if (lowPass > 1e-4) {
                Assert.Equal(ToDb(lowPass), ToDb(MagnitudeAt(high, probe, Fx.Rate)), 0.05);
            }
        }

        Assert.Equal(-3.01, ToDb(MagnitudeAt(low, cutoff, Fx.Rate)), 0.02);
        Assert.Equal(-10 * Math.Log10(1 + Math.Pow(2, 2 * order)), ToDb(MagnitudeAt(low, cutoff / 2, Fx.Rate)), 0.1);
    }

    [Fact]
    public void Bands_combine_and_the_output_gain_applies() {
        var ir = ImpulseResponse(
            (ChannelEq.Peak1Gain, 6),
            (ChannelEq.Peak1Frequency, 100),
            (ChannelEq.Peak4Gain, -6),
            (ChannelEq.Peak4Frequency, 8_000),
            (ChannelEq.Output, -3)
        );
        Assert.Equal(3, ToDb(MagnitudeAt(ir, 100, Fx.Rate)), 0.1);
        Assert.Equal(-9, ToDb(MagnitudeAt(ir, 8_000, Fx.Rate)), 0.1);
        Assert.Equal(-3, ToDb(MagnitudeAt(ir, 1_000, Fx.Rate)), 0.2);
    }

    [Fact]
    public void Flat_by_default_and_off_bands_do_nothing() {
        var flat = ImpulseResponse();
        Assert.Equal(1, flat[0]);
        Assert.Equal(0, Peak(flat.AsSpan(1)));
        var off = ImpulseResponse((ChannelEq.Peak2Gain, 12), (ChannelEq.Peak2On, 0));
        Assert.Equal(1, off[0]);
    }

    [Fact]
    public void Sweeping_a_band_does_not_click() {
        using var eq = Fx.Create(ChannelEq.Id, (ChannelEq.Peak2Gain, 12), (ChannelEq.Peak2Q, 2));
        var sine = TestWav.Sine(440, (int)Fx.Rate, 1, 0.5f);
        var output = new float[sine.Length];
        for (var at = 0; at < sine.Length; at += 64) {
            eq.SetParameter(ChannelEq.Peak2Frequency, 200 + 4_000 * (at / 48_000f));
            var l = sine[at..(at + 64)];
            var r = (float[])l.Clone();
            eq.Process(l, r, default, default);
            l.CopyTo(output, at);
        }

        // No sample jumps further than a 440 Hz sine at +12 dB can move in one sample.
        var limit = 2 * Math.PI * 440 / Fx.Rate * 0.5 * 4 * 1.2;
        for (var i = 1; i < output.Length; i++) {
            Assert.True(Math.Abs(output[i] - output[i - 1]) < limit, $"step at {i}");
        }
    }

    [Fact]
    public void The_analyzer_reads_a_tone_in_its_band_and_the_reading_is_the_output_peak() {
        using var eq = (ChannelEq)Fx.Create(ChannelEq.Id);
        var sine = TestWav.Sine(1_000, (int)Fx.Rate, 0.5, 0.5f);
        Fx.Run(eq, sine);
        Assert.Equal(-6.02, eq.Reading, 0.05);
        var bands = new float[30];
        eq.ReadSpectrum(bands);
        // 30 bands over 20 Hz … 20 kHz: 1 kHz is in band ⌊30 · ln(50) / ln(1000)⌋ = 16.
        Assert.Equal(16, Array.IndexOf(bands, bands.Max()));
        Assert.InRange(bands[16], -6.02 - 1.45, -6.02 + 0.05); // a Hann window scallops by up to 1.42 dB
        Assert.True(bands[5] < -60);
    }
}
