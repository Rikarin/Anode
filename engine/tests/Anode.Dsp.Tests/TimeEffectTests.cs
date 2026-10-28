using Anode.Dsp.Effects;
using Anode.Dsp.Effects.Delay;
using Anode.Dsp.Effects.Space;
using Anode.Dsp.Filters;
using Anode.Dsp.Tests.Support;
using static Anode.Dsp.Tests.Support.Analysis;

namespace Anode.Dsp.Tests;

/// <summary>The delays' times and feedback, and the reverb's decay against its setting.</summary>
public sealed class TimeEffectTests {
    static int PeakIndex(ReadOnlySpan<float> signal, int from, int to) {
        var best = from;
        for (var i = from; i < to; i++) {
            if (Math.Abs(signal[i]) > Math.Abs(signal[best])) {
                best = i;
            }
        }

        return best;
    }

    [Theory]
    [InlineData(250f, 500f)]
    [InlineData(1f, 3_999f)]
    [InlineData(123.4f, 77.7f)]
    public void Stereo_delay_repeats_at_its_times(float leftMs, float rightMs) {
        using var delay = Fx.Create(
            StereoDelay.Id,
            (StereoDelay.Sync, 0),
            (StereoDelay.LeftTime, leftMs),
            (StereoDelay.RightTime, rightMs),
            (StereoDelay.LeftFeedback, 50),
            (StereoDelay.RightFeedback, 0),
            (StereoDelay.Mix, 100)
        );
        var (l, r) = Fx.Run(delay, Fx.Impulse(4 * 48_000 + 10_000));
        var left = leftMs * 48.0;
        var right = rightMs * 48.0;
        // The first repeat at the time, to the sample (and between samples, the cubic's share of it).
        Assert.Equal((int)Math.Round(left), PeakIndex(l, 0, (int)left + 50));
        Assert.Equal((int)Math.Round(right), PeakIndex(r, 0, (int)right + 50));
        if (left % 1 == 0) {
            Assert.Equal(1, l[(int)left], 5);
            Assert.Equal(0.5, l[2 * (int)left], 5); // the feedback
            Assert.Equal(0.25, l[3 * (int)left], 5);
        }

        Assert.Equal(0, Peak(r.AsSpan((int)right + 10)), 4); // no feedback on the right
    }

    [Theory]
    [InlineData(120, NoteValues.Quarter, 0.5)]
    [InlineData(120, NoteValues.Eighth, 0.25)]
    [InlineData(90, NoteValues.DottedEighth, 0.5)]
    [InlineData(100, 9, 0.4)] // a quarter-note triplet at 100 bpm
    [InlineData(150, NoteValues.Sixteenth, 0.1)]
    public void Stereo_delay_syncs_to_the_host_tempo(float tempo, int note, double seconds) {
        using var delay = Fx.Create(
            StereoDelay.Id,
            (StereoDelay.Tempo, tempo),
            (StereoDelay.LeftNote, note),
            (StereoDelay.RightNote, note),
            (StereoDelay.LeftFeedback, 0),
            (StereoDelay.Mix, 100)
        );
        var (l, _) = Fx.Run(delay, Fx.Impulse(48_000));
        var at = (int)Math.Round(seconds * 48_000);
        Assert.Equal(at, PeakIndex(l, 0, l.Length));
        Assert.Equal(1, l[at], 4);
    }

    [Fact]
    public void Stereo_delay_crossfeeds_and_filters_the_repeats() {
        using var delay = Fx.Create(
            StereoDelay.Id,
            (StereoDelay.Sync, 0),
            (StereoDelay.LeftTime, 100),
            (StereoDelay.RightTime, 100),
            (StereoDelay.LeftFeedback, 100),
            (StereoDelay.RightFeedback, 100),
            (StereoDelay.Crossfeed, 100),
            (StereoDelay.Mix, 100)
        );
        var silent = new float[20_000];
        var (l, r) = Fx.Run(delay, Fx.Impulse(20_000), silent);
        // Full crossfeed: the repeats ping-pong.
        Assert.Equal(1, l[4_800], 5);
        Assert.Equal(0, r[4_800], 5);
        Assert.Equal(1, r[9_600], 5);
        Assert.Equal(0, l[9_600], 5);
        Assert.Equal(1, l[14_400], 5);

        using var dark = Fx.Create(
            StereoDelay.Id,
            (StereoDelay.Sync, 0),
            (StereoDelay.LeftTime, 10),
            (StereoDelay.HighCut, 2_000),
            (StereoDelay.LeftFeedback, 0),
            (StereoDelay.Mix, 100)
        );
        var (echo, _) = Fx.Run(dark, Fx.Impulse(8_192));
        Assert.Equal(-3, ToDb(MagnitudeAt(echo, 2_000, 48_000)), 0.1);
        Assert.True(ToDb(MagnitudeAt(echo, 8_000, 48_000)) < -20);
    }

    [Fact]
    public void Tape_delay_wanders_around_its_time_and_saturates() {
        using var clean = Fx.Create(
            TapeDelay.Id,
            (TapeDelay.Time, 200),
            (TapeDelay.Wow, 0),
            (TapeDelay.Flutter, 0),
            (TapeDelay.Saturation, 0),
            (TapeDelay.LowCut, 20),
            (TapeDelay.HighCut, 20_000),
            (TapeDelay.Feedback, 0),
            (TapeDelay.Mix, 100)
        );
        var (echo, _) = Fx.Run(clean, Fx.Impulse(20_000));
        Assert.Equal(9_600, PeakIndex(echo, 0, echo.Length));

        // Wow and flutter: a steady tone comes back with its pitch wandering, centred on the tone.
        using var worn = Fx.Create(
            TapeDelay.Id,
            (TapeDelay.Time, 200),
            (TapeDelay.Wow, 100),
            (TapeDelay.Flutter, 100),
            (TapeDelay.Feedback, 0),
            (TapeDelay.Mix, 100)
        );
        var tone = TestWav.Sine(1_000, 48_000, 4, 0.25f);
        var (wobbly, _) = Fx.Run(worn, tone);
        var rates = Enumerable.Range(0, 30)
            .Select(k => ZeroCrossingFrequency(wobbly, 12_000 + k * 4_800, 2_400, 48_000))
            .ToArray();
        Assert.True(rates.Max() - rates.Min() > 2, $"{rates.Min():0.0} … {rates.Max():0.0} Hz");
        Assert.Equal(1_000, rates.Average(), 1.5);

        // Saturation: a loud tone comes back with odd harmonics.
        using var hot = Fx.Create(
            TapeDelay.Id,
            (TapeDelay.Wow, 0),
            (TapeDelay.Flutter, 0),
            (TapeDelay.Saturation, 100),
            (TapeDelay.Feedback, 0),
            (TapeDelay.Mix, 100)
        );
        var loud = TestWav.Sine(375, 48_000, 2, 0.9f);
        var (driven, _) = Fx.Run(hot, loud);
        var spectrum = Spectrum(driven, 48_000, 16_384);
        var bin = 375 * 16_384 / 48_000;
        Assert.True(spectrum[3 * bin].Magnitude > 0.05 * spectrum[bin].Magnitude);
    }

    /// <summary>
    ///     The T30 reverberation time of an impulse response in the octave band at <paramref name="band" /> Hz (as
    ///     ISO 3382 measures it), by Schroeder's backward integration: the energy decay curve's slope from −5 to −35 dB,
    ///     extrapolated to 60 dB.
    /// </summary>
    static double Rt60(ReadOnlySpan<float> left, ReadOnlySpan<float> right, double band = 1_000) {
        var filter = BiquadCoefficients.Design(BiquadType.BandPass, 48_000, band, 1.41);
        BiquadState fl = default, fr = default;
        var energy = new double[left.Length];
        for (var i = 0; i < left.Length; i++) {
            var l = fl.Tick(filter, left[i]);
            var r = fr.Tick(filter, right[i]);
            energy[i] = l * l + r * r;
        }

        var edc = new double[left.Length];
        double sum = 0;
        for (var i = left.Length - 1; i >= 0; i--) {
            sum += energy[i];
            edc[i] = sum;
        }

        var start = Array.FindIndex(edc, e => 10 * Math.Log10(e / edc[0]) <= -5);
        var end = Array.FindIndex(edc, e => 10 * Math.Log10(e / edc[0]) <= -35);
        // A least-squares line through the decay between the two.
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        var n = end - start;
        for (var i = start; i < end; i++) {
            var y = 10 * Math.Log10(edc[i] / edc[0]);
            sx += i;
            sy += y;
            sxx += (double)i * i;
            sxy += i * y;
        }

        var slope = (n * sxy - sx * sy) / (n * sxx - sx * sx); // dB per sample
        return -60 / slope / 48_000;
    }

    [Theory]
    [InlineData(Reverb.Plate, 1.0f)]
    [InlineData(Reverb.Room, 0.6f)]
    [InlineData(Reverb.Hall, 2.5f)]
    [InlineData(Reverb.Hall, 5f)]
    [InlineData(Reverb.Plate, 3f)]
    public void Reverb_decays_in_its_decay_time(int type, float decay) {
        using var reverb = Fx.Create(
            Reverb.Id,
            (Reverb.Type, type),
            (Reverb.Decay, decay),
            (Reverb.Damping, 0),
            (Reverb.Predelay, 0),
            (Reverb.Mix, 100)
        );
        var (l, r) = Fx.Run(reverb, Fx.Impulse((int)(48_000 * decay * 1.2)));
        var measured = Rt60(l, r);
        Assert.Equal(decay, measured, decay * 0.05);
    }

    [Fact]
    public void Reverb_damping_shortens_the_highs_and_predelay_delays_the_onset() {
        using var reverb = Fx.Create(
            Reverb.Id,
            (Reverb.Type, Reverb.Hall),
            (Reverb.Decay, 2),
            (Reverb.Damping, 80),
            (Reverb.Predelay, 100),
            (Reverb.Mix, 100)
        );
        var (l, r) = Fx.Run(reverb, Fx.Impulse(96_000));
        // Nothing before the predelay (plus the diffusers' and first line's own delay).
        Assert.Equal(0, Peak(l.AsSpan(0, 4_800)));
        Assert.True(Peak(l.AsSpan(4_800, 4_800)) > 1e-3);
        // Late in the tail the highs have died away faster than the lows.
        var late = Spectrum(l, 48_000, 16_384);
        var early = Spectrum(l, 6_000, 16_384);

        static double Ratio(System.Numerics.Complex[] s, int from, int to) =>
            Enumerable.Range(from, to - from).Sum(k => s[k].Magnitude);

        var lowDrop = Ratio(late, 100, 300) / Ratio(early, 100, 300);
        var highDrop = Ratio(late, 2_700, 3_400) / Ratio(early, 2_700, 3_400);
        Assert.True(highDrop < lowDrop / 10, $"{lowDrop} {highDrop}");
        Assert.True(Math.Abs(Rms(r) - Rms(l)) < 0.5 * Rms(l));
    }

    [Fact]
    public void Reverb_tail_is_stereo_and_dry_passes_at_zero_mix() {
        using var reverb = Fx.Create(Reverb.Id, (Reverb.Mix, 100), (Reverb.Damping, 0));
        var (l, r) = Fx.Run(reverb, Fx.Impulse(48_000));
        double c = 0;
        for (var i = 0; i < l.Length; i++) {
            c += (double)l[i] * r[i];
        }

        Assert.True(Math.Abs(c / Math.Sqrt(l.Sum(static x => (double)x * x) * r.Sum(static x => (double)x * x))) < 0.3);
    }
}
