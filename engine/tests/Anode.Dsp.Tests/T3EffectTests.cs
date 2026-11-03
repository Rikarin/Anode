using Anode.Dsp.Effects;
using Anode.Dsp.Effects.Drive;
using Anode.Dsp.Effects.Dynamics;
using Anode.Dsp.Effects.Surround;
using Anode.Dsp.Spatial;
using Anode.Dsp.Tests.Spatial;
using Anode.Dsp.Tests.Support;
using static Anode.Dsp.Tests.Support.Analysis;

namespace Anode.Dsp.Tests;

/// <summary>
///     The T3 effects (docs/12-phase-11.md, exit criterion step 6): Saturation's THD rising with drive, the Down
///     Mixer's BS.775 coefficients within 0.1 dB, Binaural Post-Processing's interaural differences at 90° left, the
///     dynamics' channel link.
/// </summary>
public sealed class T3EffectTests(ITestOutputHelper output) {
    const float Rate = Fx.Rate;

    static float[] Sine(double hz, double amplitude, int frames) =>
        [..Enumerable.Range(0, frames).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / Rate)))];

    /// <summary>
    ///     The level of harmonic <paramref name="k" /> of 1 kHz relative to the fundamental (dB) in a steady 16,384-sample
    ///     stretch, by a Blackman-Harris window (whose sidelobes stay under −92 dB).
    /// </summary>
    static double Harmonic(float[] signal, int k) {
        const int size = 16_384;
        var data = new System.Numerics.Complex[size];
        var start = signal.Length - size;
        for (var i = 0; i < size; i++) {
            var x = i / (double)size;
            var w = 0.35875
                - 0.48829 * Math.Cos(2 * Math.PI * x)
                + 0.14128 * Math.Cos(4 * Math.PI * x)
                - 0.01168 * Math.Cos(6 * Math.PI * x);
            data[i] = signal[start + i] * w;
        }

        Fft.Forward(data);
        return 10 * Math.Log10(Band(1_000 * k) / Band(1_000));

        double Band(double hz) {
            var centre = (int)Math.Round(hz * size / Rate);
            var sum = 0.0;
            for (var b = centre - 4; b <= centre + 4; b++) {
                sum += data[b].Magnitude * data[b].Magnitude;
            }

            return sum;
        }
    }

    /// <summary>Total harmonic distortion (harmonics 2 … 10 over the fundamental, dB).</summary>
    static double Thd(float[] signal) =>
        10 * Math.Log10(Enumerable.Range(2, 9).Sum(k => Math.Pow(10, Harmonic(signal, k) / 10)));

    [Theory]
    [InlineData(Saturation.Tube)]
    [InlineData(Saturation.Tape)]
    [InlineData(Saturation.Transformer)]
    [InlineData(Saturation.Modern)]
    public void Saturation_thd_rises_with_the_drive(int model) {
        var tone = Sine(1_000, 0.25, 48_000);
        var thd = new[] { 10f, 40f, 80f }.Select(drive => {
                    using var effect =
                        T3Kit.Effect(Saturation.Id, (Saturation.Model, model), (Saturation.Drive, drive));
                    return Thd(Fx.Run(effect, tone).Left);
                }
            )
            .ToArray();
        output.WriteLine(
            $"model {model}: THD {string.Join(", ", thd.Select(static t => $"{t:0.0} dB"))} at 10, 40, 80 %"
        );
        Assert.True(thd[0] < thd[1] && thd[1] < thd[2], string.Join(", ", thd));
        Assert.True(thd[2] > -40, "full drive is audibly saturated");
    }

    [Fact]
    public void Saturation_models_have_their_harmonics_and_keep_their_level() {
        var tone = Sine(1_000, 0.25, 48_000);

        (double Even, double Odd, double Level) Measure(int model) {
            using var effect = T3Kit.Effect(Saturation.Id, (Saturation.Model, model), (Saturation.Drive, 50));
            var wet = Fx.Run(effect, tone).Left;
            return (Harmonic(wet, 2), Harmonic(wet, 3), ToDb(Rms(wet.AsSpan(24_000)) / Rms(tone.AsSpan(24_000))));
        }

        var tube = Measure(Saturation.Tube);
        var tape = Measure(Saturation.Tape);
        var modern = Measure(Saturation.Modern);
        output.WriteLine($"tube {tube}, tape {tape}, modern {modern}");
        Assert.True(tube.Even > tape.Even + 20, "the tube's bias makes even harmonics");
        Assert.True(tube.Even > -40);
        Assert.True(tape.Odd > tape.Even + 20 && modern.Odd > modern.Even + 20);
        foreach (var (_, _, level) in new[] { tube, tape, modern }) {
            Assert.InRange(level, -1.5, 1.5); // made up to a −12 dBFS signal's level
        }
    }

    [Fact]
    public void Saturation_tone_tilts_and_output_and_mix_scale() {
        static double Band(float tone, double hz) {
            using var effect = T3Kit.Effect(Saturation.Id, (Saturation.Tone, tone), (Saturation.Drive, 0));
            var signal = Sine(hz, 0.05, 24_000);
            return ToDb(Rms(Fx.Run(effect, signal).Left.AsSpan(12_000)) / Rms(signal.AsSpan(12_000)));
        }

        Assert.True(Band(100, 8_000) > 4 && Band(100, 100) < -4);
        Assert.True(Band(-100, 8_000) < -4 && Band(-100, 100) > 4);
        Assert.InRange(Band(0, 1_000), -0.3, 0.3); // the make-up is set at −12 dBFS

        var tone = Sine(1_000, 0.1, 24_000);
        using var louder = T3Kit.Effect(Saturation.Id, (Saturation.Output, 6), (Saturation.Drive, 0));
        Assert.InRange(ToDb(Rms(Fx.Run(louder, tone).Left.AsSpan(12_000)) / Rms(tone.AsSpan(12_000))), 5.8, 6.2);
        using var half = T3Kit.Effect(Saturation.Id, (Saturation.Mix, 50), (Saturation.Drive, 100));
        using var full = T3Kit.Effect(Saturation.Id, (Saturation.Drive, 100));
        // Half the mix is half the dry signal and half the wet.
        var blend = Fx.Run(half, tone).Left;
        var wet = Fx.Run(full, tone).Left;
        Assert.True(NullResidualDb(blend, [..tone.Select((x, i) => 0.5f * (x + wet[i]))]) < -100);
    }

    /// <summary>The gain from input channel <paramref name="input" /> to each output, measured with a signal there alone.</summary>
    static float[] Column(IFormatEffect effect, SpeakerFormat from, int input) {
        var layout = SpeakerLayout.Of(from);
        var outputs = SpeakerLayout.Of(effect.OutputFormat(from)).ChannelCount;
        var inBuffer = new float[layout.ChannelCount * 64];
        inBuffer.AsSpan(input * 64, 64).Fill(0.5f);
        var outBuffer = new float[outputs * 64];
        effect.Process(new(inBuffer, layout.ChannelCount, 64), new(outBuffer, outputs, 64));
        effect.Process(new(inBuffer, layout.ChannelCount, 64), new(outBuffer, outputs, 64)); // past the LFE's ramp
        return [..Enumerable.Range(0, outputs).Select(o => outBuffer[o * 64 + 63] / 0.5f)];
    }

    static IFormatEffect DownMixer(SpeakerFormat from, SpeakerFormat to, float lfeDb = -144) {
        var effect = new DownMixerInsert();
        effect.SetParameter(DownMixerInsert.Format, (int)to);
        effect.SetParameter(DownMixerInsert.Lfe, lfeDb);
        effect.Prepare(SpeakerLayout.Of(from), SpeakerLayout.Of(effect.OutputFormat(from)), Rate, 1_024);
        return effect;
    }

    static void Within(float expected, float actual) {
        if (expected == 0) {
            Assert.Equal(0, actual, 6);
        } else {
            Assert.InRange(20 * Math.Log10(actual / expected), -0.1, 0.1);
        }
    }

    [Fact]
    public void The_down_mixer_folds_5_1_to_stereo_by_bs_775() {
        using var effect = DownMixer(SpeakerFormat.Surround51, SpeakerFormat.Stereo);
        var h = MathF.Sqrt(0.5f);
        // L, R, C, LFE, Ls, Rs → L' = L + 0.7071 C + 0.7071 Ls (the LFE dropped), R' likewise.
        float[][] expected = [[1, 0], [0, 1], [h, h], [0, 0], [h, 0], [0, h]];
        for (var c = 0; c < 6; c++) {
            var column = Column(effect, SpeakerFormat.Surround51, c);
            Within(expected[c][0], column[0]);
            Within(expected[c][1], column[1]);
        }

        // With the LFE at 0 dB it joins the fronts at −3 dB; at −10 dB, 10 dB lower.
        using var lfe = DownMixer(SpeakerFormat.Surround51, SpeakerFormat.Stereo, 0);
        Within(h, Column(lfe, SpeakerFormat.Surround51, 3)[0]);
        using var lower = DownMixer(SpeakerFormat.Surround51, SpeakerFormat.Stereo, -10);
        Within(h * MathF.Pow(10, -0.5f), Column(lower, SpeakerFormat.Surround51, 3)[1]);
    }

    [Fact]
    public void The_down_mixer_folds_7_1_4_by_the_documented_matrices() {
        // docs/spatial/README.md: 7.1.4 → 2.0, L' = L + 0.7071 C + 0.5 (Lss + Lrs) + 0.7071 Ltf + 0.5 Ltr.
        using var stereo = DownMixer(SpeakerFormat.Surround714, SpeakerFormat.Stereo);
        float[] left = [1, 0, 0.7071f, 0, 0.5f, 0, 0.5f, 0, 0.7071f, 0, 0.5f, 0];
        float[] right = [0, 1, 0.7071f, 0, 0, 0.5f, 0, 0.5f, 0, 0.7071f, 0, 0.5f];
        for (var c = 0; c < 12; c++) {
            var column = Column(stereo, SpeakerFormat.Surround714, c);
            Within(left[c], column[0]);
            Within(right[c], column[1]);
        }

        // And to 5.1: the sides and rears into the surrounds at −3 dB each, the top fronts into L/R and the top rears
        // into the surrounds at −3 dB (shared).
        using var five = DownMixer(SpeakerFormat.Surround714, SpeakerFormat.Surround51);
        Assert.Equal(SpeakerFormat.Surround51, five.OutputFormat(SpeakerFormat.Surround714));
        var downmixer = new Downmixer(
            SpeakerLayout.Of(SpeakerFormat.Surround714),
            SpeakerLayout.Of(SpeakerFormat.Surround51)
        );
        for (var c = 0; c < 12; c++) {
            var column = Column(five, SpeakerFormat.Surround714, c);
            for (var o = 0; o < 6; o++) {
                Within(downmixer.Gain(o, c), column[o]);
            }
        }

        Within(MathF.Sqrt(0.5f), Column(five, SpeakerFormat.Surround714, 4)[4]);
        Within(1, Column(five, SpeakerFormat.Surround714, 3)[3]); // the LFE to the LFE
    }

    [Fact]
    public void The_down_mixer_passes_a_layout_it_cannot_narrow() {
        using var effect = DownMixer(SpeakerFormat.Stereo, SpeakerFormat.Surround51);
        Assert.Equal(SpeakerFormat.Stereo, effect.OutputFormat(SpeakerFormat.Stereo));
        Assert.Equal([1f, 0f], Column(effect, SpeakerFormat.Stereo, 0));
        Assert.Equal([0f, 1f], Column(effect, SpeakerFormat.Stereo, 1));
    }

    [Fact]
    public void Binaural_post_processing_puts_90_degrees_left_in_the_left_ear() {
        var hrtfs = HrtfSet.Load(HrtfSet.Find(Ear.RepositoryRoot())!);

        (float[] Left, float[] Right) Impulse(int speaker) {
            using var effect = new BinauralInsert();
            effect.UseHrtfs(hrtfs);
            var layout = SpeakerLayout.Of(SpeakerFormat.Surround714);
            effect.Prepare(layout, SpeakerLayout.Of(SpeakerFormat.Stereo), Rate, 128);
            var left = new float[1_024];
            var right = new float[1_024];
            var inBuffer = new float[12 * 128];
            var outBuffer = new float[2 * 128];
            for (var at = 0; at < 1_024; at += 128) {
                Array.Clear(inBuffer);
                inBuffer[speaker * 128] = at == 0 ? 1 : 0;
                effect.Process(new(inBuffer, 12, 128), new(outBuffer, 2, 128));
                outBuffer.AsSpan(0, 128).CopyTo(left.AsSpan(at));
                outBuffer.AsSpan(128, 128).CopyTo(right.AsSpan(at));
            }

            return (left, right);
        }

        var side = Impulse(4); // M+090
        var ild = BinauralTests.IldDb(side);
        var itd = BinauralTests.ItdMs(side, Rate);
        output.WriteLine($"90° left: ILD {ild:0.0} dB, ITD {itd:0.000} ms");
        Assert.True(ild >= 6);
        Assert.InRange(itd, 0.5, 0.8);
        var mirror = Impulse(5);
        Assert.True(BinauralTests.IldDb(mirror) <= -6);
        var ahead = Impulse(2);
        Assert.InRange(BinauralTests.IldDb(ahead), -1.5, 1.5);
    }

    [Fact]
    public void Binaural_post_processing_without_hrtfs_down_mixes() {
        using var effect = new BinauralInsert();
        var layout = SpeakerLayout.Of(SpeakerFormat.Surround51);
        effect.Prepare(layout, SpeakerLayout.Of(SpeakerFormat.Stereo), Rate, 1_024);
        Within(MathF.Sqrt(0.5f), Column(effect, SpeakerFormat.Surround51, 2)[0]);
        Within(1, Column(effect, SpeakerFormat.Surround51, 0)[0]);
    }

    [Fact]
    public void The_dynamics_link_by_speaker_group() {
        Assert.Equal("link", Compressor.Info.Parameters[Compressor.Link].Id);
        Assert.Equal("link", Limiter.Info.Parameters[Limiter.Link].Id);
        Assert.Equal(ChannelLink.Names, Compressor.Info.Parameters[Compressor.Link].ValueNames);
        var layout = SpeakerLayout.Of(SpeakerFormat.Surround714);
        Assert.Equal(new int[12], ChannelLink.Groups(layout, ChannelLink.All));
        Assert.Equal(
            [0, 0, 0, 1, 2, 2, 2, 2, 3, 3, 3, 3],
            ChannelLink.Groups(layout, ChannelLink.ByGroup)
        );
        Assert.Equal(SpeakerGroup.Front, ChannelLink.GroupOf(SpeakerLayout.Of(SpeakerFormat.Surround916), 8)); // a wide

        // On a stereo strip the link changes nothing.
        var (music, right) = Fx.Music(12_000);
        foreach (var (id, link) in new[] { (Compressor.Id, Compressor.Link), (Limiter.Id, Limiter.Link) }) {
            using var all = Fx.Create(id);
            using var groups = Fx.Create(id, (link, ChannelLink.ByGroup));
            Assert.Equal(-300, NullResidualDb(Fx.Run(all, music, right).Left, Fx.Run(groups, music, right).Left));
        }
    }
}
