using Anode.Dsp.Metering;
using Anode.Ipc;
using Anode.Ipc.Telemetry;

namespace Anode.Engine.Tests;

/// <summary>A strip's meter options against signals with known levels: a sine's peak, RMS and loudness.</summary>
public sealed class StripMeterTests {
    const int Rate = 48_000;

    /// <summary>Four seconds of a 997 Hz sine of amplitude <paramref name="amplitude" /> through the meter, block by block.</summary>
    static void Feed(Strip strip, float amplitude, int block = 64) {
        var left = new float[block];
        var phase = 0;
        for (var at = 0; at < 4 * Rate; at += block) {
            for (var i = 0; i < block; i++) {
                left[i] = amplitude * MathF.Sin(2 * MathF.PI * 997f * phase++ / Rate);
            }

            strip.MeterSignal(left, left);
        }
    }

    static float Db(float gain) => 20 * MathF.Log10(gain);

    static Strip Meter(EngineMeterType type, EngineMeterTap tap = EngineMeterTap.PostFader) {
        var strip = new Strip(new(1f, 0f, false, false, MeterType: type, MeterTap: tap));
        strip.PrepareMeter(Rate);
        return strip;
    }

    [Fact]
    public void A_sines_RMS_reads_3_dB_under_its_peak_whichever_window_the_type_has() {
        foreach (var type in new[] {
                     EngineMeterType.Rms, EngineMeterType.K20, EngineMeterType.K14, EngineMeterType.K12
                 }) {
            var strip = Meter(type);
            Feed(strip, 0.5f);
            var (left, right, _, levels) = strip.MeterLevels();
            Assert.Equal(MeterEntry.HasRms, levels);
            Assert.Equal(Db(0.5f) - 3.01f, Db(left), 0.05f);
            Assert.Equal(Db(0.5f) - 3.01f, Db(right), 0.05f);
            Assert.Equal(0.5f, strip.TakePeaks().Left, 0.001f);
        }
    }

    [Theory]
    [InlineData(EngineMeterType.Rms, 0.3)]
    [InlineData(EngineMeterType.K20, 0.6)]
    public void An_RMS_window_settles_at_its_own_pace(EngineMeterType type, double seconds) {
        var strip = Meter(type);
        var block = new float[4_800];
        block.AsSpan().Fill(1f);
        strip.MeterSignal(block, block); // a tenth of a second of DC: the mean square rises 1 − e^(−t/τ)
        var expected = Math.Sqrt(1 - Math.Exp(-0.1 / seconds));
        Assert.Equal(expected, strip.MeterLevels().RmsLeft, 1e-5);
    }

    [Fact]
    public void Loudness_reads_a_full_scale_sine_in_one_channel_at_minus_3_LUFS() {
        // BS.1770's reference: 0 dBFS at 997 Hz in one channel is −3.01 LKFS. A mono signal counts as that one channel.
        var strip = Meter(EngineMeterType.Lufs);
        Feed(strip, 1f);
        var (_, _, lufs, levels) = strip.MeterLevels();
        Assert.Equal(MeterEntry.HasLufs, levels);
        Assert.Equal(-3.01f, lufs, 0.1f);
    }

    [Fact]
    public void Loudness_of_a_stereo_signal_counts_both_channels() {
        var strip = Meter(EngineMeterType.Lufs);
        var left = new float[64];
        var right = new float[64];
        var phase = 0;
        for (var at = 0; at < 4 * Rate; at += 64) {
            for (var i = 0; i < 64; i++) {
                left[i] = right[i] = MathF.Sin(2 * MathF.PI * 997f * phase++ / Rate);
            }

            strip.MeterSignal(left, right);
        }

        Assert.Equal(0f, strip.MeterLevels().Lufs, 0.1f);
    }

    [Fact]
    public void A_strip_that_asked_for_nothing_publishes_nothing_more_and_silence_has_a_floor() {
        var strip = Meter(EngineMeterType.Peak);
        Feed(strip, 0.5f);
        Assert.Equal(0, strip.MeterLevels().Levels);
        Assert.Equal(LoudnessMeter.FloorLufs, Meter(EngineMeterType.Lufs).MeterLevels().Lufs);
    }
}
