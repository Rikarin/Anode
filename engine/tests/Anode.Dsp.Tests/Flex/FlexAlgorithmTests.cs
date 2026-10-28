using Anode.Dsp.Flex;
using static Anode.Dsp.Tests.Flex.FlexKit;

namespace Anode.Dsp.Tests.Flex;

/// <summary>The seven flex algorithms (docs/06-phase-5.md, "Flex algorithms"; docs/flex/README.md).</summary>
public sealed class FlexAlgorithmTests(ITestOutputHelper output) {
    // Clicks every 250 ms over 6 s of file, and a map that slows to 0.8×, speeds to 1.33× and 1.5×, then slows to 0.9×
    // and 0.6×.
    const int FileFrames = 6 * Rate;
    static readonly long[] ClickFrames = [..Enumerable.Range(0, 23).Select(static i => 6_000L + i * 12_000)];

    static readonly FlexWarpPoint[] Ramps = Warp(
        (0, 0),
        (22_500, 18_000),
        (48_000, 42_000),
        (84_000, 90_000),
        (124_000, 150_000),
        (164_000, 186_000),
        (224_000, 222_000),
        (270_000, 258_000)
    );

    public static TheoryData<FlexKind> All() => [..Algorithms];

    public static TheoryData<FlexKind> Timed() => [
        FlexKind.Polyphonic, FlexKind.Monophonic, FlexKind.Pitch, FlexKind.Slicing, FlexKind.Rhythmic,
        FlexKind.Speed
    ];

    static void SkipWithoutStretch(FlexKind kind) =>
        Assert.SkipWhen(
            Stretch.Contains(kind) && !FlexRenderers.StretchAvailable,
            "an_stretch is not built (engine/native/build.sh)."
        );

    /// <summary>
    ///     A map with a warp point on every other click, as flex markers on transients: Slicing and Rhythmic place
    ///     slices, not frames, so only such clicks land exactly; the others play at original speed from their slice.
    /// </summary>
    static FlexWarpPoint[] MarkerMap() {
        var points = new List<(long, double)> { (0, 0) };
        for (var i = 0; ClickFrames[i] < Ramps[^1].FileFrame; i += 2) {
            points.Add(((long)Math.Round(OffsetOf(Ramps, ClickFrames[i])), ClickFrames[i]));
        }

        points.Add((Ramps[^1].Offset, Ramps[^1].FileFrame));
        return Warp([..points]);
    }

    public static (double Median, double Worst, int Count) OnsetErrors(FlexKind kind) {
        var mono = Clicks(FileFrames, ClickFrames);
        var source = ArraySource.Stereo(mono);
        var sliced = kind is FlexKind.Slicing or FlexKind.Rhythmic;
        var warp = sliced ? MarkerMap() : Ramps;
        var audio = Channel(RenderAll(kind, source, warp), 2);
        var errors = new List<double>();
        foreach (var click in sliced ? ClickFrames.Where(static (_, i) => i % 2 == 0) : ClickFrames) {
            var expected = OffsetOf(warp, Onset(mono, click));
            if (expected > warp[^1].Offset - Rate / 10) {
                continue;
            }

            errors.Add((Onset(audio, expected) - expected) * 1_000.0 / Rate);
        }

        var (median, worst) = Stats(errors);
        return (median, worst, errors.Count);
    }

    [Theory]
    [MemberData(nameof(Timed))]
    public void Onsets_land_where_the_warp_map_puts_them(FlexKind kind) {
        SkipWithoutStretch(kind);
        var (median, worst, count) = OnsetErrors(kind);
        output.WriteLine($"{kind}: {count} onsets, median {median:F3} ms, worst {worst:F3} ms");
        Assert.True(median <= 1, $"median onset error {median:F3} ms");
        Assert.True(worst <= 3, $"worst onset error {worst:F3} ms");
    }

    [Theory]
    [MemberData(nameof(All))]
    public void A_render_from_any_offset_equals_the_continuous_render(FlexKind kind) {
        SkipWithoutStretch(kind);
        var source = ArraySource.Stereo(Melody());
        var length = Ramps[^1].Offset;
        var pitch = new FlexPitchPoint[] {
            new() { FileFrame = 0, Cents = 300 }, new() { FileFrame = 100_000, Cents = 300, GainDb = -3 },
            new() { FileFrame = 140_000, Cents = -200, FormantCents = 100 }
        };
        using var continuous = FlexRenderers.Create(kind, 2, Rate, 4_096);
        var whole = Render(continuous, source, Ramps, pitch, length, 0, (int)length, 512, kind);

        using var seeking = FlexRenderers.Create(kind, 2, Rate, 4_096);
        var priming = seeking.PrimingFrames;
        double worst = 0;
        foreach (var start in new long[] { 100_003, 17, 89_000, 92_000, 180_000, 1_234, 260_000 }) {
            var frames = (int)Math.Min(9_000, length - start);
            var part = Render(seeking, source, Ramps, pitch, length, start, frames, 333, kind);
            for (var i = 0; i < part.Length; i++) {
                worst = Math.Max(worst, Math.Abs(part[i] - whole[start * 2 + i]));
            }
        }

        output.WriteLine($"{kind}: priming {priming} frames, worst difference {worst:E2}");
        Assert.True(worst <= 1e-4, $"worst difference {worst:E2}");
    }

    /// <summary>
    ///     The pitch error in cents of a <paramref name="frequency" /> tone rendered by <paramref name="kind" /> at
    ///     <paramref name="rate" /> (file frames per output frame), shifted by <paramref name="cents" />.
    /// </summary>
    public static double PitchError(FlexKind kind, double frequency, double rate, double cents) {
        var source = ArraySource.Stereo(Tones(5 * Rate, 0.5, frequency));
        var length = (long)(4 * Rate / rate);
        var audio = Channel(RenderAll(kind, source, Constant(length, rate), cents == 0 ? null : Shift(cents)), 2);
        var expected = kind == FlexKind.Speed ? frequency * rate : frequency * Math.Pow(2, cents / 1_200);
        var measured = Support.Analysis.PeakFrequency(audio, Rate, (int)(length / 2) - 20_000);
        return Support.Analysis.Cents(measured, expected);
    }

    [Theory]
    [InlineData(FlexKind.Polyphonic)]
    [InlineData(FlexKind.Monophonic)]
    [InlineData(FlexKind.Pitch)]
    public void Stretch_transposes_tones_above_200_Hz_within_20_cents(FlexKind kind) {
        SkipWithoutStretch(kind);
        foreach (var frequency in new[] { 220.0, 440, 1_000 }) {
            foreach (var (rate, cents) in new[] { (1.0, 700.0), (1.0, -500.0), (0.8, 300.0), (1.25, -1_200.0) }) {
                var error = PitchError(kind, frequency, rate, cents);
                output.WriteLine($"{kind} {frequency} Hz, rate {rate}, {cents:+0;-0} ¢: {error:+0.00;-0.00} ¢");
                Assert.True(Math.Abs(error) <= 20, $"{frequency} Hz at {cents} ¢: {error:F2} ¢ off");
            }
        }
    }

    [Theory]
    [InlineData(0.8)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void Speed_moves_the_pitch_by_the_rate_exactly(double rate) {
        foreach (var frequency in new[] { 110.0, 440, 2_000 }) {
            var error = PitchError(FlexKind.Speed, frequency, rate, 0);
            output.WriteLine($"{frequency} Hz at {rate}×: {error:+0.000;-0.000} ¢");
            Assert.True(Math.Abs(error) <= 0.5, $"{frequency} Hz at {rate}×: {error:F3} ¢ off");
        }
    }

    /// <summary>The formant shift (semitones) of a vowel at 220 Hz shifted by <paramref name="cents" />.</summary>
    public static double FormantShift(FlexKind kind, double cents) {
        var vowel = Vowel(4 * Rate, 220);
        var length = 4L * Rate;
        var audio = Channel(RenderAll(kind, ArraySource.Stereo(vowel), Constant(length, 1), Shift(cents)), 2);
        return FlexKit.FormantShift(
            Envelope(vowel.AsSpan(Rate / 2, 3 * Rate)),
            Envelope(audio.AsSpan(Rate / 2, 3 * Rate))
        );
    }

    [Fact]
    public void Pitch_mode_keeps_the_formants_where_Polyphonic_moves_them() {
        SkipWithoutStretch(FlexKind.Pitch);
        foreach (var cents in new[] { 500.0, -500 }) {
            var kept = FormantShift(FlexKind.Pitch, cents);
            var moved = FormantShift(FlexKind.Polyphonic, cents);
            output.WriteLine($"{cents:+0;-0} ¢: Pitch {kept:+0.0;-0.0} st, Polyphonic {moved:+0.0;-0.0} st");
            Assert.True(Math.Abs(kept) <= 2.5, $"Pitch mode moved the formants {kept:F1} st");
            Assert.True(Math.Abs(moved - cents / 100) <= 1.5, $"Polyphonic moved the formants {moved:F1} st");
        }
    }

    // Slice 0 is played at half speed (its 12,000 file frames over 24,000), slice 1 at 1:1.
    static readonly FlexWarpPoint[] HalfSpeedSlice = Warp((0, 0), (24_000, 12_000), (48_000, 36_000));

    [Fact]
    public void Slicing_leaves_the_gap_of_a_stretched_slice_silent() {
        var audio = Channel(RenderAll(FlexKind.Slicing, ArraySource.Stereo(Tones(Rate, 0.5, 440)), HalfSpeedSlice), 2);
        Assert.True(Rms(audio.AsSpan(0, 11_000)) > 0.3);
        Assert.Equal(0, Rms(audio.AsSpan(12_000, 12_000)));
        Assert.True(Rms(audio.AsSpan(24_100, 23_000)) > 0.3);

        // The 2 ms fade before the gap: from full level to silence.
        Assert.True(Math.Abs(audio[11_999]) < 0.01);
        Assert.True(Rms(audio.AsSpan(11_000, 800)) > 0.3);
    }

    [Fact]
    public void Rhythmic_fills_the_gap_of_a_stretched_slice() {
        var audio = Channel(RenderAll(FlexKind.Rhythmic, ArraySource.Stereo(Tones(Rate, 0.5, 440)), HalfSpeedSlice), 2);
        var level = Rms(audio.AsSpan(0, 11_000));
        var quietest = double.MaxValue;
        for (var start = 0; start + 240 <= 23_900; start += 48) {
            quietest = Math.Min(quietest, Rms(audio.AsSpan(start, 240)));
        }

        output.WriteLine($"level {level:F3}, quietest 5 ms {quietest:F3}");
        Assert.True(quietest > 0.5 * level, $"the gap dips to {quietest:F3} (level {level:F3})");
    }

    [Theory]
    [MemberData(nameof(All))]
    public void Renders_and_resets_allocate_nothing(FlexKind kind) {
        SkipWithoutStretch(kind);
        var source = ArraySource.Stereo(Melody());
        var pitch = Shift(200, 100, -2);
        using var renderer = FlexRenderers.Create(kind, 2, Rate, 4_096);
        var buffer = new float[4_096 * 2];
        var offset = 0L;
        var bytes = Support.AllocationProbe.Measure(() => {
                renderer.Reset();
                for (var i = 0; i < 40; i++) {
                    renderer.Render(new(kind, Ramps, pitch, Ramps[^1].Offset), offset, buffer, 2, source);
                    offset = (offset + 4_096) % 200_000;
                }

                renderer.Render(new(kind, Ramps, pitch, Ramps[^1].Offset), 150_000, buffer.AsSpan(0, 1_000), 2, source);
            }
        );
        Assert.Equal(0, bytes);
    }

    [Theory]
    [MemberData(nameof(All))]
    public void Channels_stay_independent(FlexKind kind) {
        SkipWithoutStretch(kind);
        var melody = Melody();
        var opposite = new float[melody.Length * 2];
        for (var i = 0; i < melody.Length; i++) {
            opposite[i * 2] = melody[i];
            opposite[i * 2 + 1] = -melody[i];
        }

        var inverted = RenderAll(kind, new ArraySource(opposite, 2), Ramps, Shift(300));
        var same = RenderAll(kind, ArraySource.Stereo(melody), Ramps, Shift(300));
        double sum = 0, difference = 0;
        for (var i = 0; i < inverted.Length; i += 2) {
            sum = Math.Max(sum, Math.Abs(inverted[i] + inverted[i + 1]) / 2);
            difference = Math.Max(difference, Math.Abs(same[i] - same[i + 1]));
        }

        output.WriteLine($"{kind}: opposite channels sum to {sum:E2}, equal channels differ by {difference:E2}");
        Assert.True(sum <= 1e-4);
        Assert.True(difference <= 1e-5);
    }

    [Fact]
    public void Pitch_point_gain_scales_the_output() {
        var source = ArraySource.Stereo(Tones(Rate, 0.5, 440));
        var plain = Channel(RenderAll(FlexKind.Speed, source, Constant(Rate / 2, 1)), 2);
        var quieter = Channel(RenderAll(FlexKind.Speed, source, Constant(Rate / 2, 1), Shift(0, 0, -6)), 2);
        Assert.Equal(Rms(plain) * Math.Pow(10, -6 / 20.0), Rms(quieter), 3);
    }
}
